using KnitErp.Domain.Common;
using KnitErp.Domain.Production;

namespace KnitErp.UnitTests;

/// <summary>Производственный заказ (D90): сочетания шапки, запуск со снимком норм, отмена и плановая себестоимость.</summary>
public sealed class ProductionOrderTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private static ProductionOrderHeader Header(
        ProductionSource source = ProductionSource.Plan, long? sales = null, long wip = 10, long output = 20, DateOnly? due = null) =>
        new(Today, due, source, sales, wip, output, null);

    private static ProductionOrder Draft(decimal quantity = 25) => ProductionOrder.Create(1, "ПЗ-1", 100, quantity, 7, Header(), 5, Now);

    /// <summary>Свитер: на 10 шт — 3,2 кг пряжи (3% отхода) и 50 пуговиц.</summary>
    private static TechCard ActiveCard()
    {
        var card = TechCard.Create(1, 100, 1, 10, null, 5, Now);
        card.SetLine(200, 3.2m, 3m);
        card.SetLine(300, 50m, 0m);
        card.Activate(5, Now);
        return card;
    }

    [Fact]
    public void Header_combinations_are_checked()
    {
        Assert.Equal("production.order.same_warehouse",
            Assert.Throws<BusinessRuleException>(() => ProductionOrder.Create(1, "ПЗ-1", 100, 1, 7, Header(wip: 10, output: 10), 5, Now)).Code);
        Assert.Equal("production.order.sales_order_required",
            Assert.Throws<BusinessRuleException>(() => ProductionOrder.Create(1, "ПЗ-1", 100, 1, 7, Header(ProductionSource.SalesOrder), 5, Now)).Code);
        Assert.Equal("production.order.sales_order_not_allowed",
            Assert.Throws<BusinessRuleException>(() => ProductionOrder.Create(1, "ПЗ-1", 100, 1, 7, Header(sales: 3), 5, Now)).Code);
        Assert.Equal("production.order.due_date",
            Assert.Throws<BusinessRuleException>(() => ProductionOrder.Create(1, "ПЗ-1", 100, 1, 7, Header(due: Today.AddDays(-1)), 5, Now)).Code);
        Assert.Throws<BusinessRuleException>(() => ProductionOrder.Create(1, "ПЗ-1", 100, 0, 7, Header(), 5, Now));
        Assert.Equal(ProductionOrderStatus.Draft, ProductionOrder.Create(1, "ПЗ-1", 100, 5, 7, Header(ProductionSource.SalesOrder, 3), 5, Now).Status);
    }

    [Fact]
    public void Release_fixes_card_and_snapshots_requirement_with_prices()
    {
        var order = Draft(25);
        order.Release(ActiveCard(), _ => 3, id => id == 200 ? 1234.56789m : 2.5m, 6, Now);

        Assert.Equal(ProductionOrderStatus.Released, order.Status);
        Assert.Equal((6L, Now), (order.ReleasedByUserId!.Value, order.ReleasedAtUtc!.Value));
        var yarn = order.Materials.Single(m => m.ItemId == 200);
        // 3,2 × 25 / 10 = 8 кг; с отходом 3% — 8,24 кг; цена хранится с точностью до 4 знаков.
        Assert.Equal((8m, 8.24m, 3m, 1234.5679m), (yarn.PlannedNet, yarn.PlannedGross, yarn.WastePercent, yarn.PlannedPrice!.Value));
        Assert.Equal(125m, order.Materials.Single(m => m.ItemId == 300).PlannedGross);
        // 8,24 × 1234,5679 + 125 × 2,5 = 10172,84 + 312,50
        Assert.Equal(10485.34m, order.PlannedCost());

        // Запущенный заказ не правится и повторно не запускается.
        Assert.Equal("production.order.not_draft", Assert.Throws<BusinessRuleException>(() => order.UpdateDraft(30, 7, Header())).Code);
        Assert.Equal("production.order.not_draft", Assert.Throws<BusinessRuleException>(() => order.Release(ActiveCard(), _ => 3, _ => 1, 6, Now)).Code);
    }

    [Fact]
    public void Release_requires_active_card_and_unknown_price_gives_unknown_cost()
    {
        var draftCard = TechCard.Create(1, 100, 2, 10, null, 5, Now);
        draftCard.SetLine(200, 1m, 0m);
        Assert.Equal("production.order.techcard_inactive",
            Assert.Throws<BusinessRuleException>(() => Draft().Release(draftCard, _ => 3, _ => 1, 6, Now)).Code);

        var order = Draft();
        order.Release(ActiveCard(), _ => 3, id => id == 200 ? 100m : null, 6, Now);
        Assert.Null(order.Materials.Single(m => m.ItemId == 300).PlannedPrice);
        Assert.Null(order.PlannedCost());
    }

    [Fact]
    public void Cancel_needs_reason_and_no_movements()
    {
        var draft = Draft();
        Assert.Throws<BusinessRuleException>(() => draft.Cancel(false, " ", 5, Now));
        draft.Cancel(false, "Клиент отказался", 5, Now);
        Assert.Equal((ProductionOrderStatus.Cancelled, "Клиент отказался"), (draft.Status, draft.CancelReason));
        Assert.Equal("production.order.status", Assert.Throws<BusinessRuleException>(() => draft.Cancel(false, "ещё раз", 5, Now)).Code);

        var released = Draft();
        released.Release(ActiveCard(), _ => 3, _ => 1, 6, Now);
        Assert.Equal("production.order.has_movements", Assert.Throws<BusinessRuleException>(() => released.Cancel(true, "ошибка", 5, Now)).Code);
        Assert.Equal(ProductionOrderStatus.Released, released.Status);
        released.Cancel(false, "ошибка планирования", 5, Now);
        Assert.Equal(ProductionOrderStatus.Cancelled, released.Status);
    }

    [Fact]
    public void Output_sets_partial_and_done_and_close_needs_empty_workshop()
    {
        var order = Draft(20);
        Assert.Equal("production.order.not_in_work", Assert.Throws<BusinessRuleException>(() => order.ApplyOutput(1)).Code);
        order.Release(ActiveCard(), _ => 3, _ => 1, 6, Now);
        Assert.Equal("production.order.close_status", Assert.Throws<BusinessRuleException>(() => order.Close(0, false, 0, null, 5, Now)).Code);

        order.ApplyOutput(12);
        Assert.Equal(ProductionOrderStatus.PartiallyDone, order.Status);
        Assert.Equal("production.order.materials_left", Assert.Throws<BusinessRuleException>(() => order.Close(12, true, 0, "x", 5, Now)).Code);
        Assert.Equal("production.order.packed_not_released", Assert.Throws<BusinessRuleException>(() => order.Close(12, false, 3, "x", 5, Now)).Code);
        // Выполнен не полностью — только с причиной.
        Assert.Throws<BusinessRuleException>(() => order.Close(12, false, 0, null, 5, Now));

        order.ApplyOutput(20);
        Assert.Equal(ProductionOrderStatus.Done, order.Status);
        Assert.Equal("production.order.not_in_work", Assert.Throws<BusinessRuleException>(() => order.ApplyOutput(21)).Code);
        order.EnsureMaterialsOpen(); // возврат остатка и отходы — и после полного выпуска
        order.Close(20, false, 0, null, 5, Now);
        Assert.Equal((ProductionOrderStatus.Closed, (string?)null), (order.Status, order.CloseReason));
        Assert.Throws<BusinessRuleException>(() => order.EnsureMaterialsOpen());
    }

    [Fact]
    public void Output_reversal_recalculates_status()
    {
        var order = Draft(20);
        order.Release(ActiveCard(), _ => 3, _ => 1, 6, Now);
        order.ApplyOutput(20);
        order.RecalculateAfterReversal(15);
        Assert.Equal(ProductionOrderStatus.PartiallyDone, order.Status);
        order.RecalculateAfterReversal(0);
        Assert.Equal(ProductionOrderStatus.Released, order.Status);
        order.ApplyOutput(20);
        order.Close(20, false, 0, null, 5, Now);
        Assert.Throws<BusinessRuleException>(() => order.RecalculateAfterReversal(10));
    }
}
