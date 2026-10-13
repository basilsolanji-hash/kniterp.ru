using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;

namespace KnitErp.UnitTests;

/// <summary>Услуги (D300): тип, вид, признак предмета расчёта, правила карточки.</summary>
public sealed class CatServiceRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Service_type_is_known_but_not_stocked()
    {
        Assert.True(ItemTypes.IsDefined(ItemType.Service));
        Assert.Equal(10, (byte)ItemType.Service);
        Assert.Equal("Услуги", ItemTypes.Name(ItemType.Service));
        Assert.False(ItemTypes.IsStocked(ItemType.Service));
        Assert.DoesNotContain(ItemType.Service, ItemTypes.Stocked);
        Assert.Equal([ItemType.Service, ItemType.Bundle], ItemTypes.NotStocked);
        Assert.All(ItemTypes.All.Where(t => t is not (ItemType.Service or ItemType.Bundle)), t => Assert.True(ItemTypes.IsStocked(t)));
        Assert.Equal(ItemKind.Service, ItemTypes.KindOf(ItemType.Service));
        Assert.Equal(ItemKind.Goods, ItemTypes.KindOf(ItemType.Finished));
        Assert.Equal([ItemType.Service], ItemTypes.OfKind(ItemKind.Service));
        Assert.Equal(PaymentSubject.Service, ItemTypes.DefaultPaymentSubject(ItemType.Service));
        Assert.Equal(PaymentSubject.Goods, ItemTypes.DefaultPaymentSubject(ItemType.RawMaterial));
    }

    [Fact]
    public void Service_requires_vat_rate()
    {
        Assert.Equal("catalog.service.vat_required", Assert.Throws<BusinessRuleException>(() => ServiceRules.EnsureVat(ItemType.Service, null)).Code);
        ServiceRules.EnsureVat(ItemType.Service, 5);
        ServiceRules.EnsureVat(ItemType.Finished, null);
        Assert.Equal("item.service_not_stocked", ServiceRules.NotStocked(["УС-1"]).Code);
        Assert.Contains("УС-1", ServiceRules.NotStocked(["УС-1"]).Message);
    }

    [Fact]
    public void Service_has_no_weight_volume_or_min_stock()
    {
        var service = Item.Create(1, "УС-1", "Доставка", ItemType.Service, 1, null, Now);
        var details = service.Details;
        Assert.Equal("catalog.service.no_stock_fields",
            Assert.Throws<BusinessRuleException>(() => service.SetDetails(details with { WeightKg = 1 })).Code);
        Assert.Equal("catalog.service.no_stock_fields",
            Assert.Throws<BusinessRuleException>(() => service.SetDetails(details with { VolumeM3 = 0.1m })).Code);
        Assert.Equal("catalog.service.no_stock_fields",
            Assert.Throws<BusinessRuleException>(() => service.SetDetails(details with { MinStock = 2 })).Code);
        Assert.Single(service.SetDetails(details with { Article = "DLV" }));
    }

    [Fact]
    public void Turning_goods_into_service_clears_stock_fields()
    {
        var item = Item.Create(1, "УП-1", "Упаковка подарочная", ItemType.Packaging, 1, null, Now);
        item.SetDetails(item.Details with { WeightKg = 0.2m, VolumeM3 = 0.001m, MinStock = 10, Article = "GIFT" });
        var changes = item.Update("УП-1", "Упаковка подарочная", ItemType.Service, 2, null);
        Assert.Equal(["Вес, кг", "Объём, м³", "Неснижаемый остаток", "Тип", "UnitId"], changes.Select(c => c.Field));
        Assert.Equal((null, null, null, "GIFT"), (item.WeightKg, item.VolumeM3, item.MinStock, item.Article));
    }

    [Fact]
    public void Service_has_no_modifications()
    {
        var service = Item.Create(1, "УС-1", "Пошив по образцу", ItemType.Service, 1, null, Now);
        Assert.Equal("catalog.variant.not_goods",
            Assert.Throws<BusinessRuleException>(() => Item.CreateModification(service, "УС-1-1", "Пошив, срочно", "1=срочно", Now)).Code);

        var parent = Item.Create(1, "СВ-1", "Свитер", ItemType.Finished, 1, null, Now);
        var variant = Item.CreateModification(parent, "СВ-1-К", "Свитер, красный", "1=красный", Now);
        Assert.Equal("catalog.variant.type",
            Assert.Throws<BusinessRuleException>(() => variant.Update("СВ-1-К", "Свитер, красный", ItemType.Service, 1, null)).Code);
    }

    [Fact]
    public void Service_units_are_in_defaults()
    {
        Assert.Contains(UnitOfMeasure.Defaults, u => u.Code == ServiceRules.ServiceUnitCode && u.Symbol == "усл. ед" && u.Precision == 0);
        Assert.Contains(UnitOfMeasure.Defaults, u => u.Code == ServiceRules.HourUnitCode && u.Symbol == "ч");
        Assert.Equal(UnitOfMeasure.Defaults.Count, UnitOfMeasure.Defaults.Select(u => u.Code).Distinct().Count());
    }

    /// <summary>D354 (сессия 4, Р1): акт услуг — по заказу, без движений склада, номер из общего с отгрузками счётчика.</summary>
    [Fact]
    public void Service_act_needs_order_has_no_movements_and_shares_shipment_counter()
    {
        var header = new StockDocumentHeader(1, null, 5, 3, new DateOnly(2026, 10, 9), null, SalesOrderId: 9);
        var act = StockDocument.Create(1, "АУ-000016", StockOperationKind.ServiceAct, header, 7, Now);
        act.SetLine(11, 2);
        act.Post(7, false, Now);
        Assert.Empty(act.MovementDeltas());
        Assert.Equal("stock.service_act.order_required", Assert.Throws<BusinessRuleException>(() =>
            StockDocument.Create(1, "АУ-000017", StockOperationKind.ServiceAct, header with { SalesOrderId = null }, 7, Now)).Code);
        Assert.Equal("stock.service_act.purchase_order_required", Assert.Throws<BusinessRuleException>(() =>
            StockDocument.Create(1, "АП-000001", StockOperationKind.SupplierServiceAct, header with { SalesOrderId = null }, 7, Now)).Code);
        Assert.Equal("item.bundle_not_stocked", Assert.Throws<BusinessRuleException>(() =>
            StockDocument.Create(1, "АУ-000018", StockOperationKind.ServiceAct, header, 7, Now).SetBundle(4, 1, [(11, 1m)])).Code);

        Assert.Equal(("ОТ", "АУ-000016", "АП"), (StockDocument.CounterPrefix(StockOperationKind.ServiceAct),
            StockDocument.NumberFromCounter(StockOperationKind.ServiceAct, "ОТ-000016"), StockDocument.CounterPrefix(StockOperationKind.SupplierServiceAct)));
        Assert.Equal((StockOperationKind.Shipment, StockOperationKind.Receipt, StockOperationKind.CustomerReturn),
            (StockOperationKinds.OrderKind(StockOperationKind.ServiceAct), StockOperationKinds.OrderKind(StockOperationKind.SupplierServiceAct),
                StockOperationKinds.OrderKind(StockOperationKind.CustomerReturn)));
        Assert.True(StockDocument.WithCustomer(StockOperationKind.ServiceAct) && StockDocument.WithSupplier(StockOperationKind.SupplierServiceAct));
        Assert.DoesNotContain(StockOperationKind.ServiceAct, StockDocument.Kinds);
        Assert.Contains(StockOperationKind.ServiceAct, StockDocument.AllKinds);
        Assert.Equal("stock.service_act.goods", ServiceRules.ActGoods(["СВ-1"]).Code);
    }
}
