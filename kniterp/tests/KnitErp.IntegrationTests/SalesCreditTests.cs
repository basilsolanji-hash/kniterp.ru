using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Этап 2 продаж (D1105, D1111–D1114): кредитный лимит и просрочка блокируют подтверждение и проведение отгрузки, обход — только
/// с правом и причиной, дебиторка по срокам и менеджерам, напоминания, текст письма, блок главного экрана, права и организация.
/// </summary>
public sealed class SalesCreditTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 812_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly DateTime Now = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

    [SqlFact]
    public async Task Credit_limit_blocks_confirmation_and_passes_within_limit()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await StockAsync(s, f, 200);
        var agreement = await s.Agreements.CreateAsync(Terms("Отсрочка и лимит", f.Customer, limit: 100_000m));

        // 50 000 в пределах лимита 100 000 — подтверждается и отгружается; долг 50 000.
        var first = await OrderAsync(s, f, f.Customer, 50);
        Assert.Equal(agreement, (await s.Sales.GetOrderAsync(first)).AgreementId);
        await s.Sales.ConfirmOrderAsync(first, (await s.Sales.GetOrderAsync(first)).RowVersion);
        await ShipAsync(s, first);

        // Второй заказ на 60 000: 50 000 + 60 000 > 100 000 — отказ с суммами; в карточке — превышение на 10 000.
        var second = await OrderAsync(s, f, f.Customer, 60);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Sales.ConfirmOrderAsync(second, (await s.Sales.GetOrderAsync(second)).RowVersion));
        Assert.Equal("sales.credit.limit", error.Code);
        var credit = await s.Sales.GetCreditAsync(second);
        Assert.Equal((100_000m, 50_000m, 60_000m, 10_000m, true), (credit.Check.Limit!.Value, credit.Check.Debt, credit.Check.Amount, credit.Check.Excess!.Value, credit.Blocked));
        Assert.StartsWith("соглашение СГ-", credit.Check.LimitSource);
        Assert.Equal(SalesOrderStatus.Draft, (await s.Sales.GetOrderAsync(second)).Status);

        // Оплата 20 000: долг 30 000 + 60 000 = 90 000 — в пределах; отгрузка тоже в пределах.
        await s.Sales.CreatePaymentAsync(Day, f.Customer, first, 20_000m, null);
        Assert.False((await s.Sales.GetCreditAsync(second)).Blocked);
        await s.Sales.ConfirmOrderAsync(second, (await s.Sales.GetOrderAsync(second)).RowVersion);
        await ShipAsync(s, second);
        Assert.Equal(90_000m, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Debt);

        // Лимит из договора соглашения, если в соглашении его нет: 5 000 по договору — заказ на 6 000 не подтверждается.
        long contract;
        await using (var db = host.NewDb())
        {
            var c = CounterpartyContract.Create(f.Org.OrganizationId, f.Other,
                new ContractTerms("Д-7", Day.AddYears(-1), ContractKind.Sale, null, null, null, 15, 5_000m, null), Now, f.Org.OwnerUserId);
            db.CounterpartyContracts.Add(c);
            await db.SaveChangesAsync();
            contract = c.Id;
        }

        await s.Agreements.CreateAsync(Terms("По договору", f.Other, contract: contract));
        var other = await OrderAsync(s, f, f.Other, 6);
        Assert.Equal("sales.credit.limit", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Sales.ConfirmOrderAsync(other, (await s.Sales.GetOrderAsync(other)).RowVersion))).Code);
        credit = await s.Sales.GetCreditAsync(other);
        Assert.Equal((5_000m, "договор № Д-7", 15), (credit.Check.Limit!.Value, credit.Check.LimitSource, credit.Check.PaymentDays!.Value));

        // Без соглашения лимита нет — проверки нет.
        var free = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Без условий»", null, null, false, true, null));
        var freeOrder = await OrderAsync(s, f, free, 150);
        await s.Sales.ConfirmOrderAsync(freeOrder, (await s.Sales.GetOrderAsync(freeOrder)).RowVersion);
        Assert.Null((await s.Sales.GetCreditAsync(freeOrder)).Check.Limit);
    }

    [SqlFact]
    public async Task Overdue_blocks_shipment_posting_and_storekeeper_sees_no_amounts()
    {
        var f = await SetUpAsync();
        long late, next;
        try
        {
            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                await StockAsync(s, f, 100);
                await s.Agreements.CreateAsync(Terms("Отсрочка 10 дней", f.Customer, days: 10));
                late = await OrderAsync(s, f, f.Customer, 10);
                await s.Sales.ConfirmOrderAsync(late, (await s.Sales.GetOrderAsync(late)).RowVersion);
                await ShipAsync(s, late);

                // Срок оплаты ещё не прошёл — следующий заказ подтверждается, черновик отгрузки готов.
                next = await OrderAsync(s, f, f.Customer, 5);
                await s.Sales.ConfirmOrderAsync(next, (await s.Sales.GetOrderAsync(next)).RowVersion);
                await s.Sales.CreateShipmentAsync(next);

                // Покупатель без отсрочки: долг старый, но просрочка не проверяется (лимита тоже нет).
                await ShipAsync(s, await ConfirmedAsync(s, f, f.Other, 3));
            }

            // Через 15 дней: срок 19.10 прошёл, 10 000 просрочено на 5 дней.
            host.Clock.UtcNow = Now.AddDays(15);
            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                var ship = (await s.Sales.GetOrderAsync(next)).Documents.Single(d => d.Status == StockDocumentStatus.Draft).Id;
                var error = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                    await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion));
                Assert.Equal("sales.credit.overdue", error.Code);
                Assert.Contains("(до 5 дн.)", error.Message);
                Assert.Equal(StockDocumentStatus.Draft, (await s.Documents.GetAsync(ship)).Status);

                var credit = await s.Sales.GetCreditAsync(next);
                Assert.Equal((10_000m, 5, true), (credit.Check.Overdue, credit.Check.OverdueDays, credit.Blocked));

                var third = await OrderAsync(s, f, f.Customer, 1);
                Assert.Equal("sales.credit.overdue", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                    await s.Sales.ConfirmOrderAsync(third, (await s.Sales.GetOrderAsync(third)).RowVersion))).Code);

                // У покупателя без отсрочки просрочки нет — заказ подтверждается.
                await ConfirmedAsync(s, f, f.Other, 1);
            }

            // Кладовщик без права на цены: отказ без сумм.
            await using (var k = host.As(f.Senior, f.Org.OrganizationId))
            {
                var ship = (await k.Sales.GetOrderAsync(next)).Documents.Single(d => d.Status == StockDocumentStatus.Draft).Id;
                var error = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                    await k.Documents.PostAsync(ship, (await k.Documents.GetAsync(ship)).RowVersion));
                Assert.Equal("sales.credit.blocked", error.Code);
                Assert.DoesNotContain("10", error.Message.Replace(next.ToString(), "").Replace((await k.Sales.GetOrderAsync(next)).Number, ""));
                await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.GetCreditAsync(next));
            }

            // Оплата погашает просрочку — отгрузка проводится.
            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                await s.Sales.CreatePaymentAsync(Day.AddDays(15), f.Customer, late, 10_000m, null);
                var ship = (await s.Sales.GetOrderAsync(next)).Documents.Single(d => d.Status == StockDocumentStatus.Draft).Id;
                await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
                Assert.Equal(StockDocumentStatus.Posted, (await s.Documents.GetAsync(ship)).Status);
            }
        }
        finally
        {
            host.Clock.UtcNow = Now;
        }
    }

    [SqlFact]
    public async Task Override_needs_permission_and_reason_and_is_audited()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await StockAsync(s, f, 100);
            await s.Agreements.CreateAsync(Terms("Лимит 10 000", f.Customer, limit: 10_000m));
            order = await OrderAsync(s, f, f.Customer, 15);
        }

        // Менеджер продаж без права обхода: подтвердить нельзя, разрешить — тоже; в карточке разрешения нет.
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            var dto = await m.Sales.GetOrderAsync(order);
            Assert.Equal("sales.credit.limit", (await Assert.ThrowsAsync<BusinessRuleException>(() => m.Sales.ConfirmOrderAsync(order, dto.RowVersion))).Code);
            Assert.False((await m.Sales.GetCreditAsync(order)).CanApprove);
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Sales.ApproveCreditAsync(order, "Клиент надёжный", dto.RowVersion));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var dto = await s.Sales.GetOrderAsync(order);
            Assert.True((await s.Sales.GetCreditAsync(order)).CanApprove);
            Assert.Equal("sales.credit.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.ApproveCreditAsync(order, "  ", dto.RowVersion))).Code);
            await s.Sales.ApproveCreditAsync(order, "Гарантийное письмо директора", dto.RowVersion);
            var credit = await s.Sales.GetCreditAsync(order);
            Assert.True(credit.Covered);
            Assert.False(credit.Blocked);
            Assert.Equal(("Гарантийное письмо директора", 15_000m, 10_000m), (credit.Approval!.Reason, credit.Approval.Amount, credit.Approval.Limit!.Value));
            Assert.Equal("sales.credit.not_needed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Sales.ApproveCreditAsync(order, "ещё раз", dto.RowVersion))).Code);

            var audit = await s.Db.AuditEntries.SingleAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.SalesCreditOverride);
            Assert.Equal((order.ToString(), "Гарантийное письмо директора"), (audit.EntityId, audit.After));
            Assert.Contains("лимит 10000.00", audit.Before);
        }

        // Разрешение действует на подтверждение и отгрузку этого заказа — в пределах его суммы — даже у менеджера без права.
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            await m.Sales.ConfirmOrderAsync(order, (await m.Sales.GetOrderAsync(order)).RowVersion);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await ShipAsync(s, order);

            // Другой заказ того же покупателя разрешением не покрыт.
            var another = await OrderAsync(s, f, f.Customer, 1);
            Assert.Equal("sales.credit.limit", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Sales.ConfirmOrderAsync(another, (await s.Sales.GetOrderAsync(another)).RowVersion))).Code);
        }
    }

    [SqlFact]
    public async Task Receivables_by_age_and_manager_with_reminders_letter_dashboard_and_excel()
    {
        var f = await SetUpAsync();
        // Сроки считаются от «сегодня» = через 200 дней после начала: отгрузки идут вперёд по часам теста (роли действуют с создания).
        var today = Day.AddDays(200);
        var backdate = new List<(long Order, DateOnly Date)>();
        long withManager, byOrder, nobody;
        try
        {
            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                await StockAsync(s, f, 100);
                withManager = f.Customer;
                byOrder = f.Other;
                nobody = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Ничей»", null, null, false, true, null));
                await s.Agreements.SetCustomerTermsAsync(withManager, null, f.Manager, null);
                await s.Agreements.CreateAsync(Terms("Отсрочка 10 дней", withManager, days: 10));
            }

            // «Магазин» (отсрочка 10 дней): 10 000 сто дней назад, 5 000 — 45 дней назад, 3 000 — 5 дней назад; оплата 4 000 гасит старую.
            await ShipOnAsync(withManager, 10, -100);
            await ShipOnAsync(withManager, 5, -45);
            await ShipOnAsync(withManager, 3, -5);
            // «Бутик» без отсрочки: 2 000 двести дней назад; менеджер — ответственный последнего заказа (Владелец).
            await ShipOnAsync(byOrder, 2, -200);
            // «Ничей»: 1 000 вчера, ответственный у заказа снят.
            var nobodyOrder = await ShipOnAsync(nobody, 1, -1);
            await using (var db = host.NewDb())
            {
                foreach (var (order, date) in backdate)
                {
                    await db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[stock_documents] SET [DocumentDate] = {0} WHERE [SalesOrderId] = {1}", date, order);
                }
            }

            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                await s.Sales.CreatePaymentAsync(today.AddDays(-10), withManager, null, 4_000m, null);
                await s.Sales.SetOrderDetailsAsync(nobodyOrder, new SalesOrderDetails(null, null, null, null, null), new Dictionary<long, string?>(),
                    (await s.Sales.GetOrderAsync(nobodyOrder)).RowVersion);

                var report = await s.Sales.ReceivablesAsync(new ReceivablesFilter());
                Assert.Equal(today, report.AsOf);
                Assert.False(report.OwnOnly);
                Assert.Equal([f.Org.OwnerUserId, f.Manager, (long?)null], report.Groups.Select(g => g.ManagerId)); // по имени: «Владелец», «Менеджер», затем без менеджера
                var a = report.Rows.Single(r => r.CustomerId == withManager);
                // 100 дней назад + 10 = просрочка 90 дней → 61–90 (10 000 − 4 000); 45 − 10 = 35 → 31–60; 5 дней назад — срок ещё не наступил.
                Assert.Equal((14_000m, 3_000m, 0m, 5_000m, 6_000m, 0m, 90), (a.Debt, a.NotDue, a.Days1To30, a.Days31To60, a.Days61To90, a.Over90, a.OverdueDays));
                var b = report.Rows.Single(r => r.CustomerId == byOrder);
                Assert.Equal((2_000m, 2_000m, 200), (b.Debt, b.Over90, b.OverdueDays));
                var c = report.Rows.Single(r => r.CustomerId == nobody);
                Assert.Equal((1_000m, 1_000m, (string?)null), (c.Debt, c.Days1To30, c.Manager));
                Assert.Equal(17_000m, report.Groups.Sum(g => g.Debt));
                Assert.Equal(2, report.Managers.Count);

                Assert.Equal([withManager], (await s.Sales.ReceivablesAsync(new ReceivablesFilter(ManagerId: f.Manager))).Rows.Select(r => r.CustomerId));
                Assert.Equal([nobody], (await s.Sales.ReceivablesAsync(new ReceivablesFilter(ManagerId: 0))).Rows.Select(r => r.CustomerId));

                // На дату 60 дней назад: только первая отгрузка (10 000, просрочка 30 дней), оплаты ещё не было.
                var past = await s.Sales.ReceivablesAsync(new ReceivablesFilter(today.AddDays(-60)));
                Assert.Equal((10_000m, 10_000m), (past.Rows.Single(r => r.CustomerId == withManager).Debt, past.Rows.Single(r => r.CustomerId == withManager).Days1To30));

                // Excel: строки покупателей, итоги по менеджерам и общий.
                var xlsx = await s.Sales.ExportReceivablesAsync(new ReceivablesFilter(), SqlTestHost.Spreadsheet);
                var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
                Assert.Equal("Покупатель", sheet[0][1]);
                Assert.Equal(17_000m, decimal.Parse(sheet.Single(r => r[1] == "Итого")[2], System.Globalization.CultureInfo.InvariantCulture));

                // Текст письма — с просроченной отгрузкой и сроком оплаты.
                var letter = await s.Sales.OverdueLetterAsync(withManager);
                Assert.Contains("в том числе просроченная", letter);
                Assert.Contains("срок оплаты", letter);
                var fresh = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Новый»", null, null, false, true, null));
                Assert.Equal("sales.credit.no_debt", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.OverdueLetterAsync(fresh))).Code);

                // Блок главного экрана: просрочено 5 000 + 6 000 + 2 000 + 1 000.
                var block = await SalesReceivables.DashboardAsync(host.Factory, new TestUser { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId },
                    host.Clock, default);
                Assert.Equal((17_000m, 14_000m, 3), (block!.Debt, block.Overdue, block.Customers));

                // Роль «Менеджер продаж» видит только своих покупателей.
                var role = (await s.Agreements.RoleSettingsAsync()).Single(r => r.Role == SystemRoles.NameOf(SystemRoles.SalesManager));
                await s.Agreements.SetRoleSettingsAsync(role.RoleId, null, null, SalesOrderVisibility.Own);
            }

            await using (var m = host.As(f.Manager, f.Org.OrganizationId))
            {
                var report = await m.Sales.ReceivablesAsync(new ReceivablesFilter());
                Assert.True(report.OwnOnly);
                Assert.Equal([withManager], report.Rows.Select(r => r.CustomerId));
                await Assert.ThrowsAsync<NotFoundException>(() => m.Sales.OverdueLetterAsync(byOrder));

                // Напоминание менеджеру — одно на покупателя в день; у Владельца — его покупатель «Бутик».
                var alerts = (await m.Notifications.ListAsync()).Where(n => n.Key.StartsWith("sales-overdue:")).ToList();
                Assert.Equal($"sales-overdue:{withManager}:{today:yyyyMMdd}", Assert.Single(alerts).Key);
                Assert.Contains("ООО «Магазин»", alerts[0].Text);
            }

            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                var alerts = (await s.Notifications.ListAsync()).Where(n => n.Key.StartsWith("sales-overdue:")).ToList();
                Assert.Equal($"sales-overdue:{byOrder}:{today:yyyyMMdd}", Assert.Single(alerts).Key);
            }
        }
        finally
        {
            host.Clock.UtcNow = Now;
        }

        async Task<long> ShipOnAsync(long customer, decimal quantity, int days)
        {
            // Отгрузка проводится «сегодня» (пока ничего не просрочено), затем дата документа переносится в прошлое — как будто
            // отгрузили тогда; так проверка просрочки не мешает собрать историю.
            host.Clock.UtcNow = Now.AddDays(200);
            await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
            var order = await ConfirmedAsync(s, f, customer, quantity, today.AddDays(days));
            await ShipAsync(s, order);
            backdate.Add((order, today.AddDays(days)));
            return order;
        }
    }

    [SqlFact]
    public async Task Credit_needs_price_rights_and_stays_in_organization()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await StockAsync(s, f, 10);
            await s.Agreements.CreateAsync(Terms("Лимит", f.Customer, limit: 1_000m));
            order = await OrderAsync(s, f, f.Customer, 5);
        }

        // Без права на цены — ни проверки в карточке, ни отчёта, ни блока главного экрана.
        await using (var k = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.GetCreditAsync(order));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.ReceivablesAsync(new ReceivablesFilter()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.OverdueLetterAsync(f.Customer));
            Assert.Null(await SalesReceivables.DashboardAsync(host.Factory, new TestUser { UserId = f.Senior, OrganizationId = f.Org.OrganizationId },
                host.Clock, default));
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.GetCreditAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.ApproveCreditAsync(order, "чужой", [0, 0, 0, 0, 0, 0, 0, 1]));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.OverdueLetterAsync(f.Customer));
            Assert.Empty((await o.Sales.ReceivablesAsync(new ReceivablesFilter())).Rows);
        }

        // Ограничения в SQL Server: разрешение на нулевую сумму и чужой заказ (составной ключ организации) не записываются.
        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[sales_credit_approvals] (OrganizationId, OrderId, Amount, Debt, Overdue, Reason, ApprovedByUserId, ApprovedAtUtc) VALUES ({0}, {1}, 0, 0, 0, N'x', {2}, SYSUTCDATETIME())",
                f.Org.OrganizationId, order, f.Org.OwnerUserId));
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[sales_credit_approvals] (OrganizationId, OrderId, Amount, Debt, Overdue, Reason, ApprovedByUserId, ApprovedAtUtc) VALUES ({0}, {1}, 10, 0, 0, N'x', {2}, SYSUTCDATETIME())",
                other.OrganizationId, order, other.OwnerUserId));
        }
    }

    private static SalesAgreementTerms Terms(string name, long customer, decimal? limit = null, int? days = null, long? contract = null) =>
        new(name, customer, null, null, 0, Day.AddYears(-1), null, null, days, limit, null, contract, null);

    private static async Task<long> OrderAsync(Services s, Fixture f, long customer, decimal quantity, DateOnly? date = null)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(date ?? Day, customer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, quantity, 1000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        return order;
    }

    private static async Task<long> ConfirmedAsync(Services s, Fixture f, long customer, decimal quantity, DateOnly? date = null)
    {
        var order = await OrderAsync(s, f, customer, quantity, date);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        return order;
    }

    private static async Task ShipAsync(Services s, long order)
    {
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
    }

    private async Task StockAsync(Services s, Fixture f, decimal quantity, DateOnly? date = null)
    {
        long? reason;
        await using (var db = host.NewDb())
        {
            reason = await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
        }

        var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, reason, date ?? Day, null));
        await s.Documents.SetLineAsync(receipt, f.Sweater, quantity, (await s.Documents.GetAsync(receipt)).RowVersion);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Customer, long Other, long Manager, long Senior);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, customer, other, manager, senior;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            other = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Бутик»", null, null, false, true, null));
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null)))
                .UserId;
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
        }

        await using (var db = host.NewDb())
        {
            foreach (var id in new[] { manager, senior })
            {
                (await db.Users.SingleAsync(u => u.Id == id)).Activate();
            }

            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, customer, other, manager, senior);
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
