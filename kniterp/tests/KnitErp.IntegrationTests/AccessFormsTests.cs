using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Серверная часть форм выдачи доступа: предпросмотр «Было → Станет», доступные роли, меню.</summary>
public sealed class AccessFormsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 300_000_000;

    [SqlFact]
    public async Task Preview_shows_current_and_future_rights_and_keeps_individual_deny()
    {
        var org = await CreateOrganizationAsync();
        var keeper = await InviteActiveAsync(org, SystemRoles.SeniorStorekeeper);

        // Индивидуальный запрет переживает смену роли — предпросмотр должен это показать.
        await using (var db = host.NewDb())
        {
            db.RoleAssignments.Add(RoleAssignment.ForPermission(org.OrganizationId, keeper, Permissions.WarehouseReportView,
                isDeny: true, org.OwnerUserId, host.Clock.UtcNow, "Тест запрета"));
            await db.SaveChangesAsync();
        }

        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var preview = await s.Access.PreviewRoleAsync(keeper, SystemRoles.Accountant);

        Assert.Contains(preview.Before, l => l.PermissionCode == Permissions.OpeningBalanceCreate);
        Assert.DoesNotContain(preview.After, l => l.PermissionCode == Permissions.OpeningBalanceCreate);
        Assert.Contains(preview.After, l => l.PermissionCode == Permissions.PriceView && l.Level == PermissionLevel.Full);
        Assert.DoesNotContain(preview.After, l => l.PermissionCode == Permissions.WarehouseReportView);
        Assert.Contains(preview.After, l => l.PermissionCode == Permissions.OrganizationView && l.Text.EndsWith("только чтение"));
        Assert.False(preview.ReasonRequired);
        Assert.Equal("Вся организация", preview.ScopeText);
    }

    [SqlFact]
    public async Task Preview_for_new_user_starts_from_no_access()
    {
        var org = await CreateOrganizationAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);

        var preview = await s.Access.PreviewRoleAsync(null, SystemRoles.Administrator);
        Assert.Empty(preview.Before);
        Assert.Contains(preview.After, l => l.PermissionCode == Permissions.UserManage);
        Assert.True(preview.ReasonRequired);

        var employee = await s.Access.PreviewRoleAsync(null, SystemRoles.Employee);
        Assert.Contains(employee.After, l => l.PermissionCode == Permissions.AuditLogView && l.Level == PermissionLevel.OwnOnly);
    }

    [SqlFact]
    public async Task Preview_of_foreign_user_is_not_found_and_requires_manage_right()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        var auditor = await InviteActiveAsync(a, SystemRoles.Auditor);

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Access.PreviewRoleAsync(b.OwnerUserId, SystemRoles.Employee));
        }

        await using (var s = host.As(auditor, a.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => s.Access.PreviewRoleAsync(null, SystemRoles.Employee));
            Assert.Equal(Permissions.UserManage, ex.PermissionCode);
        }
    }

    [SqlFact]
    public async Task Administrator_sees_which_roles_he_cannot_grant()
    {
        var org = await CreateOrganizationAsync();
        var admin = await InviteActiveAsync(org, SystemRoles.Administrator);

        await using var s = host.As(admin, org.OrganizationId);
        var roles = await s.Access.ListGrantableRolesAsync();

        Assert.Equal(SystemRoles.Ordered, roles.Select(r => r.Code));
        Assert.False(roles.Single(r => r.Code == SystemRoles.Owner).Allowed);
        Assert.False(roles.Single(r => r.Code == SystemRoles.Administrator).Allowed);
        Assert.False(roles.Single(r => r.Code == SystemRoles.SeniorStorekeeper).Allowed);
        Assert.True(roles.Single(r => r.Code == SystemRoles.Accountant).Allowed);
        Assert.NotNull(roles.Single(r => r.Code == SystemRoles.Owner).DeniedReason);

        // Отключённый пункт — не защита: сервер всё равно отказывает.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Access.InviteAsync(new InviteUserCommand("bypass@test.local", "Обход", SystemRoles.Owner, "тест")));
        Assert.Equal("access.admin_grant_denied", ex.Code);
    }

    [SqlFact]
    public async Task Current_access_matches_server_rights()
    {
        var org = await CreateOrganizationAsync();
        var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper);
        var employee = await InviteActiveAsync(org, SystemRoles.Employee);

        await using (var s = host.As(keeper, org.OrganizationId))
        {
            var access = await s.Access.GetCurrentAccessAsync();
            Assert.Equal([SystemRoles.Storekeeper], access.RoleCodes);
            Assert.False(access.Has(Permissions.UserView));
            Assert.False(access.Has(Permissions.OrganizationView));
            Assert.Equal("Europe/Moscow", access.TimeZoneId);
        }

        await using (var s = host.As(employee, org.OrganizationId))
        {
            var access = await s.Access.GetCurrentAccessAsync();
            Assert.False(access.Has(Permissions.AuditLogView));
            Assert.True(access.HasOwnOnly(Permissions.AuditLogView));
        }
    }

    [SqlFact]
    public async Task User_list_shows_invitation_status_and_current_user()
    {
        var org = await CreateOrganizationAsync();
        InvitationResult invited;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invited = await s.Access.InviteAsync(new InviteUserCommand($"u-{Guid.NewGuid():N}@test.local", "Приглашённый", SystemRoles.Accountant, null));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var users = await s.Access.ListUsersAsync();
            var row = users.Single(u => u.UserId == invited.UserId);
            Assert.False(row.HasPassword);
            Assert.False(row.IsCurrentUser);
            Assert.Equal("Вся организация", row.ScopeText);
            Assert.True(users.Single(u => u.UserId == org.OwnerUserId).IsCurrentUser);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var audit = await s.Audit.ListAsync();
            Assert.Contains(audit, e => e.ObjectName == "Пользователь «Приглашённый»");
            Assert.Contains(audit, e => e.ObjectName.StartsWith("Организация «Тест "));
        }
    }

    [SqlFact]
    public async Task Account_wide_actions_stay_inside_own_organization_and_admins_are_managed_by_owner()
    {
        // Аудит 11.10.2026 (D109): пароль, 2FA и ссылка входа — общие для всех организаций пользователя.
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        var shared = await InviteActiveAsync(a, SystemRoles.Storekeeper);
        var email = await host.NewDb().Users.Where(u => u.Id == shared).Select(u => u.Email).SingleAsync();
        long pendingId;
        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            pendingId = (await s.Access.InviteAsync(new InviteUserCommand($"pending-{Guid.NewGuid():N}@test.local", "Ждёт входа", SystemRoles.Storekeeper, null))).UserId;
        }

        var pendingEmail = await host.NewDb().Users.Where(u => u.Id == pendingId).Select(u => u.Email).SingleAsync();
        await using (var s = host.As(b.OwnerUserId, b.OrganizationId))
        {
            await s.Access.InviteAsync(new InviteUserCommand(email, "Общий", SystemRoles.Storekeeper, null));
            // Учётная запись, приглашённая другой организацией и ещё без пароля: новая ссылка отсюда не выдаётся.
            var again = await s.Access.InviteAsync(new InviteUserCommand(pendingEmail, "Ждёт входа", SystemRoles.Storekeeper, null));
            Assert.Null(again.SetupToken);
            Assert.Equal("access.account.shared", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.ResetTwoFactorAsync(shared, "Потерял телефон"))).Code);
            Assert.Equal("access.account.shared", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.IssuePasswordResetAsync(shared, "Забыл пароль"))).Code);
            Assert.Equal("access.account.shared", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.ReissueInvitationAsync(pendingId))).Code);
        }

        // Администратор не перевыпускает ссылку приглашённому Администратору и не разблокирует Владельца или Администратора.
        var admin = await InviteActiveAsync(a, SystemRoles.Administrator);
        long pendingAdmin;
        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            pendingAdmin = (await s.Access.InviteAsync(new InviteUserCommand($"admin2-{Guid.NewGuid():N}@test.local", "Второй админ", SystemRoles.Administrator, "Тест"))).UserId;
            await s.Access.BlockAsync(pendingAdmin, "Тест");
        }

        await using (var s = host.As(admin, a.OrganizationId))
        {
            Assert.Equal("access.admin_grant_denied", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.ReissueInvitationAsync(pendingAdmin))).Code);
            Assert.Equal("access.admin_grant_denied", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.UnblockAsync(pendingAdmin, "Тест"))).Code);
        }

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            await s.Access.UnblockAsync(pendingAdmin, "Тест");
            Assert.NotNull((await s.Access.ReissueInvitationAsync(pendingAdmin)).SetupToken);
        }
    }

    [SqlFact]
    public async Task Owner_builds_custom_roles_and_individual_rights_that_act_at_once()
    {
        // D112: своя роль, особые права и запреты — без права выше своего и с административными действиями только у Владельца.
        var org = await CreateOrganizationAsync();
        var admin = await InviteActiveAsync(org, SystemRoles.Administrator);
        long master;
        string code;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            master = await s.Roles.CreateAsync("Мастер цеха", "Смена А", [Permissions.WarehouseReportView, Permissions.CatalogView]);
            Assert.Equal("access.role.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Roles.CreateAsync("Мастер цеха", null, [Permissions.CatalogView]))).Code);
            code = (await s.Roles.ListAsync()).Single(r => r.Id == master).Code;
            Assert.Contains(await s.Access.ListGrantableRolesAsync(), r => r.Code == code && r.Allowed);
            var copy = await s.Roles.CopyAsync((await s.Roles.ListAsync()).Single(r => r.Code == SystemRoles.Storekeeper).Id);
            Assert.Equal("Кладовщик (копия)", (await s.Roles.ListAsync()).Single(r => r.Id == copy).Name);
        }

        long worker;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            worker = (await s.Access.InviteAsync(new InviteUserCommand($"master-{Guid.NewGuid():N}@test.local", "Мастер", code, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == worker)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(worker, org.OrganizationId))
        {
            Assert.True((await s.Access.GetCurrentAccessAsync()).Has(Permissions.CatalogView));
        }

        // Правка роли действует сразу; архив — только когда роль никто не носит.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var card = (await s.Roles.ListAsync()).Single(r => r.Id == master);
            Assert.Equal(1, card.Users);
            await s.Roles.UpdateAsync(master, "Мастер цеха", null, [Permissions.WarehouseReportView], card.RowVersion);
            card = (await s.Roles.ListAsync()).Single(r => r.Id == master);
            Assert.Equal("access.role.in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Roles.ArchiveAsync(master, card.RowVersion))).Code);
        }

        await using (var s = host.As(worker, org.OrganizationId))
        {
            var access = await s.Access.GetCurrentAccessAsync();
            Assert.False(access.Has(Permissions.CatalogView));
            Assert.True(access.Has(Permissions.WarehouseReportView));
        }

        // Администратор: не выше своего, не административная роль, не особые права Администратору.
        await using (var s = host.As(admin, org.OrganizationId))
        {
            Assert.Equal("access.admin_grant_denied", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Roles.CreateAsync("Кадры", null, [Permissions.UserManage]))).Code);
            Assert.Equal("access.individual.self", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.GrantIndividualAsync(admin, Permissions.PriceView, false, null, null))).Code);
            await s.Access.GrantIndividualAsync(worker, Permissions.CatalogView, false, new DateOnly(2026, 12, 31), "На время инвентаризации");
            Assert.Equal("access.individual.exists",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.GrantIndividualAsync(worker, Permissions.CatalogView, false, null, null))).Code);
            Assert.Equal("access.reason.required",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.GrantIndividualAsync(worker, Permissions.WarehouseReportView, true, null, null))).Code);
            await s.Access.GrantIndividualAsync(worker, Permissions.WarehouseReportView, true, null, "Только справочники");
            Assert.Equal("access.admin_grant_denied",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.GrantIndividualAsync(org.OwnerUserId, Permissions.PriceView, false, null, null))).Code);
            var rights = await s.Access.UserRightsAsync(worker);
            var catalog = rights.Rows.Single(r => r.Code == Permissions.CatalogView);
            Assert.True(catalog is { Effective: true, Granted: true, FromRoles: null });
            var report = rights.Rows.Single(r => r.Code == Permissions.WarehouseReportView);
            Assert.True(report is { Effective: false, Denied: true, FromRoles: "Мастер цеха" });
            Assert.Equal(2, rights.Individual.Count);
        }

        await using (var s = host.As(worker, org.OrganizationId))
        {
            var access = await s.Access.GetCurrentAccessAsync();
            Assert.True(access.Has(Permissions.CatalogView));
            Assert.False(access.Has(Permissions.WarehouseReportView));
        }

        // Владельца запретом не ограничить; отзыв запрета возвращает право.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Access.GrantIndividualAsync(org.OwnerUserId, Permissions.SalesView, true, null, "Тест"));
            var deny = (await s.Access.UserRightsAsync(worker)).Individual.Single(i => i.Deny);
            await s.Access.RevokeIndividualAsync(deny.Id, "Ошибся");
            Assert.True((await s.Access.UserRightsAsync(worker)).Rows.Single(r => r.Code == Permissions.WarehouseReportView).Effective);
            var actions = await s.Db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId).Select(a => a.Action).ToListAsync();
            Assert.Contains(KnitErp.Domain.Audit.AuditActions.RoleCreated, actions);
            Assert.Contains(KnitErp.Domain.Audit.AuditActions.RoleChanged, actions);
            Assert.Contains(KnitErp.Domain.Audit.AuditActions.PermissionDenied, actions);
            Assert.Contains(KnitErp.Domain.Audit.AuditActions.AssignmentRevoked, actions);
        }

        // Другая организация — роли и права не видны.
        var other = await CreateOrganizationAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Roles.UpdateAsync(master, "Х", null, [Permissions.CatalogView], [0]));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Access.UserRightsAsync(worker));
        }
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        var inn = NextValidInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow",
            $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, SystemRoles.IsAdministrative(roleCode) ? "Тест" : null))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

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
