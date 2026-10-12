using System.Text.RegularExpressions;
using KnitErp.Application.Authentication;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D115: смена пароля и «выйти на всех устройствах» с повторной проверкой, отзыв токенов API после сброса пароля,
/// блокировка входа не закрывает доверенное устройство, резервный код гасится один раз даже при одновременных входах.
/// </summary>
public sealed class AccountSecurityTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private const string NewPassword = "совсем другая длинная фраза";
    private static int _innSeed = 840_000_000;

    [SqlFact]
    public async Task Change_password_needs_current_password_and_code_and_closes_all_sessions()
    {
        var (org, email, key) = await OwnerWithTwoFactorAsync();
        var oldStamp = await StampAsync(org.OwnerUserId);

        // Неверный текущий пароль или код — отказ и неудачная попытка в общий счётчик блокировки.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var wrong = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Account.ChangePasswordAsync("неверный пароль совсем", NewPassword, NewPassword, NextCode(key)));
            Assert.Equal("auth.confirm.failed", wrong.Code);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var noCode = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Account.ChangePasswordAsync(Password, NewPassword, NewPassword, "000000"));
            Assert.Equal("auth.confirm.failed", noCode.Code);
            Assert.Equal(2, (await s.Db.Users.SingleAsync(u => u.Id == org.OwnerUserId)).FailedSignInCount);
        }

        // Известный пароль и ряд клавиш не принимаются (NIST 800-63B).
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var common = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Account.ChangePasswordAsync(Password, "qwertyuiop", "qwertyuiop", NextCode(key)));
            Assert.Equal("auth.password.common", common.Code);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Account.ChangePasswordAsync(Password, NewPassword, NewPassword, NextCode(key));
            Assert.False(await s.SignIn.ValidateSessionAsync(org.OwnerUserId, org.OrganizationId, oldStamp, await LastSessionAsync(org.OwnerUserId)));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(0, (await db.Users.SingleAsync(u => u.Id == org.OwnerUserId)).FailedSignInCount);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Failed, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, NewPassword)).Status);
        }

        await using (var db = host.NewDb())
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.PasswordChanged));
        }

        var letter = await MailAsync(email, "Пароль knitERP изменён");
        Assert.DoesNotContain("http", letter.Text, StringComparison.OrdinalIgnoreCase);
    }

    [SqlFact]
    public async Task Sign_out_everywhere_forgets_devices_and_on_request_revokes_own_api_tokens()
    {
        var (org, email, key) = await OwnerWithTwoFactorAsync();
        var device = await SignInWithCodeAsync(email, key, trust: "Chrome, Windows");
        var tokenId = await IssueTokenAsync(org);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(0, await s.Account.SignOutEverywhereAsync(Password, NextCode(key), revokeApiTokens: false));
        }

        await using (var s = host.As(null, null))
        {
            // Доверие устройству забыто — снова нужен код; токен API жив.
            Assert.Equal(SignInStatus.TwoFactorRequired, (await s.SignIn.PasswordSignInAsync(email, Password, device)).Status);
            Assert.Null(await s.Db.ApiTokens.Where(t => t.Id == tokenId).Select(t => t.RevokedAtUtc).SingleAsync());
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(1, await s.Account.SignOutEverywhereAsync(Password, NextCode(key), revokeApiTokens: true));
        }

        await using (var db = host.NewDb())
        {
            Assert.NotNull(await db.ApiTokens.Where(t => t.Id == tokenId).Select(t => t.RevokedAtUtc).SingleAsync());
            Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SessionsRevoked));
        }
    }

    [SqlFact]
    public async Task Password_reset_link_revokes_api_tokens_issued_by_the_user()
    {
        var (org, email, _) = await OwnerWithTwoFactorAsync();
        var tokenId = await IssueTokenAsync(org);

        await using (var s = host.As(null, null))
        {
            await s.PasswordReset.RequestAsync(email);
        }

        var link = await MailAsync(email, "knitERP: смена пароля");
        var token = Regex.Match(link.Text, @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;
        await using (var s = host.As(null, null))
        {
            await s.PasswordReset.ResetAsync(token, NewPassword, NewPassword);
        }

        await using var db = host.NewDb();
        Assert.NotNull(await db.ApiTokens.Where(t => t.Id == tokenId).Select(t => t.RevokedAtUtc).SingleAsync());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.PasswordReset
                                                        && a.Reason!.Contains("отозвано токенов API: 1")));
    }

    [SqlFact]
    public async Task Lockout_from_guessing_does_not_close_the_owners_trusted_device()
    {
        var (_, email, key) = await OwnerWithTwoFactorAsync();
        var device = await SignInWithCodeAsync(email, key, trust: "Firefox, Linux");

        await using (var s = host.As(null, null))
        {
            for (var i = 0; i < SignInPolicy.MaxFailedAttempts; i++)
            {
                await s.SignIn.PasswordSignInAsync(email, "подбор пароля номер " + i);
            }

            Assert.Equal(SignInStatus.LockedOut, (await s.SignIn.PasswordSignInAsync(email, Password)).Status);
        }

        await using (var s = host.As(null, null))
        {
            // С доверенного устройства владелец входит; неверный пароль и там не проходит.
            Assert.Equal(SignInStatus.LockedOut, (await s.SignIn.PasswordSignInAsync(email, "подбор с устройства", device)).Status);
        }

        await using (var s = host.As(null, null))
        {
            Assert.Equal(SignInStatus.Succeeded, (await s.SignIn.PasswordSignInAsync(email, Password, device)).Status);
        }
    }

    [SqlFact]
    public async Task Recovery_code_signs_in_once_even_when_used_concurrently()
    {
        var (org, email, _) = await OwnerWithTwoFactorAsync();
        string code;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            code = (await s.Recovery.IssueAsync())[0];
        }

        PendingSignIn pending;
        await using (var s = host.As(null, null))
        {
            pending = (await s.SignIn.PasswordSignInAsync(email, Password)).Pending!;
        }

        async Task<SignInStatus> TryAsync()
        {
            await using var s = host.As(null, null);
            try
            {
                return (await s.SignIn.TwoFactorSignInAsync(pending, code)).Status;
            }
            catch (DbUpdateConcurrencyException)
            {
                return SignInStatus.Failed;
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(TryAsync)));
        Assert.Equal(1, results.Count(r => r == SignInStatus.Succeeded));
        await using var db = host.NewDb();
        Assert.Equal(1, await db.RecoveryCodes.CountAsync(r => r.UserId == org.OwnerUserId && r.UsedAtUtc != null));
    }

    private string NextCode(string key)
    {
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(Totp.StepSeconds);
        return Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow));
    }

    private async Task<Guid> StampAsync(long userId)
    {
        await using var db = host.NewDb();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
    }

    private async Task<long> IssueTokenAsync(CreatedOrganization org)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        await s.Integrations.IssueTokenAsync("Интеграция D115", [Permissions.SalesView], 30);
        return await s.Db.ApiTokens.Where(t => t.OrganizationId == org.OrganizationId).Select(t => t.Id).SingleAsync();
    }

    private async Task<(string To, string Subject, string Text)> MailAsync(string email, string subject)
    {
        for (var i = 0; i < 50; i++)
        {
            var hit = host.Mail.Sent.LastOrDefault(m => m.To == email && m.Subject == subject);
            if (hit.To is not null)
            {
                return hit;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"Письмо «{subject}» не пришло.");
    }

    private async Task<string?> SignInWithCodeAsync(string email, string key, string? trust)
    {
        await using var s = host.As(null, null);
        var pending = (await s.SignIn.PasswordSignInAsync(email, Password)).Pending!;
        var result = await s.SignIn.TwoFactorSignInAsync(pending, NextCode(key), trust);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        return result.DeviceToken;
    }

    private async Task<(CreatedOrganization Org, string Email, string Key)> OwnerWithTwoFactorAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }

        var email = $"owner-{inn}@test.local";
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

        return (org, email, key);
    }

    /// <summary>D122: последняя серверная сессия пользователя (вход при подключении 2FA).</summary>
    private async Task<Guid> LastSessionAsync(long userId)
    {
        await using var db = host.NewDb();
        return await db.UserSessions.Where(x => x.UserId == userId).OrderByDescending(x => x.Id).Select(x => x.PublicId).FirstAsync();
    }
}
