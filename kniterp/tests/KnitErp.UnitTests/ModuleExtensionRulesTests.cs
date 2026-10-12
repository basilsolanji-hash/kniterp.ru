using KnitErp.Application.Workspace;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;

namespace KnitErp.UnitTests;

/// <summary>D117: блок модулей на главном экране и связь заказа со сделкой CRM.</summary>
public sealed class ModuleExtensionRulesTests
{
    [Fact]
    public void Saved_layout_without_module_block_gets_it_after_production()
    {
        var saved = new DashboardLayout(
            [DashboardBlocks.Charts, DashboardBlocks.Actions, DashboardBlocks.Kpi, DashboardBlocks.Money, DashboardBlocks.Production,
                DashboardBlocks.Attention, DashboardBlocks.Debtors, DashboardBlocks.Recent, DashboardBlocks.Month, DashboardBlocks.Reference], []);
        var order = DashboardBlocks.Clean(saved).Order.ToList();
        Assert.Equal(order.IndexOf(DashboardBlocks.Production) + 1, order.IndexOf(DashboardBlocks.Modules));
        Assert.Contains(DashboardBlocks.Modules, DashboardBlocks.DefaultOrder);
    }

    [Fact]
    public void Crm_deal_link_accepts_positive_id_or_null()
    {
        var order = (SalesOrder)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SalesOrder));
        order.LinkCrmDeal(15);
        Assert.Equal(15, order.CrmDealId);
        order.LinkCrmDeal(null);
        Assert.Null(order.CrmDealId);
        Assert.Throws<BusinessRuleException>(() => order.LinkCrmDeal(0));
    }
}
