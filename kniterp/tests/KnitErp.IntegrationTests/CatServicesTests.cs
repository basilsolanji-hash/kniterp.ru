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
/// Услуги (D300): продаются и покупаются, но не хранятся на складе. Правила карточки, запрет складских документов
/// (сервис и триггеры базы), заказы и счёт принимают услугу, отгрузка и поступление по заказу её пропускают.
/// </summary>
public sealed class CatServicesTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 930_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Service_card_rules_vat_units_barcodes_and_kind_filter()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        Assert.Contains(units, u => u.Code == "876" && u.Symbol == "усл. ед" && u.Precision == 0);
        Assert.Contains(units, u => u.Code == "356" && u.Symbol == "ч");

        // Ставка НДС обязательна — и при создании, и при изменении.
        Assert.Equal("catalog.service.vat_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.CreateItemAsync(new ItemCommand("УС-2", "Пошив по образцу", ItemType.Service, f.ServiceUnit, null)))).Code);
        var item = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "УС-1"))).Items.Single();
        Assert.Equal("catalog.service.vat_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(f.Service, new ItemCommand("УС-1", "Доставка", ItemType.Service, f.ServiceUnit, null), item.RowVersion))).Code);

        // Нет веса, объёма, неснижаемого остатка; нет GTIN и модификаций.
        var card = await s.Nomenclature.GetItemAsync(f.Service);
        Assert.False(card.IsStocked);
        Assert.Equal(PaymentSubject.Service, ItemTypes.DefaultPaymentSubject(card.Type));
        Assert.Equal("catalog.service.no_stock_fields", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SaveItemDetailsAsync(f.Service, card.Details with { WeightKg = 1.5m }, new Dictionary<long, string?>(), card.RowVersion))).Code);
        await s.Nomenclature.SaveItemDetailsAsync(f.Service, card.Details with { Article = "DLV" }, new Dictionary<long, string?>(), card.RowVersion);
        Assert.Equal("catalog.service.gtin", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.AddBarcodeAsync(f.Service, "04600000000015"))).Code);
        await s.Nomenclature.AddBarcodeAsync(f.Service, "4600000000008");
        var urgency = await s.Modifications.CreateCharacteristicAsync("Срочность");
        Assert.Equal("catalog.variant.not_goods", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Modifications.CreateModificationsAsync(f.Service, new Dictionary<long, string?> { [urgency] = "обычная, срочная" }))).Code);

        // Отбор «Товары / Услуги».
        Assert.Equal([f.Service], (await s.Catalog.ListItemsAsync(new ItemFilter(Kind: ItemKind.Service))).Items.Select(i => i.Id));
        Assert.DoesNotContain(f.Service, (await s.Catalog.ListItemsAsync(new ItemFilter(Kind: ItemKind.Goods))).Items.Select(i => i.Id));
        Assert.Contains(f.Sweater, (await s.Catalog.ListItemsAsync(new ItemFilter(Kind: ItemKind.Goods))).Items.Select(i => i.Id));

        // Товар без складской истории можно перевести в услуги: вес и неснижаемый остаток очищаются.
        var fresh = await s.Catalog.CreateItemAsync(new ItemCommand("УП-1", "Упаковка подарочная", ItemType.Packaging, f.Piece, null, f.Vat));
        var freshCard = await s.Nomenclature.GetItemAsync(fresh);
        await s.Nomenclature.SaveItemDetailsAsync(fresh, freshCard.Details with { WeightKg = 0.2m, MinStock = 5 }, new Dictionary<long, string?>(),
            freshCard.RowVersion);
        var freshRow = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "УП-1"))).Items.Single();
        await s.Catalog.UpdateItemAsync(fresh, new ItemCommand("УП-1", "Упаковка подарочная", ItemType.Service, f.ServiceUnit, null, f.Vat),
            freshRow.RowVersion);
        freshCard = await s.Nomenclature.GetItemAsync(fresh);
        Assert.Equal((ItemType.Service, (decimal?)null, (decimal?)null), (freshCard.Type, freshCard.Details.WeightKg, freshCard.Details.MinStock));
    }

    [SqlFact]
    public async Task Service_is_never_stocked_service_and_database_guard()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Выбор строк складских документов — без услуг.
            Assert.DoesNotContain(f.Service, (await s.Documents.ListItemsForEntryAsync()).Select(i => i.Id));
            Assert.DoesNotContain(f.Service, (await s.Inventory.ListItemsForEntryAsync()).Select(i => i.Id));

            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host, StockOperationKind.Receipt), Day, null));
            Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.SetLineAsync(receipt, f.Service, 1, (await s.Documents.GetAsync(receipt)).RowVersion))).Code);
            Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.CreateWithLinesAsync(StockOperationKind.WriteOff,
                    new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host, StockOperationKind.WriteOff), Day, "списание"), [(f.Service, 1m)]))).Code);

            var count = await s.Inventory.CreateAsync(f.Store, Day, null);
            Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Inventory.SetCountedAsync(count, f.Service, 1, (await s.Inventory.GetAsync(count)).RowVersion))).Code);

            Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Batches.CreateAsync(f.Service, new StockBatchData("П-1", null, null, null, null, null)))).Code);

            // Загрузка строк из Excel: ошибка в строке услуги, ничего не загружено.
            var import = await s.Documents.ImportLinesAsync(receipt, new MemoryStream(SqlTestHost.Spreadsheet.Write(
                [new SheetData("Строки", ["Код", "Количество"], [["УС-1", "1"], ["СВ-1", "2"]])])), (await s.Documents.GetAsync(receipt)).RowVersion);
            Assert.False(import.Applied);
            Assert.Contains("услуга", Assert.Single(import.Rows, r => r.Errors.Count > 0).Errors.Single());

            // Позицию со складской историей нельзя сделать услугой.
            await s.Documents.SetLineAsync(receipt, f.Sweater, 10, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            var sweater = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-1"))).Items.Single();
            Assert.Equal("catalog.service.has_stock", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Catalog.UpdateItemAsync(f.Sweater, new ItemCommand("СВ-1", "Свитер", ItemType.Service, f.Piece, null, f.Vat), sweater.RowVersion))).Code);
        }

        // Начальные остатки вводит Старший кладовщик (D05).
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.DoesNotContain(f.Service, (await s.OpeningBalances.ListItemsForEntryAsync()).Select(i => i.Id));
            var opening = await s.OpeningBalances.CreateAsync(f.Store, Day, null);
            Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.OpeningBalances.SetLineAsync(opening, f.Service, 3, (await s.OpeningBalances.GetAsync(opening)).RowVersion))).Code);
        }

        // Триггеры базы — защита в обход приложения.
        await using (var db = host.NewDb())
        {
            db.StockMovements.Add(StockMovement.Create(f.Org.OrganizationId, f.Store, f.Service, 1, Day, StockSource.StockDocument, 1, host.Clock.UtcNow));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(51300, Assert.IsType<SqlException>(ex.InnerException).Number);
        }

        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[items] SET [Type] = 10, [VatRateId] = {f.Vat} WHERE [Id] = {f.Sweater}"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[items] SET [VatRateId] = NULL WHERE [Id] = {f.Service}"));
            Assert.Contains("ck_items_service", ex.Message);
        }
    }

    [SqlFact]
    public async Task Orders_and_invoice_accept_services_shipment_and_receipt_skip_them()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
            new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host, StockOperationKind.Receipt), Day, null), [(f.Sweater, 5m)]);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

        // Заказ покупателя: свитер и доставка. Резерв держит только свитер; у услуги нет «Доступно» и нехватки.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, 2, 3000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderLineAsync(order, f.Service, 1, 500m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderReserveAsync(order, true, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var dto = await s.Sales.GetOrderAsync(order);
        var serviceLine = dto.Lines.Single(l => l.ItemId == f.Service);
        Assert.False(serviceLine.Stocked);
        Assert.False(serviceLine.Short);
        Assert.True(dto.Lines.Single(l => l.ItemId == f.Sweater).Stocked);

        var other = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(other, f.Sweater, 1, 3000m, 22m, (await s.Sales.GetOrderAsync(other)).RowVersion);
        await s.Sales.SetOrderLineAsync(other, f.Service, 1, 500m, 22m, (await s.Sales.GetOrderAsync(other)).RowVersion);
        var otherDto = await s.Sales.GetOrderAsync(other);
        Assert.Equal(2m, otherDto.Lines.Single(l => l.ItemId == f.Sweater).ReservedByOthers);
        Assert.Equal(0m, otherDto.Lines.Single(l => l.ItemId == f.Service).ReservedByOthers);

        // Счёт — со строкой услуги; отгрузка по заказу — без неё.
        var invoice = await s.Invoices.CreateAsync(order, null, null);
        Assert.Contains("УС-1", (await s.Invoices.GetAsync(invoice)).Lines.Select(l => l.Code));
        var ship = await s.Sales.CreateShipmentAsync(order);
        Assert.Equal([f.Sweater], (await s.Documents.GetAsync(ship)).Lines.Select(l => l.ItemId));

        // Заказ только из услуг: отгружать со склада нечего.
        var servicesOnly = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(servicesOnly, f.Service, 2, 500m, 22m, (await s.Sales.GetOrderAsync(servicesOnly)).RowVersion);
        await s.Sales.ConfirmOrderAsync(servicesOnly, (await s.Sales.GetOrderAsync(servicesOnly)).RowVersion);
        Assert.Equal("sales.order.services_only", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateShipmentAsync(servicesOnly))).Code);

        // Заказ поставщику: пряжа и доработка (услуга) — поступление только пряжи.
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Поставщик»", null, null, true, false, null));
        var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, f.Store, null, null, true, null));
        await s.Purchases.SetOrderLineAsync(po, f.Sweater, 3, 1000m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        await s.Purchases.SetOrderLineAsync(po, f.Service, 1, 200m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        var poReceipt = await s.Purchases.CreateReceiptAsync(po);
        Assert.Equal([f.Sweater], (await s.Documents.GetAsync(poReceipt)).Lines.Select(l => l.ItemId));
    }

    [SqlFact]
    public async Task Excel_import_creates_service_with_default_vat()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var plan = await s.ItemExchange.PreviewAsync(new MemoryStream(SqlTestHost.Spreadsheet.Write(
        [
            new SheetData("Номенклатура", ItemExchangeService.Columns, [["УС-9", "Ремонт оборудования", "Услуги", "ч", ""]]),
        ])));
        Assert.Equal(ImportAction.Create, plan.Rows.Single().Action);
        await s.ItemExchange.ApplyAsync(plan.Rows);
        var created = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "УС-9"))).Items.Single();
        Assert.Equal((ItemType.Service, f.Vat), (created.Type, created.VatRateId));

        // Товар со складской историей файлом в услугу не превращается.
        var receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
            new StockDocumentHeader(f.Store, null, null, await f.ReasonAsync(host, StockOperationKind.Receipt), Day, null), [(f.Sweater, 1m)]);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        plan = await s.ItemExchange.PreviewAsync(new MemoryStream(SqlTestHost.Spreadsheet.Write(
        [
            new SheetData("Номенклатура", ItemExchangeService.Columns, [["СВ-1", "Свитер", "Услуги", "шт", ""]]),
        ])));
        Assert.Equal(ImportAction.Error, plan.Rows.Single().Action);
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Sweater, long Service, long Customer, long Piece, long ServiceUnit, long Vat, long Senior)
    {
        public async Task<long?> ReasonAsync(SqlTestHost host, StockOperationKind kind)
        {
            await using var db = host.NewDb();
            return await db.OperationReasons.Where(r => r.OrganizationId == Org.OrganizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstAsync();
        }
    }

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var serviceUnit = units.Single(u => u.Code == ServiceRules.ServiceUnitCode).Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, vat));
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, serviceUnit, null, vat));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
            SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == senior)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, service, customer, piece, serviceUnit, vat, senior);
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
