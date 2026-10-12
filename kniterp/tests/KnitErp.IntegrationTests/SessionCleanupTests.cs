using KnitErp.Application.Authentication;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D126: старые записи сессий удаляются, действующие и недавно завершённые остаются.</summary>
public sealed class SessionCleanupTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Old_and_long_revoked_sessions_are_removed_fresh_ones_stay()
    {
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                "Тестовая организация 8730000015", "Тест 8730000015", "8730000015", null, false, "Europe/Moscow", "owner-cleanup@test.local", "Владелец"));
        }

        var now = host.Clock.UtcNow;
        long fresh, recentlyRevoked, idle, longRevoked;
        await using (var db = host.NewDb())
        {
            var stamp = await db.Users.Where(u => u.Id == org.OwnerUserId).Select(u => u.SecurityStamp).SingleAsync();
            UserSession Make(DateTime at) => UserSession.Create(org.OwnerUserId, stamp, "Mozilla/5.0 (X11; Linux x86_64) Chrome/140", "10.0.0.7", at);
            var a = Make(now.AddHours(-1));
            var b = Make(now.AddDays(-10));
            b.Revoke(now.AddDays(-10));
            var c = Make(now - SessionCleanup.Retention - TimeSpan.FromDays(1));
            var d = Make(now.AddDays(-60));
            d.Revoke(now.AddDays(-45));
            db.UserSessions.AddRange(a, b, c, d);
            await db.SaveChangesAsync();
            (fresh, recentlyRevoked, idle, longRevoked) = (a.Id, b.Id, c.Id, d.Id);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(2, await new SessionCleanup(db, host.Clock).RunAsync());
            var left = await db.UserSessions.Where(s => s.UserId == org.OwnerUserId).Select(s => s.Id).ToListAsync();
            Assert.Contains(fresh, left);
            Assert.Contains(recentlyRevoked, left);
            Assert.DoesNotContain(idle, left);
            Assert.DoesNotContain(longRevoked, left);
            Assert.Equal(0, await new SessionCleanup(db, host.Clock).RunAsync());
        }
    }
}
