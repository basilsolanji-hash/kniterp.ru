using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D352 (сессия 4, Р2): отгрузка и возврат комплектов по заказу покупателя — разворот в компоненты со ссылкой на комплект,
/// частичная отгрузка, «отгружено» и резерв по строке комплекта, возврат по зафиксированному составу, УПД одной строкой,
/// себестоимость по компонентам и ограничения базы.
/// </summary>
public sealed class CatBundleShipmentTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 932_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Bundle_ships_and_returns_as_components()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Заказ: комплект 3 × 2000 и та же шапка отдельной строкой 2 × 1200 — строки отгрузки не сливаются.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Bundle, 3, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderLineAsync(order, f.Hat, 2, 1200m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderReserveAsync(order, true, (await s.Sales.GetOrderAsync(order)).RowVersion);

        // Резерв держит компоненты комплекта: другой заказ видит 2 + 3 шапки и 3 × 0,035 кг пряжи в резерве.
        var probe = await ProbeOrderAsync(s, f);
        Assert.Equal((5m, 3m, 0.105m), Reserved(await s.Sales.GetOrderAsync(probe), f));
        var bundleLine = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Bundle);
        Assert.Equal((true, 10m, false), (bundleLine.Stocked, bundleLine.Available, bundleLine.Short));

        // Отгрузка: комплект развёрнут по составу, шапка — двумя строками (отдельно и из комплекта).
        var shipment = await s.Sales.CreateShipmentAsync(order);
        Assert.Equal(
            [("Ш-1", 2m, (long?)null, (decimal?)null), ("Ш-1", 3m, f.Bundle, 3m), ("ПР-1", 0.105m, f.Bundle, 3m), ("ШФ-1", 3m, f.Bundle, 3m)],
            Lines(await s.Documents.GetAsync(shipment)).OrderBy(l => l.Item3 is null ? 0 : 1).ThenBy(l => l.Item1 == "Ш-1" ? 0 : l.Item1 == "ПР-1" ? 1 : 2));

        // Частичная отгрузка: целые наборы (единица комплекта — шт), компоненты пересчитаны.
        Assert.Equal("stock.quantity.precision", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.SetBundleAsync(shipment, f.Bundle, 1.5m, (await s.Documents.GetAsync(shipment)).RowVersion))).Code);
        await s.Documents.SetBundleAsync(shipment, f.Bundle, 2, (await s.Documents.GetAsync(shipment)).RowVersion);
        var draft = await s.Documents.GetAsync(shipment);
        var parts = draft.Lines.Where(l => l.BundleItemId == f.Bundle).ToDictionary(l => l.ItemId, l => (l.Quantity, l.BundleQuantity!.Value));
        Assert.Equal(((2m, 2m), (2m, 2m), (0.07m, 2m)), (parts[f.Hat], parts[f.Scarf], parts[f.Yarn]));
        Assert.Equal(2m, draft.Lines.Single(l => l.ItemId == f.Hat && l.BundleItemId == null).Quantity);
        // Загрузка строк из Excel заменила бы строки разворота — в документе с комплектом она запрещена.
        Assert.Equal("stock.import.bundles", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Documents.ImportLinesAsync(shipment, new MemoryStream([1, 2, 3]), draft.RowVersion))).Code);
        draft = await s.Documents.GetAsync(shipment);
        await s.Documents.PostAsync(shipment, draft.RowVersion);

        // Движения — по компонентам; сам комплект на склад не попал.
        Assert.Equal((16m, 8m, 0.93m, 0m), await BalancesAsync(f));

        // «Отгружено» по строке комплекта — количество комплектов; статус, сумма и резерв — от него.
        var dto = await s.Sales.GetOrderAsync(order);
        Assert.Equal((2m, 2m), (dto.Lines.Single(l => l.ItemId == f.Bundle).Shipped, dto.Lines.Single(l => l.ItemId == f.Hat).Shipped));
        Assert.Equal(ShipmentState.Partial, dto.Shipped);
        var bundleAmount = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Bundle).Amount!.Value;
        var hatAmount = dto.Lines.Single(l => l.ItemId == f.Hat).Amount!.Value;
        Assert.Equal(Money.Round(bundleAmount / 3 * 2 + hatAmount), dto.ShippedValue);
        Assert.Equal(dto.ShippedValue, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Shipped);
        Assert.Equal(ShipmentState.Partial, (await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Single(o => o.Id == order).Shipped);
        Assert.Equal((1m, 1m, 0.035m), Reserved(await s.Sales.GetOrderAsync(probe), f));

        // УПД: комплект одной строкой по цене заказа, код — код комплекта.
        var upd = await s.Print.UpdAsync(shipment);
        Assert.Equal([("НБ-1", 2m), ("Ш-1", 2m)], upd.Lines.Select(l => (l.Code, l.Quantity)).OrderBy(l => l.Code));
        Assert.Equal(dto.ShippedValue, upd.Total);
        // Журнал счетов-фактур, акт сверки и главный экран считают комплект строкой заказа.
        var issued = (await s.VatInvoices.IssuedAsync(new KnitErp.Application.Taxes.VatJournalFilter(Day, Day))).Single(i => i.DocumentId == shipment);
        Assert.Equal(dto.ShippedValue, issued.AmountWithoutVat + issued.VatAmount);
        var statement = await s.Settlements.BuildAsync(f.Customer, null, Day, Day);
        Assert.Equal(dto.ShippedValue, statement.Rows.Sum(r => r.Debit));
        var home = await s.Dashboard.GetAsync(refresh: true);
        Assert.NotEmpty(home.Tiles);
        // Аудит D352: правило SQL главного экрана «комплект — один раз на документ» — долг покупателя равен отгруженному по заказу.
        Assert.Equal(dto.ShippedValue, home.Finance?.CustomerDebt);

        // Прибыльность: себестоимость комплекта — по фактически ушедшим компонентам (2 × 600 + 2 × 450 + 0,07 × 3000).
        var profit = await s.Analytics.ProfitabilityAsync(new SalesAnalyticsFilter(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)), ProfitGrouping.Item);
        var bundleRow = profit.Rows.Single(r => r.Id == f.Bundle);
        Assert.Equal((2m, 2310m, true, CostSource.Components), (bundleRow.Quantity!.Value, bundleRow.Cost, bundleRow.CostComplete, bundleRow.Source));

        // Остаток — второй отгрузкой; заказ отгружен полностью.
        var second = await s.Sales.CreateShipmentAsync(order);
        Assert.Equal([1m], (await s.Documents.GetAsync(second)).Lines.Select(l => l.BundleQuantity ?? 0).Distinct());
        await s.Documents.PostAsync(second, (await s.Documents.GetAsync(second)).RowVersion);
        Assert.Equal(ShipmentState.Full, (await s.Sales.GetOrderAsync(order)).Shipped);
        Assert.Equal((0m, 0m, 0m), Reserved(await s.Sales.GetOrderAsync(probe), f));

        // Состав изменился после отгрузки: проведённые документы прежние, возврат — по отгруженному составу.
        await s.Bundles.SetComponentAsync(f.Bundle, f.Scarf, 2);
        Assert.Equal(2m, (await s.Documents.GetAsync(shipment)).Lines.Single(l => l.ItemId == f.Scarf).Quantity);
        var ret = await s.Sales.CreateReturnAsync(order);
        Assert.Equal([("Ш-1", 2m, (long?)null, (decimal?)null), ("Ш-1", 3m, f.Bundle, 3m), ("ПР-1", 0.105m, f.Bundle, 3m), ("ШФ-1", 3m, f.Bundle, 3m)],
            Lines(await s.Documents.GetAsync(ret)).OrderBy(l => l.Item3 is null ? 0 : 1).ThenBy(l => l.Item1 == "Ш-1" ? 0 : l.Item1 == "ПР-1" ? 1 : 2));
        await s.Documents.SetBundleAsync(ret, f.Bundle, 1, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.RemoveLineAsync(ret, f.Hat, (await s.Documents.GetAsync(ret)).RowVersion);
        Assert.Equal([("Ш-1", 1m, f.Bundle, 1m), ("ПР-1", 0.035m, f.Bundle, 1m), ("ШФ-1", 1m, f.Bundle, 1m)],
            Lines(await s.Documents.GetAsync(ret)).OrderBy(l => l.Item1 == "Ш-1" ? 0 : l.Item1 == "ПР-1" ? 1 : 2));
        Assert.Equal("sales.return.bundle_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.SetBundleAsync(ret, f.Bundle, 4, (await s.Documents.GetAsync(ret)).RowVersion))).Code);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
        Assert.Equal((16m, 8m, 0.93m, 0m), await BalancesAsync(f));
        var afterReturn = await s.Sales.GetOrderAsync(order);
        Assert.Equal((3m, 1m), (afterReturn.Lines.Single(l => l.ItemId == f.Bundle).Shipped, afterReturn.Lines.Single(l => l.ItemId == f.Bundle).Returned));
        Assert.Equal(ShipmentState.Partial, afterReturn.Shipped);

        // Два черновика возврата на оставшиеся 2 комплекта: второй при проведении — больше отгруженного.
        var r1 = await s.Sales.CreateReturnAsync(order);
        var r2 = await s.Sales.CreateReturnAsync(order);
        await s.Documents.PostAsync(r1, (await s.Documents.GetAsync(r1)).RowVersion);
        Assert.Equal("sales.return.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.PostAsync(r2, (await s.Documents.GetAsync(r2)).RowVersion))).Code);
        // Всё возвращено: компоненты — ровно отгруженное, без остатков округления.
        Assert.Equal((20m, 10m, 1m, 0m), await BalancesAsync(f));
    }

    [SqlFact]
    public async Task Bundle_lines_rules_and_database_guards()
    {
        var f = await SetUpAsync();
        long shipment;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Строку комплекта нельзя добавить в документ без заказа покупателя.
            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host), Day, null));
            Assert.Equal("item.bundle_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.SetBundleAsync(receipt, f.Bundle, 1, (await s.Documents.GetAsync(receipt)).RowVersion))).Code);

            // Комплект без состава не отгружается; комплект не из заказа в отгрузку не добавить.
            var empty = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-2", "Пустой набор", ItemType.Bundle, f.Piece, null, f.Vat));
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, true, null));
            await s.Sales.SetOrderLineAsync(order, empty, 1, 100m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            Assert.Equal("sales.order.bundle_empty", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateShipmentAsync(order))).Code);

            var order2 = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, true, null));
            await s.Sales.SetOrderLineAsync(order2, f.Hat, 1, 100m, 22m, (await s.Sales.GetOrderAsync(order2)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order2, (await s.Sales.GetOrderAsync(order2)).RowVersion);
            shipment = await s.Sales.CreateShipmentAsync(order2);
            Assert.Equal("sales.order.bundle_not_in_order", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.SetBundleAsync(shipment, f.Bundle, 1, (await s.Documents.GetAsync(shipment)).RowVersion))).Code);
            Assert.Equal("catalog.bundle.not_bundle", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.SetBundleAsync(shipment, f.Hat, 1, (await s.Documents.GetAsync(shipment)).RowVersion))).Code);

            // Закупка по-прежнему не принимает комплект.
            var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, f.Supplier, f.Store, null, null, true, null));
            Assert.Equal("purchase.order.bundle", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Purchases.SetOrderLineAsync(po, f.Bundle, 1, 100m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion))).Code);
        }

        // Чужая организация не видит документ.
        var otherOrg = await CreateOrgAsync();
        await using (var s = host.As(otherOrg.OwnerUserId, otherOrg.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Documents.SetBundleAsync(shipment, f.Bundle, 1, []));
        }

        // Ограничения базы: комплект не бывает позицией строки; ссылка — только на комплект; комплект и количество — вместе.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity]) VALUES ({shipment}, {f.Bundle}, 1)"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity], [BundleItemId], [BundleQuantity]) VALUES ({shipment}, {f.Scarf}, 1, {f.Hat}, 1)"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity], [BundleItemId]) VALUES ({shipment}, {f.Scarf}, 1, {f.Bundle})"));
            Assert.Contains("ck_stock_document_lines_bundle", ex.Message);
            // Та же позиция в документе из комплекта — отдельная строка; дважды отдельной — нельзя.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity], [BundleItemId], [BundleQuantity]) VALUES ({shipment}, {f.Hat}, 1, {f.Bundle}, 1)");
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity]) VALUES ({shipment}, {f.Hat}, 1)"));
            Assert.Contains("ux_stock_document_lines_doc_item_bundle", ex.Message);
        }
    }

    private static List<(string, decimal, long?, decimal?)> Lines(KnitErp.Application.Warehousing.StockDocumentDto doc) =>
        doc.Lines.Select(l => (l.Code, l.Quantity, l.BundleItemId, l.BundleQuantity)).ToList();

    /// <summary>Резерв других заказов по шапке, шарфу и пряже — из карточки заказа-пробника.</summary>
    private static (decimal, decimal, decimal) Reserved(SalesOrderDto probe, Fixture f) =>
        (probe.Lines.Single(l => l.ItemId == f.Hat).ReservedByOthers, probe.Lines.Single(l => l.ItemId == f.Scarf).ReservedByOthers,
            probe.Lines.Single(l => l.ItemId == f.Yarn).ReservedByOthers);

    private static async Task<long> ProbeOrderAsync(Services s, Fixture f)
    {
        var probe = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, true, null));
        foreach (var item in new[] { f.Hat, f.Scarf, f.Yarn })
        {
            await s.Sales.SetOrderLineAsync(probe, item, 1, 10m, 22m, (await s.Sales.GetOrderAsync(probe)).RowVersion);
        }

        return probe;
    }

    /// <summary>Остатки шапки, шарфа, пряжи и комплекта на складе.</summary>
    private async Task<(decimal, decimal, decimal, decimal)> BalancesAsync(Fixture f)
    {
        await using var db = host.NewDb();
        var stock = await db.StockMovements.Where(m => m.OrganizationId == f.Org.OrganizationId && m.WarehouseId == f.Store)
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        return (stock.GetValueOrDefault(f.Hat), stock.GetValueOrDefault(f.Scarf), stock.GetValueOrDefault(f.Yarn), stock.GetValueOrDefault(f.Bundle));
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Hat, long Scarf, long Yarn, long Bundle, long Customer, long Supplier, long Piece, long Vat)
    {
        public async Task<long?> ReasonAsync(SqlTestHost host)
        {
            await using var db = host.NewDb();
            return await db.OperationReasons.Where(r => r.OrganizationId == Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
        }
    }

    /// <summary>Набор НБ-1 = шапка + шарф + 0,035 кг пряжи; на складе закуплены шапки 20 × 600, шарфы 10 × 450, пряжа 1 кг × 3000.</summary>
    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var kg = units.Single(u => u.Code == "166").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null, vat));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null, vat));
        var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа на помпон", ItemType.RawMaterial, kg, null, vat));
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор шапка + шарф", ItemType.Bundle, piece, null, vat));
        await s.Bundles.SetComponentAsync(bundle, hat, 1);
        await s.Bundles.SetComponentAsync(bundle, scarf, 1);
        await s.Bundles.SetComponentAsync(bundle, yarn, 0.035m);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));

        var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, store, null, null, false, null));
        foreach (var (item, qty, price) in new[] { (hat, 20m, 600m), (scarf, 10m, 450m), (yarn, 1m, 3000m) })
        {
            await s.Purchases.SetOrderLineAsync(po, item, qty, price, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        }

        await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        var receipt = await s.Purchases.CreateReceiptAsync(po);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        return new Fixture(org, store, hat, scarf, yarn, bundle, customer, supplier, piece, vat);
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
