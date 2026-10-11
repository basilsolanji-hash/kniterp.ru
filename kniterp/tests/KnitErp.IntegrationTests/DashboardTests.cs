using System.Data.Common;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Application.Taxes;
using KnitErp.Application.Warehousing;
using KnitErp.Application.Common;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace KnitErp.IntegrationTests;

/// <summary>Главный экран: те же цифры, что в разделах, за малое число запросов к базе.</summary>
public sealed class DashboardTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 820_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Dashboard_reads_everything_in_few_queries_and_caches()
    {
        var f = await SeedAsync();
        var counter = new CommandCounter();
        var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var db = host.NewDb(counter))
        {
            var data = await NewDashboard(db, Owner(f), cache).GetAsync();
            Assert.NotNull(data.Finance);
            Assert.NotNull(data.Finance!.Accounts);

            // Все блоки, деньги и просрочки — ограниченное число запросов, а не запрос на документ или контрагента.
            Assert.InRange(counter.Count, 1, 30);
        }

        var first = counter.Count;
        await using (var db = host.NewDb(counter))
        {
            await NewDashboard(db, Owner(f), cache).GetAsync();
        }

        // Повтор в пределах минуты — из кэша: только права и шапка.
        Assert.InRange(counter.Count - first, 1, 6);
    }

    [SqlFact]
    public async Task Money_on_accounts_and_cash_desks_of_all_legal_entities()
    {
        var f = await SeedAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var ooo = (await s.LegalEntities.ListAsync()).Single();

        // До D80 оплаты без счёта — строка «Без счёта», итог сходится с расчётами.
        var before = await s.Money.BalancesAsync();
        Assert.Equal((1_000m, 500m), (before.Single(b => b.AccountId is null).Out, before.Single(b => b.AccountId is null).In));

        var bank = await s.LegalEntities.AddAccountAsync(ooo.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "40702810938000000001", null));
        var cash = await s.LegalEntities.AddCashAsync(ooo.Id, "Касса цеха");
        Assert.Equal("money.cash_duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.LegalEntities.AddCashAsync(ooo.Id, "Касса цеха"))).Code);
        var accounts = (await s.LegalEntities.ListAsync()).Single().Accounts;
        Assert.Equal("money.cash_default", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.LegalEntities.MakeDefaultAccountAsync(cash, accounts.Single(a => a.Id == cash).RowVersion))).Code);

        // Начальные остатки: банк 10 000 на 08.10, касса 500 на 09.10.
        await s.LegalEntities.SetOpeningAsync(bank, Day.AddDays(-1), 10_000m, accounts.Single(a => a.Id == bank).RowVersion);
        await s.LegalEntities.SetOpeningAsync(cash, Day, 500m, accounts.Single(a => a.Id == cash).RowVersion);
        Assert.Equal("money.opening_date", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.LegalEntities.SetOpeningAsync(cash, null, 1m, s.Db.LegalEntityAccounts.AsNoTracking().Single(a => a.Id == cash).RowVersion))).Code);

        // Без счёта — основной счёт основного юрлица; касса — явно; оплата до даты начального остатка не считается.
        await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 2_000m, null);
        await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 300m, null, null, cash);
        await s.Sales.CreatePaymentAsync(Day.AddDays(-3), f.Customer, null, 7_000m, null, null, bank);
        await s.Purchases.CreatePaymentAsync(Day, f.Supplier, null, 1_000m, null);
        await s.Purchases.CreatePaymentAsync(Day, f.Supplier, null, 100m, null, cash);

        var balances = await s.Money.BalancesAsync();
        Assert.Equal(11_000m, balances.Single(b => b.AccountId == bank).Balance);
        Assert.Equal(700m, balances.Single(b => b.AccountId == cash).Balance);
        Assert.Equal(MoneyAccountKind.Cash, balances.Single(b => b.AccountId == cash).Kind);

        // Касса не попадает в счета для заказа (счёт на оплату), но есть в выборе для оплаты.
        Assert.DoesNotContain((await s.LegalEntities.OptionsAsync()).Single().Accounts, a => a.Id == cash);
        Assert.Contains(await s.Money.OptionsAsync(), a => a.Id == cash && a.Label.Contains("Касса"));

        // Второе юрлицо (ИП): оплата заказа ООО не может уйти в кассу ИП.
        var ip = await s.LegalEntities.CreateAsync(new LegalEntityData(LegalEntityKind.SoleProprietor,
            "Индивидуальный предприниматель Петров Пётр Петрович", "ИП Петров П. П.", "500100732259", null, "304500116000157",
            null, null, "Петров П. П.", null, VatExempt: true));
        var ipCash = await s.LegalEntities.AddCashAsync(ip, "Касса ИП");
        Assert.Equal("money.account_entity", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.CreatePaymentAsync(Day, f.Customer, f.SalesOrder, 10m, null, null, ipCash))).Code);

        // Архивный счёт не выбрать; касса в архиве — тоже.
        var ipCashRow = (await s.LegalEntities.ListAsync()).Single(e => e.Id == ip).Accounts.Single();
        await s.LegalEntities.SetAccountArchivedAsync(ipCash, true, ipCashRow.RowVersion);
        Assert.Equal("legal_entity.account_archived", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Purchases.CreatePaymentAsync(Day, f.Supplier, null, 10m, null, ipCash))).Code);

        // Главный экран: итог по всем счетам и кассам; аудит записан.
        var data = await s.Dashboard.GetAsync(refresh: true);
        Assert.Equal(balances.Sum(b => b.Balance), data.Finance!.AccountsTotal);
        Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.OrganizationId == f.Org.OrganizationId && a.EntityId == cash.ToString()
                                                         && a.Reason == "Начальный остаток"));

        // Чужая организация: её счёт — «не найден».
        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        var otherCustomer = await o.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Чужой»", null, null, false, true, null));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.CreatePaymentAsync(Day, otherCustomer, null, 10m, null, null, bank));
        await Assert.ThrowsAsync<NotFoundException>(() => o.LegalEntities.SetOpeningAsync(bank, Day, 1m, [1]));
    }

    [SqlFact]
    public async Task Overdue_shipments_and_receipts_on_dashboard_and_in_lists()
    {
        var f = await SeedAsync();
        long lateSale, latePurchase;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var start = await s.Dashboard.GetAsync(refresh: true);
            Assert.Equal((0, 0, 1), (start.Finance!.OverdueShipments, start.Finance.OverdueReceipts, start.Finance.OrdersToShip));

            // Заказ покупателя: отгрузить до 08.10, не отгружен; заказ поставщику: ждали 08.10, не привезли.
            lateSale = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day.AddDays(-5), f.Customer, f.Store, Day.AddDays(-1), null, false, null));
            await s.Sales.SetOrderLineAsync(lateSale, f.Sweater, 1, 100m, 22m, (await s.Sales.GetOrderAsync(lateSale)).RowVersion);
            await s.Sales.ConfirmOrderAsync(lateSale, (await s.Sales.GetOrderAsync(lateSale)).RowVersion);
            latePurchase = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day.AddDays(-5), f.Supplier, f.Store, Day.AddDays(-1), null, true, null));
            await s.Purchases.SetOrderLineAsync(latePurchase, f.Wool, 2, 50m, 22m, (await s.Purchases.GetOrderAsync(latePurchase)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(latePurchase, (await s.Purchases.GetOrderAsync(latePurchase)).RowVersion);

            var data = await s.Dashboard.GetAsync(refresh: true);
            Assert.Equal((1, 1, 2), (data.Finance!.OverdueShipments, data.Finance.OverdueReceipts, data.Finance.OrdersToShip));
            Assert.Equal([lateSale], (await s.Sales.ListOrdersAsync(new SalesOrderFilter(OverdueOn: Day))).Select(r => r.Id));
            Assert.Equal([latePurchase], (await s.Purchases.ListOrdersAsync(new PurchaseOrderFilter(OverdueOn: Day))).Select(r => r.Id));

            // Отгрузили и приняли полностью — просрочки нет.
            var ship = await s.Sales.CreateShipmentAsync(lateSale);
            await s.Documents.SetLineAsync(ship, f.Sweater, 1, (await s.Documents.GetAsync(ship)).RowVersion);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(latePurchase);
            await s.Documents.SetLineAsync(receipt, f.Wool, 2, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            data = await s.Dashboard.GetAsync(refresh: true);
            Assert.Equal((0, 0), (data.Finance!.OverdueShipments, data.Finance.OverdueReceipts));
            Assert.Empty(await s.Sales.ListOrdersAsync(new SalesOrderFilter(OverdueOn: Day)));
            Assert.Empty(await s.Purchases.ListOrdersAsync(new PurchaseOrderFilter(OverdueOn: Day)));
        }
    }

    [SqlFact]
    public async Task Layout_hides_blocks_and_their_data_and_is_personal()
    {
        var f = await SeedAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var layout = await s.Personal.GetDashboardLayoutAsync();
        Assert.Equal(DashboardBlocks.DefaultOrder, layout.Order);

        // Показатели, деньги и «требует внимания» скрыты, «Справочно» — первым; неизвестный блок отброшен.
        await s.Personal.SaveDashboardLayoutAsync(new DashboardLayout(
            [DashboardBlocks.Reference, "hack", DashboardBlocks.Kpi], [DashboardBlocks.Kpi, DashboardBlocks.Money, DashboardBlocks.Attention, "hack"]));
        var data = await s.Dashboard.GetAsync(refresh: true);
        // Блок, которого не было в сохранённой настройке (быстрые действия, D108), встаёт на своё место по умолчанию — наверх.
        Assert.Equal([DashboardBlocks.Actions, DashboardBlocks.Reference], data.Layout!.Order.Take(2));
        Assert.DoesNotContain("hack", data.Layout.Order);
        Assert.Equal([DashboardBlocks.Kpi, DashboardBlocks.Money, DashboardBlocks.Attention], data.Layout.Hidden);
        Assert.Null(data.Finance!.Accounts);
        Assert.Null(data.Finance.OverdueShipments);

        // У другого пользователя — своя настройка (по умолчанию).
        await using (var senior = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.Empty((await senior.Personal.GetDashboardLayoutAsync()).Hidden);
        }

        // Быстрые действия: свой набор и порядок; неизвестные отбрасываются; без настройки — набор по умолчанию.
        Assert.Equal(DashboardActions.Default, data.Layout.ActionKeys);
        var saved = await s.Personal.SaveDashboardLayoutAsync(new DashboardLayout(DashboardBlocks.DefaultOrder, [], ["write-off", "hack", "sales-order", "write-off"]));
        Assert.Equal(["write-off", "sales-order"], saved.ActionKeys);

        Assert.Empty((await s.Personal.ResetDashboardLayoutAsync()).Hidden);
        Assert.NotNull((await s.Dashboard.GetAsync(refresh: true)).Finance!.Accounts);
    }

    [SqlFact]
    public async Task Production_block_shows_funnel_by_stages_and_data_of_production_and_staff_modules()
    {
        // D111: воронка по этапам из правил D110 + готовность и смена из модулей производства и сотрудников.
        var f = await SeedAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var inWork = (await s.Stages.ListAsync()).Single(x => x.Name == "В работе").Id;
            await s.Sales.SetOrderStageAsync(f.SalesOrder, inWork, (await s.Sales.GetOrderAsync(f.SalesOrder)).RowVersion);
        }

        await using (var db = host.NewDb())
        {
            var data = await NewDashboard(db, Owner(f)).GetAsync(refresh: true);
            var funnel = data.Production!.Funnel;
            Assert.Equal(["Оплачен — в производство", "В работе", "Собран"], funnel.Select(x => x.Stage));
            Assert.Equal((0, 1), (funnel[0].Orders, funnel[1].Orders));
            Assert.True(funnel[1].Amount > 0);
            Assert.Null(data.Production.Production);
            Assert.Null(data.Production.Shift);

            var guard = new AccessGuard(db, Owner(f), host.Clock);
            var withModules = new DashboardService(db, guard, host.Clock, new LaunchReadinessService(db, guard, host.Clock), new MemoryCache(new MemoryCacheOptions()),
                [new FakeProduction()], [new FakeShift()]);
            var full = (await withModules.GetAsync(refresh: true)).Production!;
            Assert.Equal((3, 1, 62.5m), (full.Production!.OrdersInWork, full.Production.OrdersLate, full.Production.Orders.Single().Percent));
            Assert.Equal((11, 12, 1), (full.Shift!.OnShift, full.Shift.Planned, full.Shift.Absent));
            Assert.Equal(1, full.OverdueTotal);
        }

        // Скрыт пользователем — не считается.
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Personal.SaveDashboardLayoutAsync(new DashboardLayout(DashboardBlocks.DefaultOrder, [DashboardBlocks.Production]));
            Assert.Null((await s.Dashboard.GetAsync(refresh: true)).Production);
        }
    }

    private sealed class FakeProduction : IProductionDashboardSource
    {
        public Task<ProductionDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct) =>
            Task.FromResult<ProductionDashboardData?>(new(3, 1, [new(1, "ПЗ-1", "Свитер", 62.5m, today, false, "production-orders/1", "Вязка")], 40m, "шт",
                [new("Брак выше нормы", "production-orders/1", true)]));
    }

    private sealed class FakeShift : IShiftStaffingSource
    {
        public Task<ShiftStaffingDto?> TodayAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct) =>
            Task.FromResult<ShiftStaffingDto?>(new(11, 12, 1, "timesheet", ["Вязальщица 1"]));
    }

    private static TestUser Owner(Fixture f) => new() { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId };

    private DashboardService NewDashboard(KnitErp.Infrastructure.Persistence.KnitErpDbContext db, TestUser user, IMemoryCache? cache = null)
    {
        var guard = new AccessGuard(db, user, host.Clock);
        return new DashboardService(db, guard, host.Clock, new LaunchReadinessService(db, guard, host.Clock),
            cache ?? new MemoryCache(new MemoryCacheOptions()));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return base.ScalarExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return base.NonQueryExecutingAsync(command, eventData, result, ct);
        }
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Customer, long Supplier, long Senior, long SalesOrder, long PurchaseOrder, long Sweater, long Wool);

    /// <summary>Склад, покупатель и поставщик; заказ поставщику с поступлением, возвратом и частичной оплатой; заказ покупателя с отгрузкой и счётом.</summary>
    private async Task<Fixture> SeedAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, wool, customer, supplier, salesOrder, purchaseOrder;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Основной склад", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));

            // Закупка: 7 кг по 333,33 ₽ с НДС, поступило 6, вернули 1, оплачено 1 000, счёт поставщика указан.
            purchaseOrder = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, store, Day.AddDays(5), "№ 15 от 05.10.2026", true, null));
            await s.Purchases.SetOrderLineAsync(purchaseOrder, wool, 7, 333.33m, 22m, (await s.Purchases.GetOrderAsync(purchaseOrder)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(purchaseOrder, (await s.Purchases.GetOrderAsync(purchaseOrder)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(purchaseOrder);
            await s.Documents.SetLineAsync(receipt, wool, 6, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            var back = await s.Purchases.CreateReturnAsync(purchaseOrder);
            await s.Documents.SetLineAsync(back, wool, 1, (await s.Documents.GetAsync(back)).RowVersion);
            await s.Documents.PostAsync(back, (await s.Documents.GetAsync(back)).RowVersion);
            await s.Purchases.CreatePaymentAsync(Day, supplier, purchaseOrder, 1_000m, null);

            // Продажа: свитера на складе, заказ на 3 шт. по 1 000,01 ₽, отгружено 2, оплачено 500, выставлен счёт со сроком.
            var stock = await s.Documents.CreateAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(store, null, null, await ReasonAsync(org.OrganizationId, StockOperationKind.Receipt), Day, null));
            await s.Documents.SetLineAsync(stock, sweater, 10, (await s.Documents.GetAsync(stock)).RowVersion);
            await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);
            salesOrder = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, store, Day.AddDays(2), null, false, null));
            await s.Sales.SetOrderLineAsync(salesOrder, sweater, 3, 1000.01m, 22m, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
            await s.Sales.ConfirmOrderAsync(salesOrder, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
            var ship = await s.Sales.CreateShipmentAsync(salesOrder);
            await s.Documents.SetLineAsync(ship, sweater, 2, (await s.Documents.GetAsync(ship)).RowVersion);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
            await s.Sales.CreatePaymentAsync(Day, customer, salesOrder, 500m, null);
            await s.Invoices.CreateAsync(salesOrder, Day.AddDays(3), null);

            // Черновик документа — «ждёт проведения».
            await s.Documents.CreateAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(store, null, null, await ReasonAsync(org.OrganizationId, StockOperationKind.Receipt), Day, null));
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

        return new Fixture(org, store, customer, supplier, senior, salesOrder, purchaseOrder, sweater, wool);
    }

    private async Task<long?> ReasonAsync(long organizationId, StockOperationKind kind)
    {
        await using var db = host.NewDb();
        return await db.OperationReasons.Where(r => r.OrganizationId == organizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstAsync();
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
