using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Excel каталога (D308): столбцы предмета расчёта, маркировки, GTIN, минимальной цены, упаковок и лист «Состав комплектов» —
/// выгрузка и загрузка обратно без изменений, правка через файл, ошибки в протоколе, старые файлы. Поставщики позиции (D309).
/// </summary>
public sealed class CatExcelSuppliersTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 934_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Excel_round_trip_trade_packs_and_bundle_sheet()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var box = await s.Catalog.CreateUnitAsync(new UnitCommand("BOX", "Коробка", "кор", 0));
        await s.Packs.SetAsync(f.Hat, box, 12, "4600000000008");
        var trade = await s.Trade.GetAsync(f.Hat);
        await s.Trade.SaveAsync(f.Hat, new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "4601234567893", 900m), trade.RowVersion);
        await s.Bundles.SetComponentAsync(f.Bundle, f.Hat, 1);
        await s.Bundles.SetComponentAsync(f.Bundle, f.Scarf, 2);

        // Выгрузка → загрузка без правок: всё «без изменений», состав комплектов без ошибок.
        var exported = await s.ItemExchange.ExportAsync();
        var plan = await s.ItemExchange.PreviewAsync(new MemoryStream(exported));
        Assert.All(plan.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
        Assert.Equal([("НБ-1", "Ш-1", "1"), ("НБ-1", "ШФ-1", "2")], plan.Bundles.Select(b => (b.BundleCode, b.ComponentCode, b.QuantityText)));
        Assert.Equal(0, plan.ErrorRows);

        // Правка файлом: новая шапка-модификация с маркировкой и упаковкой, состав комплекта меняется.
        IReadOnlyList<string> header = [.. ItemExchangeService.Columns, ItemExchangeService.PaymentSubjectColumn, ItemExchangeService.MarkingColumn,
            ItemExchangeService.GtinColumn, ItemExchangeService.MinPriceColumn, ItemExchangeService.PacksColumn];
        var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
        [
            new SheetData("Номенклатура", header,
            [
                ["Ш-2", "Шапка зимняя", "Готовая продукция", "шт", "", "Товар", "Предметы одежды (лёгкая промышленность)", "4601234567886", "1200", "кор = 10 / 4600000000015"],
                ["УС-1", "Доставка", "Услуги", "усл. ед", "", "Услуга", "нет", "", "", ""],
            ]),
            new SheetData(ItemExchangeService.BundleSheet, ItemExchangeService.BundleColumns, [["НБ-1", "Ш-2", "1"], ["НБ-1", "ШФ-1", "1"]]),
        ]));
        plan = await s.ItemExchange.PreviewAsync(file);
        Assert.Equal(0, plan.ErrorRows);
        var result = await s.ItemExchange.ApplyAsync(plan.Rows, plan.Bundles);
        Assert.Equal((2, 1), (result.Created, result.Bundles)); // шапка Ш-2 и услуга УС-1
        var hat2 = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "Ш-2"))).Items.Single().Id;
        var hat2Trade = (await s.Trade.GetAsync(hat2)).Trade;
        Assert.Equal((true, TrackingType.Clothes, "04601234567886", 1200m), (hat2Trade.IsMarked, hat2Trade.TrackingType, hat2Trade.Gtin, hat2Trade.MinPrice!.Value));
        Assert.Equal(("кор", 10m, "4600000000015"), (await s.Packs.ListAsync(hat2)).Single() is var p ? (p.UnitSymbol, p.Factor, p.Barcode) : default);
        Assert.Equal([("Ш-2", 1m), ("ШФ-1", 1m)], (await s.Bundles.GetAsync(f.Bundle)).Components.Select(c => (c.Code, c.Quantity)));

        // Ошибки — в протоколе, ничего не применяется.
        file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
        [
            new SheetData("Номенклатура", header,
            [
                ["Ш-3", "Шапка летняя", "Готовая продукция", "шт", "", "Товар", "Кружево", "4601234567886", "", "кор = 1,5"],
                ["Ш-4", "Шапка детская", "Готовая продукция", "шт", "", "Товар", "нет", "", "", "кор = 6 / 4600000000008"],
            ]),
            new SheetData(ItemExchangeService.BundleSheet, ItemExchangeService.BundleColumns, [["НБ-1", "УС-1", "1"], ["Ш-1", "ШФ-1", "1"]]),
        ]));
        plan = await s.ItemExchange.PreviewAsync(file);
        var bad = plan.Rows.Single(r => r.Code == "Ш-3").Errors;
        Assert.Contains(bad, e => e.Contains("Маркировка «Кружево»"));
        Assert.Contains(bad, e => e.Contains("GTIN 04601234567886 уже у другой позиции"));
        Assert.Contains(bad, e => e.Contains("целое число"));
        Assert.Contains(plan.Rows.Single(r => r.Code == "Ш-4").Errors, e => e.Contains("4600000000008 уже занят"));
        Assert.All(plan.Bundles, b => Assert.NotEmpty(b.Errors));
        Assert.False(plan.CanApply);
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.ItemExchange.ApplyAsync(plan.Rows, plan.Bundles));

        // Старый файл — только пять обязательных столбцов: торговые поля, упаковки и составы не меняются.
        plan = await s.ItemExchange.PreviewAsync(new MemoryStream(SqlTestHost.Spreadsheet.Write(
            [new SheetData("Номенклатура", ItemExchangeService.Columns, [["Ш-2", "Шапка зимняя тёплая", "Готовая продукция", "шт", ""]])])));
        Assert.Empty(plan.Bundles);
        await s.ItemExchange.ApplyAsync(plan.Rows);
        Assert.Equal("04601234567886", (await s.Trade.GetAsync(hat2)).Trade.Gtin);
        Assert.Single(await s.Packs.ListAsync(hat2));
        Assert.Equal(2, (await s.Bundles.GetAsync(f.Bundle)).Components.Count);
    }

    [SqlFact]
    public async Task Item_suppliers_main_and_last_purchase_price()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var yarnCo = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
            var other = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Нитки»", null, null, true, false, null));
            var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));

            await s.Suppliers.SetAsync(f.Hat, yarnCo, "H-77", "Шапка вязаная арт. 77", false);
            Assert.True((await s.Suppliers.ListAsync(f.Hat)).Single().IsMain); // первый — основной
            await s.Suppliers.SetAsync(f.Hat, other, null, null, true);
            var list = await s.Suppliers.ListAsync(f.Hat);
            Assert.Equal([("ООО «Нитки»", true), ("ООО «Пряжа»", false)], list.Select(x => (x.Supplier, x.IsMain)));
            await s.Suppliers.SetAsync(f.Hat, yarnCo, "H-78", null, true);
            Assert.Equal(("ООО «Пряжа»", "H-78"), (await s.Suppliers.MainAsync(f.Hat)) is { } m ? (m.Supplier, m.SupplierArticle) : default);

            Assert.Equal("catalog.supplier.not_supplier", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Suppliers.SetAsync(f.Hat, customer, null, null, false))).Code);
            Assert.Equal("catalog.supplier.bundle", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Suppliers.SetAsync(f.Bundle, yarnCo, null, null, false))).Code);

            // Последняя цена — из подтверждённого заказа поставщику: 10 шт по 600 без НДС.
            var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, yarnCo, f.Store, null, null, false, null));
            await s.Purchases.SetOrderLineAsync(po, f.Hat, 10, 600m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            Assert.Null((await s.Suppliers.MainAsync(f.Hat))!.LastPrice); // черновик не считается
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            Assert.Equal((600m, Day), (await s.Suppliers.MainAsync(f.Hat)) is { LastPrice: { } lp, LastOrderDate: { } d } ? (lp, d) : default);

            var row = (await s.Suppliers.ListAsync(f.Hat)).Single(x => x.SupplierId == other);
            await s.Suppliers.RemoveAsync(row.Id, row.RowVersion);
            Assert.Single(await s.Suppliers.ListAsync(f.Hat));
        }

        // Без права «Цены и суммы» последняя цена скрыта; без права изменения — отказ; чужая организация — 404.
        await using (var s = host.As(f.Accountant, f.Org.OrganizationId))
        {
            Assert.Single(await s.Suppliers.ListAsync(f.Hat));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Suppliers.SupplierOptionsAsync());
        }

        var otherOrg = await CreateOrgAsync();
        await using (var s = host.As(otherOrg.OwnerUserId, otherOrg.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Suppliers.ListAsync(f.Hat));
        }

        await using (var db = host.NewDb())
        {
            // Один основной поставщик у позиции — и в базе.
            Assert.Equal(1, await db.ItemSuppliers.CountAsync(x => x.ItemId == f.Hat && x.IsMain));
        }
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Hat, long Scarf, long Bundle, long Accountant);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null, vat));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null, vat));
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null, vat));
        var accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер",
            SystemRoles.Accountant, null))).UserId;
        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, hat, scarf, bundle, accountant);
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
