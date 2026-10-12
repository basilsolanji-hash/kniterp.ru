using KnitErp.Application.Access;
using KnitErp.Application.Authentication;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D123: повторное подтверждение (step-up) опасных действий администратора. Без свежего подтверждения — «auth.stepup.required»,
/// после пароля (и кода при 2FA) — действие проходит 10 минут, потом снова нужно подтверждение. Неверный пароль — отказ,
/// запись в журнале и попытка в общий счётчик блокировки входа. Данные вымышленные.
/// </summary>
public sealed class StepUpTests : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private static int _innSeed = 941_230_000;
    private readonly SqlTestHost host;

    public StepUpTests(SqlTestHost host)
    {
        this.host = host;
        host.AutoStepUp = false; // этот класс проверяет сам порядок подтверждения
    }

    [SqlFact]
    public async Task Block_requires_step_up_succeeds_after_password_and_code_and_expires_after_10_minutes()
    {
        var (org, key) = await OwnerAsync(twoFactor: true);
        var worker = await InviteActiveAsync(org, SystemRoles.Employee);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var required = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.BlockAsync(worker, "тест"));
            Assert.Equal(StepUp.RequiredCode, required.Code);
        }

        Assert.Equal(MembershipStatus.Active, await MembershipAsync(org, worker));

        // Пароль без кода при 2FA не подходит.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("auth.confirm.failed", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.StepUp.ConfirmAsync(Password, null))).Code);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.StepUp.ConfirmAsync(Password, NextCode(key!));
        }

        var confirmedAt = host.Clock.UtcNow;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.BlockAsync(worker, "тест");
        }

        Assert.Equal(MembershipStatus.Blocked, await MembershipAsync(org, worker));

        // Через 9 минут отметка ещё действует.
        host.Clock.UtcNow = confirmedAt.AddMinutes(9);
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.UnblockAsync(worker, "тест");
        }

        // Через 10 минут — снова нужно подтверждение.
        host.Clock.UtcNow = confirmedAt.AddMinutes(10);
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(StepUp.RequiredCode, (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.BlockAsync(worker, "тест"))).Code);
        }

        Assert.Equal(MembershipStatus.Active, await MembershipAsync(org, worker));
        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.ActorUserId == org.OwnerUserId
                                                         && a.Action == AuditActions.StepUpConfirmed));
    }

    [SqlFact]
    public async Task Wrong_password_is_refused_audited_and_counts_toward_lockout()
    {
        var (org, _) = await OwnerAsync(twoFactor: false);
        for (var i = 1; i <= SignInPolicy.MaxFailedAttempts; i++)
        {
            await using var s = host.As(org.OwnerUserId, org.OrganizationId);
            var failed = await Assert.ThrowsAsync<BusinessRuleException>(() => s.StepUp.ConfirmAsync("неверный пароль совсем", null));
            Assert.Equal("auth.confirm.failed", failed.Code);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            // Счётчик общий со входом: после 5 неудач подтверждение (и вход) заблокированы даже с верным паролем.
            Assert.Equal("auth.locked", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.StepUp.ConfirmAsync(Password, null))).Code);
            Assert.Equal(StepUp.RequiredCode,
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Integrations.IssueTokenAsync("Интеграция", [Permissions.SalesView], 30))).Code);
        }

        await using var db = host.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == org.OwnerUserId);
        Assert.True(user.IsLockedOut(host.Clock.UtcNow));
        var failures = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.StepUpFailed).ToListAsync();
        Assert.Equal(SignInPolicy.MaxFailedAttempts, failures.Count);
        Assert.All(failures, a => Assert.Contains("неверный пароль при повторном подтверждении", a.Reason));
        Assert.False(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.StepUpConfirmed));
        Assert.Equal(0, await db.ApiTokens.CountAsync(t => t.OrganizationId == org.OrganizationId));
    }

    [SqlFact]
    public async Task Protected_actions_require_step_up_and_ordinary_ones_do_not()
    {
        var (org, _) = await OwnerAsync(twoFactor: false);
        var worker = await InviteActiveAsync(org, SystemRoles.Employee);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            async Task Required(Func<Task> action) =>
                Assert.Equal(StepUp.RequiredCode, (await Assert.ThrowsAsync<BusinessRuleException>(action)).Code);

            await Required(() => s.Access.ChangeRoleAsync(new ChangeRoleCommand(worker, SystemRoles.Owner, "передача")));
            await Required(() => s.Access.ChangeRoleAsync(new ChangeRoleCommand(worker, SystemRoles.Administrator, "тест")));
            await Required(() => s.Access.InviteAsync(new InviteUserCommand("stepup-admin@test.local", "Администратор", SystemRoles.Administrator, "тест")));
            await Required(() => s.Access.GrantIndividualAsync(worker, Permissions.UserManage, false, null, "тест"));
            await Required(() => s.Access.ResetTwoFactorAsync(worker, "тест"));
            await Required(() => s.Access.IssuePasswordResetAsync(worker, "тест"));
            await Required(() => s.Roles.CreateAsync("Кадровик с доступом", null, [Permissions.UserView, Permissions.UserManage]));
            await Required(() => s.Integrations.IssueTokenAsync("Интеграция", [Permissions.SalesView], 30));

            // Обычные действия — без подтверждения.
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(worker, SystemRoles.Accountant, null));
            await s.Access.GrantIndividualAsync(worker, Permissions.CatalogView, false, null, null);
            await s.Roles.CreateAsync("Только просмотр каталога", null, [Permissions.CatalogView]);
        }

        // Передача роли Владельца после подтверждения.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.StepUp.ConfirmAsync(Password, null);
        }

        long tokenId;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(worker, SystemRoles.Owner, "передача"));
            tokenId = (await s.Integrations.IssueTokenAsync("Интеграция", [Permissions.SalesView], 30)).Id;
        }

        // Отметка — у сессии того, кто подтвердил, а не у организации: новый Владелец подтверждает сам.
        await using (var s = host.As(worker, org.OrganizationId))
        {
            Assert.Equal(StepUp.RequiredCode, (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Integrations.RevokeTokenAsync(tokenId))).Code);
        }

        // Запрос по токену API подтвердить нельзя, даже если у пользователя есть отметка в браузере.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.True(new StepUpGuard(host.StepUpStateOf(org.OwnerUserId), new TestUser { UserId = org.OwnerUserId }, host.Clock).IsFresh(org.OwnerUserId));
            Assert.False(new StepUpGuard(host.StepUpStateOf(org.OwnerUserId), new TestUser { UserId = org.OwnerUserId, ApiTokenId = tokenId }, host.Clock)
                .IsFresh(org.OwnerUserId));
            await s.Integrations.RevokeTokenAsync(tokenId);
        }
    }

    private string NextCode(string key)
    {
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(Totp.StepSeconds);
        return Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow));
    }

    private async Task<MembershipStatus> MembershipAsync(CreatedOrganization org, long userId)
    {
        await using var db = host.NewDb();
        return await db.OrganizationMembers.Where(m => m.OrganizationId == org.OrganizationId && m.UserId == userId).Select(m => m.Status).SingleAsync();
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local", SystemRoles.NameOf(roleCode), roleCode, null))).UserId;
        }

        await using var db = host.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(CreatedOrganization Org, string? Key)> OwnerAsync(bool twoFactor)
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        var email = $"owner-{inn}@test.local";
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", email, $"Владелец {inn}"));
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }

        if (!twoFactor)
        {
            return (org, null);
        }

        PendingSignIn pending;
        string key;
        await using (var s = host.As(null, null))
        {
            pending = (await s.SignIn.PasswordSignInAsync(email, Password)).Pending!;
            key = (await s.SignIn.BeginTwoFactorSetupAsync(pending)).Key;
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.CompleteTwoFactorSetupAsync(pending, NextCode(key))).Status);
        }

        return (org, key);
    }
}
