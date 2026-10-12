using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Приёмка ядра доступа ТЗ §4.14 на настоящем SQL Server.</summary>
public sealed class AccessCoreTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 100_000_000;

    [SqlFact]
    public async Task Organization_B_data_is_invisible_to_organization_A()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Organizations.GetByIdAsync(b.OrganizationId));
        }

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            var users = await s.Access.ListUsersAsync();
            Assert.DoesNotContain(users, u => u.UserId == b.OwnerUserId);
        }

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Access.BlockAsync(b.OwnerUserId, "чужой"));
        }

        await using (var s = host.As(a.OwnerUserId, a.OrganizationId))
        {
            var audit = await s.Audit.ListAsync();
            Assert.NotEmpty(audit);
            Assert.DoesNotContain(audit, e => e.EntityId == b.OrganizationId.ToString() && e.EntityType == "Organization");
        }
    }

    [SqlFact]
    public async Task User_without_permission_gets_403_and_denial_is_audited()
    {
        var org = await CreateOrganizationAsync();
        var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper);

        await using (var s = host.As(keeper, org.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => s.Access.ListUsersAsync());
            Assert.Equal(Permissions.UserView, ex.PermissionCode);
            Assert.StartsWith("Недостаточно прав", ex.Message);
        }

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(e =>
            e.OrganizationId == org.OrganizationId && e.ActorUserId == keeper
            && e.Action == AuditActions.AccessDenied && e.After == Permissions.UserView));

        await using var owner = host.As(org.OwnerUserId, org.OrganizationId);
        Assert.Contains(await owner.Audit.ListAsync(), e =>
            e.Action == AuditActions.AccessDenied && e.ObjectName == "Право «Пользователи и роли: просмотр»");
    }

    [SqlFact]
    public async Task Administrator_cannot_grant_owner_or_rights_above_own()
    {
        var org = await CreateOrganizationAsync();
        var admin = await InviteActiveAsync(org, SystemRoles.Administrator);

        await using (var s = host.As(admin, org.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("new-owner@test.local", "Новый владелец", SystemRoles.Owner, "тест")));
            Assert.Equal("access.admin_grant_denied", ex.Code);
        }

        await using (var s = host.As(admin, org.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("senior@test.local", "Старший кладовщик", SystemRoles.SeniorStorekeeper, null)));
            Assert.Equal("access.escalation", ex.Code);
        }

        await using (var s = host.As(admin, org.OrganizationId))
        {
            var invitation = await s.Access.InviteAsync(new InviteUserCommand("buh@test.local", "Бухгалтер", SystemRoles.Accountant, null));
            Assert.True(invitation.UserId > 0);
            Assert.NotNull(invitation.SetupToken);
        }
    }

    [SqlFact]
    public async Task Last_owner_cannot_be_demoted()
    {
        var org = await CreateOrganizationAsync();

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.ChangeRoleAsync(new ChangeRoleCommand(org.OwnerUserId, SystemRoles.Administrator, "тест")));
            Assert.Equal("access.last_owner", ex.Code);
        }

        // Со вторым владельцем понижение первого разрешено.
        var second = await InviteActiveAsync(org, SystemRoles.Owner, "Второй владелец");
        await using (var s = host.As(second, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(org.OwnerUserId, SystemRoles.Administrator, "Передача управления"));
        }
    }

    [SqlFact]
    public async Task Role_change_is_audited_with_before_and_after()
    {
        var org = await CreateOrganizationAsync();
        var user = await InviteActiveAsync(org, SystemRoles.Storekeeper);

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(user, SystemRoles.SeniorStorekeeper, "Повышение"));
        }

        await using var db = host.NewDb();
        var entry = await db.AuditEntries.Where(e => e.OrganizationId == org.OrganizationId
                && e.Action == AuditActions.RoleGranted && e.EntityId == user.ToString())
            .OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(SystemRoles.NameOf(SystemRoles.Storekeeper), entry.Before);
        Assert.Equal(SystemRoles.NameOf(SystemRoles.SeniorStorekeeper), entry.After);
        Assert.Equal("Повышение", entry.Reason);
        Assert.Equal(org.OwnerUserId, entry.ActorUserId);
    }

    /// <summary>
    /// D128 (аудит 12.10.2026): выдающий со своей областью (склад) не выдаёт право на всю организацию и на чужой склад, не создаёт
    /// роль выше своего уровня, не выдаёт особое право «Д» и не меняет свою роль; Владелец может всё.
    /// </summary>
    [SqlFact]
    public async Task Scoped_grantor_cannot_grant_outside_own_scope_or_change_own_role()
    {
        var org = await CreateOrganizationAsync();
        long w1, w2, scopedRole, reportsRole;
        string scopedCode, reportsCode;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            w1 = await s.Warehouses.CreateWarehouseAsync("Склад 1", null);
            w2 = await s.Warehouses.CreateWarehouseAsync("Склад 2", null);
            scopedRole = await s.Roles.CreateAsync("Управляющий складом", null,
                [Permissions.UserManage, Permissions.UserView, Permissions.WarehouseReportView]);
            reportsRole = await s.Roles.CreateAsync("Отчёты склада", null, [Permissions.WarehouseReportView]);
            scopedCode = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == scopedRole)).Code;
            reportsCode = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == reportsRole)).Code;
        }

        var grantor = await InviteActiveAsync(org, scopedCode, "Управляющий складом 1", warehouseId: w1);

        long colleague;
        await using (var s = host.As(grantor, org.OrganizationId))
        {
            // Своя область — склад 1: на всю организацию и на склад 2 выдать нельзя.
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("wide@test.local", "Вся организация", reportsCode, null)))).Code);
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("other@test.local", "Чужой склад", reportsCode, null, WarehouseId: w2)))).Code);
            colleague = (await s.Access.InviteAsync(new InviteUserCommand("own@test.local", "Свой склад", reportsCode, null, WarehouseId: w1))).UserId;
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            // Смена роли с расширением области — тоже отказ.
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.ChangeRoleAsync(new ChangeRoleCommand(colleague, reportsCode, null, WarehouseId: w2)))).Code);
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            // Своя роль даёт «Д» на всю организацию — выше своего «О».
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Roles.CreateAsync("Отчёты всех складов", null, [Permissions.WarehouseReportView]))).Code);
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            // Особое право — «Д» на всю организацию.
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.GrantIndividualAsync(colleague, Permissions.WarehouseReportView, false, null, "тест"))).Code);
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            // Свою роль и область не меняют даже в пределах своей области.
            Assert.Equal("access.role.self", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.ChangeRoleAsync(new ChangeRoleCommand(grantor, reportsCode, null, WarehouseId: w1)))).Code);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            // Владелец может всё: склад 2, вся организация, особое право.
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(colleague, reportsCode, null, WarehouseId: w2));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ChangeRoleAsync(new ChangeRoleCommand(colleague, reportsCode, null));
            await s.Access.GrantIndividualAsync(colleague, Permissions.WarehouseReportView, false, null, "тест");
        }

        await using var db = host.NewDb();
        Assert.Equal(1, await db.RoleAssignments.CountAsync(a => a.OrganizationId == org.OrganizationId && a.UserId == colleague
                                                              && a.RoleId == reportsRole && a.RevokedAtUtc == null && a.WarehouseId == null));
    }

    /// <summary>D128: область подразделения включает вложенные — в дочернее можно, в соседнее нельзя.</summary>
    [SqlFact]
    public async Task Department_scoped_grantor_grants_only_inside_own_subtree()
    {
        var org = await CreateOrganizationAsync();
        long shop, sewing, office;
        string grantorRole, viewRole;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            shop = await s.Structure.CreateDepartmentAsync("Цех", null);
            sewing = await s.Structure.CreateDepartmentAsync("Швейный участок", shop);
            office = await s.Structure.CreateDepartmentAsync("Офис", null);
            var g = await s.Roles.CreateAsync("Начальник цеха (кадры)", null, [Permissions.UserManage, Permissions.UserView, Permissions.DepartmentView]);
            var v = await s.Roles.CreateAsync("Структура", null, [Permissions.DepartmentView]);
            grantorRole = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == g)).Code;
            viewRole = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == v)).Code;
        }

        var grantor = await InviteActiveAsync(org, grantorRole, "Начальник цеха", departmentId: shop);
        await using (var s = host.As(grantor, org.OrganizationId))
        {
            await s.Access.InviteAsync(new InviteUserCommand("sewing@test.local", "Мастер участка", viewRole, null, DepartmentId: sewing));
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            Assert.Equal("access.escalation.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("office@test.local", "Офис", viewRole, null, DepartmentId: office)))).Code);
        }
    }

    /// <summary>D128: право «Ч» (только чтение) не даёт выдать то же право с изменением («Д») ни ролью, ни особым правом.</summary>
    [SqlFact]
    public async Task Read_only_grantor_cannot_grant_full_level()
    {
        var org = await CreateOrganizationAsync();
        string grantorRole, salesRole;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var g = await s.Roles.CreateAsync("Кадровик продаж", null, [Permissions.UserManage, Permissions.UserView, Permissions.SalesView]);
            var v = await s.Roles.CreateAsync("Продажи", null, [Permissions.SalesView]);
            grantorRole = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == g)).Code;
            salesRole = (await s.Db.Roles.AsNoTracking().SingleAsync(r => r.Id == v)).Code;
        }

        await using (var db = host.NewDb())
        {
            // Уровень «Ч» в своей роли задаётся только настройкой организации — здесь напрямую.
            var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Code == grantorRole);
            role.Toggle(Permissions.SalesView, PermissionLevel.ReadOnly, false);
            await db.SaveChangesAsync();
            role.Toggle(Permissions.SalesView, PermissionLevel.ReadOnly, true);
            await db.SaveChangesAsync();
        }

        var grantor = await InviteActiveAsync(org, grantorRole, "Только чтение");
        var target = await InviteActiveAsync(org, SystemRoles.Employee, "Сотрудник");
        await using (var s = host.As(grantor, org.OrganizationId))
        {
            Assert.Equal("access.escalation", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.InviteAsync(new InviteUserCommand("sales@test.local", "Продажи", salesRole, null)))).Code);
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            Assert.Equal("access.escalation", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Access.GrantIndividualAsync(target, Permissions.SalesView, false, null, "тест"))).Code);
        }

        await using (var s = host.As(grantor, org.OrganizationId))
        {
            Assert.Equal("access.escalation", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Roles.CreateAsync("Продажи 2", null, [Permissions.SalesView]))).Code);
            var options = await s.Access.ListGrantableRolesAsync();
            Assert.False(options.Single(o => o.Code == salesRole).Allowed);
        }
    }

    [SqlFact]
    public async Task Blocked_user_loses_access_on_next_request()
    {
        var org = await CreateOrganizationAsync();
        var accountant = await InviteActiveAsync(org, SystemRoles.Accountant);

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            await s.Organizations.GetCurrentAsync();
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.BlockAsync(accountant, "Увольнение");
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Organizations.GetCurrentAsync());
        }
    }

    [SqlFact]
    public async Task Audit_log_cannot_be_changed_by_application_or_sql()
    {
        var org = await CreateOrganizationAsync();

        await using (var db = host.NewDb())
        {
            var entry = await db.AuditEntries.FirstAsync(e => e.OrganizationId == org.OrganizationId);
            db.AuditEntries.Remove(entry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[audit_log] SET [Reason] = N'подмена' WHERE [OrganizationId] = {org.OrganizationId}"));
        }

        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM [kniterp].[audit_log] WHERE [OrganizationId] = {org.OrganizationId}"));
        }
    }

    [SqlFact]
    public async Task Stale_edit_of_requisites_is_a_conflict_not_an_overwrite()
    {
        var org = await CreateOrganizationAsync();
        OrganizationDto first;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            first = await s.Organizations.GetCurrentAsync();
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Organizations.UpdateRequisitesAsync(new UpdateRequisitesCommand("Электросталь", "Europe/Moscow", first.RowVersion));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
                s.Organizations.UpdateRequisitesAsync(new UpdateRequisitesCommand("Москва", "Europe/Moscow", first.RowVersion)));
        }
    }

    [SqlFact]
    public async Task Duplicate_inn_is_rejected_by_database()
    {
        var org = await CreateOrganizationAsync();
        string inn;
        await using (var db = host.NewDb())
        {
            inn = (await db.Organizations.SingleAsync(o => o.Id == org.OrganizationId)).Inn;
        }

        await using var s = host.As(null, null);
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Organizations.CreateWithOwnerAsync(
            new CreateOrganizationCommand("Дубль", "Дубль", inn, null, false, "Europe/Moscow", "dup@test.local", "Дубль")));
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        var inn = NextValidInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow",
            $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    /// <summary>Приглашение от имени владельца и активация без входа — для тестов прав, не самого входа.</summary>
    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode, string? name = null, long? warehouseId = null,
        long? departmentId = null)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand(
                $"{roleCode}-{Guid.NewGuid():N}@test.local", name ?? SystemRoles.NameOf(roleCode), roleCode,
                "Тест", DepartmentId: departmentId, WarehouseId: warehouseId))).UserId;
        }

        await using var db = host.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.Activate();
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
