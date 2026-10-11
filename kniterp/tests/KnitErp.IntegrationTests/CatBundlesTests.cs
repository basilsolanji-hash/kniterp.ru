using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
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
/// Комплекты (D302): состав, доступность по компонентам за вычетом резерва, подсказка цены и себестоимость, права, ограничения базы,
/// запрет складских документов и отгрузки комплекта до доработки сессии № 1 (запрос Р2).
/// </summary>
public sealed class CatBundlesTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 931_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Composition_availability_price_and_cost()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Закупка: шапки 10 × 600, шарфы 4 × 450, пряжа 0,2 кг × 3 000 — средняя себестоимость компонентов.
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
        var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, f.Store, null, null, false, null));
        foreach (var (item, qty, price) in new[] { (f.Hat, 10m, 600m), (f.Scarf, 4m, 450m), (f.Yarn, 0.2m, 3000m) })
        {
            await s.Purchases.SetOrderLineAsync(po, item, qty, price, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        }

        await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        var receipt = await s.Purchases.CreateReceiptAsync(po);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

        await s.Bundles.SetComponentAsync(f.Bundle, f.Hat, 1);
        await s.Bundles.SetComponentAsync(f.Bundle, f.Scarf, 2);
        await s.Bundles.SetComponentAsync(f.Bundle, f.Scarf, 1); // повтор — изменение количества
        await s.Bundles.SetComponentAsync(f.Bundle, f.Yarn, 0.035m);
        var dto = await s.Bundles.GetAsync(f.Bundle);
        Assert.Equal([("Ш-1", 1m), ("ШФ-1", 1m), ("ПР-1", 0.035m)], dto.Components.Select(c => (c.Code, c.Quantity)));
        // min(10 / 1, 4 / 1, ⌊0,2 / 0,035⌋ = 5) = 4.
        Assert.Equal(4m, dto.TotalAvailable);
        Assert.Equal(4m, dto.Availability.Single(a => a.WarehouseId == f.Store).Available);
        Assert.Equal(1050m + 0.035m * 3000m, dto.Cost);
        Assert.Equal((600m, 450m), (dto.Components[0].UnitCost!.Value, dto.Components[1].UnitCost!.Value));

        // Резерв подтверждённого заказа на шарф уменьшает доступность набора.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(order, f.Scarf, 1, 900m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderReserveAsync(order, true, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Equal(3m, (await s.Bundles.GetAsync(f.Bundle)).TotalAvailable);
        Assert.Equal(3m, await s.Bundles.AvailableAsync(f.Bundle, f.Store));

        // Цена: своя у комплекта; подсказка — сумма цен компонентов и скидка набора.
        var priceType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        await s.Nomenclature.SetPricesAsync(f.Hat, new Dictionary<long, decimal?> { [priceType] = 1200 });
        await s.Nomenclature.SetPricesAsync(f.Scarf, new Dictionary<long, decimal?> { [priceType] = 900 });
        await s.Nomenclature.SetPricesAsync(f.Yarn, new Dictionary<long, decimal?> { [priceType] = 4000 });
        await s.Nomenclature.SetPricesAsync(f.Bundle, new Dictionary<long, decimal?> { [priceType] = 2000 });
        var hint = (await s.Bundles.GetAsync(f.Bundle)).Price!;
        Assert.Equal((2240m, 240m), (hint.ComponentsTotal!.Value, hint.Discount!.Value));

        // Порядок и удаление строки; аудит изменений состава.
        var yarnRow = dto.Components.Single(c => c.ItemId == f.Yarn);
        await s.Bundles.MoveComponentAsync(yarnRow.Id, -1);
        Assert.Equal(["Ш-1", "ПР-1", "ШФ-1"], (await s.Bundles.GetAsync(f.Bundle)).Components.Select(c => c.Code));
        await s.Bundles.RemoveComponentAsync(yarnRow.Id, (await s.Bundles.GetAsync(f.Bundle)).Components.Single(c => c.ItemId == f.Yarn).RowVersion);
        Assert.Equal(2, (await s.Bundles.GetAsync(f.Bundle)).Components.Count);
        await using (var db = host.NewDb())
        {
            Assert.True(await db.AuditEntries.CountAsync(a => a.OrganizationId == f.Org.OrganizationId && a.EntityId == f.Bundle.ToString()
                                                              && a.Reason!.StartsWith("НБ-1")) >= 4);
        }

        Assert.Equal([f.Bundle], (await s.Catalog.ListItemsAsync(new ItemFilter(Kind: ItemKind.Bundle))).Items.Select(i => i.Id));
    }

    [SqlFact]
    public async Task Composition_rules_rights_and_database_guards()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal("catalog.bundle.service", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bundles.SetComponentAsync(f.Bundle, f.Service, 1))).Code);
            Assert.Equal("catalog.bundle.self", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bundles.SetComponentAsync(f.Bundle, f.Bundle, 1))).Code);
            var other = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-2", "Другой набор", ItemType.Bundle, f.Piece, null, f.Vat));
            Assert.Equal("catalog.bundle.nested", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bundles.SetComponentAsync(f.Bundle, other, 1))).Code);
            Assert.Equal("catalog.bundle.not_bundle", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bundles.SetComponentAsync(f.Hat, f.Scarf, 1))).Code);
            Assert.Equal("catalog.bundle.precision", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bundles.SetComponentAsync(f.Bundle, f.Hat, 1.5m))).Code);
            Assert.DoesNotContain(f.Service, (await s.Bundles.ComponentOptionsAsync()).Select(o => o.Id));
            Assert.DoesNotContain(f.Bundle, (await s.Bundles.ComponentOptionsAsync()).Select(o => o.Id));
            await s.Bundles.SetComponentAsync(f.Bundle, f.Hat, 1);

            // Смена типа: комплект с составом и компонент комплекта.
            var bundleRow = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "НБ-1"))).Items.Single();
            Assert.Equal("catalog.bundle.has_components", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Catalog.UpdateItemAsync(f.Bundle, new ItemCommand("НБ-1", "Набор", ItemType.Finished, f.Piece, null, f.Vat), bundleRow.RowVersion))).Code);
            var hatRow = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "Ш-1"))).Items.Single(i => i.Id == f.Hat);
            Assert.Equal("catalog.bundle.component_in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Catalog.UpdateItemAsync(f.Hat, new ItemCommand("Ш-1", "Шапка", ItemType.Bundle, f.Piece, null, f.Vat), hatRow.RowVersion))).Code);

            // Комплект не проводится складом.
            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host), Day, null));
            Assert.Equal("item.bundle_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.SetLineAsync(receipt, f.Bundle, 1, (await s.Documents.GetAsync(receipt)).RowVersion))).Code);
            Assert.DoesNotContain(f.Bundle, (await s.Documents.ListItemsForEntryAsync()).Select(i => i.Id));

            // Заказ покупателя: комплект — одной строкой; счёт — одной строкой по цене комплекта; отгрузка — запрос Р2.
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
            await s.Sales.SetOrderLineAsync(order, f.Bundle, 2, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            var line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
            Assert.Equal((true, false, false), (line.IsBundle, line.Stocked, line.Short));
            var invoice = await s.Invoices.CreateAsync(order, null, null);
            Assert.Equal(("НБ-1", 2m, 2000m), (await s.Invoices.GetAsync(invoice)).Lines.Select(l => (l.Code, l.Quantity, l.Price)).Single());
            Assert.Equal("sales.order.bundle_shipment", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateShipmentAsync(order))).Code);
        }

        // Право: Бухгалтер видит состав, но не меняет его; чужая организация — 404.
        await using (var s = host.As(f.Accountant, f.Org.OrganizationId))
        {
            Assert.Single((await s.Bundles.GetAsync(f.Bundle)).Components);
            Assert.False((await s.Bundles.GetAsync(f.Bundle)).CanEdit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Bundles.SetComponentAsync(f.Bundle, f.Scarf, 1));
        }

        var otherOrg = await CreateOrgAsync();
        await using (var s = host.As(otherOrg.OwnerUserId, otherOrg.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Bundles.GetAsync(f.Bundle));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Bundles.SetComponentAsync(f.Bundle, f.Hat, 1));
        }

        // Ограничения базы: состав только у комплекта, компонент — не услуга; пара уникальна.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[bundle_components] ([OrganizationId], [BundleItemId], [ComponentItemId], [Quantity], [SortOrder]) VALUES ({f.Org.OrganizationId}, {f.Bundle}, {f.Service}, 1, 5)"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[bundle_components] ([OrganizationId], [BundleItemId], [ComponentItemId], [Quantity], [SortOrder]) VALUES ({f.Org.OrganizationId}, {f.Hat}, {f.Scarf}, 1, 5)"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[bundle_components] ([OrganizationId], [BundleItemId], [ComponentItemId], [Quantity], [SortOrder]) VALUES ({f.Org.OrganizationId}, {f.Bundle}, {f.Hat}, 1, 5)"));
            Assert.Contains("ux_bundle_components_pair", ex.Message);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[items] SET [Type] = 4 WHERE [Id] = {f.Bundle}"));
            Assert.Equal(51300, ex.Number);
        }
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Hat, long Scarf, long Yarn, long Bundle, long Service, long Customer, long Piece, long Vat, long Accountant)
    {
        public async Task<long?> ReasonAsync(SqlTestHost host)
        {
            await using var db = host.NewDb();
            return await db.OperationReasons.Where(r => r.OrganizationId == Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
        }
    }

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
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер",
            SystemRoles.Accountant, null))).UserId;
        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, hat, scarf, yarn, bundle, service, customer, piece, vat, accountant);
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
