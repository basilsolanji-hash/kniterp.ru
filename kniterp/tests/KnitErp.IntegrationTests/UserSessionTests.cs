using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D122: серверная сессия на каждый вход, список своих сессий, завершение одной сессии с повторной проверкой,
/// чужая сессия — «не найдено», «Выйти везде» и выход завершают сессии, время активности пишется не чаще раза в 5 минут.
/// </summary>
public sealed class UserSessionTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
    private const string FirefoxLinux = "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0";
    private static int _innSeed = 850_000_000;

    [SqlFact]
    public async Task Sign_in_creates_server_session_with_label_and_network_only()
    {
        var owner = await OwnerWithTwoFactorAsync();
        var session = await SignInAsync(owner, ChromeWindows, "203.0.113.57");

        Assert.NotNull(session.SessionId);
        await using (var db = host.NewDb())
        {
            var row = await db.UserSessions.SingleAsync(x => x.PublicId == session.SessionId);
            Assert.Equal(owner.Org.OwnerUserId, row.UserId);
            Assert.Equal("Chrome, Windows", row.Label);
            Assert.Equal("203.0.113.0/24", row.AddressPrefix);
            Assert.Null(row.RevokedAtUtc);
        }

        await using (var s = host.As(null, null))
        {
            Assert.True(await ValidAsync(s, session));

            // Cookie без серверной сессии или с выдуманной — недействительна.
            Assert.False(await s.SignIn.ValidateSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, null));
            Assert.False(await s.SignIn.ValidateSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, Guid.NewGuid()));
        }
    }

    [SqlFact]
    public async Task Revoking_other_session_needs_password_and_code_and_closes_only_it()
    {
        var owner = await OwnerWithTwoFactorAsync();
        var here = await SignInAsync(owner, ChromeWindows, "203.0.113.57");
        var other = await SignInAsync(owner, FirefoxLinux, "2001:db8:abcd:12::5");

        await using (var s = As(owner, here))
        {
            var list = await s.Account.ListSessionsAsync();
            var mine = Assert.Single(list, x => x.Id == here.SessionId);
            Assert.True(mine.Current);
            var theirs = Assert.Single(list, x => x.Id == other.SessionId);
            Assert.False(theirs.Current);
            Assert.Equal("Firefox, Linux", theirs.Label);
            Assert.Equal("2001:db8:abcd::/48", theirs.AddressPrefix);
        }

        // Неверный пароль — отказ, неудачная попытка в счётчик блокировки, сессия жива.
        await using (var s = As(owner, here))
        {
            var wrong = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Account.RevokeSessionAsync(other.SessionId!.Value, "неверный пароль совсем", NextCode(owner.Key)));
            Assert.Equal("auth.confirm.failed", wrong.Code);
        }

        await using (var s = As(owner, here))
        {
            var noCode = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Account.RevokeSessionAsync(other.SessionId!.Value, Password, "000000"));
            Assert.Equal("auth.confirm.failed", noCode.Code);
            Assert.Equal(2, (await s.Db.Users.SingleAsync(u => u.Id == owner.Org.OwnerUserId)).FailedSignInCount);
            Assert.True(await ValidAsync(s, other));
        }

        // Текущую сессию завершает «Выйти», а не эта кнопка.
        await using (var s = As(owner, here))
        {
            var current = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Account.RevokeSessionAsync(here.SessionId!.Value, Password, NextCode(owner.Key)));
            Assert.Equal("auth.session.current", current.Code);
        }

        await using (var s = As(owner, here))
        {
            await s.Account.RevokeSessionAsync(other.SessionId!.Value, Password, NextCode(owner.Key));
        }

        await using (var s = As(owner, here))
        {
            Assert.False(await ValidAsync(s, other));
            Assert.True(await ValidAsync(s, here));
            // Остаются эта сессия и вход при подключении 2FA; завершённой нет.
            var left = await s.Account.ListSessionsAsync();
            Assert.Equal(here.SessionId, left[0].Id);
            Assert.DoesNotContain(left, x => x.Id == other.SessionId);
            Assert.Equal(0, (await s.Db.Users.SingleAsync(u => u.Id == owner.Org.OwnerUserId)).FailedSignInCount);

            // Повторно — «не найдено».
            await Assert.ThrowsAsync<NotFoundException>(() => s.Account.RevokeSessionAsync(other.SessionId!.Value, Password, NextCode(owner.Key)));
        }

        await using var db = host.NewDb();
        var audit = await db.AuditEntries.SingleAsync(a => a.OrganizationId == owner.Org.OrganizationId && a.Action == AuditActions.SessionRevoked);
        Assert.Equal(owner.Org.OwnerUserId, audit.ActorUserId);
        Assert.Contains("Firefox, Linux", audit.Reason);
        Assert.Contains("2001:db8:abcd::/48", audit.Reason);
    }

    [SqlFact]
    public async Task Someone_elses_session_is_not_found_and_stays_alive()
    {
        var victim = await OwnerWithTwoFactorAsync();
        var intruder = await OwnerWithTwoFactorAsync();
        var victimSession = await SignInAsync(victim, ChromeWindows, "198.51.100.7");
        var intruderSession = await SignInAsync(intruder, FirefoxLinux, "192.0.2.10");

        await using (var s = As(intruder, intruderSession))
        {
            // До проверки пароля: попытка ничего не раскрывает и не засчитывается в блокировку.
            await Assert.ThrowsAsync<NotFoundException>(() => s.Account.RevokeSessionAsync(victimSession.SessionId!.Value, Password, NextCode(intruder.Key)));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Account.RevokeSessionAsync(Guid.NewGuid(), Password, NextCode(intruder.Key)));
            Assert.DoesNotContain(await s.Account.ListSessionsAsync(), x => x.Id == victimSession.SessionId);
        }

        await using (var s = host.As(null, null))
        {
            Assert.True(await ValidAsync(s, victimSession));

            // Cookie одного пользователя с идентификатором сессии другого не принимается.
            Assert.False(await s.SignIn.ValidateSessionAsync(intruderSession.UserId, intruderSession.OrganizationId, intruderSession.SecurityStamp,
                victimSession.SessionId));
        }

        await using var db = host.NewDb();
        Assert.Equal(0, (await db.Users.SingleAsync(u => u.Id == intruder.Org.OwnerUserId)).FailedSignInCount);
        Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SessionRevoked
                                                         && (a.OrganizationId == victim.Org.OrganizationId || a.OrganizationId == intruder.Org.OrganizationId)));
    }

    [SqlFact]
    public async Task Sign_out_everywhere_and_sign_out_revoke_server_sessions()
    {
        var owner = await OwnerWithTwoFactorAsync();
        var first = await SignInAsync(owner, ChromeWindows, "203.0.113.57");
        var second = await SignInAsync(owner, FirefoxLinux, "203.0.113.58");

        // Выход завершает свою серверную сессию: скопированная cookie больше не действует.
        await using (var s = host.As(null, null))
        {
            await s.SignIn.RecordSignOutAsync(second.UserId, second.OrganizationId, second.SessionId);
            Assert.False(await ValidAsync(s, second));
            Assert.True(await ValidAsync(s, first));
        }

        var third = await SignInAsync(owner, FirefoxLinux, "203.0.113.59");
        await using (var s = As(owner, first))
        {
            await s.Account.SignOutEverywhereAsync(Password, NextCode(owner.Key), revokeApiTokens: false);
        }

        await using (var db = host.NewDb())
        {
            Assert.False(await db.UserSessions.AnyAsync(x => x.UserId == owner.Org.OwnerUserId && x.RevokedAtUtc == null));
        }

        await using (var s = As(owner, first))
        {
            Assert.False(await ValidAsync(s, first));
            Assert.False(await ValidAsync(s, third));
            Assert.Empty(await s.Account.ListSessionsAsync());
        }
    }

    [SqlFact]
    public async Task Last_seen_is_written_at_most_every_five_minutes_and_idle_session_expires()
    {
        var owner = await OwnerWithTwoFactorAsync();
        var session = await SignInAsync(owner, ChromeWindows, "203.0.113.57");
        var created = await LastSeenAsync(session);

        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(2);
        await using (var s = host.As(null, null))
        {
            Assert.True(await ValidAsync(s, session));
        }

        Assert.Equal(created, await LastSeenAsync(session));

        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(4);
        await using (var s = host.As(null, null))
        {
            Assert.True(await ValidAsync(s, session));
        }

        Assert.Equal(host.Clock.UtcNow, await LastSeenAsync(session), TimeSpan.FromMilliseconds(5));

        // Без активности дольше срока cookie сессия больше не принимается и не показывается в списке.
        host.Clock.UtcNow = host.Clock.UtcNow + SignInPolicy.SessionIdleLimit;
        await using (var s = As(owner, null))
        {
            Assert.False(await ValidAsync(s, session));
            Assert.DoesNotContain(await s.Account.ListSessionsAsync(), x => x.Id == session.SessionId);
        }
    }

    private static Task<bool> ValidAsync(Services s, SessionIdentity session) =>
        s.SignIn.ValidateSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, session.SessionId);

    private Services As(Owner owner, SessionIdentity? session) =>
        host.As(owner.Org.OwnerUserId, owner.Org.OrganizationId, sessionId: session?.SessionId);

    private async Task<DateTime> LastSeenAsync(SessionIdentity session)
    {
        await using var db = host.NewDb();
        return await db.UserSessions.Where(x => x.PublicId == session.SessionId).Select(x => x.LastSeenAtUtc).SingleAsync();
    }

    private async Task<SessionIdentity> SignInAsync(Owner owner, string userAgent, string address)
    {
        (host.UserAgent, host.ClientAddress) = (userAgent, address);
        try
        {
            await using var s = host.As(null, null);
            var pending = (await s.SignIn.PasswordSignInAsync(owner.Email, Password)).Pending!;
            var result = await s.SignIn.TwoFactorSignInAsync(pending, NextCode(owner.Key));
            Assert.Equal(SignInStatus.Succeeded, result.Status);
            return result.Session!;
        }
        finally
        {
            (host.UserAgent, host.ClientAddress) = (null, null);
        }
    }

    private string NextCode(string key)
    {
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(Totp.StepSeconds);
        return Totp.Compute(key, Totp.StepAt(host.Clock.UtcNow));
    }

    private sealed record Owner(CreatedOrganization Org, string Email, string Key);

    private async Task<Owner> OwnerWithTwoFactorAsync()
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

        return new Owner(org, email, key);
    }
}
