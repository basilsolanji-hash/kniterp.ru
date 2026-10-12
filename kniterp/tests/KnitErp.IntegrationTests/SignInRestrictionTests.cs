using KnitErp.Application.Access;
using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D127: ограничения входа пользователя в организации — одно устройство (прежняя сессия закрывается через 30 с), разрешённые
/// сети (IP), время работы в часовом поясе организации; сохранение — администратор с повторным подтверждением, не себе и не Владельцу.
/// Данные вымышленные, адреса — из документационных диапазонов (RFC 5737, RFC 3849).
/// </summary>
public sealed class SignInRestrictionTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "длинная парольная фраза";
    private const string Factory = "203.0.113.57";
    private static int _innSeed = 861_400_000;

    // 12.10.2026, понедельник, 10:00 по Москве.
    private static readonly DateTime MondayTenMoscow = new(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);

    [SqlFact]
    public async Task Single_device_second_sign_in_closes_first_after_30_seconds()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        await SaveAsync(org, worker.Id, singleDevice: true);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.True((await s.Access.ListUsersAsync()).Single(u => u.UserId == worker.Id).HasSignInRestrictions);
            Assert.False((await s.Access.ListUsersAsync()).Single(u => u.UserId == org.OwnerUserId).HasSignInRestrictions);
        }

        var first = await SignInAsync(worker, Factory);
        Assert.True((await CheckAsync(first, Factory)).Valid);

        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(5);
        var signedSecondAt = host.Clock.UtcNow;
        var second = await SignInAsync(worker, "198.51.100.20");

        // Новый вход разрешён; прежняя сессия получила момент закрытия через 30 с — сервер хранит его в записи сессии.
        await using (var db = host.NewDb())
        {
            var row = await db.UserSessions.SingleAsync(x => x.PublicId == first.SessionId);
            Assert.Equal(signedSecondAt.AddSeconds(30), row.EndsAtUtc!.Value, TimeSpan.FromMilliseconds(5));
            Assert.Equal(SessionEndReason.OtherDevice, row.EndReason);
            Assert.Null((await db.UserSessions.SingleAsync(x => x.PublicId == second.SessionId)).EndsAtUtc);
            var audit = await db.AuditEntries.SingleAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SessionEnding);
            Assert.Equal(worker.Id.ToString(), audit.EntityId);
            Assert.Contains("другого устройства", audit.Reason);
        }

        // До момента закрытия старая сессия действует и знает, когда закроется (баннер с отсчётом), — и быстрая, и полная проверка.
        host.Clock.UtcNow = signedSecondAt.AddSeconds(29);
        var quick = await CheckAsync(first, Factory, full: false);
        Assert.True(quick.Valid);
        Assert.Equal(SessionEndReason.OtherDevice, quick.EndReason);
        Assert.NotNull(quick.EndsAtUtc);
        Assert.True((await CheckAsync(first, Factory)).Valid);

        // Через 30 с старая отклоняется (cookie, вкладка, HTTP), новая работает.
        host.Clock.UtcNow = signedSecondAt.AddSeconds(30);
        var closed = await CheckAsync(first, Factory, full: false);
        Assert.False(closed.Valid);
        Assert.Equal(SessionClosure.OtherDevice, closed.Closure);
        Assert.False((await CheckAsync(first, Factory)).Valid);
        await using (var s = host.As(null, null))
        {
            Assert.False(await s.SignIn.ValidateSessionAsync(first.UserId, first.OrganizationId, first.SecurityStamp, first.SessionId));
        }

        var alive = await CheckAsync(second, "198.51.100.20");
        Assert.True(alive.Valid);
        Assert.Null(alive.EndsAtUtc);

        await using (var db = host.NewDb())
        {
            Assert.NotNull((await db.UserSessions.SingleAsync(x => x.PublicId == first.SessionId)).RevokedAtUtc);
        }
    }

    [SqlFact]
    public async Task Without_single_device_both_sessions_keep_working()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        await SaveAsync(org, worker.Id, singleDevice: false, networks: Factory + "\n198.51.100.0/24");

        var first = await SignInAsync(worker, Factory);
        var second = await SignInAsync(worker, "198.51.100.20");
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(2);
        Assert.True((await CheckAsync(first, Factory)).Valid);
        Assert.True((await CheckAsync(second, "198.51.100.20")).Valid);

        await using var db = host.NewDb();
        Assert.False(await db.UserSessions.AnyAsync(x => x.UserId == worker.Id && x.EndsAtUtc != null));
    }

    [SqlFact]
    public async Task Address_outside_allowed_networks_is_refused_and_audited_and_closes_open_session()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        await SaveAsync(org, worker.Id, networks: "203.0.113.0/24\n2001:db8:abcd::/48");

        await using (var s = Anonymous("192.0.2.44"))
        {
            Assert.Equal(SignInStatus.NetworkDenied, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
        }

        // Адрес неизвестен — при ограничении по сетям вход тоже не пускают.
        await using (var s = Anonymous(null))
        {
            Assert.Equal(SignInStatus.NetworkDenied, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
        }

        await using (var db = host.NewDb())
        {
            var refused = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SignInRestricted)
                .ToListAsync();
            Assert.Equal(2, refused.Count);
            Assert.Contains(refused, a => a.Reason!.Contains("адрес вне разрешённых сетей") && a.Reason.Contains("192.0.2.44"));
            // Отказ по ограничению — не неудачная попытка пароля: блокировка входа не копится.
            Assert.Equal(0, (await db.Users.SingleAsync(u => u.Id == worker.Id)).FailedSignInCount);
        }

        var v4 = await SignInAsync(worker, Factory);
        var v6 = await SignInAsync(worker, "2001:db8:abcd:12::5");

        // Вкладка Blazor может не знать адреса — сессию это не закрывает; следующий HTTP-запрос с чужого адреса закрывает сразу.
        Assert.True((await CheckAsync(v4, null)).Valid);
        var moved = await CheckAsync(v4, "192.0.2.44");
        Assert.False(moved.Valid);
        Assert.Equal(SessionClosure.Network, moved.Closure);
        Assert.False((await CheckAsync(v4, Factory)).Valid);
        Assert.True((await CheckAsync(v6, "2001:db8:abcd:12::5")).Valid);

        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SignInRestricted
                                                                   && a.After == "сессия закрыта"));
        }
    }

    [SqlFact]
    public async Task Sign_in_outside_work_time_is_refused_and_session_closes_at_window_end()
    {
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        // Пн–пт 09:00–18:00 по Москве (часовой пояс организации).
        await SaveAsync(org, worker.Id, schedule: (0b0011111, 9 * 60, 18 * 60));

        host.Clock.UtcNow = new DateTime(2026, 10, 12, 5, 30, 0, DateTimeKind.Utc); // пн 08:30 МСК
        await using (var s = Anonymous(Factory))
        {
            Assert.Equal(SignInStatus.ScheduleDenied, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
        }

        host.Clock.UtcNow = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc); // сб 11:00 МСК
        await using (var s = Anonymous(Factory))
        {
            Assert.Equal(SignInStatus.ScheduleDenied, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SignInRestricted
                                                                  && a.Reason!.Contains("вне разрешённого времени работы")));
        }

        host.Clock.UtcNow = new DateTime(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc); // пн 17:00 МСК
        var session = await SignInAsync(worker, Factory);
        Assert.Null((await CheckAsync(session, Factory)).EndsAtUtc);

        // За минуту до конца окна полная проверка назначает закрытие ровно в 18:00 (предупреждение не меньше 30 с).
        host.Clock.UtcNow = new DateTime(2026, 10, 12, 14, 59, 0, DateTimeKind.Utc);
        var ending = await CheckAsync(session, Factory);
        Assert.True(ending.Valid);
        Assert.Equal(SessionEndReason.Schedule, ending.EndReason);
        Assert.Equal(new DateTime(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc), ending.EndsAtUtc);

        host.Clock.UtcNow = new DateTime(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc);
        var closed = await CheckAsync(session, Factory, full: false);
        Assert.False(closed.Valid);
        Assert.Equal(SessionClosure.Schedule, closed.Closure);
    }

    [SqlFact]
    public async Task Night_shift_window_crosses_midnight()
    {
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        await SaveAsync(org, worker.Id, schedule: (0b0011111, 22 * 60, 6 * 60)); // пн–пт 22:00–06:00

        host.Clock.UtcNow = new DateTime(2026, 10, 13, 0, 30, 0, DateTimeKind.Utc); // вт 03:30 МСК — смена понедельника
        var session = await SignInAsync(worker, Factory);
        Assert.True((await CheckAsync(session, Factory)).Valid);

        host.Clock.UtcNow = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc); // вт 12:00 МСК
        await using var s = Anonymous(Factory);
        Assert.Equal(SignInStatus.ScheduleDenied, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
    }

    [SqlFact]
    public async Task Cannot_restrict_self_or_owner_and_administrator_only_by_owner()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var admin = await WorkerAsync(org, SystemRoles.Administrator);
        var otherAdmin = await WorkerAsync(org, SystemRoles.Administrator);
        var worker = await WorkerAsync(org);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var self = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(org.OwnerUserId, singleDevice: true)));
            Assert.Equal("access.restriction.self", self.Code);
            Assert.False((await s.Restrictions.GetAsync(org.OwnerUserId)).CanEdit);
        }

        await using (var s = host.As(admin.Id, org.OrganizationId))
        {
            Assert.Equal("access.restriction.owner",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(org.OwnerUserId, singleDevice: true)))).Code);
            Assert.Equal("access.restriction.self",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(admin.Id, singleDevice: true)))).Code);
            Assert.Equal("access.restriction.admin",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(otherAdmin.Id, singleDevice: true)))).Code);
            await s.Restrictions.SaveAsync(Command(worker.Id, singleDevice: true));
        }

        // Неверная сеть — понятный отказ, ничего не сохраняется.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("access.restriction.network_host_bits",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(admin.Id, networks: "192.168.1.10/24")))).Code);
        }

        // Владелец ограничивает Администратора.
        await SaveAsync(org, admin.Id, networks: Factory);

        // Чужая организация — «не найдено».
        var stranger = await OrganizationAsync();
        await using (var s = host.As(stranger.OwnerUserId, stranger.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Restrictions.GetAsync(worker.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Restrictions.SaveAsync(Command(worker.Id, singleDevice: false)));
        }

        // Без права управления пользователями — отказ.
        await using (var s = host.As(worker.Id, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Restrictions.SaveAsync(Command(admin.Id, singleDevice: false)));
        }

        await using var db = host.NewDb();
        var changes = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SignInRestrictionsChanged)
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.Equal(("нет", "одно устройство"), (changes[0].Before, changes[0].After));
        Assert.Equal(admin.Id.ToString(), changes[1].EntityId);
        Assert.Equal($"сети: {Factory}/32", changes[1].After);
    }

    [SqlFact]
    public async Task Saving_requires_step_up_and_restriction_on_owner_is_ignored()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var worker = await WorkerAsync(org);
        // Подтверждение, выданное помощниками теста, истекает через 10 минут.
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(11);
        host.AutoStepUp = false;
        try
        {
            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                var required = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Restrictions.SaveAsync(Command(worker.Id, singleDevice: true)));
                Assert.Equal(StepUp.RequiredCode, required.Code);
                Assert.False((await s.Restrictions.GetAsync(worker.Id)).SingleDevice);
            }

            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                await s.StepUp.ConfirmAsync(Password, null);
            }

            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                await s.Restrictions.SaveAsync(Command(worker.Id, singleDevice: true));
                Assert.True((await s.Restrictions.GetAsync(worker.Id)).SingleDevice);
            }
        }
        finally
        {
            host.AutoStepUp = true;
        }

        // Ограничение, оставшееся у пользователя, которого позже сделали Владельцем, не действует: организация не останется без входа.
        await SaveAsync(org, worker.Id, networks: "198.51.100.0/24");
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(worker.Id, SystemRoles.Owner, "передача"));
        }

        await using (var s = Anonymous("192.0.2.1"))
        {
            // Владелец входит с кодом 2FA, которого нет, — значит пароль принят и сети не проверялись.
            Assert.Equal(SignInStatus.TwoFactorSetupRequired, (await s.SignIn.PasswordSignInAsync(worker.Email, Password)).Status);
        }
    }

    [SqlFact]
    public async Task Emergency_access_lifts_restrictions()
    {
        host.Clock.UtcNow = MondayTenMoscow;
        var org = await OrganizationAsync();
        var admin = await WorkerAsync(org, SystemRoles.Administrator);
        await SaveAsync(org, admin.Id, singleDevice: true, networks: "198.51.100.0/24");

        await using (var s = host.As(null, null))
        {
            await s.Recovery.EmergencyRestoreAsync(admin.Email, "администратор ограничен по ошибке");
        }

        await using var db = host.NewDb();
        var r = await db.SignInRestrictions.SingleAsync(x => x.OrganizationId == org.OrganizationId);
        Assert.True(r.IsEmpty);
        Assert.Null(r.UpdatedByUserId);
        Assert.Contains("ограничения входа сняты",
            (await db.AuditEntries.SingleAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.EmergencyAccess)).After);
    }

    private sealed record Worker(long Id, string Email);

    private Services Anonymous(string? address)
    {
        host.ClientAddress = address;
        try
        {
            return host.As(null, null);
        }
        finally
        {
            host.ClientAddress = null;
        }
    }

    private async Task<SessionIdentity> SignInAsync(Worker worker, string address)
    {
        await using var s = Anonymous(address);
        var result = await s.SignIn.PasswordSignInAsync(worker.Email, Password);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        return result.Session!;
    }

    private async Task<SessionCheck> CheckAsync(SessionIdentity session, string? address, bool full = true)
    {
        await using var s = host.As(null, null);
        return await s.SignIn.CheckSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, session.SessionId, address, full);
    }

    private static SaveSignInRestrictionCommand Command(long userId, bool singleDevice = false, string? networks = null,
        (byte Days, int From, int To)? schedule = null, byte[]? rowVersion = null) =>
        new(userId, singleDevice, networks, schedule is not null, schedule?.Days ?? 0, schedule?.From ?? 0, schedule?.To ?? 0, "тест", rowVersion);

    private async Task SaveAsync(CreatedOrganization org, long userId, bool singleDevice = false, string? networks = null,
        (byte Days, int From, int To)? schedule = null)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var current = await s.Restrictions.GetAsync(userId);
        await s.Restrictions.SaveAsync(Command(userId, singleDevice, networks, schedule, current.RowVersion));
    }

    private async Task<Worker> WorkerAsync(CreatedOrganization org, string roleCode = SystemRoles.Employee)
    {
        var email = $"{roleCode}-{Guid.NewGuid():N}@test.local";
        InvitationResult invitation;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new InviteUserCommand(email, SystemRoles.NameOf(roleCode), roleCode,
                roleCode == SystemRoles.Employee ? null : "тест"));
        }

        await using (var s = host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password);
        }

        return new Worker(invitation.UserId, email);
    }

    private async Task<CreatedOrganization> OrganizationAsync()
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
        var org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        return org;
    }
}
