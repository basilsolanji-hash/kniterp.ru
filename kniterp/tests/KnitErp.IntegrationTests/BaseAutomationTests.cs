using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Automation;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Сессия № 1, этап 2 (D145–D150): настройки автоматизации, курсы ЦБ и ручные курсы, регулярные операции, автозакрытие заказов,
/// напоминания об оплате (письма и отписка), загрузка XML УПД поставщика. Данные вымышленные; часы теста — host.Clock (09.10.2026).
/// </summary>
public sealed class BaseAutomationTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 962_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Automation_settings_defaults_rights_and_validation()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var d = await s.Automation.GetAsync();
            Assert.Equal((false, 3, 7, true, true, true, true), (d.PaymentRemindersEnabled, d.RemindDaysBefore, d.OverdueRepeatDays, d.AutoCloseSalesOrders,
                d.AutoClosePurchaseOrders, d.RecurringOperationsEnabled, d.CanEdit));

            // Неверное значение — отказ; тем же сервисом верное — сохраняется (вкладка не вставляет вторую строку).
            Assert.Equal("automation.remind_days", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Automation.SaveAsync(new AutomationSettingsCommand(true, 31, 7, true, true, true), null))).Code);
            await s.Automation.SaveAsync(new AutomationSettingsCommand(true, 2, 5, false, true, true), null);
            d = await s.Automation.GetAsync();
            Assert.Equal((true, 2, 5, false), (d.PaymentRemindersEnabled, d.RemindDaysBefore, d.OverdueRepeatDays, d.AutoCloseSalesOrders));
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.Automation.SaveAsync(new AutomationSettingsCommand(true, 2, 5, true, true, true), null));
        }

        // Менеджер продаж видит, но не меняет; кладовщик без цен — не видит.
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            var d = await m.Automation.GetAsync();
            Assert.False(d.CanEdit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Automation.SaveAsync(new AutomationSettingsCommand(false, 3, 7, true, true, true), d.RowVersion));
        }

        await using (var k = host.As(f.Storekeeper, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Automation.GetAsync());
        }

        await using var db = host.NewDb();
        Assert.Single(await db.AuditEntries.Where(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.AutomationSettingsChanged).ToListAsync());
    }

    [SqlFact]
    public async Task Cbr_rates_import_is_idempotent_manual_rate_wins_and_xxe_is_rejected()
    {
        var f = await SetUpAsync();
        var code = UniqueCurrency();
        await using (var db = host.NewDb())
        {
            var importer = new CbrRateImporter(db, host.Clock);
            var (date, changed) = await importer.ImportAsync(CbrXml("08.10.2026", (code, 10, "123,4567"), ("BAD", 0, "x")));
            Assert.Equal((new DateOnly(2026, 10, 8), 1), (date, changed));
            Assert.Equal(0, (await importer.ImportAsync(CbrXml("08.10.2026", (code, 10, "123,4567")))).Changed); // повтор — без дублей
            Assert.Equal(1, (await importer.ImportAsync(CbrXml("08.10.2026", (code, 10, "124,0000")))).Changed); // уточнение курса той же даты

            // Внешние сущности (XXE) и DTD — отказ, файл не читается.
            var xxe = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><ValCurs Date=\"08.10.2026\">&e;</ValCurs>");
            Assert.Equal("xml.invalid", (await Assert.ThrowsAsync<BusinessRuleException>(() => importer.ImportAsync(xxe))).Code);
        }

        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        // Курс на дату — последний не позже неё; рубль — 1; пересчёт — до копеек.
        var rate = await s.CurrencyRates.RateAsync(code, Day);
        Assert.Equal((12.4m, new DateOnly(2026, 10, 8), CurrencyRateSource.Cbr), (rate.PerUnit, rate.Date, rate.Source));
        Assert.Equal(1240m, await s.CurrencyRates.ConvertAsync(100m, code, "RUB", Day));
        Assert.Equal("currency.rate.missing", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.CurrencyRates.RateAsync(code, new DateOnly(2026, 10, 7)))).Code);

        // Ручной курс на ту же дату важнее ЦБ, только в своей организации; повторный ввод — изменение, в журнал.
        await s.CurrencyRates.SetManualAsync(code.ToLowerInvariant(), new DateOnly(2026, 10, 8), 1, 13m, null);
        await s.CurrencyRates.SetManualAsync(code, new DateOnly(2026, 10, 8), 1, 13.5m, null);
        Assert.Equal((13.5m, CurrencyRateSource.Manual), ((await s.CurrencyRates.RateAsync(code, Day)).PerUnit, (await s.CurrencyRates.RateAsync(code, Day)).Source));
        Assert.Contains((await s.CurrencyRates.ListAsync(Day)).Rates, r => r.CurrencyCode == code && r.Source == CurrencyRateSource.Manual);
        Assert.Equal("currency.code", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.CurrencyRates.SetManualAsync("US", Day, 1, 1m, null))).Code);
        Assert.Equal("currency.rate.value", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.CurrencyRates.SetManualAsync(code, Day, 1, 0m, null))).Code);

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        Assert.Equal(CurrencyRateSource.Cbr, (await o.CurrencyRates.RateAsync(code, Day)).Source);

        await using (var k = host.As(f.Storekeeper, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.CurrencyRates.SetManualAsync(code, Day, 1, 1m, null));
        }
    }

    [SqlFact]
    public async Task Recurring_operation_creates_drafts_on_schedule_and_human_posts_or_skips()
    {
        var f = await SetUpAsync();
        long template;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Аренда — 31-го числа: в сентябре черновик на 30.09; начало 01.09, часы — 09.10.
            template = await s.Recurring.CreateAsync(new RecurringOperationData("Аренда цеха", MoneyOperationKind.Expense, f.Cash, null, 1_000m, "ООО «Арендодатель»",
                "Аренда за месяц", RecurrencePeriod.Monthly, 31, new DateOnly(2026, 9, 1), null));
            Assert.Equal("recurring.day", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Recurring.CreateAsync(new RecurringOperationData("Связь",
                MoneyOperationKind.Expense, f.Cash, null, 10m, null, "Связь", RecurrencePeriod.Weekly, 8, Day, null)))).Code);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await RecurringOperationService.GenerateAsync(db, host.Clock));
            Assert.Equal(0, await RecurringOperationService.GenerateAsync(db, host.Clock)); // повтор — без дублей
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var overview = await s.Recurring.GetAsync();
            var run = Assert.Single(overview.Runs);
            Assert.Equal((new DateOnly(2026, 9, 30), RecurringRunStatus.Pending, 1_000m), (run.DueDate, run.Status, run.Amount));
            Assert.Equal(new DateOnly(2026, 10, 31), overview.Templates.Single().NextDate);

            // Проведение — денежная операция обычным путём; повтор нажатия — отказ, второй операции нет.
            var op = await s.Recurring.PostRunAsync(run.Id, null, null, run.RowVersion);
            await using (var db = host.NewDb())
            {
                var money = await db.MoneyOperations.SingleAsync(m => m.Id == op);
                Assert.Equal((new DateOnly(2026, 9, 30), 1_000m, MoneyOperationKind.Expense), (money.OperationDate, money.Amount, money.Kind));
            }

            var posted = (await s.Recurring.GetAsync()).Runs.Single();
            Assert.Equal(RecurringRunStatus.Posted, posted.Status);
            await Assert.ThrowsAnyAsync<Exception>(() => s.Recurring.PostRunAsync(run.Id, null, null, posted.RowVersion));
            await using (var db = host.NewDb())
            {
                Assert.Equal(1, await db.MoneyOperations.CountAsync(m => m.OrganizationId == f.Org.OrganizationId && m.Basis == "Аренда за месяц"));
            }
        }

        // Следующий месяц: черновик 31.10 — пропуск только с причиной.
        host.Clock.UtcNow = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc);
        try
        {
            await using (var db = host.NewDb())
            {
                Assert.Equal(1, await RecurringOperationService.GenerateAsync(db, host.Clock));
            }

            await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
            var run = (await s.Recurring.GetAsync()).Runs.Single(r => r.Status == RecurringRunStatus.Pending);
            Assert.Equal(new DateOnly(2026, 10, 31), run.DueDate);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Recurring.SkipRunAsync(run.Id, " ", run.RowVersion));
            await s.Recurring.SkipRunAsync(run.Id, "Арендодатель дал отсрочку", run.RowVersion);

            // Выключено в настройках — черновики не создаются; отключённый шаблон — тоже.
            await s.Automation.SaveAsync(new AutomationSettingsCommand(false, 3, 7, true, true, false), null);
            host.Clock.UtcNow = new DateTime(2026, 12, 2, 5, 0, 0, DateTimeKind.Utc);
            await using (var db = host.NewDb())
            {
                Assert.Equal(0, await RecurringOperationService.GenerateAsync(db, host.Clock));
            }

            // Чужая организация — «не найдено»; без права — отказ.
            var other = await CreateOrgAsync();
            await using var o = host.As(other.OwnerUserId, other.OrganizationId);
            await Assert.ThrowsAsync<NotFoundException>(() => o.Recurring.SkipRunAsync(run.Id, "x", run.RowVersion));
            await using var k = host.As(f.Storekeeper, f.Org.OrganizationId);
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Recurring.CreateAsync(new RecurringOperationData("Связь", MoneyOperationKind.Expense, f.Cash,
                null, 10m, null, "Связь", RecurrencePeriod.Monthly, 1, Day, null)));
        }
        finally
        {
            host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        }
    }

    [SqlFact]
    public async Task Fully_shipped_and_paid_orders_close_automatically_but_not_with_open_return()
    {
        var f = await SetUpAsync();
        long closedOrder, withDraft, unpaid, po;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            closedOrder = await ShippedOrderAsync(s, f, 2, 1_000m, 2_000m);
            withDraft = await ShippedOrderAsync(s, f, 1, 1_000m, 1_000m);
            await s.Sales.CreateReturnAsync(withDraft); // черновик возврата — заказ не закрывается
            unpaid = await ShippedOrderAsync(s, f, 1, 1_000m, 500m);

            po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, f.Supplier, f.Store, null, null, true, null));
            await s.Purchases.SetOrderLineAsync(po, f.Wool, 3, 100m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(po);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Purchases.CreatePaymentAsync(Day, f.Supplier, po, 300m, null);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(2, await OrderAutoClose.SalesAsync(db, host.Clock, f.Org.OrganizationId, default) + await OrderAutoClose.PurchasesAsync(db, host.Clock,
                f.Org.OrganizationId, default));
            Assert.Equal(0, await OrderAutoClose.SalesAsync(db, host.Clock, f.Org.OrganizationId, default)); // повтор — ничего
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal(SalesOrderStatus.Closed, (await s.Sales.GetOrderAsync(closedOrder)).Status);
            Assert.Equal(SalesOrderStatus.Confirmed, (await s.Sales.GetOrderAsync(withDraft)).Status);
            Assert.Equal(SalesOrderStatus.Confirmed, (await s.Sales.GetOrderAsync(unpaid)).Status);
            Assert.Equal(PurchaseOrderStatus.Closed, (await s.Purchases.GetOrderAsync(po)).Status);

            // Выключено — не закрывается; вернуть в работу можно как обычно.
            await s.Automation.SaveAsync(new AutomationSettingsCommand(false, 3, 7, false, false, true), null);
            await s.Sales.CreatePaymentAsync(Day, f.Customer, unpaid, 500m, null);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(0, await OrderAutoClose.RunAsync(db, host.Clock));
            var audit = await db.AuditEntries.Where(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.OrderAutoClosed).ToListAsync();
            Assert.Equal(2, audit.Count);
            Assert.All(audit, a => Assert.Null(a.ActorUserId));
        }
    }

    [SqlFact]
    public async Task Payment_reminders_are_queued_once_overdue_repeats_and_unsubscribe_stops_them()
    {
        var f = await SetUpAsync();
        long order, invoice;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await ShippedOrderAsync(s, f, 1, 1_000m, 400m);
            invoice = await s.Invoices.CreateAsync(order, Day.AddDays(2), null);
            await s.OrderDocuments.SetCustomerMailAsync(f.Customer, "buyer@test.local", false, null);
        }

        // Выключено по умолчанию — писем нет.
        await using (var db = host.NewDb())
        {
            Assert.Equal(0, await PaymentReminders.QueueAsync(db, host.Clock, "https://erp.test.local"));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Automation.SaveAsync(new AutomationSettingsCommand(true, 3, 5, false, false, true), null);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await PaymentReminders.QueueAsync(db, host.Clock, "https://erp.test.local"));
            Assert.Equal(0, await PaymentReminders.QueueAsync(db, host.Clock, "https://erp.test.local")); // повтор — без дублей
            var m = await db.SalesMailMessages.Include(x => x.Attachments).SingleAsync(x => x.OrganizationId == f.Org.OrganizationId);
            Assert.Equal((SalesMailKind.PaymentReminder, "buyer@test.local", true), (m.Kind, m.Recipients, m.IsAuto));
            Assert.Contains("11.10.2026", m.Body); // {Срок}
            Assert.Contains("600,00", m.Body); // неоплаченный остаток
            Assert.Contains("/account/payment-reminders-off/", m.Body);
            Assert.Equal(SalesMailDocument.Invoice, m.Attachments.Single().Document);
        }

        // Ответственный видит срок в колокольчике.
        await using (var db = host.NewDb())
        {
            var guard = new AccessGuard(db, new TestUser { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId, ClientAddress = host.ClientAddress }, host.Clock);
            var alerts = await PaymentReminders.AlertsAsync(db, await guard.CurrentAsync(), Day, default);
            Assert.Contains(alerts, a => a.InvoiceId == invoice && a.Outstanding == 600m);
        }

        // Просрочка: 12.10 — первое, 17.10 — второе (повтор через 5 дней), 16.10 — ничего нового.
        try
        {
            foreach (var (day, expected) in new[] { (12, 1), (16, 0), (17, 1) })
            {
                host.Clock.UtcNow = new DateTime(2026, 10, day, 6, 0, 0, DateTimeKind.Utc);
                await using var db = host.NewDb();
                Assert.Equal(expected, await PaymentReminders.QueueAsync(db, host.Clock, "https://erp.test.local"));
            }

            // Отписка по ссылке — любой ключ даёт одинаковый ответ; дальше писем нет.
            await using (var db = host.NewDb())
            {
                Assert.False(await PaymentReminders.UnsubscribeAsync(db, Guid.NewGuid(), host.Clock.UtcNow));
                var token = (await db.CustomerMailSettings.SingleAsync(x => x.OrganizationId == f.Org.OrganizationId && x.CounterpartyId == f.Customer)).ReminderToken;
                Assert.True(await PaymentReminders.UnsubscribeAsync(db, token, host.Clock.UtcNow));
            }

            host.Clock.UtcNow = new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc);
            await using (var db = host.NewDb())
            {
                Assert.Equal(0, await PaymentReminders.QueueAsync(db, host.Clock, null));
                Assert.Equal(3, await db.SalesMailMessages.CountAsync(x => x.OrganizationId == f.Org.OrganizationId));
                Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.PaymentReminderUnsubscribed));
            }
        }
        finally
        {
            host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        }
    }

    [SqlFact]
    public async Task Supplier_upd_xml_creates_receipt_draft_with_matching_binding_and_no_duplicates()
    {
        var f = await SetUpAsync();
        long po, hat;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.RawMaterial, unit, null));
            await s.Suppliers.SetAsync(f.Wool, f.Supplier, "ART-77", null, true); // артикул поставщика
            po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, f.Supplier, f.Store, null, null, true, null));
            await s.Purchases.SetOrderLineAsync(po, f.Wool, 10, 100m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.SetOrderLineAsync(po, hat, 5, 50m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
        }

        // Строка 1 — по артикулу поставщика, 2 — неизвестный код (привяжем к шапке), 3 — неизвестная (пропустим).
        var xml = UpdXml("УПД-15", f.SupplierInn, f.OrgInn, ("ART-77", "Пряжа шерсть", 8m, 100m), ("X-1", "Шапка зимняя", 5m, 52m), ("Z-9", "Доставка", 1m, 300m));
        await using var s2 = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var p = await s2.UpdImport.PreviewAsync(xml, null);
        Assert.Equal((f.Supplier, po, 2, 0), (p.SupplierId, p.OrderId, p.Unmatched, p.Problems.Count));
        Assert.Equal((UpdMatch.SupplierArticle, f.Wool, true), (p.Lines[0].MatchedBy, p.Lines[0].ItemId, p.Lines[0].InOrder));
        Assert.Contains(p.OrderItems, i => i.Id == hat);

        Assert.Equal("purchase.upd.unmatched", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s2.UpdImport.ImportAsync(xml, "upd.xml", po, [], false))).Code);
        var result = await s2.UpdImport.ImportAsync(xml, "upd.xml", po, [new UpdLineDecision(2, hat), new UpdLineDecision(3, null)], true);
        Assert.Equal((2, 1, 1), (result.Lines, result.Skipped, result.BindingsSaved));
        var receipt = await s2.Documents.GetAsync(result.ReceiptId);
        Assert.Equal((StockOperationKind.Receipt, StockDocumentStatus.Draft, po), (receipt.Kind, receipt.Status, receipt.PurchaseOrderId));
        Assert.Equal(8m, receipt.Lines.Single(l => l.ItemId == f.Wool).Quantity);
        Assert.Equal(5m, receipt.Lines.Single(l => l.ItemId == hat).Quantity);
        Assert.Contains("УПД-15", receipt.Comment);

        // Привязка запомнилась: при следующем УПД шапка находится по артикулу. Тот же файл — отказ.
        Assert.Equal("purchase.upd.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s2.UpdImport.ImportAsync(xml, "upd.xml", po, [new UpdLineDecision(2, hat), new UpdLineDecision(3, null)], true))).Code);
        Assert.NotNull((await s2.UpdImport.PreviewAsync(xml, po)).ImportedReceipt);
        var next = await s2.UpdImport.PreviewAsync(UpdXml("УПД-16", f.SupplierInn, f.OrgInn, ("X-1", "Шапка", 1m, 50m)), po);
        Assert.Equal(UpdMatch.SupplierArticle, next.Lines.Single().MatchedBy);

        // Чужой покупатель, неизвестный поставщик, не УПД, XXE — отказы.
        var foreign = await s2.UpdImport.PreviewAsync(UpdXml("УПД-17", f.SupplierInn, "7707083893", ("ART-77", "Пряжа", 1m, 1m)), null);
        Assert.Contains(foreign.Problems, x => x.Contains("не ваше юрлицо"));
        Assert.Contains((await s2.UpdImport.PreviewAsync(UpdXml("УПД-18", "500100732259", f.OrgInn, ("A", "B", 1m, 1m)), null)).Problems,
            x => x.Contains("Поставщика с ИНН"));
        Assert.Equal("purchase.upd.format", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s2.UpdImport.PreviewAsync(Encoding.UTF8.GetBytes("<root/>"), null))).Code);
        var xxe = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE Файл [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Файл>&e;</Файл>");
        Assert.Equal("xml.invalid", (await Assert.ThrowsAsync<BusinessRuleException>(() => s2.UpdImport.PreviewAsync(xxe, null))).Code);

        // Без права на цены — отказ.
        await using var k = host.As(f.Storekeeper, f.Org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => k.UpdImport.PreviewAsync(xml, null));
    }

    [SqlFact]
    public async Task Own_sales_upd_xml_is_read_back_by_supplier_parser()
    {
        // Наш исходящий УПД (продажи, windows-1251) — тот же формат, что приходит от поставщика.
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var buyerInn = Inn(Interlocked.Increment(ref _innSeed));
        var buyer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель с ИНН»", buyerInn, null, false, true, null));
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, buyer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, 3, 1_000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        var file = await s.Print.UpdXmlAsync(ship, null, host.Clock.UtcNow);
        var parsed = SupplierUpdXml.Parse(file.Content);
        Assert.Equal((f.OrgInn, buyerInn), (parsed.SellerInn, parsed.BuyerInn));
        var line = Assert.Single(parsed.Lines);
        Assert.Equal(("СВ-1", 3m, "Свитер"), (line.Code, line.Quantity, line.Name));
    }

    private static async Task<long> ShippedOrderAsync(Services s, Fixture f, decimal quantity, decimal price, decimal paid)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, quantity, price, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        await s.Sales.CreatePaymentAsync(Day, f.Customer, order, paid, null);
        return order;
    }

    private static int _currency;

    /// <summary>Выдуманный трёхбуквенный код — курсы ЦБ общие для всех тестовых организаций базы класса.</summary>
    private static string UniqueCurrency()
    {
        var n = Interlocked.Increment(ref _currency);
        return "Q" + (char)('A' + n / 26 % 26) + (char)('A' + n % 26);
    }

    private static byte[] CbrXml(string date, params (string Code, int Nominal, string Value)[] rows) =>
        Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"utf-8\"?><ValCurs Date=\"{date}\" name=\"Foreign Currency Market\">" +
                               string.Concat(rows.Select((r, i) => $"<Valute ID=\"R{i}\"><NumCode>999</NumCode><CharCode>{r.Code}</CharCode><Nominal>{r.Nominal}</Nominal>" +
                                                                   $"<Name>Тест</Name><Value>{r.Value}</Value></Valute>")) + "</ValCurs>");

    private static byte[] UpdXml(string number, string sellerInn, string buyerInn, params (string Code, string Name, decimal Quantity, decimal Price)[] lines)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rows = string.Concat(lines.Select((l, i) =>
            $"<СведТов НомСтр=\"{i + 1}\" НаимТов=\"{l.Name}\" КолТов=\"{l.Quantity.ToString(inv)}\" ЦенаТов=\"{l.Price.ToString(inv)}\" " +
            $"СтТовБезНДС=\"{(l.Quantity * l.Price).ToString(inv)}\" НалСт=\"без НДС\" СтТовУчНал=\"{(l.Quantity * l.Price).ToString(inv)}\">" +
            $"<ДопСведТов ПрТовРаб=\"1\" КодТов=\"{l.Code}\" НаимЕдИзм=\"шт\"/></СведТов>"));
        return Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Файл ИдФайл=\"ON_NSCHFDOPPR_test\" ВерсФорм=\"5.03\"><Документ КНД=\"1115131\" Функция=\"СЧФДОП\">" +
            $"<СвСчФакт НомерДок=\"{number}\" ДатаДок=\"09.10.2026\">" +
            $"<СвПрод><ИдСв><СвЮЛУч НаимОрг=\"ООО «Пряжа»\" ИННЮЛ=\"{sellerInn}\" КПП=\"770101001\"/></ИдСв></СвПрод>" +
            $"<СвПокуп><ИдСв><СвЮЛУч НаимОрг=\"Мы\" ИННЮЛ=\"{buyerInn}\"/></ИдСв></СвПокуп><ДенИзм КодОКВ=\"643\"/></СвСчФакт>" +
            $"<ТаблСчФакт>{rows}<ВсегоОпл СтТовУчНалВсего=\"0\"/></ТаблСчФакт></Документ></Файл>");
    }

    private sealed record Fixture(CreatedOrganization Org, string OrgInn, long Store, long Sweater, long Wool, long Customer, long Supplier, string SupplierInn,
        long Cash, long Manager, long Storekeeper);

    private async Task<Fixture> SetUpAsync()
    {
        var (org, inn) = await CreateOrgWithInnAsync();
        var supplierInn = Inn(Interlocked.Increment(ref _innSeed));
        long store, sweater, wool, customer, supplier, cash, manager, storekeeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", supplierInn, null, true, false, null));
            var entity = (await s.LegalEntities.ListAsync()).Single().Id;
            cash = await s.LegalEntities.AddCashAsync(entity, "Касса");
            await s.Sales.CreatePaymentAsync(Day.AddDays(-60), customer, null, 50_000m, null, null, cash); // деньги в кассе для выдач
            long reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt).Select(r => r.Id).FirstAsync();
            }

            var stock = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, Day.AddDays(-30), null));
            await s.Documents.SetLineAsync(stock, sweater, 100, (await s.Documents.GetAsync(stock)).RowVersion);
            await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"m-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null))).UserId;
            storekeeper = (await s.Access.InviteAsync(new InviteUserCommand($"k-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            foreach (var id in new[] { manager, storekeeper })
            {
                (await db.Users.SingleAsync(u => u.Id == id)).Activate();
            }

            await db.SaveChangesAsync();
        }

        return new Fixture(org, inn, store, sweater, wool, customer, supplier, supplierInn, cash, manager, storekeeper);
    }

    private static string Inn(int seed)
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = seed.ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    private async Task<CreatedOrganization> CreateOrgAsync() => (await CreateOrgWithInnAsync()).Org;

    private async Task<(CreatedOrganization Org, string Inn)> CreateOrgWithInnAsync()
    {
        var inn = Inn(Interlocked.Increment(ref _innSeed));
        await using var s = host.As(null, null);
        return (await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU")), inn);
    }
}
