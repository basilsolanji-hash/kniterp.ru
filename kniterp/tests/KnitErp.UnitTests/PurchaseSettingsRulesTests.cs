using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;

namespace KnitErp.UnitTests;

/// <summary>D133: допуск поступления сверх заказа (0–10 %) и вводный остаток расчётов с поставщиком.</summary>
public sealed class PurchaseSettingsRulesTests
{
    [Fact]
    public void Over_receipt_tolerance_is_zero_by_default_and_limited_to_ten_percent()
    {
        var s = PurchaseSettings.Default(1);
        Assert.Equal(0m, s.OverReceiptPercent);
        foreach (var bad in new[] { -1m, 10.01m, 11m, 2.345m })
        {
            Assert.Equal("purchase.settings.over_receipt", Assert.Throws<BusinessRuleException>(() => s.Update(bad, 1, DateTime.UtcNow)).Code);
        }

        s.Update(10m, 7, DateTime.UtcNow);
        Assert.Equal((10m, 7L), (s.OverReceiptPercent, s.UpdatedByUserId!.Value));
        Assert.Equal("допуск поступления сверх заказа 10%", s.Describe());
    }

    [Fact]
    public void Allowed_quantity_is_ordered_times_one_plus_tolerance()
    {
        Assert.Equal(50m, PurchaseSettings.AllowedQuantity(50m, 0m));
        Assert.Equal(55m, PurchaseSettings.AllowedQuantity(50m, 10m));
        Assert.Equal(10.25m, PurchaseSettings.AllowedQuantity(10m, 2.5m));
        Assert.Equal(0.333333m, PurchaseSettings.AllowedQuantity(0.333333m, 0m));
        Assert.Equal(0.343332m, PurchaseSettings.AllowedQuantity(0.333333m, 3m)); // 0,34333299 — вниз до 6 знаков, не больше допуска
    }

    [Fact]
    public void Supplier_opening_balance_is_not_zero_with_kopecks_and_cancelled_once_with_reason()
    {
        foreach (var bad in new[] { 0m, 1.005m, 1_000_000_000_000m })
        {
            Assert.Equal("purchase.opening.amount",
                Assert.Throws<BusinessRuleException>(() => SupplierOpeningBalance.Create(1, 1, 1, new DateOnly(2026, 1, 1), bad, null, 1, DateTime.UtcNow)).Code);
        }

        var b = SupplierOpeningBalance.Create(1, 2, 3, new DateOnly(2026, 1, 1), -1500m, "  аванс  ", 1, DateTime.UtcNow);
        Assert.Equal((-1500m, "аванс"), (b.Amount, b.Comment));
        Assert.Throws<BusinessRuleException>(() => b.Cancel("  ", 1, DateTime.UtcNow));
        b.Cancel("Ошибка ввода", 4, DateTime.UtcNow);
        Assert.Equal((true, 4L, "Ошибка ввода"), (b.IsCancelled, b.CancelledByUserId!.Value, b.CancelReason));
        Assert.Equal("purchase.opening.cancelled", Assert.Throws<BusinessRuleException>(() => b.Cancel("ещё раз", 4, DateTime.UtcNow)).Code);
    }
}
