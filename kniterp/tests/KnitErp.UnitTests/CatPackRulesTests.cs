using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>Упаковки (D307): коэффициент, пересчёт в основные единицы, штрихкод, только у товаров.</summary>
public sealed class CatPackRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Packs_convert_to_base_units()
    {
        Assert.Equal(30m, Packs.ToBase(2.5m, 12, 0));
        Assert.Equal(51m, Packs.ToBase(2, 25.5m, 3));
        Assert.Equal("catalog.pack.not_whole", Assert.Throws<BusinessRuleException>(() => Packs.ToBase(0.1m, 12, 0)).Code);
        Assert.Equal("catalog.pack.quantity", Assert.Throws<BusinessRuleException>(() => Packs.ToBase(0, 12, 0)).Code);
        Assert.Equal((2m, 6m), Packs.Split(30, 12));
        Assert.Equal((0m, 5m), Packs.Split(5, 12));
    }

    [Fact]
    public void Pack_rules()
    {
        var sweater = Item.Create(1, "СВ-1", "Свитер", ItemType.Finished, 1, null, Now);
        var pack = ItemPack.Create(1, sweater, 2, 12, 0, " 4600000000008 ");
        Assert.Equal((12m, "4600000000008"), (pack.Factor, pack.Barcode));
        Assert.Null(ItemPack.Create(1, sweater, 2, 12, 0, "  ").Barcode);
        Assert.Equal("catalog.pack.same_unit", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, sweater, 1, 12, 0, null)).Code);
        Assert.Equal("catalog.pack.factor", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, sweater, 2, 0, 0, null)).Code);
        Assert.Equal("catalog.pack.precision", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, sweater, 2, 1.5m, 0, null)).Code);
        Assert.Equal("catalog.barcode.invalid", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, sweater, 2, 12, 0, "4600000000009")).Code);
        var service = Item.Create(1, "УС-1", "Доставка", ItemType.Service, 1, null, Now);
        Assert.Equal("catalog.pack.not_goods", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, service, 2, 1, 0, null)).Code);
        var bundle = Item.Create(1, "НБ-1", "Набор", ItemType.Bundle, 1, null, Now);
        Assert.Equal("catalog.pack.not_goods", Assert.Throws<BusinessRuleException>(() => ItemPack.Create(1, bundle, 2, 1, 0, null)).Code);
    }
}
