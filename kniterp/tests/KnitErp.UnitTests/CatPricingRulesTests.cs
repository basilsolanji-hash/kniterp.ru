using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Integrations;

namespace KnitErp.UnitTests;

/// <summary>Торговые сведения (D305) и массовая переоценка (D306).</summary>
public sealed class CatPricingRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("04601234567893", "04601234567893")]
    [InlineData("4601234567893", "04601234567893")] // EAN-13 → GTIN-14
    [InlineData(" 0460 1234 5678 93 ", "04601234567893")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Gtin_is_normalized(string? input, string? expected) => Assert.Equal(expected, Marking.NormalizeGtin(input));

    [Theory]
    [InlineData("04601234567894")] // неверная контрольная цифра
    [InlineData("0460123456789")]  // 13 цифр с ведущим нулём — неверная контрольная после дополнения
    [InlineData("0460123456789A")]
    [InlineData("123")]
    public void Wrong_gtin_is_rejected(string input) =>
        Assert.Equal("catalog.gtin.invalid", Assert.Throws<BusinessRuleException>(() => Marking.NormalizeGtin(input)).Code);

    [Fact]
    public void Trade_rules_for_goods_and_services()
    {
        var sweater = Item.Create(1, "СВ-1", "Свитер", ItemType.Finished, 1, null, Now);
        Assert.Equal(PaymentSubject.Goods, sweater.PaymentSubject);
        var changes = sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "4601234567893", 1500m));
        Assert.Equal(["Маркировка", "GTIN", "Минимальная цена"], changes.Select(c => c.Field));
        Assert.Equal(("04601234567893", TrackingType.Clothes, 1500m), (sweater.Gtin, sweater.TrackingType, sweater.MinPrice!.Value));
        Assert.Equal("catalog.marking.type_required", Assert.Throws<BusinessRuleException>(() =>
            sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.None, null, null))).Code);
        Assert.Equal("catalog.payment_subject.goods", Assert.Throws<BusinessRuleException>(() =>
            sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Service, false, TrackingType.None, null, null))).Code);
        Assert.Equal("catalog.min_price", Assert.Throws<BusinessRuleException>(() =>
            sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, false, TrackingType.None, null, -1))).Code);
        // Снятый признак маркировки сбрасывает вид продукции.
        sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, false, TrackingType.Clothes, null, null));
        Assert.Equal(TrackingType.None, sweater.TrackingType);

        var service = Item.Create(1, "УС-1", "Доставка", ItemType.Service, 1, null, Now);
        Assert.Equal(PaymentSubject.Service, service.PaymentSubject);
        Assert.Equal("catalog.service.marking", Assert.Throws<BusinessRuleException>(() =>
            service.SetTrade(new ItemTradeDetails(PaymentSubject.Service, true, TrackingType.Clothes, null, null))).Code);
        service.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, false, TrackingType.None, null, 300));
        Assert.Equal(PaymentSubject.Service, service.PaymentSubject); // у услуги всегда «услуга»

        // Смена типа: товар в услугу — маркировка снимается; услуга в товар — «товар».
        var item = Item.Create(1, "П-1", "Позиция", ItemType.Finished, 1, null, Now);
        item.SetTrade(new ItemTradeDetails(PaymentSubject.ExcisableGoods, true, TrackingType.Linens, "04601234567893", null));
        item.Update("П-1", "Позиция", ItemType.Service, 1, null);
        Assert.Equal((PaymentSubject.Service, false, (string?)null), (item.PaymentSubject, item.IsMarked, item.Gtin));
        item.Update("П-1", "Позиция", ItemType.Finished, 1, null);
        Assert.Equal(PaymentSubject.Goods, item.PaymentSubject);

        // Модификация наследует маркировку и минимальную цену, но не GTIN.
        sweater.SetTrade(new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "04601234567893", 1500m));
        var red = Item.CreateModification(sweater, "СВ-1-К", "Свитер красный", "1=красный", Now);
        Assert.Equal((true, TrackingType.Clothes, (string?)null, 1500m), (red.IsMarked, red.TrackingType, red.Gtin, red.MinPrice!.Value));

        Assert.True(TradeRules.BelowMinimum(1499.99m, 1500m));
        Assert.False(TradeRules.BelowMinimum(1500m, 1500m));
        Assert.False(TradeRules.BelowMinimum(1m, null));
    }

    [Fact]
    public void Moysklad_tracking_codes_round_trip()
    {
        Assert.All(Marking.All, t => Assert.Equal(t, Marking.FromMoySklad(Marking.MoySkladCode(t))));
        Assert.Equal(TrackingType.Other, Marking.FromMoySklad("TOBACCO"));
        Assert.Equal(TrackingType.None, Marking.FromMoySklad(null));
    }

    [Fact]
    public void Revaluation_rules_and_rounding()
    {
        Assert.Equal(1100m, Revaluation.NewPrice(RevaluationMode.Percent, 10, PriceRounding.Kopeck, 1000m, null));
        Assert.Equal(950m, Revaluation.NewPrice(RevaluationMode.Percent, -5, PriceRounding.Kopeck, 1000m, null));
        Assert.Equal(1234.56m, Revaluation.NewPrice(RevaluationMode.Percent, 1, PriceRounding.Kopeck, 1222.34m, null)); // 1234,5634
        Assert.Equal(1235m, Revaluation.NewPrice(RevaluationMode.Percent, 1, PriceRounding.Ruble, 1222.34m, null));
        Assert.Equal(1230m, Revaluation.NewPrice(RevaluationMode.Percent, 1, PriceRounding.Ten, 1222.34m, null));
        Assert.Equal(1200m, Revaluation.NewPrice(RevaluationMode.Percent, 1, PriceRounding.Hundred, 1222.34m, null));
        Assert.Null(Revaluation.NewPrice(RevaluationMode.Percent, 10, PriceRounding.Kopeck, null, 500m));
        Assert.Equal(900m, Revaluation.NewPrice(RevaluationMode.FromPurchase, 1.8m, PriceRounding.Kopeck, null, 500m));
        Assert.Null(Revaluation.NewPrice(RevaluationMode.FromPurchase, 1.8m, PriceRounding.Kopeck, 1000m, null));

        Assert.Equal("catalog.revaluation.percent", Assert.Throws<BusinessRuleException>(() => Revaluation.Validate(RevaluationMode.Percent, -100)).Code);
        Assert.Equal("catalog.revaluation.percent", Assert.Throws<BusinessRuleException>(() => Revaluation.Validate(RevaluationMode.Percent, 0)).Code);
        Assert.Equal("catalog.revaluation.factor", Assert.Throws<BusinessRuleException>(() => Revaluation.Validate(RevaluationMode.FromPurchase, 0)).Code);
        Assert.Equal("catalog.revaluation.mode", Assert.Throws<BusinessRuleException>(() => Revaluation.Validate((RevaluationMode)9, 1)).Code);
        Revaluation.Validate(RevaluationMode.Percent, -99.99m);
        Assert.Equal("+10 % к текущей цене, округление до рубля", Revaluation.Describe(RevaluationMode.Percent, 10, PriceRounding.Ruble));
    }

    [Fact]
    public void Price_edit_is_never_granted_to_api_tokens()
    {
        Assert.Contains(Permissions.PriceEdit, ApiToken.Forbidden);
        Assert.Equal("api.token.permission_forbidden", Assert.Throws<BusinessRuleException>(() =>
            ApiToken.Issue(1, "Магазин", [Permissions.CatalogView, Permissions.PriceEdit], 1, Now, 30)).Code);
    }
}
