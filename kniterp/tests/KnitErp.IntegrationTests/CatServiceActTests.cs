using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D354 (сессия 4, Р1): акт оказания услуг по заказу покупателя и акт услуг поставщика. Строки-услуги закрываются актом без
/// движений склада; акт входит в «отгружено» («получено»), суммы, долг, статус заказа, расчёты, журнал счетов-фактур, главный
/// экран, аналитику и выгрузку в 1С; УПД по отгрузке и акту одной датой — один документ, у услуги «код вида товара» — «--».
/// </summary>
public sealed class CatServiceActTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 961_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Service_act_closes_service_line_of_sales_order()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Заказ «Свитер + Доставка»: 2 × 3000 и 1 × 500, НДС 22 % сверху.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, 2, 3000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderLineAsync(order, f.Delivery, 1, 500m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var total = (await s.Sales.GetOrderAsync(order)).Total!.Value;
        var deliveryAmount = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Delivery).Amount!.Value;

        // Отгрузка — только свитер; заказ после неё «частично отгружен».
        var shipment = await s.Sales.CreateShipmentAsync(order);
        Assert.Equal([f.Sweater], (await s.Documents.GetAsync(shipment)).Lines.Select(l => l.ItemId));
        await s.Documents.PostAsync(shipment, (await s.Documents.GetAsync(shipment)).RowVersion);
        Assert.Equal(ShipmentState.Partial, (await s.Sales.GetOrderAsync(order)).Shipped);

        // Отгрузка по-прежнему не принимает услугу.
        Assert.Equal("item.service_not_stocked", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.CreateWithLinesAsync(StockOperationKind.Shipment,
                new StockDocumentHeader(f.Store, null, f.Customer, null, Day, null, SalesOrderId: order), [(f.Delivery, 1m)]))).Code);

        // Акт на услуги: строка доставки на незакрытое количество; номер — из общего счётчика с отгрузками.
        var act = await s.Sales.CreateServiceActAsync(order);
        var draft = await s.Documents.GetAsync(act);
        Assert.Equal((StockOperationKind.ServiceAct, StockDocumentStatus.Draft), (draft.Kind, draft.Status));
        Assert.Equal([(f.Delivery, 1m)], draft.Lines.Select(l => (l.ItemId, l.Quantity)));
        Assert.StartsWith("АУ-", draft.Number);
        Assert.NotEqual(KnitErp.Application.Printing.PrintService.VatInvoiceNumber((await s.Documents.GetAsync(shipment)).Number),
            KnitErp.Application.Printing.PrintService.VatInvoiceNumber(draft.Number));
        Assert.Contains(f.Delivery, (await s.Documents.ListItemsForEntryAsync(StockOperationKind.ServiceAct)).Select(i => i.Id));
        Assert.DoesNotContain(f.Sweater, (await s.Documents.ListItemsForEntryAsync(StockOperationKind.ServiceAct)).Select(i => i.Id));

        // В акте — только услуги заказа и не больше заказанного.
        Assert.Equal("stock.service_act.goods", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.SetLineAsync(act, f.Sweater, 1, (await s.Documents.GetAsync(act)).RowVersion))).Code);
        Assert.Equal("stock.service_act.not_in_order", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.SetLineAsync(act, f.Packing, 1, (await s.Documents.GetAsync(act)).RowVersion))).Code);
        Assert.Equal("stock.service_act.order_required", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.CreateAsync(StockOperationKind.ServiceAct, new StockDocumentHeader(f.Store, null, f.Customer, null, Day, null)))).Code);
        await s.Documents.SetLineAsync(act, f.Delivery, 2, (await s.Documents.GetAsync(act)).RowVersion);
        Assert.Equal("stock.service_act.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.PostAsync(act, (await s.Documents.GetAsync(act)).RowVersion))).Code);
        await s.Documents.SetLineAsync(act, f.Delivery, 1, (await s.Documents.GetAsync(act)).RowVersion);
        await s.Documents.PostAsync(act, (await s.Documents.GetAsync(act)).RowVersion);

        // Движений склада у акта нет; свитер ушёл отгрузкой.
        await using (var db = host.NewDb())
        {
            Assert.False(await db.StockMovements.AnyAsync(m => m.SourceId == act
                && (m.Source == StockSource.StockDocument || m.Source == StockSource.StockDocumentReversal)));
            Assert.False(await db.StockMovements.AnyAsync(m => m.ItemId == f.Delivery));
            Assert.Equal(3m, await db.StockMovements.Where(m => m.ItemId == f.Sweater).SumAsync(m => m.Quantity));
        }

        // Заказ отгружен полностью: отгруженная стоимость и долг — с доставкой.
        var dto = await s.Sales.GetOrderAsync(order);
        Assert.Equal(ShipmentState.Full, dto.Shipped);
        Assert.Equal((1m, 0m), (dto.Lines.Single(l => l.ItemId == f.Delivery).Shipped, dto.Lines.Single(l => l.ItemId == f.Delivery).Left));
        Assert.Equal((total, total), (dto.ShippedValue!.Value, dto.Debt!.Value));
        Assert.Contains(dto.Documents, d => d.Id == act && d.Kind == StockOperationKind.ServiceAct);
        var row = (await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Single(o => o.Id == order);
        Assert.Equal((ShipmentState.Full, total), (row.Shipped, row.ShippedValue!.Value));
        Assert.Empty(await s.Sales.ListOrdersAsync(new SalesOrderFilter(OverdueOn: Day.AddDays(1))));
        Assert.Equal(total, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Shipped);
        Assert.Equal("sales.order.services_closed", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateServiceActAsync(order))).Code);

        // УПД: отгрузка и акт одной датой — один документ; у услуги «код вида товара» — «--». По акту печатается тот же УПД.
        var upd = await s.Print.UpdAsync(shipment);
        Assert.Equal([("СВ-1", 2m), ("УС-1", 1m)], upd.Lines.Select(l => (l.Code, l.Quantity)));
        Assert.Equal("--", upd.Lines.Single(l => l.Code == "УС-1").TnVedCode);
        Assert.Equal("6110201000", upd.Lines.Single(l => l.Code == "СВ-1").TnVedCode);
        Assert.Equal(total, upd.Total);
        Assert.Contains(draft.Number, upd.ShipmentNumber);
        var byAct = await s.Print.UpdAsync(act);
        Assert.Equal((upd.Number, upd.Total), (byAct.Number, byAct.Total));
        Assert.Contains(byAct.Warnings, w => w.Contains(draft.Number));

        // Журнал выданных счетов-фактур: один счёт-фактура на отгрузку с доставкой.
        var issued = await s.VatInvoices.IssuedAsync(new KnitErp.Application.Taxes.VatJournalFilter(Day, Day));
        Assert.Equal([shipment], issued.Select(i => i.DocumentId));
        Assert.Equal(total, issued.Single().Amount);

        // Акт сверки: акт — в дебет.
        var statement = await s.Settlements.BuildAsync(f.Customer, null, Day, Day);
        Assert.Equal(total, statement.Rows.Sum(r => r.Debit));
        Assert.Contains(statement.Rows, r => r.Document.StartsWith("Акт оказания услуг") && r.Debit == deliveryAmount);

        // Главный экран: долг покупателя с доставкой, заказ не ждёт отгрузки.
        var finance = (await s.Dashboard.GetAsync(refresh: true)).Finance!;
        Assert.Equal((total, 0), (finance.CustomerDebt!.Value, finance.OrdersToShip!.Value));

        // Реализация и прибыльность: доставка — выручкой, себестоимость услуги 0.
        var period = new SalesAnalyticsFilter(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var sold = (await s.Analytics.SalesByItemAsync(period)).Single(r => r.ItemId == f.Delivery);
        Assert.Equal((1m, deliveryAmount), (sold.Quantity, sold.RevenueWithVat));
        var profit = (await s.Analytics.ProfitabilityAsync(period, ProfitGrouping.Item)).Rows.Single(r => r.Id == f.Delivery);
        Assert.Equal((0m, true, CostSource.Service), (profit.Cost, profit.CostComplete, profit.Source));

        // Выгрузка в 1С: акт — реализация с табличной частью «Услуги».
        var file = await s.Exchange.ExportAsync(Day, Day, null);
        Assert.Equal(2, file.Summary.Documents["Документ.РеализацияТоваровУслуг"]);
        Assert.Contains("Услуги>", Encoding.UTF8.GetString(file.Xml));

        // Сторно акта: услуга снова не закрыта, суммы — без доставки; закрывается новым актом.
        await s.Documents.ReverseAsync(act, "ошибка в акте", (await s.Documents.GetAsync(act)).RowVersion);
        dto = await s.Sales.GetOrderAsync(order);
        Assert.Equal((ShipmentState.Partial, total - deliveryAmount, 1m),
            (dto.Shipped, dto.ShippedValue!.Value, dto.Lines.Single(l => l.ItemId == f.Delivery).Left));
        Assert.Equal(total - deliveryAmount, (await s.Print.UpdAsync(shipment)).Total);
        Assert.Equal(total - deliveryAmount, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Shipped);

        // Отменённый черновик ничего не закрывает.
        var cancelled = await s.Sales.CreateServiceActAsync(order);
        await s.Documents.CancelAsync(cancelled, (await s.Documents.GetAsync(cancelled)).RowVersion);
        Assert.Equal(ShipmentState.Partial, (await s.Sales.GetOrderAsync(order)).Shipped);

        var again = await s.Sales.CreateServiceActAsync(order);
        await s.Documents.PostAsync(again, (await s.Documents.GetAsync(again)).RowVersion);
        Assert.Equal(ShipmentState.Full, (await s.Sales.GetOrderAsync(order)).Shipped);

        // Новый акт той же датой снова входит в УПД отгрузки.
        Assert.Equal([("СВ-1", 2m), ("УС-1", 1m)], (await s.Print.UpdAsync(shipment)).Lines.Select(l => (l.Code, l.Quantity)));
    }

    [SqlFact]
    public async Task Service_act_alone_prints_upd_and_database_guards()
    {
        var f = await SetUpAsync();
        long act;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Заказ только из услуг: отгрузки нет, закрывается актом; УПД — по акту, номер — цифры номера акта.
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, true, null));
            await s.Sales.SetOrderLineAsync(order, f.Delivery, 2, 610m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            Assert.Equal("sales.order.services_only", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateShipmentAsync(order))).Code);
            var posted = await s.Sales.CreateServiceActAsync(order);
            await s.Documents.SetLineAsync(posted, f.Delivery, 1, (await s.Documents.GetAsync(posted)).RowVersion);
            await s.Documents.PostAsync(posted, (await s.Documents.GetAsync(posted)).RowVersion);
            Assert.Equal(ShipmentState.Partial, (await s.Sales.GetOrderAsync(order)).Shipped);

            var upd = await s.Print.UpdAsync(posted);
            var number = (await s.Documents.GetAsync(posted)).Number;
            Assert.Equal((KnitErp.Application.Printing.PrintService.VatInvoiceNumber(number), number, 610m),
                (upd.Number, upd.ShipmentNumber, upd.Total));
            Assert.Equal("--", Assert.Single(upd.Lines).TnVedCode);
            Assert.Equal([posted], (await s.VatInvoices.IssuedAsync(new KnitErp.Application.Taxes.VatJournalFilter(Day, Day))).Select(i => i.DocumentId));
            Assert.Equal("print.upd.document", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Print.UpdAsync(await s.Sales.CreateServiceActAsync(order)))).Code);
            act = (await s.Sales.GetOrderAsync(order)).Documents.Single(d => d.Status == StockDocumentStatus.Draft).Id;

            // Партий и загрузки строк из Excel у акта нет.
            Assert.Empty((await s.Batches.ForDocumentAsync(act)).Items);
            Assert.Equal("stock.service_act.import", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.ImportLinesAsync(act, new MemoryStream([1, 2, 3]), (await s.Documents.GetAsync(act)).RowVersion))).Code);
        }

        // Чужая организация акт не видит.
        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Documents.GetAsync(act));
        }

        // Ограничения базы: в строке акта — только услуга; вид документа не меняется; акт без заказа не сохраняется.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity]) VALUES ({act}, {f.Sweater}, 1)"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[stock_documents] SET [Kind] = 6 WHERE [Id] = {act}"));
            Assert.Equal(51300, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[stock_documents] SET [SalesOrderId] = NULL WHERE [Id] = {act}"));
            Assert.Contains("ck_stock_documents_service_act_order", ex.Message);
            // Услуга в строке акта допустима, а в строке отгрузки — нет (как раньше).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [kniterp].[stock_document_lines] ([DocumentId], [ItemId], [Quantity]) VALUES ({act}, {f.Packing}, 1)");
        }
    }

    [SqlFact]
    public async Task Supplier_service_act_counts_in_supplier_debt_and_receipt_state()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Заказ поставщику: свитеры и доработка (услуга), цены с НДС.
        var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, f.Supplier, f.Store, Day, "СЧ-7", true, null));
        await s.Purchases.SetOrderLineAsync(po, f.Sweater, 3, 1220m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        await s.Purchases.SetOrderLineAsync(po, f.Delivery, 1, 244m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        var receipt = await s.Purchases.CreateReceiptAsync(po);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        var dto = await s.Purchases.GetOrderAsync(po);
        Assert.Equal((ReceiptState.Partial, false), (dto.Received, dto.Lines.Single(l => l.ItemId == f.Delivery).Stocked));

        // Акт услуг поставщика: только услуга, движений нет.
        var act = await s.Purchases.CreateServiceActAsync(po);
        var draft = await s.Documents.GetAsync(act);
        Assert.Equal((StockOperationKind.SupplierServiceAct, f.Supplier), (draft.Kind, draft.CounterpartyId!.Value));
        Assert.StartsWith("АП-", draft.Number);
        Assert.Equal([(f.Delivery, 1m)], draft.Lines.Select(l => (l.ItemId, l.Quantity)));
        Assert.Equal("stock.service_act.goods", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.SetLineAsync(act, f.Sweater, 1, (await s.Documents.GetAsync(act)).RowVersion))).Code);
        await s.Documents.PostAsync(act, draft.RowVersion);
        await using (var db = host.NewDb())
        {
            Assert.False(await db.StockMovements.AnyAsync(m => m.ItemId == f.Delivery));
        }

        // Получено полностью; долг поставщику — с доработкой.
        dto = await s.Purchases.GetOrderAsync(po);
        Assert.Equal((ReceiptState.Full, dto.Total!.Value, dto.Total.Value), (dto.Received, dto.ReceivedValue!.Value, dto.Debt!.Value));
        Assert.Equal(ReceiptState.Full, (await s.Purchases.ListOrdersAsync(new PurchaseOrderFilter())).Single(o => o.Id == po).Received);
        Assert.Equal(dto.Total.Value, (await s.Purchases.BalancesAsync()).Single(b => b.SupplierId == f.Supplier).Received);
        Assert.Equal("purchase.order.services_closed", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Purchases.CreateServiceActAsync(po))).Code);
        var statement = await s.Settlements.BuildAsync(f.Supplier, null, Day, Day);
        Assert.Equal(dto.Total.Value, statement.Rows.Sum(r => r.Credit));
        Assert.Contains(statement.Rows, r => r.Document.StartsWith("Акт услуг поставщика") && r.Credit == 244m);
        Assert.Equal(dto.Total.Value, (await s.Dashboard.GetAsync(refresh: true)).Finance!.SupplierDebt);

        // Счёт-фактура поставщика регистрируется и к акту.
        Assert.Contains(await s.VatInvoices.ReceiptsToRegisterAsync(), r => r.Id == act && r.Amount == 244m);
        await s.VatInvoices.RegisterAsync(new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(act, "Д-15", Day, 244m, 44m, null));
        Assert.DoesNotContain(await s.VatInvoices.ReceiptsToRegisterAsync(), r => r.Id == act);

        // Выгрузка в 1С: акт поставщика — поступление с «Услугами».
        var file = await s.Exchange.ExportAsync(Day, Day, null);
        Assert.Equal(3, file.Summary.Documents["Документ.ПоступлениеТоваровУслуг"] + file.Summary.Documents.GetValueOrDefault("Документ.ОприходованиеТоваров"));

        // Сторно: строка-услуга снова открыта, долг без доработки.
        await s.Documents.ReverseAsync(act, "не тот поставщик", (await s.Documents.GetAsync(act)).RowVersion);
        dto = await s.Purchases.GetOrderAsync(po);
        Assert.Equal((ReceiptState.Partial, dto.Total!.Value - 244m), (dto.Received, dto.ReceivedValue!.Value));
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Delivery, long Packing, long Customer, long Supplier);

    /// <summary>Свитер (5 шт на складе, ТН ВЭД 6110201000), услуги «Доставка» и «Упаковка», покупатель и поставщик.</summary>
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
        var card = await s.Nomenclature.GetItemAsync(sweater);
        await s.Nomenclature.SaveItemDetailsAsync(sweater, card.Details with { TnVedCode = "6110201000" }, new Dictionary<long, string?>(), card.RowVersion);
        var delivery = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, serviceUnit, null, vat));
        var packing = await s.Catalog.CreateItemAsync(new ItemCommand("УС-2", "Упаковка подарочная", ItemType.Service, serviceUnit, null, vat));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Фабрика»", null, null, true, false, null));
        await using (var db = host.NewDb())
        {
            var reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
            var receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(store, null, null, reason, Day, null), [(sweater, 5m)]);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        }

        return new Fixture(org, store, sweater, delivery, packing, customer, supplier);
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
