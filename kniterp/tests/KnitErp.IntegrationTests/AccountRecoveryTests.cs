using System.Text.RegularExpressions;
using KnitErp.Application.Access;
using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D81: «Доверять этому устройству», «Забыли пароль» по почте и ссылка смены пароля от администратора.</summary>
public sealed class AccountRecoveryTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private const string NewPassword = "совсем другая длинная фраза";
    private static int _innSeed = 830_000_000;

    [SqlFact]
    public async Task Trusted_device_skips_code_until_revoked_expired_or_password_changed()
    {
        var (org, email, key) = await OwnerWithTwoFactorAsync();

        // Вход с кодом и «доверять устройству» — токен для cookie; в журнале запись.
        var device = await SignInWithCodeAsync(email, key, trust: "Chrome, Windows");
        Assert.NotNull(device);

        // С этим браузером — только пароль; неверный пароль не спасает никакое доверие; чужой токен — снова код.
        await using (var s = host.As(null, null))
        {
            var result = await s.SignIn.PasswordSignInAsync(email, Password, device);
            Assert.Equal(SignInStatus.Succeeded, result.Status);
            Assert.True(result.Session!.UsedTwoFactor);
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync(email, "неверный пароль совсем", device)).Status);
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, Password, SetupTokens.Generate())).Status);
        }

        await using (var db = host.NewDb())
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.DeviceTrusted));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SignedIn
                                                            && a.After == "пароль и доверенное устройство"));
            Assert.Null(await db.TrustedDevices.Where(d => d.UserId == org.OwnerUserId).Select(d => d.RevokedAtUtc).SingleAsync());
        }

        // Список и отзыв — только своих устройств.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var mine = Assert.Single(await s.Devices.ListAsync());
            Assert.Equal("Chrome, Windows", mine.Label);
            Assert.NotNull(mine.LastUsedAtUtc);
            await s.Devices.RevokeAsync(mine.Id);
            Assert.Empty(await s.Devices.ListAsync());
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, Password, device)).Status);
        }

        // Новое доверие истекает через 30 дней.
        device = await SignInWithCodeAsync(email, key, trust: "Firefox, Linux");
        var stranger = await OwnerWithTwoFactorAsync();
        await using (var s = host.As(stranger.Org.OwnerUserId, stranger.Org.OrganizationId))
        {
            var foreign = await s.Db.TrustedDevices.Where(d => d.UserId == org.OwnerUserId && d.RevokedAtUtc == null).Select(d => d.Id).SingleAsync();
            await Assert.ThrowsAsync<NotFoundException>(() => s.Devices.RevokeAsync(foreign));
        }

        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(31);
        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, Password, device)).Status);
        }

        // Смена пароля по ссылке закрывает доверенные устройства, даже действующие.
        device = await SignInWithCodeAsync(email, key, trust: "Safari, iOS");
        await using (var s = host.As(null, null))
        {
            await s.PasswordReset.RequestAsync(email);
        }

        var token = await TokenFromMailAsync(email);
        await using (var s = host.As(null, null))
        {
            await s.PasswordReset.ResetAsync(token, NewPassword, NewPassword);
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, NewPassword, device)).Status);
        }
    }

    [SqlFact]
    public async Task Forgot_password_mail_link_changes_only_password_and_closes_sessions()
    {
        var (org, email, key) = await OwnerWithTwoFactorAsync();
        Guid oldStamp;
        await using (var db = host.NewDb())
        {
            oldStamp = (await db.Users.SingleAsync(u => u.Id == org.OwnerUserId)).SecurityStamp;
        }

        await using (var s = host.As(null, null))
        {
            // Неизвестный адрес — тот же ответ, письма нет.
            await s.PasswordReset.RequestAsync("nobody-" + Guid.NewGuid().ToString("N") + "@test.local");
            await s.PasswordReset.RequestAsync(email);
            await s.PasswordReset.RequestAsync(email);
        }

        // Повторный запрос раньше чем через 5 минут — второго письма нет.
        Assert.NotNull(await host.Mail.WaitForAsync(email));
        await Task.Delay(300);
        var letter = Assert.Single(host.Mail.Sent, m => m.To == email);
        var token = await TokenFromMailAsync(email);
        Assert.Contains("https://erp.test/account/reset-password?token=", letter.Text);
        Assert.Equal("knitERP: смена пароля", letter.Subject);

        await using (var s = host.As(null, null))
        {
            Assert.Equal(email, await s.PasswordReset.DescribeAsync(token));
            Assert.Null(await s.PasswordReset.DescribeAsync("не-тот-токен"));
            Assert.Equal("auth.password.mismatch", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.PasswordReset.ResetAsync(token, NewPassword, NewPassword + "x"))).Code);
            Assert.Equal("auth.password.too_short", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.PasswordReset.ResetAsync(token, "коротко", "коротко"))).Code);

            // До смены прежний пароль действует.
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
            await s.PasswordReset.ResetAsync(token, NewPassword, NewPassword);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);

            // Второй фактор ссылка не отменяет; прежние сессии закрыты; ссылка одноразовая.
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, NewPassword)).Status);
            Assert.False(await s.SignIn.ValidateSessionAsync(org.OwnerUserId, org.OrganizationId, oldStamp, await LastSessionAsync(org.OwnerUserId)));
            Assert.Equal("auth.reset.invalid", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.PasswordReset.ResetAsync(token, NewPassword, NewPassword))).Code);
        }

        // Ссылка живёт 1 час.
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(10);
        await using (var s = host.As(null, null))
        {
            await s.PasswordReset.RequestAsync(email);
        }

        var late = await TokenFromMailAsync(email);
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(61);
        await using (var s = host.As(null, null))
        {
            Assert.Null(await s.PasswordReset.DescribeAsync(late));
            Assert.Equal("auth.reset.expired", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.PasswordReset.ResetAsync(late, Password, Password))).Code);
        }

        await using (var db = host.NewDb())
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.PasswordReset));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.PasswordResetRequested
                                                            && a.Reason!.Contains("повтор")));
        }

        // Почта не настроена — самостоятельное восстановление недоступно.
        host.Mail.IsConfigured = false;
        try
        {
            await using var s = host.As(null, null);
            Assert.False(s.PasswordReset.SelfServiceAvailable);
            Assert.Equal("auth.reset.unavailable", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.PasswordReset.RequestAsync(email))).Code);
        }
        finally
        {
            host.Mail.IsConfigured = true;
        }

        _ = key;
    }

    [SqlFact]
    public async Task Administrator_issues_password_link_by_the_same_rules_as_two_factor_reset()
    {
        var (org, ownerEmail, _) = await OwnerWithTwoFactorAsync();
        var (adminId, _) = await InviteAndAcceptAsync(org, SystemRoles.Administrator);
        var (keeperId, keeperEmail) = await InviteAndAcceptAsync(org, SystemRoles.Storekeeper);

        await using (var s = host.As(adminId, org.OrganizationId))
        {
            Assert.Equal("access.admin_grant_denied", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.IssuePasswordResetAsync(org.OwnerUserId, "Забыл пароль"))).Code);
            Assert.Equal("access.password_reset.self", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.IssuePasswordResetAsync(adminId, "Забыл пароль"))).Code);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.IssuePasswordResetAsync(keeperId, null));

            var link = await s.Access.IssuePasswordResetAsync(keeperId, "Забыл пароль");
            Assert.Equal(host.Clock.UtcNow.AddHours(24), link.ExpiresAtUtc);
            await s.PasswordReset.ResetAsync(link.SetupToken, NewPassword, NewPassword);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.PasswordSignInAsync(keeperEmail, NewPassword)).Status);
        }

        // Чужая организация — пользователь «не найден».
        var other = await CreateOrganizationAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => s.Access.IssuePasswordResetAsync(keeperId, "чужой"));
        }

        _ = ownerEmail;
    }

    private async Task<string?> SignInWithCodeAsync(string email, string key, string? trust)
    {
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(Totp.StepSeconds);
        await using var s = host.As(null, null);
        var pending = (await s.SignIn.PasswordSignInAsync(email, Password)).Pending!;
        var result = await s.SignIn.TwoFactorSignInAsync(pending, Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow)), trust);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        return result.DeviceToken;
    }

    private async Task<string> TokenFromMailAsync(string email)
    {
        // D115: после смены пароля приходит ещё и уведомление — берём письмо со ссылкой.
        (string To, string Subject, string Text) mail = default;
        for (var i = 0; i < 50 && mail.To is null; i++)
        {
            mail = host.Mail.Sent.LastOrDefault(m => m.To == email && m.Text.Contains("token=", StringComparison.Ordinal));
            if (mail.To is null)
            {
                await Task.Delay(100);
            }
        }

        if (mail.To is null)
        {
            throw new InvalidOperationException("Письмо не пришло.");
        }

        var token = Regex.Match(mail.Text!, @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;
        host.Mail.Sent.Clear();
        return token;
    }

    /// <summary>Владелец новой организации с паролем и подключённой 2FA.</summary>
    private async Task<(CreatedOrganization Org, string Email, string Key)> OwnerWithTwoFactorAsync()
    {
        var org = await CreateOrganizationAsync();
        string email;
        await using (var db = host.NewDb())
        {
            email = (await db.Users.SingleAsync(u => u.Id == org.OwnerUserId)).Email;
        }

        await using (var s = host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
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
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.CompleteTwoFactorSetupAsync(pending, Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow)))).Status);
        }

        return (org, email, key);
    }

    private async Task<(long UserId, string Email)> InviteAndAcceptAsync(CreatedOrganization org, string roleCode)
    {
        var email = $"user-{Guid.NewGuid():N}@test.local";
        InvitationResult invitation;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new InviteUserCommand(email, SystemRoles.NameOf(roleCode), roleCode,
                SystemRoles.IsAdministrative(roleCode) ? "Тест" : null));
        }

        await using (var s = host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password);
        }

        return (invitation.UserId, email);
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    /// <summary>D122: последняя серверная сессия пользователя (вход при подключении 2FA).</summary>
    private async Task<Guid> LastSessionAsync(long userId)
    {
        await using var db = host.NewDb();
        return await db.UserSessions.Where(x => x.UserId == userId).OrderByDescending(x => x.Id).Select(x => x.PublicId).FirstAsync();
    }
}
