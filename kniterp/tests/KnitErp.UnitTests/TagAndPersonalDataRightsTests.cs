using KnitErp.Domain.Access;
using KnitErp.Domain.Integrations;
using KnitErp.Domain.Tagging;
using static KnitErp.Domain.Access.PermissionLevel;

namespace KnitErp.UnitTests;

/// <summary>D130: права на справочник тегов и на персональные данные сотрудников.</summary>
public class TagAndPersonalDataRightsTests
{
    [Theory]
    [InlineData(SystemRoles.Owner, Full)]
    [InlineData(SystemRoles.Administrator, Full)]
    [InlineData(SystemRoles.DepartmentHead, Full)]
    [InlineData(SystemRoles.SeniorStorekeeper, Full)]
    [InlineData(SystemRoles.Accountant, Full)]
    [InlineData(SystemRoles.HrOfficer, Full)]
    [InlineData(SystemRoles.SalesHead, Full)]
    [InlineData(SystemRoles.DevChief, Full)]
    [InlineData(SystemRoles.Storekeeper, None)]
    [InlineData(SystemRoles.Auditor, None)]
    [InlineData(SystemRoles.Employee, None)]
    [InlineData(SystemRoles.SalesManager, None)]
    [InlineData(SystemRoles.DevTechnologist, None)]
    public void Tag_manage_by_role(string role, PermissionLevel expected) =>
        Assert.Equal(expected, RoleMatrixP0.LevelOf(role, Permissions.TagManage));

    [Fact]
    public void Tag_manage_is_known_listed_once_and_grouped_with_catalogs()
    {
        Assert.True(Permissions.IsKnown(Permissions.TagManage));
        Assert.Equal("Теги: управление справочником", Permissions.Describe(Permissions.TagManage));
        Assert.Equal("Справочники", Permissions.GroupOf(Permissions.TagManage));
        Assert.Single(RoleMatrixP0.PermissionCodes, c => c == Permissions.TagManage);
        Assert.Equal(RoleMatrixP0.PermissionCodes.Count, RoleMatrixP0.PermissionCodes.Distinct().Count());
        Assert.False(Permissions.IsAdministrative(Permissions.TagManage));
    }

    [Fact]
    public void Personal_data_rights_keep_owner_and_hr_and_stay_out_of_api()
    {
        Assert.Equal("Персональные данные сотрудников: просмотр", Permissions.Describe(Permissions.PersonalDataView));
        foreach (var code in new[] { Permissions.PersonalDataView, Permissions.PersonalDataEdit })
        {
            Assert.Equal(Full, RoleMatrixP0.LevelOf(SystemRoles.Owner, code));
            Assert.Equal(Full, RoleMatrixP0.LevelOf(SystemRoles.HrOfficer, code));
            Assert.Equal(ByGrant, RoleMatrixP0.LevelOf(SystemRoles.Administrator, code));
            Assert.Equal(None, RoleMatrixP0.LevelOf(SystemRoles.Storekeeper, code));
            Assert.Contains(code, ApiToken.Forbidden);
        }
    }

    [Fact]
    public void Every_tagged_object_kind_names_its_edit_rights()
    {
        // Объекты модулей, подключаемые через ObjectDirectory.Register, объявляют права сами (DevDetail, DevMachine).
        TaggedEntity[] builtIn =
        [
            TaggedEntity.Item, TaggedEntity.Counterparty, TaggedEntity.SalesOrder, TaggedEntity.PurchaseOrder, TaggedEntity.StockDocument,
            TaggedEntity.StockBatch, TaggedEntity.InventoryCount, TaggedEntity.OpeningBalance, TaggedEntity.CustomerInvoice, TaggedEntity.ExpenseReport,
            TaggedEntity.ProductionOrder, TaggedEntity.Employee, TaggedEntity.DevModel, TaggedEntity.CrmLead, TaggedEntity.CrmDeal, TaggedEntity.CrmContact,
        ];
        foreach (var entity in builtIn)
        {
            var codes = KnitErp.Application.Common.ObjectDirectory.EditPermissions(entity);
            Assert.NotEmpty(codes);
            Assert.All(codes, c => Assert.True(Permissions.IsKnown(c), c));
            Assert.DoesNotContain(Permissions.TagManage, codes);
        }
    }
}
