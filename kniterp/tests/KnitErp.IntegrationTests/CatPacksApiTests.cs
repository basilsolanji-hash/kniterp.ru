using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Упаковки (D307): коэффициент, штрихкод упаковки уникален вместе со штрихкодами позиций, скан, защита смены типа и единицы.
/// Запросы для API каталога: обогащение списка и доступность (товар, комплект, услуга) с правами токена.
/// </summary>
public sealed class CatPacksApiTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 933_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Packs_barcodes_scan_and_guards()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var box = await s.Catalog.CreateUnitAsync(new UnitCommand("BOX", "Коробка", "кор", 0));
        var bag = await s.Catalog.CreateUnitAsync(new UnitCommand("BAG", "Мешок", "меш", 0));

        await s.Packs.SetAsync(f.Sweater, box, 12, "4600000000008");
        await s.Packs.SetAsync(f.Sweater, box, 10, "4600000000008"); // та же единица — замена коэффициента
        var packs = await s.Packs.ListAsync(f.Sweater);
        Assert.Equal(("кор", 10m, "4600000000008"), (packs.Single().UnitSymbol, packs.Single().Factor, packs.Single().Barcode));

        // Скан: штрихкод упаковки — 10 шт; штрихкод позиции — 1 шт.
        await s.Nomenclature.AddBarcodeAsync(f.Sweater, "4600000000015");
        Assert.Equal((f.Sweater, 10m, "кор"), (await s.Packs.ScanAsync("4600000000008")) is { } a ? (a.ItemId, a.Quantity, a.PackUnit) : default);
        Assert.Equal((f.Sweater, 1m), (await s.Packs.ScanAsync("4600000000015")) is { } b ? (b.ItemId, b.Quantity) : default);
        Assert.Null(await s.Packs.ScanAsync("0000"));

        // Один штрихкод — одна позиция: упаковка и позиция не делят код.
        Assert.Equal("catalog.barcode.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Packs.SetAsync(f.Yarn, bag, 25, "4600000000015"))).Code);
        Assert.Equal("catalog.barcode.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.AddBarcodeAsync(f.Yarn, "4600000000008"))).Code);

        // Правила: единица не основная, коэффициент по точности основной единицы, только товары.
        Assert.Equal("catalog.pack.same_unit", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Packs.SetAsync(f.Sweater, f.Piece, 2, null))).Code);
        Assert.Equal("catalog.pack.precision", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Packs.SetAsync(f.Sweater, bag, 2.5m, null))).Code);
        await s.Packs.SetAsync(f.Yarn, bag, 25.5m, null); // кг — 3 знака
        Assert.Equal("catalog.pack.not_goods", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Packs.SetAsync(f.Service, box, 1, null))).Code);

        // Позицию с упаковками нельзя сделать услугой, у неё нельзя сменить основную единицу.
        var row = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-1"))).Items.Single();
        Assert.Equal("catalog.pack.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(f.Sweater, new ItemCommand("СВ-1", "Свитер", ItemType.Service, f.Piece, null, f.Vat), row.RowVersion))).Code);
        Assert.Equal("catalog.pack.unit_change", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(f.Sweater, new ItemCommand("СВ-1", "Свитер", ItemType.Finished, f.Pair, null, f.Vat), row.RowVersion))).Code);

        // Удаление упаковки — с проверкой версии и записью в журнал.
        var pack = (await s.Packs.ListAsync(f.Sweater)).Single();
        await s.Packs.RemoveAsync(pack.Id, pack.RowVersion);
        Assert.Empty(await s.Packs.ListAsync(f.Sweater));
        await using (var db = host.NewDb())
        {
            Assert.True(await db.AuditEntries.CountAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Reason == "Упаковка") >= 3);
        }

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Packs.ListAsync(f.Sweater));
        Assert.Null(await o.Packs.ScanAsync("4600000000015"));
    }

    [SqlFact]
    public async Task Catalog_api_enrichment_and_availability()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var box = await s.Catalog.CreateUnitAsync(new UnitCommand("BOX", "Коробка", "кор", 0));
            await s.Packs.SetAsync(f.Sweater, box, 12, null);
            var trade = await s.Trade.GetAsync(f.Sweater);
            await s.Trade.SaveAsync(f.Sweater, new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "4601234567893", 1500m), trade.RowVersion);
            var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, f.Piece, null, f.Vat));
            await s.Bundles.SetComponentAsync(bundle, f.Sweater, 2);

            // 10 свитеров на складе, 3 в резерве заказа: доступно 7, наборов — 3.
            var receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(f.Store, null, null, await ReasonAsync(f), Day, null), [(f.Sweater, 10m)]);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 3, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.SetOrderReserveAsync(order, true, (await s.Sales.GetOrderAsync(order)).RowVersion);

            var list = await s.Catalog.ListItemsAsync(new ItemFilter());
            var items = await s.CatalogQuery.EnrichAsync(list.Items);
            var sweater = items.Single(i => i.Id == f.Sweater);
            Assert.Equal((ItemKind.Goods, "GOOD", true, "LP_CLOTHES", "04601234567893", 1500m),
                (sweater.Kind, sweater.PaymentSubject, sweater.IsMarked, sweater.TrackingType, sweater.Gtin, sweater.MinPrice!.Value));
            Assert.Equal(("кор", 12m), (sweater.Packs.Single().Unit, sweater.Packs.Single().Factor));
            Assert.Null(sweater.Components);
            Assert.Equal([("СВ-1", 2m)], items.Single(i => i.Id == bundle).Components!.Select(c => (c.Code, c.Quantity)));
            Assert.Equal((ItemKind.Service, "SERVICE"), items.Single(i => i.Id == f.Service) is var sv ? (sv.Kind, sv.PaymentSubject) : default);

            var availability = await s.CatalogQuery.AvailabilityAsync(f.Sweater);
            Assert.Equal((true, 7m), (availability.Stocked, availability.Total));
            Assert.Equal(7m, availability.Warehouses.Single().Available);
            Assert.Equal((false, 3m), (await s.CatalogQuery.AvailabilityAsync(bundle)) is var ba ? (ba.Stocked, ba.Total) : default);
            Assert.Equal((false, 0m), (await s.CatalogQuery.AvailabilityAsync(f.Service)) is var sa ? (sa.Stocked, sa.Total) : default);
        }

        // Токен API только со справочниками: минимальная цена скрыта; чужая организация — 404.
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId, apiScope: [Permissions.CatalogView]))
        {
            var items = await s.CatalogQuery.EnrichAsync((await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-1"))).Items);
            Assert.Null(items.Single().MinPrice);
            Assert.Null(items.Single().SalePrice);
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.CatalogQuery.AvailabilityAsync(f.Sweater));
        }
    }

    private async Task<long?> ReasonAsync(Fixture f)
    {
        await using var db = host.NewDb();
        return await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
            .Select(r => (long?)r.Id).FirstAsync();
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Yarn, long Service, long Customer, long Piece, long Pair, long Vat);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, vat));
        var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Code == "166").Id, null, vat));
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        return new Fixture(org, store, sweater, yarn, service, customer, piece, units.Single(u => u.Code == "715").Id, vat);
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
