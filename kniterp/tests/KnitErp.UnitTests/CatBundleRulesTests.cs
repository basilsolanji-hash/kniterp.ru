using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>Комплекты (D302, D303): разворот в компоненты, доступность, подсказка цены, себестоимость, правила состава.</summary>
public sealed class CatBundleRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);

    // Комплект 100 «Набор шапка + шарф + пряжа на помпон»: шапка 1 шт, шарф 1 шт, пряжа 0,035 кг. Комплект 200 — другой.
    private static readonly BundleComponentSpec[] Specs =
    [
        new(100, 1, 1, 0), new(100, 2, 1, 0), new(100, 3, 0.035m, 3),
        new(200, 1, 2, 0),
    ];

    [Fact]
    public void Expand_multiplies_by_quantity_and_rounds_up_by_component_precision()
    {
        Assert.Equal([(1L, 3m), (2L, 3m), (3L, 0.105m)], BundleExpansion.Expand(100, 3, Specs));
        Assert.Equal([(1L, 4m)], BundleExpansion.Expand(200, 2, Specs));

        // 0,035 кг × 1,5 = 0,0525 → 0,053 (вверх по точности кг — 3 знака): не списать меньше, чем ушло в наборы.
        Assert.Equal(0.053m, BundleExpansion.Expand(100, 1.5m, Specs).Single(x => x.ItemId == 3).Quantity);
        // Штуки — целые: 1 × 1,5 = 1,5 → 2.
        Assert.Equal(2m, BundleExpansion.Expand(100, 1.5m, Specs).Single(x => x.ItemId == 1).Quantity);

        Assert.Equal("catalog.bundle.empty", Assert.Throws<BusinessRuleException>(() => BundleExpansion.Expand(300, 1, Specs)).Code);
        Assert.Equal("catalog.bundle.quantity", Assert.Throws<BusinessRuleException>(() => BundleExpansion.Expand(100, 0, Specs)).Code);
    }

    [Fact]
    public void Expand_lines_replaces_bundles_and_sums_same_items()
    {
        // Строки: комплект 100 × 2, отдельная шапка × 1, комплект 200 × 1 (2 шапки).
        var lines = BundleExpansion.ExpandLines([(100, 2m), (1, 1m), (200, 1m)], new HashSet<long> { 100, 200 }, Specs);
        Assert.Equal(new Dictionary<long, decimal> { [1] = 5m, [2] = 2m, [3] = 0.07m }, lines.ToDictionary(l => l.ItemId, l => l.Quantity));
    }

    [Fact]
    public void Availability_is_minimum_over_components()
    {
        var stock = new Dictionary<long, decimal> { [1] = 10, [2] = 4, [3] = 0.2m };
        Assert.Equal(4m, BundleExpansion.Available(100, Specs, stock));
        // Пряжи хватает на ⌊0,1 / 0,035⌋ = 2 набора.
        Assert.Equal(2m, BundleExpansion.Available(100, Specs, new Dictionary<long, decimal> { [1] = 10, [2] = 10, [3] = 0.1m }));
        Assert.Equal(5m, BundleExpansion.Available(200, Specs, stock));
        // Нет компонента или отрицательный «доступно» (резерв больше остатка) — 0; нет состава — 0.
        Assert.Equal(0m, BundleExpansion.Available(100, Specs, new Dictionary<long, decimal> { [1] = 10, [2] = 10 }));
        Assert.Equal(0m, BundleExpansion.Available(100, Specs, new Dictionary<long, decimal> { [1] = -3, [2] = 10, [3] = 1 }));
        Assert.Equal(0m, BundleExpansion.Available(300, Specs, stock));
        // Комплект в единице с точностью 1: ⌊0,2 / 0,035 × 10⌋ / 10 = 5,7.
        Assert.Equal(5.7m, BundleExpansion.Available(100, Specs, new Dictionary<long, decimal> { [1] = 10, [2] = 10, [3] = 0.2m }, 1));
    }

    [Fact]
    public void Price_hint_and_cost_sum_components()
    {
        var prices = new Dictionary<long, decimal> { [1] = 1200, [2] = 900, [3] = 4000 };
        var hint = BundleExpansion.PriceHint(100, Specs, prices, 2000);
        Assert.Equal((2240m, 240m, 10.71m), (hint.ComponentsTotal!.Value, hint.Discount!.Value, hint.DiscountPercent!.Value));
        var noPrice = BundleExpansion.PriceHint(100, Specs, new Dictionary<long, decimal> { [1] = 1200 }, 2000);
        Assert.Null(noPrice.ComponentsTotal);
        Assert.Equal([2L, 3L], noPrice.ComponentsWithoutPrice);
        Assert.Null(BundleExpansion.PriceHint(100, Specs, prices, null).Discount);

        Assert.Equal(1050m + 0.035m * 3000, BundleExpansion.Cost(100, Specs, new Dictionary<long, decimal> { [1] = 600, [2] = 450, [3] = 3000 }));
        Assert.Null(BundleExpansion.Cost(100, Specs, new Dictionary<long, decimal> { [1] = 600 }));
    }

    [Fact]
    public void Component_rules()
    {
        var bundle = Item.Create(1, "НБ-1", "Набор", ItemType.Bundle, 1, null, Now);
        var hat = Item.Create(1, "Ш-1", "Шапка", ItemType.Finished, 1, null, Now);
        var service = Item.Create(1, "УС-1", "Доставка", ItemType.Service, 1, null, Now);
        var other = Item.Create(1, "НБ-2", "Другой набор", ItemType.Bundle, 1, null, Now);
        BundleRules.EnsureBundle(bundle);
        BundleRules.EnsureComponent(bundle, hat);
        Assert.Equal("catalog.bundle.not_bundle", Assert.Throws<BusinessRuleException>(() => BundleRules.EnsureBundle(hat)).Code);
        Assert.Equal("catalog.bundle.nested", Assert.Throws<BusinessRuleException>(() => BundleRules.EnsureComponent(bundle, other)).Code);
        Assert.Equal("catalog.bundle.service", Assert.Throws<BusinessRuleException>(() => BundleRules.EnsureComponent(bundle, service)).Code);
        hat.Archive(Now);
        Assert.Equal("catalog.archived", Assert.Throws<BusinessRuleException>(() => BundleRules.EnsureComponent(bundle, hat)).Code);

        Assert.Equal("catalog.bundle.self", Assert.Throws<BusinessRuleException>(() => BundleComponent.Create(1, 5, 5, 1, 0, 0)).Code);
        Assert.Equal("catalog.bundle.quantity", Assert.Throws<BusinessRuleException>(() => BundleComponent.Create(1, 5, 6, 0, 0, 0)).Code);
        Assert.Equal("catalog.bundle.precision", Assert.Throws<BusinessRuleException>(() => BundleComponent.Create(1, 5, 6, 1.5m, 0, 0)).Code);
        Assert.Equal(0.035m, BundleComponent.Create(1, 5, 6, 0.035m, 3, 0).Quantity);
    }

    [Fact]
    public void Bundle_card_rules()
    {
        Assert.Equal(ItemKind.Bundle, ItemTypes.KindOf(ItemType.Bundle));
        Assert.Equal("Комплекты", ItemTypes.Name(ItemType.Bundle));
        Assert.False(ItemTypes.IsStocked(ItemType.Bundle));
        var bundle = Item.Create(1, "НБ-1", "Набор", ItemType.Bundle, 1, null, Now);
        Assert.Equal("catalog.bundle.no_min_stock", Assert.Throws<BusinessRuleException>(() => bundle.SetDetails(bundle.Details with { MinStock = 1 })).Code);
        bundle.SetDetails(bundle.Details with { WeightKg = 0.6m }); // вес набора — для доставки
        Assert.Equal("catalog.variant.not_goods",
            Assert.Throws<BusinessRuleException>(() => Item.CreateModification(bundle, "НБ-1-К", "Набор красный", "1=красный", Now)).Code);

        // Товар в комплект: неснижаемый остаток очищается, вес остаётся.
        var item = Item.Create(1, "Н-1", "Набор", ItemType.Finished, 1, null, Now);
        item.SetDetails(item.Details with { WeightKg = 0.5m, MinStock = 3 });
        Assert.Equal(["Неснижаемый остаток", "Тип"], item.Update("Н-1", "Набор", ItemType.Bundle, 1, null).Select(c => c.Field));
        Assert.Equal((0.5m, (decimal?)null), (item.WeightKg!.Value, item.MinStock));
        Assert.Equal("item.bundle_not_stocked", ServiceRules.BundleNotStocked(["НБ-1"]).Code);
    }

    [Fact]
    public void Return_is_proportional_to_shipped_composition_d352()
    {
        // Отгружено 3 набора: шапки 3, пряжа 0,105 кг; состав с тех пор мог измениться — возврат идёт от отгруженного.
        var shipped = new Dictionary<long, decimal> { [1] = 3, [3] = 0.105m };
        var precisions = new Dictionary<long, byte> { [1] = 0, [3] = 3 };
        Assert.Equal([(1L, 1m), (3L, 0.035m)], BundleExpansion.Proportional(1, 3, shipped, precisions));
        Assert.Equal([(1L, 3m), (3L, 0.105m)], BundleExpansion.Proportional(3, 3, shipped, precisions));
        // Округление по точности и не больше отгруженного.
        Assert.Equal([(1L, 2m), (3L, 0.07m)], BundleExpansion.Proportional(2, 3, shipped, precisions));
        Assert.Equal("sales.return.bundle_exceeds",
            Assert.Throws<BusinessRuleException>(() => BundleExpansion.Proportional(4, 3, shipped, precisions)).Code);
        Assert.Equal("catalog.bundle.quantity", Assert.Throws<BusinessRuleException>(() => BundleExpansion.Proportional(0, 3, shipped, precisions)).Code);
    }

    [Fact]
    public void Document_lines_in_order_terms_count_bundle_once_per_document_d352()
    {
        var lines = new (int Doc, long ItemId, decimal Quantity, long? BundleItemId, decimal? BundleQuantity)[]
        {
            (1, 1, 2, null, null), (1, 1, 3, 100, 3), (1, 2, 3, 100, 3), (1, 3, 0.105m, 100, 3), (2, 1, 1, 100, 1), (2, 2, 1, 100, 1),
        };
        Assert.Equal([(1, 1L, 2m), (1, 100L, 3m), (2, 100L, 1m)], KnitErp.Domain.Warehousing.BundleLines.InOrderTerms(lines));
    }
}
