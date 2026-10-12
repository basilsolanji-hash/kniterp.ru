using System.Globalization;
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
/// Аналитика продаж (D1160–D1167): выручка и маржа по клиентам, менеджерам, группам, каналам и периодам на известных данных,
/// ABC, план-факт, воронка, средний чек и повторные покупки, без права цен — null, видимость «Свои», блоки главного экрана,
/// Excel, права и чужая организация.
/// </summary>
public sealed class SalesReportTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 816_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly SalesReportFilter Period = new(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));

    [SqlFact]
    public async Task Revenue_and_margin_by_dimensions_abc_average_check_and_repeat_customers()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // По покупателям: «Магазин» — 16 000 (два заказа, у шапок нет себестоимости), «Бутик» — 1 000.
        var byCustomer = await s.SalesReports.ReportAsync(Period);
        Assert.True(byCustomer.AmountsVisible);
        Assert.False(byCustomer.OwnOnly);
        var a = byCustomer.Rows[0];
        Assert.Equal((f.ShopA, "ООО «Магазин»", 16_000m, 8_400m, false, 2, (decimal?)null), (a.Id!.Value, a.Name, a.Revenue!.Value, a.Cost!.Value, a.CostComplete, a.Orders, a.Margin));
        Assert.Equal((f.ShopB, 1_000m), (byCustomer.Rows[1].Id!.Value, byCustomer.Rows[1].Revenue!.Value));
        Assert.Equal((17_000m, 3, 2, 5_666.67m), (byCustomer.Total.Revenue!.Value, byCustomer.Total.Orders, byCustomer.Total.Customers, byCustomer.Total.AverageCheck!.Value));
        Assert.Equal((1, 50m), (byCustomer.RepeatCustomers, byCustomer.RepeatSharePercent!.Value));

        // По менеджерам: Владелец — 10 000, себестоимость 6 000, маржа 40%; менеджер — 7 000 (шапки без себестоимости).
        var byManager = await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Manager });
        var owner = byManager.Rows.Single(r => r.Id == f.Org.OwnerUserId);
        Assert.Equal((10_000m, 6_000m, 4_000m, 40m), (owner.Revenue!.Value, owner.Cost!.Value, owner.Margin!.Value, owner.MarginPercent!.Value));
        Assert.Equal((7_000m, false), (byManager.Rows.Single(r => r.Id == f.Manager).Revenue!.Value, byManager.Rows.Single(r => r.Id == f.Manager).CostComplete));

        // По группам: «Свитера» — 14 шт., 14 000, себестоимость 8 400; без группы (шапки) — 3 000.
        var byGroup = await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Group });
        var sweaters = byGroup.Rows.Single(r => r.Id == f.Group);
        Assert.Equal(("Свитера", 14m, 14_000m, 8_400m, 5_600m), (sweaters.Name, sweaters.Quantity, sweaters.Revenue!.Value, sweaters.Cost!.Value, sweaters.Margin!.Value));
        Assert.Equal(3_000m, byGroup.Rows.Single(r => r.Id is null).Revenue!.Value);

        // По каналам: Wildberries (FBO) — 10 000, «Сайт» — 6 000, без канала — 1 000.
        var byChannel = await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Channel });
        var wb = byChannel.Rows.Single(r => r.Id == f.Wb);
        Assert.Equal((10_000m, "Wildberries, FBO"), (wb.Revenue!.Value, wb.Note));
        Assert.Equal(6_000m, byChannel.Rows.Single(r => r.Id == f.Site).Revenue!.Value);
        Assert.Equal(1_000m, byChannel.Rows.Single(r => r.Id is null).Revenue!.Value);
        Assert.Equal([f.Wb], (await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Channel, ChannelId = f.Wb })).Rows.Select(r => r.Id!.Value));

        // По периодам: месяцы, недели (с понедельника), дни.
        var byMonth = await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Period });
        Assert.Equal([("09.2026", 1_000m), ("10.2026", 16_000m)], byMonth.Rows.Select(r => (r.Name!, r.Revenue!.Value)));
        var byWeek = await s.SalesReports.ReportAsync(new SalesReportFilter(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), SalesDimension.Period,
            SalesPeriodStep.Week));
        Assert.Equal([(new DateOnly(2026, 9, 28), 10_000m), (new DateOnly(2026, 10, 5), 6_000m)], byWeek.Rows.Select(r => (r.PeriodStart!.Value, r.Revenue!.Value)));
        var byDay = await s.SalesReports.ReportAsync(new SalesReportFilter(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), SalesDimension.Period,
            SalesPeriodStep.Day));
        Assert.Equal([("02.10.2026", 10_000m), ("05.10.2026", 7_000m), ("06.10.2026", -1_000m)], byDay.Rows.Select(r => (r.Name!, r.Revenue!.Value)));
        Assert.Equal("sales.report.days", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.SalesReports.ReportAsync(Period with { From = new DateOnly(2026, 6, 1), By = SalesDimension.Period, Step = SalesPeriodStep.Day }))).Code);
        Assert.Equal("report.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.SalesReports.ReportAsync(new SalesReportFilter(Day, Day.AddDays(-1))))).Code);

        // Рентабельность по разрезу совпадает с отчётом «Прибыльность» (D69).
        var profit = await s.Analytics.ProfitabilityAsync(new SalesAnalyticsFilter(Period.From, Period.To), ProfitGrouping.Item);
        var byItem = await s.SalesReports.ReportAsync(Period with { By = SalesDimension.Item });
        Assert.Equal(profit.Rows.Single(r => r.Id == f.Sweater).Cost, byItem.Rows.Single(r => r.Id == f.Sweater).Cost);
        Assert.Equal(profit.Revenue, byItem.Total.Revenue);

        // ABC: 80/15/5 — «Магазин» (94%) в A, «Бутик» — в B; при 50/30 — «Бутик» в C. Позиции: свитер A (82%), шапка B.
        var abc = await s.SalesReports.AbcAsync(Period, AbcSubject.Customer);
        Assert.Equal([('A', f.ShopA), ('B', f.ShopB)], abc.Rows.Select(r => (r.Class, r.Id)));
        Assert.Equal(94.12m, abc.Rows[0].SharePercent);
        Assert.Equal('C', (await s.SalesReports.AbcAsync(Period, AbcSubject.Customer, AbcBasis.Revenue, 50, 30)).Rows[1].Class);
        var abcItems = await s.SalesReports.AbcAsync(Period, AbcSubject.Item);
        Assert.Equal([('A', f.Sweater), ('B', f.Hat)], abcItems.Rows.Select(r => (r.Class, r.Id)));
        Assert.Equal("sales.abc.thresholds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.SalesReports.AbcAsync(Period, AbcSubject.Item, AbcBasis.Revenue, 80, 20))).Code);
    }

    [SqlFact]
    public async Task Plan_fact_funnel_dashboard_blocks_and_excel()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // План: Владелец 20 000, менеджер 5 000; факт октября — 10 000 и 6 000 (сентябрьская отгрузка менеджера не входит).
            await s.SalesReports.SetPlanAsync(Day, f.Org.OwnerUserId, 20_000m, null);
            await s.SalesReports.SetPlanAsync(Day, f.Manager, 4_000m, null);
            var plan = await s.SalesReports.PlanFactAsync(Day);
            var mine = plan.Rows.Single(r => r.ManagerId == f.Manager);
            await s.SalesReports.SetPlanAsync(Day, f.Manager, 5_000m, mine.PlanVersion);
            Assert.Equal("sales.plan.amount", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.SalesReports.SetPlanAsync(Day, f.Senior, -1m, null))).Code);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.SalesReports.SetPlanAsync(Day, f.Manager, 6_000m, mine.PlanVersion));

            plan = await s.SalesReports.PlanFactAsync(new DateOnly(2026, 10, 20));
            Assert.Equal(new DateOnly(2026, 10, 1), plan.Month);
            Assert.True(plan.CanEdit);
            Assert.Equal((20_000m, 10_000m, 50m), (plan.Rows.Single(r => r.ManagerId == f.Org.OwnerUserId).Plan!.Value,
                plan.Rows.Single(r => r.ManagerId == f.Org.OwnerUserId).Fact!.Value, plan.Rows.Single(r => r.ManagerId == f.Org.OwnerUserId).Percent!.Value));
            Assert.Equal((5_000m, 6_000m, 120m), (plan.Rows.Single(r => r.ManagerId == f.Manager).Plan!.Value, plan.Rows.Single(r => r.ManagerId == f.Manager).Fact!.Value,
                plan.Rows.Single(r => r.ManagerId == f.Manager).Percent!.Value));
            Assert.Equal((25_000m, 16_000m, 29m), (plan.Plan!.Value, plan.Fact!.Value, plan.ElapsedPercent));
            Assert.Equal(2, await s.Db.AuditEntries.CountAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.SalesPlanChanged
                                                                    && e.EntityId == $"2026-10:{f.Manager}"));
            // Для ввода плана — все активные сотрудники (старший кладовщик тоже, без плана).
            Assert.Contains((await s.SalesReports.PlanFactAsync(Day, allManagers: true)).Rows, r => r.ManagerId == f.Senior && r.Plan == 0);

            // Воронка за сентябрь–октябрь: оформлено 5 заказов, подтверждено 4, отгружено 3, оплачено полностью 1.
            var funnel = await s.SalesReports.FunnelAsync(Period);
            Assert.Equal([(FunnelStage.Created, 5, 21_500m), (FunnelStage.Confirmed, 4, 21_000m), (FunnelStage.Shipped, 3, 17_000m), (FunnelStage.Paid, 1, 10_000m)],
                funnel.Stages.Select(x => (x.Stage, x.Orders, x.Amount!.Value)));
            Assert.Equal([null, 80m, 75m, 33.3m], funnel.Stages.Select(x => x.ConversionPercent));

            // Главный экран: «Отгрузить сегодня» — заказ «Бутика» на 3 000; «Продажи к плану» — 16 000 из 25 000.
            var ship = await SalesReportService.ShipTodayAsync(host.Factory, User(f.Org.OwnerUserId, f), host.Clock, default);
            Assert.Equal((1, 0, 3_000m, f.O4), (ship!.DueToday, ship.Overdue, ship.Amount!.Value, ship.Top.Single().OrderId));
            Assert.Equal(3m, ship.Top[0].LeftQuantity);
            var block = await SalesReportService.PlanBlockAsync(host.Factory, User(f.Org.OwnerUserId, f), host.Clock, default);
            Assert.Equal((25_000m, 16_000m, 64m), (block!.Plan, block.Fact, block.Percent!.Value));

            // Excel: лист разреза с итогом, ABC, план-факт, воронка; суммы — числами.
            var xlsx = await s.SalesReports.ExportAsync(Period, SqlTestHost.Spreadsheet);
            var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
            Assert.Equal("Покупатель", sheet[0][0]);
            Assert.Equal(17_000m, decimal.Parse(sheet.Single(r => r[0] == "Итого")[3].Replace(",", ""), CultureInfo.InvariantCulture));
            Assert.NotNull(SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "Воронка", 100));
            Assert.NotNull(SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "План-факт", 100));
        }

        // Старший кладовщик: продажи видит, отчётов — нет; блок «Отгрузить сегодня» — без суммы, «к плану» — нет.
        await using (var k = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.SalesReports.ReportAsync(Period));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.SalesReports.PlanFactAsync(Day));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.SalesReports.SetPlanAsync(Day, f.Senior, 1m, null));
            var ship = await SalesReportService.ShipTodayAsync(host.Factory, User(f.Senior, f), host.Clock, default);
            Assert.Null(ship!.Amount);
            Assert.Null(ship.Top[0].LeftAmount);
            Assert.Null(await SalesReportService.PlanBlockAsync(host.Factory, User(f.Senior, f), host.Clock, default));
        }
    }

    [SqlFact]
    public async Task Without_price_right_amounts_are_null_and_own_visibility_shows_only_own_orders()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.SalesReports.SetPlanAsync(Day, f.Manager, 5_000m, null);
            var role = (await s.Agreements.RoleSettingsAsync()).Single(r => r.Role == SystemRoles.NameOf(SystemRoles.SalesManager));
            await s.Agreements.SetRoleSettingsAsync(role.RoleId, null, null, SalesOrderVisibility.Own);
        }

        // «Свои»: менеджер видит только свои заказы (заказ «Магазина» на 6 000 и «Бутика» на 1 000).
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            var report = await m.SalesReports.ReportAsync(Period);
            Assert.True(report.OwnOnly);
            Assert.Equal(7_000m, report.Total.Revenue);
            Assert.Equal([f.ShopA, f.ShopB], report.Rows.Select(r => r.Id!.Value));
            Assert.Equal([f.Manager], (await m.SalesReports.ReportAsync(Period with { By = SalesDimension.Manager })).Rows.Select(r => r.Id!.Value));
            Assert.Equal(2, (await m.SalesReports.FunnelAsync(Period)).Stages[0].Orders);
            var plan = await m.SalesReports.PlanFactAsync(Day);
            Assert.Equal((f.Manager, 5_000m, 6_000m), (plan.Rows.Single().ManagerId!.Value, plan.Rows.Single().Plan!.Value, plan.Rows.Single().Fact!.Value));
            Assert.False(plan.CanEdit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.SalesReports.SetPlanAsync(Day, f.Manager, 1m, null));
            Assert.Equal([f.Manager], (await m.SalesReports.OptionsAsync()).Managers.Select(x => x.Id));
            Assert.Null(await SalesReportService.ShipTodayAsync(host.Factory, User(f.Manager, f), host.Clock, default)); // заказ «Бутика» на сегодня — не его
        }

        // Без права «Цены и суммы» (запрет): выручка, себестоимость, маржа, средний чек, план и суммы воронки — null; ABC — по количеству.
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Access.GrantIndividualAsync(f.Manager, Permissions.PriceView, true, null, "Проверка скрытия сумм");
        }

        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            var report = await m.SalesReports.ReportAsync(Period with { By = SalesDimension.Item });
            Assert.False(report.AmountsVisible);
            Assert.All(report.Rows, r => Assert.True(r.Revenue is null && r.Cost is null && r.Margin is null && r.MarginPercent is null && r.AverageCheck is null));
            Assert.Null(report.Total.Revenue);
            Assert.Equal(6m, report.Rows.Single(r => r.Id == f.Hat).Quantity);
            var abc = await m.SalesReports.AbcAsync(Period, AbcSubject.Item);
            Assert.Equal(AbcBasis.Quantity, abc.Basis);
            Assert.All((await m.SalesReports.FunnelAsync(Period)).Stages, x => Assert.Null(x.Amount));
            var plan = await m.SalesReports.PlanFactAsync(Day);
            Assert.True(plan.Rows.Single().Plan is null && plan.Rows.Single().Fact is null && plan.Plan is null);
            var xlsx = await m.SalesReports.ExportAsync(Period, SqlTestHost.Spreadsheet);
            var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
            Assert.All(sheet.Skip(1), r => Assert.Equal("", r[3]));
            Assert.Null(await SalesReportService.PlanBlockAsync(host.Factory, User(f.Manager, f), host.Clock, default));
        }

        // Чужая организация не видит чужих продаж и не ставит план чужому сотруднику.
        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            Assert.Empty((await o.SalesReports.ReportAsync(Period)).Rows);
            Assert.All((await o.SalesReports.FunnelAsync(Period)).Stages, x => Assert.Equal(0, x.Orders));
            await Assert.ThrowsAsync<NotFoundException>(() => o.SalesReports.SetPlanAsync(Day, f.Manager, 1m, null));
            Assert.DoesNotContain((await o.SalesReports.PlanFactAsync(Day, allManagers: true)).Rows, r => r.ManagerId == f.Manager);
        }

        // Ограничения в SQL Server: план с отрицательной суммой и не на первое число не записывается.
        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[sales_plans] (OrganizationId, Month, ManagerUserId, Amount, UpdatedByUserId, UpdatedAtUtc) VALUES ({0}, '2026-11-01', {1}, -1, {1}, SYSUTCDATETIME())",
                f.Org.OrganizationId, f.Manager));
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[sales_plans] (OrganizationId, Month, ManagerUserId, Amount, UpdatedByUserId, UpdatedAtUtc) VALUES ({0}, '2026-11-02', {1}, 1, {1}, SYSUTCDATETIME())",
                f.Org.OrganizationId, f.Manager));
        }
    }

    private static TestUser User(long userId, Fixture f) => new() { UserId = userId, OrganizationId = f.Org.OrganizationId };

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Hat, long Group, long ShopA, long ShopB, long ShopC, long Manager,
        long Senior, long Wb, long Site, long O4);

    /// <summary>
    /// Данные (цены без НДС, себестоимость свитера — закупка по 600, у шапки себестоимости нет):
    /// О1 «Магазин», Владелец, Wildberries: 10 свитеров × 1 000 — отгружено 02.10, оплачено полностью;
    /// О2 «Магазин», менеджер, «Сайт»: 5 свитеров × 1 000 + 4 шапки × 500 — отгружено 05.10, 1 свитер возвращён 06.10;
    /// О3 «Бутик», менеджер, без канала, заказ 18.09: 2 шапки × 500 — отгружено 20.09;
    /// О4 «Бутик», Владелец: 3 свитера × 1 000 — подтверждён, отгрузка сегодня, не отгружен;
    /// О5 «Ателье»: черновик, 1 шапка × 500.
    /// </summary>
    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, hat, group, a, b, c, manager, senior, wb, site, o4;
        var backdate = new List<(long Doc, DateOnly Date)>();
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            group = await s.Nomenclature.CreateGroupAsync(null, "Свитера");
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null, GroupId: group));
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.Finished, unit, null));
            a = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            b = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Бутик»", null, null, false, true, null));
            c = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Ателье»", null, null, false, true, null));
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Фабрика»", null, null, true, false, null));
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null))).UserId;
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
            wb = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Wildberries");
            site = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Сайт");
            await s.Marketplace.SetChannelAsync(wb, MarketplaceKind.Wildberries, FulfillmentScheme.Fbo, null, null);

            // Свитера — закупка 100 шт. по 600 без НДС; шапки — оприходование без цены.
            var po = await s.Purchases.CreateOrderAsync(new KnitErp.Domain.Purchasing.PurchaseOrderHeader(Day, supplier, store, null, null, false, null));
            await s.Purchases.SetOrderLineAsync(po, sweater, 100, 600m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(po);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                    .Select(r => (long?)r.Id).FirstAsync();
            }

            var stock = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(stock, hat, 50, (await s.Documents.GetAsync(stock)).RowVersion);
            await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);
        }

        await using (var db = host.NewDb())
        {
            foreach (var id in new[] { manager, senior })
            {
                (await db.Users.SingleAsync(u => u.Id == id)).Activate();
            }

            await db.SaveChangesAsync();
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var o1 = await OrderAsync(s, a, store, Day, null, (sweater, 10, 1000m));
            await DetailsAsync(s, o1, wb, org.OwnerUserId);
            await ConfirmAsync(s, o1);
            backdate.Add((await ShipAsync(s, o1), new DateOnly(2026, 10, 2)));
            await s.Sales.CreatePaymentAsync(Day, a, o1, 10_000m, null);

            var o2 = await OrderAsync(s, a, store, Day, null, (sweater, 5, 1000m), (hat, 4, 500m));
            await DetailsAsync(s, o2, site, manager);
            await ConfirmAsync(s, o2);
            backdate.Add((await ShipAsync(s, o2), new DateOnly(2026, 10, 5)));
            var ret = await s.Sales.CreateReturnAsync(o2);
            await s.Documents.RemoveLineAsync(ret, hat, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.SetLineAsync(ret, sweater, 1, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
            backdate.Add((ret, new DateOnly(2026, 10, 6)));

            var o3 = await OrderAsync(s, b, store, new DateOnly(2026, 9, 18), null, (hat, 2, 500m));
            await DetailsAsync(s, o3, null, manager);
            await ConfirmAsync(s, o3);
            backdate.Add((await ShipAsync(s, o3), new DateOnly(2026, 9, 20)));

            o4 = await OrderAsync(s, b, store, Day, Day, (sweater, 3, 1000m));
            await ConfirmAsync(s, o4);
            await OrderAsync(s, c, store, Day, null, (hat, 1, 500m));
        }

        await using (var db = host.NewDb())
        {
            foreach (var (doc, date) in backdate)
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[stock_documents] SET [DocumentDate] = {0} WHERE [Id] = {1}", date, doc);
            }
        }

        return new Fixture(org, store, sweater, hat, group, a, b, c, manager, senior, wb, site, o4);
    }

    private static async Task<long> OrderAsync(Services s, long customer, long store, DateOnly date, DateOnly? shipDate, params (long Item, decimal Qty, decimal Price)[] lines)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(date, customer, store, shipDate, null, true, null));
        foreach (var (item, qty, price) in lines)
        {
            await s.Sales.SetOrderLineAsync(order, item, qty, price, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        }

        return order;
    }

    private static async Task DetailsAsync(Services s, long order, long? channel, long responsible) =>
        await s.Sales.SetOrderDetailsAsync(order, new SalesOrderDetails(null, null, channel, null, responsible), new Dictionary<long, string?>(),
            (await s.Sales.GetOrderAsync(order)).RowVersion);

    private static async Task ConfirmAsync(Services s, long order) =>
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);

    private static async Task<long> ShipAsync(Services s, long order)
    {
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        return ship;
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
