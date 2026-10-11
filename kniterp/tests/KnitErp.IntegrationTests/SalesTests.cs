using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Organizations;
using KnitErp.Application.Printing;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Продажи (D65): заказ покупателя, отгрузка и возврат по заказу, оплаты, долг покупателя, права.</summary>
public sealed class SalesTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 810_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Order_shipment_return_and_payments_make_customer_debt()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // На складе 30 свитеров (поступление без заказа).
            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, await Reason(StockOperationKind.Receipt), Day, null));
            await s.Documents.SetLineAsync(receipt, f.Sweater, 30, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day.AddDays(2), "Договор № 7", false, null));
            var dto = await s.Sales.GetOrderAsync(order);
            Assert.StartsWith("ЗК-", dto.Number);
            // Цены без НДС: 25 × 2 000 = 50 000 + НДС 22% 11 000 = 61 000.
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 25, 2000m, 22m, dto.RowVersion);
            dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((61_000m, 11_000m), (dto.Total!.Value, dto.VatTotal!.Value));
            await s.Sales.ConfirmOrderAsync(order, dto.RowVersion);

            // Отгрузка по заказу: 25 со склада; остаток склада — 5.
            var ship = await s.Sales.CreateShipmentAsync(order);
            var doc = await s.Documents.GetAsync(ship);
            Assert.Equal((StockOperationKind.Shipment, order, 25m), (doc.Kind, doc.SalesOrderId!.Value, doc.Lines.Single().Quantity));
            Assert.Equal(30m, doc.Lines.Single().Available);
            await s.Documents.PostAsync(ship, doc.RowVersion);
            Assert.Equal(5m, (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single(b => b.ItemId == f.Sweater).Quantity);
            dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((ShipmentState.Full, 61_000m), (dto.Shipped, dto.ShippedValue!.Value));
            Assert.Equal("sales.order.shipped", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateShipmentAsync(order))).Code);

            // Возврат 2 шт (брак) — больше отгруженного нельзя.
            var ret = await s.Sales.CreateReturnAsync(order);
            await s.Documents.SetLineAsync(ret, f.Sweater, 26, (await s.Documents.GetAsync(ret)).RowVersion);
            Assert.Equal("sales.return.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion))).Code);
            await s.Documents.SetLineAsync(ret, f.Sweater, 2, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
            Assert.Equal(7m, (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single(b => b.ItemId == f.Sweater).Quantity);

            await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 40_000m, "п/п 55");
            dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((61_000m, 4_880m, 40_000m, 16_120m), (dto.ShippedValue!.Value, dto.ReturnedValue!.Value, dto.Paid!.Value, dto.Debt!.Value));
            Assert.Equal(16_120m, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Debt);

            // Колонки списка как в МойСклад (D75): сумма, выставлено счетов, оплачено, не оплачено, отгружено (за вычетом возврата).
            var row = (await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Single(r => r.Id == order);
            Assert.Equal((61_000m, 0m, 40_000m, 21_000m, 56_120m), (row.Total!.Value, row.Invoiced!.Value, row.Paid!.Value, row.Unpaid!.Value, row.ShippedValue!.Value));

            // Этапы: у новой организации — шесть по умолчанию; этап ставится в заказ, по нему фильтруется список.
            var stages = await s.Stages.ListAsync();
            Assert.Equal(KnitErp.Domain.Sales.SalesOrderStage.Defaults.Select(d => d.Name), stages.Select(st => st.Name));
            var ready = await s.Stages.CreateAsync("Готов к запуску — оплачен", "green");
            Assert.Equal("sales.stage.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Stages.CreateAsync("Собран", "teal"))).Code);
            Assert.Equal("sales.stage.color", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Stages.CreateAsync("Розовый", "pink"))).Code);
            await s.Sales.SetOrderStageAsync(order, ready, (await s.Sales.GetOrderAsync(order)).RowVersion);
            Assert.Equal(("Готов к запуску — оплачен", "green"), ((await s.Sales.GetOrderAsync(order)).Stage!.Name, (await s.Sales.GetOrderAsync(order)).Stage!.Color));
            Assert.Single(await s.Sales.ListOrdersAsync(new SalesOrderFilter(StageId: ready)));
            Assert.Empty(await s.Sales.ListOrdersAsync(new SalesOrderFilter(StageId: 0)));
            Assert.Equal(1, (await s.Stages.ListAsync()).Single(st => st.Id == ready).Orders);
            await s.Stages.MoveAsync(ready, -1);
            Assert.Equal("Готов к запуску — оплачен", (await s.Stages.ListAsync())[^2].Name);
            var archived = (await s.Stages.ListAsync()).Single(st => st.Name == "Собран");
            await s.Stages.ArchiveAsync(archived.Id, archived.RowVersion);
            Assert.Equal("catalog.archived", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Sales.SetOrderStageAsync(order, archived.Id, (await s.Sales.GetOrderAsync(order)).RowVersion))).Code);

            // Динамика на главной: продажи за день = отгружено 61 000 − возвращено 4 880; поступления — 40 000.
            var charts = (await s.Dashboard.GetAsync()).Charts;
            var sales = charts.Single(c => c.Title == "Продажи");
            Assert.Equal((DashboardService.ChartDays, Day, 56_120m, 56_120m, 0m), (sales.Points.Count, sales.From.AddDays(sales.Points.Count - 1), sales.Points[^1], sales.Total, sales.PreviousTotal));
            Assert.Equal(40_000m, charts.Single(c => c.Title == "Поступления от покупателей").Total);
            Assert.Equal(0m, charts.Single(c => c.Title == "Закупки").Total);

            // Деньги на главном: долг покупателя 16 120, крупнейший должник — он же; после возврата 2 шт. заказ снова ждёт отгрузки.
            var finance = (await s.Dashboard.GetAsync()).Finance!;
            Assert.Equal((16_120m, 0m, 1), (finance.CustomerDebt!.Value, finance.SupplierDebt!.Value, finance.OrdersToShip!.Value));
            Assert.Equal((f.Customer, 16_120m), (finance.TopDebtors.Single().Id, finance.TopDebtors.Single().Debt));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var dto = await s.Sales.GetOrderAsync(order);
            Assert.Null(dto.Total);
            Assert.False(dto.CanEdit);
            Assert.Empty((await s.Dashboard.GetAsync()).Charts);
            Assert.Null((await s.Dashboard.GetAsync()).Finance);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.BalancesAsync());
            Assert.Null((await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Single().Paid);
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await s.Sales.SetOrderStageAsync(order, null, (await s.Sales.GetOrderAsync(order)).RowVersion));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.GetOrderAsync(order));
        }

        async Task<long?> Reason(StockOperationKind kind)
        {
            await using var db = host.NewDb();
            return await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstAsync();
        }
    }

    [SqlFact]
    public async Task Invoice_print_forms_and_supplier_invoices()
    {
        var f = await SetUpAsync();
        long order, invoice, ship;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                    .Select(r => (long?)r.Id).FirstAsync();
            }

            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(receipt, f.Sweater, 30, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, "Договор № 7 от 01.10.2026", false, null));
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 25, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            Assert.Equal("sales.invoice.order_status",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Invoices.CreateAsync(order, null, null))).Code);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);

            // Счёт по заказу: строки и суммы — из заказа; второй действующий счёт по заказу не выставить.
            Assert.Equal("sales.invoice.due_date",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Invoices.CreateAsync(order, Day.AddDays(-1), null))).Code);
            invoice = await s.Invoices.CreateAsync(order, Day.AddDays(5), null);
            var dto = await s.Invoices.GetAsync(invoice);
            Assert.StartsWith("СЧ-", dto.Number);
            Assert.Equal((61_000m, 11_000m, 0m, InvoicePaymentState.Unpaid), (dto.Total, dto.VatTotal, dto.Paid, dto.PaymentState));
            Assert.Equal(invoice, await s.Invoices.FindForOrderAsync(order));
            Assert.Equal("sales.invoice.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Invoices.CreateAsync(order, null, null))).Code);

            await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 40_000m, null);
            var row = (await s.Invoices.ListAsync(new CustomerInvoiceFilter())).Single(r => r.Id == invoice);
            Assert.Equal((40_000m, InvoicePaymentState.Partial, false), (row.Paid, row.PaymentState, row.Overdue));

            // Реквизиты для печати — у юрлица (D78): основное создано из реквизитов организации; БИК и счета проверяются по ключу.
            var entity = (await s.LegalEntities.ListAsync()).Single();
            Assert.True(entity.IsDefault);
            await s.LegalEntities.UpdateAsync(entity.Id, entity.Data with { LegalAddress = "г. Москва, ул. Тестовая, д. 1", DirectorName = "Иванов И. И." },
                entity.RowVersion);
            Assert.Equal("org.bank.bik", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.LegalEntities.AddAccountAsync(entity.Id,
                new LegalEntityAccountCommand("Банк", "12345", "40702810938000000001", null)))).Code);
            Assert.Equal("org.bank.account", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.LegalEntities.AddAccountAsync(entity.Id,
                new LegalEntityAccountCommand("Банк", "044525225", "40702810138000000000", null)))).Code);
            await s.LegalEntities.AddAccountAsync(entity.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "4070 2810 9380 0000 0001",
                "30101810400000000225"));
            entity = (await s.LegalEntities.ListAsync()).Single();
            Assert.Equal(("40702810938000000001", true, "Иванов И. И."), (entity.Accounts.Single().Account, entity.Accounts.Single().IsDefault, entity.DirectorName));

            var cp = (await s.Counterparties.ListAsync(new CounterpartyFilter())).Counterparties.Single(c => c.Id == f.Customer);
            await s.Counterparties.UpdateAsync(f.Customer, new CounterpartyCommand(cp.Name, null, null, false, true, null, Address: "г. Тверь, пр. Ленина, 5"),
                cp.RowVersion);
            cp = (await s.Counterparties.ListAsync(new CounterpartyFilter())).Counterparties.Single(c => c.Id == f.Customer);
            await s.Counterparties.UpdateAsync(f.Customer, new CounterpartyCommand(cp.Name, null, null, false, true, "без адреса в команде"), cp.RowVersion);

            var print = await s.Print.InvoiceAsync(invoice);
            Assert.Equal("Шестьдесят одна тысяча рублей 00 копеек", print.TotalInWords);
            Assert.Equal(("044525225", "ООО «Магазин»", "г. Тверь, пр. Ленина, 5"), (print.Requisites.BankBic, print.Buyer.Name, print.Buyer.Address));
            Assert.Equal("Договор № 7 от 01.10.2026; заказ " + (await s.Sales.GetOrderAsync(order)).Number + " от 09.10.2026", print.Basis);
            var line = print.Lines.Single();
            Assert.Equal((50_000m, 11_000m, 61_000m, "796"), (line.AmountWithoutVat, line.VatAmount, line.Amount, line.UnitCode));

            // УПД — только по проведённой отгрузке; цены — из заказа.
            ship = await s.Sales.CreateShipmentAsync(order);
            await s.Documents.SetLineAsync(ship, f.Sweater, 10, (await s.Documents.GetAsync(ship)).RowVersion);
            Assert.Equal("print.upd.document", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Print.UpdAsync(ship))).Code);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            var upd = await s.Print.UpdAsync(ship);
            Assert.Equal((20_000m, 4_400m, 24_400m), (upd.TotalWithoutVat, upd.VatTotal, upd.Total));
            Assert.Equal((2_000m, "Двадцать четыре тысячи четыреста рублей 00 копеек"), (upd.Lines.Single().Price, upd.TotalInWords));
            Assert.Equal("Российский рубль, 643", upd.Currency);
            // Порядковый номер счёта-фактуры — цифры номера отгрузки; оплата до отгрузки — в строке 5, без номера п/п — предупреждение.
            Assert.Equal(PrintService.VatInvoiceNumber(upd.ShipmentNumber), upd.Number);
            Assert.Matches("^[1-9][0-9]*$", upd.Number);
            Assert.Equal("№ — от 09.10.2026", upd.PaymentDocuments);
            Assert.Contains(upd.Warnings, w => w.Contains("строка 5)"));
            Assert.Contains(upd.Warnings, w => w.Contains("строка 2б"));

            // Отмена счёта с причиной — затем можно выставить новый.
            Assert.Equal("field.required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Invoices.CancelAsync(invoice, " ", dto.RowVersion))).Code);
            await s.Invoices.CancelAsync(invoice, "Покупатель просит другой срок", (await s.Invoices.GetAsync(invoice)).RowVersion);
            Assert.True((await s.Print.InvoiceAsync(invoice)).Cancelled);
            Assert.Empty(await s.Invoices.ListAsync(new CustomerInvoiceFilter()));
            Assert.Single(await s.Invoices.ListAsync(new CustomerInvoiceFilter(IncludeCancelled: true)));
            Assert.NotEqual(invoice, await s.Invoices.CreateAsync(order, null, null));

            // Счёт поставщика — номер в шапке заказа поставщику; оплачено — по оплатам заказа.
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
            var po = await s.Purchases.CreateOrderAsync(new KnitErp.Domain.Purchasing.PurchaseOrderHeader(Day, supplier, f.Store, null, "№ 15 от 05.10.2026", true, null));
            await s.Purchases.SetOrderLineAsync(po, f.Sweater, 10, 500m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            Assert.Empty(await s.Purchases.ListSupplierInvoicesAsync());
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.CreatePaymentAsync(Day, supplier, po, 1_000m, null);
            var si = (await s.Purchases.ListSupplierInvoicesAsync("№ 15")).Single();
            Assert.Equal((5_000m, 1_000m, 4_000m), (si.Total, si.Paid, si.ToPay));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Invoices.ListAsync(new CustomerInvoiceFilter()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Print.UpdAsync(ship));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Invoices.GetAsync(invoice));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Print.InvoiceAsync(invoice));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Print.UpdAsync(ship));
        }
    }

    [SqlFact]
    public async Task Sales_by_item_and_profitability_use_purchase_and_tech_card_costs()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            var hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                    .Select(r => (long?)r.Id).FirstAsync();
            }

            // Пряжа: куплено 100 кг по 200 без НДС, 20 кг возвращено — средняя цена 200.
            var po = await s.Purchases.CreateOrderAsync(new KnitErp.Domain.Purchasing.PurchaseOrderHeader(Day, supplier, f.Store, null, null, false, null));
            await s.Purchases.SetOrderLineAsync(po, yarn, 100, 200m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(po);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            var back = await s.Purchases.CreateReturnAsync(po);
            await s.Documents.SetLineAsync(back, yarn, 20, (await s.Documents.GetAsync(back)).RowVersion);
            await s.Documents.PostAsync(back, (await s.Documents.GetAsync(back)).RowVersion);

            // Свитер — по техкарте: 0,5 кг пряжи на штуку, отход 10% → 200 × 0,5 × 1,1 = 110.
            var card = await s.TechCards.CreateAsync(f.Sweater, 1, null);
            await s.TechCards.SetLineAsync(card, yarn, 0.5m, 10m, (await s.TechCards.GetAsync(card)).RowVersion);
            await s.TechCards.ActivateAsync(card, (await s.TechCards.GetAsync(card)).RowVersion);

            var stock = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(stock, f.Sweater, 30, (await s.Documents.GetAsync(stock)).RowVersion);
            await s.Documents.SetLineAsync(stock, hat, 10, (await s.Documents.GetAsync(stock)).RowVersion);
            await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);

            // Продано 25 свитеров по 2 000 + НДС 22% и 5 шапок по 500; 2 свитера вернули.
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 25, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.SetOrderLineAsync(order, hat, 5, 500m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            var ship = await s.Sales.CreateShipmentAsync(order);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            var ret = await s.Sales.CreateReturnAsync(order);
            await s.Documents.RemoveLineAsync(ret, hat, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.SetLineAsync(ret, f.Sweater, 2, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);

            var filter = new SalesAnalyticsFilter(Day.AddDays(-30), Day);
            var byItem = await s.Analytics.SalesByItemAsync(filter);
            var sw = byItem.Single(r => r.ItemId == f.Sweater);
            Assert.Equal((23m, 46_000m, 10_120m, 1, 2, 2_000m), (sw.Quantity, sw.Revenue, sw.Vat, sw.Customers, sw.Documents, sw.AveragePrice!.Value));
            Assert.Equal(2_500m, byItem.Single(r => r.ItemId == hat).Revenue);
            Assert.Single(await s.Analytics.SalesByItemAsync(filter with { Search = "Шапк" }));
            Assert.Empty(await s.Analytics.SalesByItemAsync(filter with { Customer = "нет такого" }));
            Assert.Empty(await s.Analytics.SalesByItemAsync(new SalesAnalyticsFilter(Day.AddDays(1), Day.AddDays(5))));
            Assert.Equal("report.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Analytics.SalesByItemAsync(new SalesAnalyticsFilter(Day, Day.AddDays(-1))))).Code);

            var profit = await s.Analytics.ProfitabilityAsync(filter, ProfitGrouping.Item);
            var swp = profit.Rows.Single(r => r.Id == f.Sweater);
            Assert.Equal((2_530m, 43_470m, 94.5m, CostSource.TechCard), (swp.Cost, swp.Profit!.Value, swp.MarginPercent!.Value, swp.Source));
            var hp = profit.Rows.Single(r => r.Id == hat);
            Assert.Equal((false, (decimal?)null, CostSource.None), (hp.CostComplete, hp.Profit, hp.Source));
            Assert.Equal((48_500m, 46_000m, 43_470m, 2_500m), (profit.Revenue, profit.CostedRevenue, profit.Profit, profit.UncostedRevenue));

            var byCustomer = await s.Analytics.ProfitabilityAsync(filter, ProfitGrouping.Customer);
            var cust = byCustomer.Rows.Single();
            Assert.Equal((f.Customer, 48_500m, 2_530m, false), (cust.Id, cust.Revenue, cust.Cost, cust.CostComplete));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Analytics.ProfitabilityAsync(new SalesAnalyticsFilter(Day, Day), ProfitGrouping.Item));
        }
    }

    [SqlFact]
    public async Task Vat_invoice_journals_issued_from_shipments_and_received_registered_to_receipts()
    {
        var f = await SetUpAsync();
        var period = new KnitErp.Application.Taxes.VatJournalFilter(Day.AddDays(-5), Day.AddDays(5));
        long registered;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Полученный: приёмка 10 свитеров по 500 с НДС 22% → 5 000, НДС 901,64.
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
            var po = await s.Purchases.CreateOrderAsync(new KnitErp.Domain.Purchasing.PurchaseOrderHeader(Day, supplier, f.Store, null, "№ 77", true, null));
            await s.Purchases.SetOrderLineAsync(po, f.Sweater, 10, 500m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(po);
            Assert.Empty(await s.VatInvoices.ReceiptsToRegisterAsync());
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            var todo = (await s.VatInvoices.ReceiptsToRegisterAsync()).Single();
            Assert.Equal((receipt, 5_000m, 901.64m, "№ 77"), (todo.Id, todo.Amount, todo.VatAmount, todo.SupplierInvoice));

            Assert.Equal("purchase.vat_invoice.amount", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.VatInvoices.RegisterAsync(
                new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(receipt, "77", Day, 900m, 901.64m, null)))).Code);
            Assert.Equal("field.required", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.VatInvoices.RegisterAsync(
                new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(receipt, " ", Day, 5_000m, 901.64m, null)))).Code);
            registered = await s.VatInvoices.RegisterAsync(new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(receipt, "77", Day, 5_000m, 901.64m, null));
            Assert.Equal("purchase.vat_invoice.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.VatInvoices.RegisterAsync(
                new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(receipt, "78", Day, 5_000m, 901.64m, null)))).Code);
            Assert.Empty(await s.VatInvoices.ReceiptsToRegisterAsync());
            var row = (await s.VatInvoices.ReceivedAsync(period)).Single();
            Assert.Equal(("77", 4_098.36m, 0m, "ООО «Пряжа»"), (row.SupplierNumber, row.AmountWithoutVat, row.Difference, row.Supplier));

            // Отмена с причиной — счёт-фактуру можно зарегистрировать заново.
            await s.VatInvoices.CancelAsync(registered, "ошибка в номере", row.RowVersion);
            Assert.Empty(await s.VatInvoices.ReceivedAsync(period));
            Assert.Single(await s.VatInvoices.ReceivedAsync(period with { IncludeCancelled = true }));
            registered = await s.VatInvoices.RegisterAsync(new KnitErp.Application.Taxes.RegisterVatInvoiceCommand(receipt, "77/1", Day, 5_100m, 919.67m, "доставка"));
            Assert.Equal(100m, (await s.VatInvoices.ReceivedAsync(period)).Single().Difference);

            // Выданный: отгрузка 4 свитеров по 2 000 + НДС 22%; после сторно — «аннулирован», в журнал по умолчанию не входит.
            var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 4, 2000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            var ship = await s.Sales.CreateShipmentAsync(order);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            var issued = (await s.VatInvoices.IssuedAsync(period)).Single();
            Assert.Equal((8_000m, 1_760m, 9_760m, false), (issued.AmountWithoutVat, issued.VatAmount, issued.Amount, issued.Annulled));
            Assert.Equal(PrintService.VatInvoiceNumber(issued.Number), issued.InvoiceNumber);
            Assert.Empty(await s.VatInvoices.IssuedAsync(period with { Search = "нет такого" }));
            await s.Documents.ReverseAsync(ship, "ошибочная отгрузка", (await s.Documents.GetAsync(ship)).RowVersion);
            Assert.Empty(await s.VatInvoices.IssuedAsync(period));
            Assert.True((await s.VatInvoices.IssuedAsync(period with { IncludeCancelled = true })).Single().Annulled);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.VatInvoices.ReceivedAsync(period));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            Assert.Empty(await s.VatInvoices.ReceivedAsync(period));
            await Assert.ThrowsAsync<NotFoundException>(() => s.VatInvoices.CancelAsync(registered, "чужой", [0]));
        }
    }

    [SqlFact]
    public async Task Reserve_holds_unshipped_quantity_and_discount_flows_into_upd()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        long? reason;
        await using (var db = host.NewDb())
        {
            reason = await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
        }

        var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, reason, Day, null));
        await s.Documents.SetLineAsync(receipt, f.Sweater, 30, (await s.Documents.GetAsync(receipt)).RowVersion);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

        // Заказ А: 20 шт. с резервом и скидкой 10 % — после подтверждения 20 шт. держатся на складе.
        var a = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
        await s.Sales.SetOrderLineAsync(a, f.Sweater, 20, 1000m, 22m, 10m, (await s.Sales.GetOrderAsync(a)).RowVersion);
        await s.Sales.SetOrderReserveAsync(a, true, (await s.Sales.GetOrderAsync(a)).RowVersion);
        Assert.Equal(0m, (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single().Reserved);
        await s.Sales.ConfirmOrderAsync(a, (await s.Sales.GetOrderAsync(a)).RowVersion);
        var balance = (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single();
        Assert.Equal((30m, 20m, 10m), (balance.Quantity, balance.Reserved, balance.Available));
        var orderA = await s.Sales.GetOrderAsync(a);
        Assert.Equal((true, 21_960m, 2_000m), (orderA.Reserve, orderA.Total!.Value, orderA.DiscountTotal!.Value));
        Assert.Equal((30m, 0m, false), (orderA.Lines.Single().Stock, orderA.Lines.Single().ReservedByOthers, orderA.Lines.Single().Short));

        // Заказ Б на 15 шт.: доступно ему 10 — строка подсвечена «не хватает».
        var b = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
        await s.Sales.SetOrderLineAsync(b, f.Sweater, 15, 1000m, 22m, (await s.Sales.GetOrderAsync(b)).RowVersion);
        var lineB = (await s.Sales.GetOrderAsync(b)).Lines.Single();
        Assert.Equal((20m, 10m, true), (lineB.ReservedByOthers, lineB.Available, lineB.Short));

        // Отгрузка 5 шт. по заказу А уменьшает его резерв; УПД — по цене со скидкой.
        var ship = await s.Sales.CreateShipmentAsync(a);
        await s.Documents.SetLineAsync(ship, f.Sweater, 5, (await s.Documents.GetAsync(ship)).RowVersion);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        balance = (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single();
        Assert.Equal((25m, 15m, 10m), (balance.Quantity, balance.Reserved, balance.Available));
        var upd = await s.Print.UpdAsync(ship);
        Assert.Equal((900m, 4_500m, 5_490m), (upd.Lines.Single().Price, upd.TotalWithoutVat, upd.Total));

        // Снятие резерва освобождает остаток; закрытый заказ резерв не держит и не меняет.
        await s.Sales.SetOrderReserveAsync(a, false, (await s.Sales.GetOrderAsync(a)).RowVersion);
        Assert.Equal(0m, (await s.Stock.BalancesAsync(new StockFilter(f.Store))).Single().Reserved);
        await s.Sales.CloseOrderAsync(a, (await s.Sales.GetOrderAsync(a)).RowVersion);
        Assert.Equal("sales.reserve.status", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Sales.SetOrderReserveAsync(a, true, (await s.Sales.GetOrderAsync(a)).RowVersion))).Code);
    }

    [SqlFact]
    public async Task Order_details_custom_fields_and_customer_balance()
    {
        var f = await SetUpAsync();
        long order, project, channel, pieces, knitHours, deadline, urgent;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            var dto = await s.Sales.GetOrderAsync(order);
            // Ответственный по умолчанию — автор; баланс покупателя без отгрузок и оплат — ноль.
            Assert.Equal((f.Org.OwnerUserId, 0m), (dto.Details!.ResponsibleUserId!.Value, dto.Details.CustomerBalance!.Value));
            Assert.Empty(dto.Details.CustomFields);

            project = await s.SalesSettings.CreateLookupAsync(LookupKind.Project, "Подвязы — поло воротники");
            channel = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Маркетплейс");
            // То же название в другом справочнике можно, в том же — нет.
            await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Подвязы — поло воротники");
            Assert.Equal("catalog.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.SalesSettings.CreateLookupAsync(LookupKind.Project, "Подвязы — поло воротники"))).Code);
            pieces = await s.SalesSettings.CreateFieldAsync("Количество, штук", CustomFieldType.Number);
            knitHours = await s.SalesSettings.CreateFieldAsync("Время вязания", CustomFieldType.Number);
            deadline = await s.SalesSettings.CreateFieldAsync("Срок у клиента", CustomFieldType.Date);
            urgent = await s.SalesSettings.CreateFieldAsync("Срочный", CustomFieldType.Flag);
            Assert.Equal("custom_field.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.SalesSettings.CreateFieldAsync("Срочный", CustomFieldType.Text))).Code);

            // Проект нельзя подставить вместо канала.
            await Assert.ThrowsAsync<NotFoundException>(async () => await s.Sales.SetOrderDetailsAsync(order,
                new SalesOrderDetails(null, null, project, null, null), new Dictionary<long, string?>(), (await s.Sales.GetOrderAsync(order)).RowVersion));
            Assert.Equal("custom_field.number", (await Assert.ThrowsAsync<BusinessRuleException>(async () => await s.Sales.SetOrderDetailsAsync(order,
                new SalesOrderDetails(null, null, null, null, null), new Dictionary<long, string?> { [pieces] = "много" },
                (await s.Sales.GetOrderAsync(order)).RowVersion))).Code);

            await s.Sales.SetOrderDetailsAsync(order, new SalesOrderDetails(new TimeOnly(16, 43, 27), project, channel, " г. Москва, ул. Ткацкая, 5 ", f.Senior),
                new Dictionary<long, string?> { [pieces] = "1 200,5", [knitHours] = "36", [deadline] = "17.10.2026", [urgent] = "true" },
                (await s.Sales.GetOrderAsync(order)).RowVersion);
            dto = await s.Sales.GetOrderAsync(order);
            var d = dto.Details!;
            Assert.Equal((new TimeOnly(16, 43), "Подвязы — поло воротники", "Маркетплейс", "г. Москва, ул. Ткацкая, 5", "Старший кладовщик"),
                (d.OrderTime!.Value, d.Project, d.Channel, d.DeliveryAddress, d.Responsible));
            Assert.Equal(["1200.5", "36", "2026-10-17", "true"], d.CustomFields.Select(c => c.Value));

            // Пустое значение удаляет запись; поле в архиве не меняется и показывается, пока у заказа есть значение.
            var knit = (await s.SalesSettings.FieldsAsync()).Single(x => x.Id == knitHours);
            Assert.Equal(1, knit.Filled);
            await s.SalesSettings.SetFieldArchivedAsync(knitHours, true, knit.RowVersion);
            await s.Sales.SetOrderDetailsAsync(order, new SalesOrderDetails(null, project, channel, null, f.Senior),
                new Dictionary<long, string?> { [urgent] = null, [knitHours] = "99" }, dto.RowVersion);
            d = (await s.Sales.GetOrderAsync(order)).Details!;
            Assert.Equal((null, null), (d.OrderTime, d.DeliveryAddress));
            Assert.Equal(["1200.5", "2026-10-17", null, "36"], d.CustomFields.Select(c => c.Value));
            Assert.True(d.CustomFields.Single(c => c.FieldId == knitHours).IsArchived);
            Assert.Null(d.CustomFields.Single(c => c.FieldId == urgent).Value);
            Assert.Equal(0, (await s.SalesSettings.FieldsAsync()).Single(x => x.Id == urgent).Filled);

            // Архивный проект нельзя выбрать заново, но у заказа он остаётся.
            var p = (await s.SalesSettings.LookupsAsync(LookupKind.Project)).Single();
            Assert.Equal(1, p.Orders);
            await s.SalesSettings.SetLookupArchivedAsync(project, true, p.RowVersion);
            Assert.Empty((await s.Sales.DetailOptionsAsync()).Projects);
            Assert.Equal("Подвязы — поло воротники", (await s.Sales.GetOrderAsync(order)).Details!.Project);
            var second = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            Assert.Equal("catalog.archived", (await Assert.ThrowsAsync<BusinessRuleException>(async () => await s.Sales.SetOrderDetailsAsync(second,
                new SalesOrderDetails(null, project, null, null, null), new Dictionary<long, string?>(), (await s.Sales.GetOrderAsync(second)).RowVersion))).Code);

            // Баланс покупателя: аванс 5 000 без отгрузок — минус (переплата).
            await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 5_000m, "п/п 1");
            Assert.Equal(-5_000m, (await s.Sales.GetOrderAsync(order)).Details!.CustomerBalance);

            // Отменённый заказ не меняется.
            await s.Sales.CancelOrderAsync(second, (await s.Sales.GetOrderAsync(second)).RowVersion);
            Assert.Equal("sales.order.cancelled", (await Assert.ThrowsAsync<BusinessRuleException>(async () => await s.Sales.SetOrderDetailsAsync(second,
                new SalesOrderDetails(null, null, null, null, null), new Dictionary<long, string?>(), (await s.Sales.GetOrderAsync(second)).RowVersion))).Code);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            // Без права на цены баланс не показывается; без права на продажи детали не меняются.
            Assert.Null((await s.Sales.GetOrderAsync(order)).Details!.CustomerBalance);
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await s.Sales.SetOrderDetailsAsync(order,
                new SalesOrderDetails(null, null, null, null, null), new Dictionary<long, string?>(), (await s.Sales.GetOrderAsync(order)).RowVersion));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.SalesSettings.CreateFieldAsync("Чужое", CustomFieldType.Text));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            // Чужие проекты, поля и сотрудники — «не найдено».
            var mine = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, await s.Counterparties.CreateAsync(
                new CounterpartyCommand("ООО «Другой»", null, null, false, true, null)), await s.Warehouses.CreateWarehouseAsync("Склад", null), null, null, true, null));
            var rv = (await s.Sales.GetOrderAsync(mine)).RowVersion;
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.SetOrderDetailsAsync(mine,
                new SalesOrderDetails(null, null, channel, null, null), new Dictionary<long, string?>(), rv));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.SetOrderDetailsAsync(mine,
                new SalesOrderDetails(null, null, null, null, f.Senior), new Dictionary<long, string?>(), rv));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.SetOrderDetailsAsync(mine,
                new SalesOrderDetails(null, null, null, null, null), new Dictionary<long, string?> { [pieces] = "1" }, rv));
            await Assert.ThrowsAsync<NotFoundException>(() => s.SalesSettings.RenameLookupAsync(channel, "Моё", rv));
        }
    }

    [SqlFact]
    public async Task Sole_proprietor_sells_with_own_requisites_account_and_journal()
    {
        var f = await SetUpAsync();
        long ip, ipAccount, order, ship;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Основное юрлицо создано из реквизитов организации; ИП добавляется вторым, освобождён от НДС.
            var ooo = (await s.LegalEntities.ListAsync()).Single();
            Assert.Equal((LegalEntityKind.Company, true, f.Org.OrganizationId > 0), (ooo.Kind, ooo.IsDefault, true));
            ip = await s.LegalEntities.CreateAsync(new LegalEntityData(LegalEntityKind.SoleProprietor,
                "Индивидуальный предприниматель Петров Пётр Петрович", "ИП Петров П. П.", "500100732259", null, "304500116000157",
                "г. Тверь, ул. Ткацкая, 3", null, "Петров П. П.", null, VatExempt: true));
            Assert.Equal("legal_entity.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.LegalEntities.CreateAsync(
                new LegalEntityData(LegalEntityKind.SoleProprietor, "ИП", "ИП", "500100732259", null, null, null, null, null, null, false)))).Code);
            ipAccount = await s.LegalEntities.AddAccountAsync(ip, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "40802810138000000002", null));
            Assert.True((await s.LegalEntities.ListAsync()).Single(e => e.Id == ip).Accounts.Single().IsDefault);

            // Заполнение по ИНН — подсказка из реестра (в тестах — подставной справочник).
            var found = await s.Requisites.ForLegalEntityAsync("7707 083 893");
            Assert.Equal(("ООО «ТФ»", "773601001"), (found.ShortName, found.Kpp));
            Assert.Equal("requisites.inn", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Requisites.ForCounterpartyAsync("123"))).Code);
            Assert.Equal("requisites.not_found", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Requisites.ForCounterpartyAsync("500100732259"))).Code);

            // Товар на склад.
            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null,
                await ReasonAsync(f.Org.OrganizationId, StockOperationKind.Receipt), Day, null));
            await s.Documents.SetLineAsync(receipt, f.Sweater, 10, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            // Заказ от ИП: юрлицо в шапке, счёт — в деталях; счёт другого юрлица не подходит.
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null, ip));
            var dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((ip, "ИП Петров П. П.", true), (dto.Details!.LegalEntityId, dto.Details.LegalEntity, dto.Details.VatExempt));
            Assert.Equal("ПАО «Тестбанк», р/с …0002 (основной)", dto.Details.BankAccount);
            var oooAccount = await s.LegalEntities.AddAccountAsync(ooo.Id, new LegalEntityAccountCommand("Банк", "044525225", "40702810938000000001", null));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.SetOrderDetailsAsync(order,
                new SalesOrderDetails(null, null, null, null, f.Org.OwnerUserId, oooAccount), new Dictionary<long, string?>(), dto.RowVersion));
            await s.Sales.SetOrderDetailsAsync(order, new SalesOrderDetails(null, null, null, null, f.Org.OwnerUserId, ipAccount),
                new Dictionary<long, string?>(), dto.RowVersion);
            await s.Sales.SetOrderLineAsync(order, f.Sweater, 4, 2500m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);

            // Счёт на оплату — реквизиты ИП и его расчётный счёт; подпись — предприниматель.
            var invoice = await s.Invoices.CreateAsync(order, null, null);
            var print = await s.Print.InvoiceAsync(invoice);
            Assert.Equal(("Индивидуальный предприниматель Петров Пётр Петрович", "500100732259", null, "40802810138000000002", true, true),
                (print.Seller.Name, print.Seller.Inn, print.Seller.Kpp, print.Requisites.BankAccount, print.Requisites.SoleProprietor, print.Requisites.VatExempt));

            // УПД от ИП: ОГРНИП для подписи есть — предупреждений об ИП нет; в журнал выданных счетов-фактур освобождённый не попадает.
            ship = await s.Sales.CreateShipmentAsync(order);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            var upd = await s.Print.UpdAsync(ship);
            Assert.Equal(("500100732259", "304500116000157"), (upd.Seller.Inn, upd.Requisites.Ogrn));
            Assert.DoesNotContain(upd.Warnings, w => w.Contains("ОГРНИП") || w.Contains("строка 2б"));
            Assert.Empty(await s.VatInvoices.IssuedAsync(new KnitErp.Application.Taxes.VatJournalFilter(Day, Day)));

            // Юрлицо в подтверждённом заказе не меняется; основное — нельзя в архив; основным можно сделать ИП.
            Assert.Equal("sales.not_draft", (await Assert.ThrowsAsync<BusinessRuleException>(async () => await s.Sales.UpdateOrderHeaderAsync(order,
                new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null, ooo.Id), (await s.Sales.GetOrderAsync(order)).RowVersion))).Code);
            ooo = (await s.LegalEntities.ListAsync()).Single(e => e.Id == ooo.Id);
            Assert.Equal("legal_entity.default_archive", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.LegalEntities.SetArchivedAsync(ooo.Id, true, ooo.RowVersion))).Code);
            await s.LegalEntities.MakeDefaultAsync(ip, (await s.LegalEntities.ListAsync()).Single(e => e.Id == ip).RowVersion);
            ooo = (await s.LegalEntities.ListAsync()).Single(e => e.Id == ooo.Id);
            await s.LegalEntities.SetArchivedAsync(ooo.Id, true, ooo.RowVersion);
            var next = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
            Assert.Equal(ip, (await s.Sales.GetOrderAsync(next)).Details!.LegalEntityId);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null, ooo.Id)));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.LegalEntities.CreateAsync(
                new LegalEntityData(LegalEntityKind.Company, "ООО", "ООО", "7707083893", null, null, null, null, null, null, false)));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.LegalEntities.AddAccountAsync(ip, new LegalEntityAccountCommand("Банк", "044525225", "40702810938000000001", null)));
            var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Другой»", null, null, false, true, null));
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, store, null, null, false, null, ip)));
        }
    }

    [SqlFact]
    public async Task Stage_changes_itself_on_confirm_invoice_full_payment_production_and_shipment_only_forward()
    {
        // D110: «выставлен счёт → Ждём оплату», «оплачено 100 % → Оплачен — в производство» и т. д.; только вперёд.
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var rules = await s.Stages.RulesAsync();
        Assert.Equal(6, rules.Count(r => r.StageId != null));
        Assert.Null(rules.Single(r => r.Trigger == StageTrigger.PartiallyPaid).StageId);

        var receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
            new StockDocumentHeader(f.Store, null, null, await ReasonAsync(f.Org.OrganizationId, StockOperationKind.Receipt), Day, null), [(f.Sweater, 10m)]);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day.AddDays(2), null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, 4, 2500m, 20m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Null(await StageAsync(s, order));
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Equal("В обработке", await StageAsync(s, order));

        await s.Invoices.CreateAsync(order, Day.AddDays(5), null);
        Assert.Equal("Ждём оплату", await StageAsync(s, order));

        // Частичная оплата — правила нет, этап прежний; доплата до 100 % — «Оплачен — в производство».
        await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 4_000m, "п/п 1");
        Assert.Equal("Ждём оплату", await StageAsync(s, order));
        await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 6_000m, "п/п 2");
        Assert.Equal("Оплачен — в производство", await StageAsync(s, order));

        await s.Sales.NotifyProductionAsync(order, completed: false);
        Assert.Equal("В работе", await StageAsync(s, order));
        await s.Sales.NotifyProductionAsync(order, completed: true);
        Assert.Equal("Собран", await StageAsync(s, order));

        // Своё правило: частичная оплата → «Ждём оплату» — не откатит этап назад.
        var waiting = (await s.Stages.ListAsync()).Single(x => x.Name == "Ждём оплату").Id;
        await s.Stages.SetRuleAsync(StageTrigger.PartiallyPaid, waiting);
        await s.Sales.NotifyProductionAsync(order, completed: false);
        Assert.Equal("Собран", await StageAsync(s, order));

        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        Assert.Equal("Отгружен", await StageAsync(s, order));
        Assert.Contains(await s.Db.AuditEntries.Where(a => a.EntityType == nameof(SalesOrder) && a.EntityId == order.ToString()).Select(a => a.Reason).ToListAsync(),
            r => r != null && r.Contains("автоматически — Отгружен полностью"));

        // Правило выключено — событие этап не меняет; чужой этап — «не найдено».
        await s.Stages.SetRuleAsync(StageTrigger.Invoiced, null);
        Assert.Null((await s.Stages.RulesAsync()).Single(r => r.Trigger == StageTrigger.Invoiced).StageId);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Stages.SetRuleAsync(StageTrigger.Invoiced, 999_999_999));
    }

    private static async Task<string?> StageAsync(Services s, long order) =>
        await (from o in s.Db.SalesOrders.AsNoTracking()
               join st in s.Db.SalesOrderStages.AsNoTracking() on o.StageId equals st.Id
               where o.Id == order
               select st.Name).SingleOrDefaultAsync();

    private async Task<long?> ReasonAsync(long organizationId, StockOperationKind kind)
    {
        await using var db = host.NewDb();
        return await db.OperationReasons.Where(r => r.OrganizationId == organizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstAsync();
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Customer, long Senior);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, customer;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        }

        long senior;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == senior)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, customer, senior);
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
