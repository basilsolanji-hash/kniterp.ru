using KnitErp.Domain.Access;
using static KnitErp.Domain.Access.PermissionLevel;

namespace KnitErp.UnitTests;

/// <summary>Матрица 69.3 по каждой роли (приёмка §4.14 «и»).</summary>
public class RoleMatrixP0Tests
{
    [Theory]
    // Организация
    [InlineData(SystemRoles.Owner, Permissions.OrganizationEdit, Full)]
    [InlineData(SystemRoles.Administrator, Permissions.OrganizationEdit, Full)]
    [InlineData(SystemRoles.DepartmentHead, Permissions.OrganizationView, ReadOnly)]
    [InlineData(SystemRoles.Storekeeper, Permissions.OrganizationView, None)]
    // Пользователи и роли
    [InlineData(SystemRoles.Auditor, Permissions.UserView, ReadOnly)]
    [InlineData(SystemRoles.Auditor, Permissions.UserManage, None)]
    [InlineData(SystemRoles.Administrator, Permissions.AdminGrant, None)]
    [InlineData(SystemRoles.Owner, Permissions.AdminGrant, Full)]
    // Персональные данные
    [InlineData(SystemRoles.Administrator, Permissions.PersonalDataView, ByGrant)]
    [InlineData(SystemRoles.Employee, Permissions.PersonalDataView, OwnOnly)]
    // Склад
    [InlineData(SystemRoles.Storekeeper, Permissions.WarehouseDocumentCreate, Scoped)]
    [InlineData(SystemRoles.Storekeeper, Permissions.WarehouseDocumentPost, ByGrant)]
    [InlineData(SystemRoles.SeniorStorekeeper, Permissions.WarehouseDocumentPost, Scoped)]
    [InlineData(SystemRoles.Administrator, Permissions.WarehouseDocumentPost, None)]
    [InlineData(SystemRoles.Accountant, Permissions.WarehouseDocumentCreate, None)]
    // Начальные остатки: создаёт один, утверждает другой
    [InlineData(SystemRoles.SeniorStorekeeper, Permissions.OpeningBalanceCreate, Full)]
    [InlineData(SystemRoles.SeniorStorekeeper, Permissions.OpeningBalanceApprove, None)]
    [InlineData(SystemRoles.DepartmentHead, Permissions.OpeningBalanceApprove, Full)]
    // Закрытый период, цены, аудит
    [InlineData(SystemRoles.Owner, Permissions.ClosedPeriodReopen, Full)]
    [InlineData(SystemRoles.Administrator, Permissions.ClosedPeriodReopen, None)]
    [InlineData(SystemRoles.Storekeeper, Permissions.PriceView, None)]
    [InlineData(SystemRoles.Accountant, Permissions.PriceView, Full)]
    [InlineData(SystemRoles.Auditor, Permissions.AuditLogView, Full)]
    // Производство: начальник цеха видит продажи без цен (D91, дополнение)
    [InlineData(SystemRoles.DepartmentHead, Permissions.SalesView, ReadOnly)]
    [InlineData(SystemRoles.DepartmentHead, Permissions.SalesEdit, None)]
    [InlineData(SystemRoles.DepartmentHead, Permissions.PriceView, ByGrant)]
    [InlineData(SystemRoles.Employee, Permissions.AuditLogView, OwnOnly)]
    public void Matrix_matches_specification(string role, string permission, PermissionLevel expected) =>
        Assert.Equal(expected, RoleMatrixP0.LevelOf(role, permission));

    [Fact]
    public void Every_matrix_permission_is_a_known_code() =>
        Assert.All(RoleMatrixP0.PermissionCodes, code => Assert.True(Permissions.IsKnown(code), code));

    [Fact]
    public void Every_known_permission_is_in_matrix() =>
        Assert.Equal(Permissions.All.OrderBy(x => x), RoleMatrixP0.PermissionCodes.OrderBy(x => x));

    [Fact]
    public void Auditor_is_read_only_everywhere()
    {
        var writes = RoleMatrixP0.PermissionsOf(SystemRoles.Auditor)
            .Where(p => p.Level.Grants() && !p.PermissionCode.EndsWith(".view", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(writes);
    }

    [Fact]
    public void Owner_has_every_grantable_permission_except_opening_create()
    {
        var owner = EffectivePermissionSet.Compute(
            RoleMatrixP0.PermissionsOf(SystemRoles.Owner).Select(p => new PermissionGrant(p.PermissionCode, p.Level, false)));
        var missing = Permissions.All.Where(c => !owner.Has(c)).ToList();
        Assert.Equal(new[] { Permissions.OpeningBalanceCreate }, missing);
    }
}
