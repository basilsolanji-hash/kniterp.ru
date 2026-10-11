using KnitErp.Domain.Access;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D112: своя роль — название, права, административность; группы прав.</summary>
public sealed class CustomRoleTests
{
    [Fact]
    public void Custom_role_validates_and_marks_user_management_as_administrative()
    {
        var role = Role.CreateCustom(1, " Мастер цеха ", null, [Permissions.WarehouseReportView, Permissions.CatalogView, Permissions.CatalogView]);
        Assert.StartsWith(Role.CustomPrefix, role.Code);
        Assert.Equal(("Мастер цеха", 2, false), (role.Name, role.Permissions.Count, role.IsAdministrative));
        Assert.All(role.Permissions, p => Assert.Equal(PermissionLevel.Full, p.Level));

        role.Update("Мастер цеха", "Смена А", [Permissions.WarehouseReportView, Permissions.UserManage]);
        Assert.True(role.IsAdministrative);
        Assert.DoesNotContain(role.Permissions, p => p.PermissionCode == Permissions.CatalogView);

        Assert.Equal("access.role.admin_grant", Assert.Throws<BusinessRuleException>(() => Role.CreateCustom(1, "Почти владелец", null, [Permissions.AdminGrant])).Code);
        Assert.Equal("access.role.empty", Assert.Throws<BusinessRuleException>(() => Role.CreateCustom(1, "Пустая", null, [])).Code);
        Assert.Equal("access.permission.unknown", Assert.Throws<BusinessRuleException>(() => Role.CreateCustom(1, "Х", null, ["hack.all"])).Code);
        Assert.Throws<BusinessRuleException>(() => Role.CreateCustom(1, " ", null, [Permissions.CatalogView]));
        Assert.Equal("access.role.system", Assert.Throws<BusinessRuleException>(() => Role.CreateSystem(1, SystemRoles.Storekeeper).Update("Х", null, [Permissions.CatalogView])).Code);

        role.Archive();
        Assert.Equal("access.role.archived", Assert.Throws<BusinessRuleException>(() => role.Update("Х", null, [Permissions.CatalogView])).Code);
    }

    [Theory]
    [InlineData(Permissions.SalesEdit, "Продажи")]
    [InlineData(Permissions.ClosedPeriodReopen, "Склад")]
    [InlineData(Permissions.PriceView, "Деньги и цены")]
    [InlineData(Permissions.PersonalDataView, "Организация и сотрудники")]
    [InlineData("production.order.view", "Производство")]
    [InlineData("other.thing", "Прочее")]
    public void Permission_groups_follow_code_prefix(string code, string group) => Assert.Equal(group, Permissions.GroupOf(code));
}
