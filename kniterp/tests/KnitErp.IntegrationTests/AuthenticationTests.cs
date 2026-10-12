using KnitErp.Application.Access;
using KnitErp.Application.Authentication;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Вход: приглашение 72 ч, пароль, блокировка после неудачных попыток, 2FA для Владельца и Администратора.</summary>
public sealed class AuthenticationTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private static int _innSeed = 200_000_000;

    [SqlFact]
    public async Task Invited_storekeeper_sets_password_and_signs_in_without_second_factor()
    {
        var org = await CreateOrganizationAsync();
        var (userId, email) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        await using var s = host.As(null, null);
        var result = await s.SignIn.PasswordSignInAsync(email, Password);

        Assert.Equal(SignInStatus.Succeeded, result.Status);
        Assert.Equal(userId, result.Session!.UserId);
        Assert.Equal(org.OrganizationId, result.Session.OrganizationId);
        Assert.False(result.Session.UsedTwoFactor);
        Assert.True(await s.SignIn.ValidateSessionAsync(userId, org.OrganizationId, result.Session.SecurityStamp, result.Session.SessionId));

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(e => e.OrganizationId == org.OrganizationId && e.ActorUserId == userId
                                                         && e.Action == AuditActions.SignedIn));
        Assert.True(await db.AuditEntries.AnyAsync(e => e.OrganizationId == org.OrganizationId && e.ActorUserId == userId
                                                         && e.Action == AuditActions.InvitationAccepted));
    }

    [SqlFact]
    public async Task Expired_invitation_is_rejected_and_reissued_link_works()
    {
        var org = await CreateOrganizationAsync();
        InvitationResult invitation;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new InviteUserCommand(NewEmail(), "Бухгалтер", SystemRoles.Accountant, null));
        }

        Assert.Equal(host.Clock.UtcNow.AddHours(72), invitation.ExpiresAtUtc);
        host.Clock.UtcNow = host.Clock.UtcNow.AddHours(73);

        await using (var s = host.As(null, null))
        {
            Assert.Null(await s.SignIn.DescribeInvitationAsync(invitation.SetupToken));
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password));
            Assert.Equal("auth.invitation.expired", ex.Code);
        }

        InvitationResult reissued;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            reissued = await s.Access.ReissueInvitationAsync(invitation.UserId);
        }

        await using (var s = host.As(null, null))
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password));
            await s.SignIn.AcceptInvitationAsync(reissued.SetupToken, Password, Password);
        }
    }

    [SqlFact]
    public async Task Five_wrong_passwords_lock_sign_in_for_fifteen_minutes()
    {
        var org = await CreateOrganizationAsync();
        var (userId, email) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        for (var i = 1; i < SignInPolicy.MaxFailedAttempts; i++)
        {
            await using var s = host.As(null, null);
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync(email, "неверный пароль номер " + i)).Status);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.LockedOut, (await s.SignIn.PasswordSignInAsync(email, "последняя неверная попытка")).Status);
        }

        // Во время блокировки не принимается даже верный пароль.
        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.LockedOut, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
        }

        host.Clock.UtcNow = host.Clock.UtcNow.Add(SignInPolicy.LockoutDuration);
        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
        }

        await using var db = host.NewDb();
        Assert.Equal(1, await db.AuditEntries.CountAsync(e => e.ActorUserId == userId && e.Action == AuditActions.LockedOut));
    }

    [SqlFact]
    public async Task Parallel_wrong_passwords_are_all_counted()
    {
        var org = await CreateOrganizationAsync();
        var (_, email) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        var attempts = Enumerable.Range(0, SignInPolicy.MaxFailedAttempts).Select(async i =>
        {
            await using var s = host.As(null, null);
            return (await s.SignIn.PasswordSignInAsync(email, "параллельная попытка " + i)).Status;
        });
        await Task.WhenAll(attempts);

        await using var check = host.As(null, null);
        Assert.Equal(SignInStatus.LockedOut, (await check.SignIn.PasswordSignInAsync(email, Password)).Status);
    }

    [SqlFact]
    public async Task Unknown_email_and_wrong_password_look_the_same()
    {
        var org = await CreateOrganizationAsync();
        var (_, email) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        await using var s = host.As(null, null);
        Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync("nobody@test.local", Password)).Status);
        Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync(email, "не тот пароль")).Status);
    }

    [SqlFact]
    public async Task Owner_must_set_up_two_factor_before_first_sign_in()
    {
        var org = await CreateOrganizationAsync();
        Assert.NotNull(org.OwnerSetupToken);
        var email = await EmailOfAsync(org.OwnerUserId);

        await using (var s = host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }

        PendingSignIn pending;
        await using (var s = host.As(null, null))
        {
            var result = await s.SignIn.PasswordSignInAsync(email, Password);
            Assert.Equal(SignInStatus.TwoFactorSetupRequired, result.Status);
            Assert.Null(result.Session);
            pending = result.Pending!;
        }

        string key;
        await using (var s = host.As(null, null))
        {
            key = (await s.SignIn.BeginTwoFactorSetupAsync(pending)).Key;
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.CompleteTwoFactorSetupAsync(pending, WrongCode(key))).Status);
        }

        await using (var s = host.As(null, null))
        {
            var result = await s.SignIn.CompleteTwoFactorSetupAsync(pending, CurrentCode(key));
            Assert.Equal(SignInStatus.Succeeded, result.Status);
            Assert.True(result.Session!.UsedTwoFactor);
        }

        // Следующий вход — пароль и код; повтор того же кода не принимается.
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(Totp.StepSeconds);
        await using (var s = host.As(null, null))
        {
            var result = await s.SignIn.PasswordSignInAsync(email, Password);
            Assert.Equal(SignInStatus.TwoFactorRequired, result.Status);
            pending = result.Pending!;
        }

        var code = CurrentCode(key);
        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.TwoFactorSignInAsync(pending, code)).Status);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.TwoFactorSignInAsync(pending, code)).Status);
        }
    }

    [SqlFact]
    public async Task Administrator_without_two_factor_gets_no_session()
    {
        var org = await CreateOrganizationAsync();
        var (_, email) = await InviteAndAcceptAsync(org, SystemRoles.Administrator);

        await using var s = host.As(null, null);
        var result = await s.SignIn.PasswordSignInAsync(email, Password);
        Assert.Equal(SignInStatus.TwoFactorSetupRequired, result.Status);
        Assert.Null(result.Session);
    }

    [SqlFact]
    public async Task Blocking_closes_session_and_prevents_sign_in()
    {
        var org = await CreateOrganizationAsync();
        var (userId, email) = await InviteAndAcceptAsync(org, SystemRoles.Accountant);

        SessionIdentity session;
        await using (var s = host.As(null, null))
        {
            session = (await s.SignIn.PasswordSignInAsync(email, Password)).Session!;
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.BlockAsync(userId, "Увольнение");
        }

        await using (var s = host.As(null, null))
        {
            Assert.False(await s.SignIn.ValidateSessionAsync(userId, org.OrganizationId, session.SecurityStamp, session.SessionId));
            Assert.Equal(SignInStatus.NoOrganization, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
        }
    }

    [SqlFact]
    public async Task Role_change_closes_existing_session()
    {
        var org = await CreateOrganizationAsync();
        var (userId, email) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        SessionIdentity session;
        await using (var s = host.As(null, null))
        {
            session = (await s.SignIn.PasswordSignInAsync(email, Password)).Session!;
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(userId, SystemRoles.SeniorStorekeeper, "Повышение"));
        }

        await using (var s = host.As(null, null))
        {
            Assert.False(await s.SignIn.ValidateSessionAsync(userId, org.OrganizationId, session.SecurityStamp, session.SessionId));
        }
    }

    [SqlFact]
    public async Task Session_cannot_switch_into_foreign_organization()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        var (_, email) = await InviteAndAcceptAsync(a, SystemRoles.Storekeeper);

        await using var s = host.As(null, null);
        var session = (await s.SignIn.PasswordSignInAsync(email, Password)).Session!;
        await Assert.ThrowsAsync<KnitErp.Application.Common.NotFoundException>(() =>
            s.SignIn.SwitchOrganizationAsync(session, b.OrganizationId));
    }

    [SqlFact]
    public async Task Only_owner_may_reset_two_factor_of_administrator()
    {
        var org = await CreateOrganizationAsync();
        var (adminId, _) = await InviteAndAcceptAsync(org, SystemRoles.Administrator);
        var (otherAdminId, _) = await InviteAndAcceptAsync(org, SystemRoles.Administrator);
        var (keeperId, _) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        await using (var s = host.As(adminId, org.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.ResetTwoFactorAsync(otherAdminId, "Потерян телефон"));
            Assert.Equal("access.admin_grant_denied", ex.Code);
        }

        await using (var s = host.As(adminId, org.OrganizationId))
        {
            await s.Access.ResetTwoFactorAsync(keeperId, "Потерян телефон");
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ResetTwoFactorAsync(otherAdminId, "Потерян телефон");
        }
    }

    private async Task<(long UserId, string Email)> InviteAndAcceptAsync(CreatedOrganization org, string roleCode)
    {
        var email = NewEmail();
        InvitationResult invitation;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new InviteUserCommand(email, SystemRoles.NameOf(roleCode), roleCode,
                SystemRoles.IsAdministrative(roleCode) ? "Тест" : null));
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(email, await s.SignIn.DescribeInvitationAsync(invitation.SetupToken));
            await s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password);
        }

        return (invitation.UserId, email);
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        var inn = NextValidInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow",
            $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    private async Task<string> EmailOfAsync(long userId)
    {
        await using var db = host.NewDb();
        return (await db.Users.SingleAsync(u => u.Id == userId)).Email;
    }

    private string CurrentCode(string key) => Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow));

    private string WrongCode(string key)
    {
        var step = Totp.StepAt(host.Clock.UtcNow);
        var valid = new[] { step - 1, step, step + 1 }.Select(x => Totp.Compute(key, x)).ToHashSet();
        return Enumerable.Range(0, 10).Select(d => new string((char)('0' + d), 6)).First(c => !valid.Contains(c));
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@test.local";

    private static string NextValidInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }
}
