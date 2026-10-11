using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using static KnitErp.Domain.Production.ProductionDocumentPurpose;

namespace KnitErp.UnitTests;

/// <summary>Материальный баланс (D94): выдано = израсходовано + отходы + возвращено + остаток в НЗП, отклонение от нормы.</summary>
public sealed class MaterialBalanceTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Заказ на 30 свитеров: пряжа 9,888 кг с отходом (3,2 кг на 10 шт + 3%).</summary>
    private static IReadOnlyList<ProductionOrderMaterial> Materials()
    {
        var card = TechCard.Create(1, 100, 1, 10, null, 5, Now);
        card.SetLine(200, 3.2m, 3m);
        card.Activate(5, Now);
        var order = ProductionOrder.Create(1, "ПЗ-1", 100, 30, 7, new(DateOnly.FromDateTime(Now), null, ProductionSource.Plan, null, 10, 20, null), 5, Now);
        order.Release(card, _ => 3, _ => 1000m, 5, Now);
        return order.Materials;
    }

    [Fact]
    public void Balance_identity_and_deviation_after_output()
    {
        // Выдано 11, израсходовано 6,6 на 20 выпущенных, отходы 0,4, возвращено 1 — в цехе 3.
        var line = MaterialBalance.Calculate(Materials(), 30,
            [new(200, Issue, 10), new(200, Issue, 1), new(200, Consumption, 6.6m), new(200, Waste, 0.4m), new(200, Return, 1)], released: 20, defect: 0).Single();
        Assert.Equal((11m, 6.6m, 0.4m, 1m, 3m), (line.Issued, line.Consumed, line.Waste, line.Returned, line.Remaining));
        Assert.Equal(line.Issued, line.Consumed + line.Waste + line.Returned + line.Remaining);
        // Норма на 20 шт: 9,888 × 20 / 30 = 6,592; факт 7,0 — больше на 6,19% при допуске 5%.
        Assert.Equal(6.592m, line.Normative);
        Assert.Equal(6.19m, line.DeviationPercent);
        Assert.True(line.OverTolerance(MaterialBalance.TolerancePercent));
        Assert.False(line.OverTolerance(7m));
    }

    [Fact]
    public void No_norm_before_output_and_defect_counts_in_norm()
    {
        var before = MaterialBalance.Calculate(Materials(), 30, [new(200, Issue, 5)], released: 0, defect: 2).Single();
        Assert.Null(before.Normative);
        Assert.Null(before.DeviationPercent);
        var after = MaterialBalance.Calculate(Materials(), 30, [new(200, Issue, 5), new(200, Consumption, 3.626m)], released: 9, defect: 2).Single();
        Assert.Equal(3.6256m, decimal.Round(after.Normative!.Value, 4)); // 9,888 × 11 / 30
        Assert.False(after.OverTolerance(MaterialBalance.TolerancePercent));
    }

    [Fact]
    public void Outgoing_cannot_exceed_order_remaining()
    {
        var balance = MaterialBalance.Calculate(Materials(), 30, [new(200, Issue, 2), new(200, Waste, 0.5m)], 0, 0);
        MaterialBalance.EnsureWithinRemaining(balance, [(200, 1.5m)], _ => "ПР-1");
        var ex = Assert.Throws<BusinessRuleException>(() => MaterialBalance.EnsureWithinRemaining(balance, [(200, 1m), (200, 0.6m)], _ => "ПР-1"));
        Assert.Equal("production.material.exceeds_remaining", ex.Code);
        Assert.Contains("ПР-1: нужно 1,6, осталось 1,5", ex.Message);
        Assert.Throws<BusinessRuleException>(() => MaterialBalance.EnsureWithinRemaining(balance, [(999, 0.1m)], _ => "X"));
    }
}
