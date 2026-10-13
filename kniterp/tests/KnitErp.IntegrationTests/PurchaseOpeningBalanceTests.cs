using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D133: вводные остатки расчётов с поставщиком (долг, главный экран, акт сверки, аннулирование, права, 404); допуск поступления сверх
/// заказа из настроек закупок (D132 п. 3); разноски оплат в закрытом периоде (D132 п. 2).
/// </summary>
public sealed class PurchaseOpeningBalanceTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 873_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Supplier_opening_balance_counts_in_debt_dashboard_and_settlement_statement()
    {
        var org = await CreateOrgAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(host.Clock.UtcNow, "Europe/Moscow"));
        long supplier, advance, customer, entity, opening, storekeeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Старый поставщик»", null, null, true, false, null));
            advance = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Аванс поставщику»", null, null, true, false, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Только покупатель»", null, null, false, true, null));
            await using (var db = host.NewDb())
            {
                entity = await db.LegalEntities.Where(e => e.OrganizationId == org.OrganizationId).Select(e => e.Id).FirstAsync();
            }

            var start = today.AddDays(-40);
            opening = await s.Purchases.AddOpeningBalanceAsync(supplier, entity, start, 7000m, "Сальдо из прежней программы");
            await s.Purchases.AddOpeningBalanceAsync(advance, entity, start, -1500m, null);
            Assert.Equal("purchase.opening.exists",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Purchases.AddOpeningBalanceAsync(supplier, entity, start, 100m, null))).Code);
            Assert.Equal("purchase.opening.amount",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Purchases.AddOpeningBalanceAsync(advance, entity, start, 0.001m, null))).Code);
            Assert.Equal("stock.document.not_supplier",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Purchases.AddOpeningBalanceAsync(customer, entity, start, 100m, null))).Code);
            await Assert.ThrowsAsync<NotFoundException>(() => s.Purchases.AddOpeningBalanceAsync(supplier, entity + 1_000_000, start, 100m, null));

            // Расчёты с поставщиками: долг 7 000, аванс 1 500 (долг −1 500); в карточке — то же.
            var balances = await s.Purchases.BalancesAsync();
            Assert.Equal((7000m, 7000m), (balances.Single(b => b.SupplierId == supplier).Received, balances.Single(b => b.SupplierId == supplier).Debt));
            Assert.Equal((1500m, -1500m), (balances.Single(b => b.SupplierId == advance).Paid, balances.Single(b => b.SupplierId == advance).Debt));
            Assert.DoesNotContain(await s.Purchases.BalancesAsync(start.AddDays(-1)), b => b.SupplierId == supplier);
            var card = await s.Purchases.OpeningBalancesAsync(supplier);
            Assert.Equal((true, true, 7000m, 1), (card.CanEdit, card.IsSupplier, card.Debt, card.Rows.Count));
            Assert.False((await s.Purchases.OpeningBalancesAsync(customer)).IsSupplier);

            // Главный экран: «Мы должны поставщикам» — только положительные долги.
            Assert.Equal(7000m, (await s.Dashboard.GetAsync(refresh: true)).Finance!.SupplierDebt);

            // Акт сверки: до периода — во входящем сальдо (наш долг — минус), в периоде — строкой в кредите; аванс — в дебете.
            var before = await s.Settlements.BuildAsync(supplier, entity, today.AddDays(-10), today);
            Assert.Equal(-7000m, before.Opening);
            var inside = await s.Settlements.BuildAsync(supplier, entity, start, today);
            Assert.Equal((0m, 7000m), (inside.Opening, inside.Rows.Single(r => r.Document == "Вводный остаток расчётов с поставщиком").Credit));
            Assert.Equal(1500m, (await s.Settlements.BuildAsync(advance, entity, start, today)).Rows.Single().Debit);

            // Аннулирование только с причиной — долга нет, можно ввести новый остаток; всё — в журнале аудита.
            var row = (await s.Purchases.OpeningBalancesAsync(supplier)).Rows.Single();
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Purchases.CancelOpeningBalanceAsync(opening, " ", row.RowVersion));
            await s.Purchases.CancelOpeningBalanceAsync(opening, "Неверная сумма", row.RowVersion);
            var cancelled = (await s.Purchases.OpeningBalancesAsync(supplier)).Rows.Single();
            Assert.Equal("purchase.opening.cancelled", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Purchases.CancelOpeningBalanceAsync(opening, "ещё раз", cancelled.RowVersion))).Code);
            Assert.DoesNotContain(await s.Purchases.BalancesAsync(), b => b.SupplierId == supplier && b.Debt != 0);
            await s.Purchases.AddOpeningBalanceAsync(supplier, entity, start, 6500m, null);
            Assert.Equal(2, (await s.Purchases.OpeningBalancesAsync(supplier)).Rows.Count);

            storekeeper = (await s.Access.InviteAsync(new InviteUserCommand($"storekeeper-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null)))
                .UserId;
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(4, await db.AuditEntries.CountAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SupplierOpeningBalanceChanged));
            (await db.Users.SingleAsync(u => u.Id == storekeeper)).Activate();
            await db.SaveChangesAsync();
        }

        // Без прав закупок и «Цены и суммы» — отказ; чужая организация — 404.
        await using (var k = host.As(storekeeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Purchases.OpeningBalancesAsync(supplier));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Purchases.AddOpeningBalanceAsync(supplier, entity, today, 1m, null));
        }

        var foreign = await CreateOrgAsync();
        await using (var o = host.As(foreign.OwnerUserId, foreign.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Purchases.OpeningBalancesAsync(supplier));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Purchases.CancelOpeningBalanceAsync(opening, "чужое", [1]));
        }
    }

    [SqlFact]
    public async Task Over_receipt_tolerance_from_purchase_settings_limits_receipt_by_order()
    {
        var f = await SetUpAsync();
        long order, purchaser;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var settings = await s.PurchaseSettings.GetAsync();
            Assert.Equal((0m, true, null), (settings.OverReceiptPercent, settings.CanEdit, settings.RowVersion));
            Assert.Equal("purchase.settings.over_receipt",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.PurchaseSettings.SaveAsync(10.5m, null))).Code);
            await s.PurchaseSettings.SaveAsync(10m, null);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.PurchaseSettings.SaveAsync(5m, null));
            Assert.Equal(10m, (await s.PurchaseSettings.GetAsync()).OverReceiptPercent);

            order = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, f.Supplier, f.Store, Day.AddDays(5), null, true, null));
            await s.Purchases.SetOrderLineAsync(order, f.Item, 50, 100m, null, (await s.Purchases.GetOrderAsync(order)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(order, (await s.Purchases.GetOrderAsync(order)).RowVersion);

            // 50 × 1,10 = 55: 56 нельзя, 55 можно; затем ещё 1 сверх — нельзя.
            var receipt = await s.Purchases.CreateReceiptAsync(order);
            await s.Documents.SetLineAsync(receipt, f.Item, 56, (await s.Documents.GetAsync(receipt)).RowVersion);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion));
            Assert.Equal("purchase.receipt.exceeds", ex.Code);
            Assert.Contains("с допуском 10%", ex.Message);
            await s.Documents.SetLineAsync(receipt, f.Item, 55, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            // «Оформить поступление» по полностью принятому заказу не даёт — документ по заказу вручную.
            var reason = await s.Db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstOrDefaultAsync();
            var extra = await s.Documents.CreateAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(f.Store, null, f.Supplier, reason, Day, null, PurchaseOrderId: order));
            await s.Documents.SetLineAsync(extra, f.Item, 1, (await s.Documents.GetAsync(extra)).RowVersion);
            Assert.Equal("purchase.receipt.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(extra, (await s.Documents.GetAsync(extra)).RowVersion))).Code);

            // Допуск снят — по заказу ничего сверх; сообщение без допуска.
            await s.PurchaseSettings.SaveAsync(0m, (await s.PurchaseSettings.GetAsync()).RowVersion);
            var noTolerance = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(extra, (await s.Documents.GetAsync(extra)).RowVersion));
            Assert.DoesNotContain("допуск", noTolerance.Message);

            purchaser = (await s.Access.InviteAsync(new InviteUserCommand($"accountant-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            var audit = await db.AuditEntries.Where(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.PurchaseSettingsChanged)
                .OrderBy(a => a.Id).Select(a => a.After).ToListAsync();
            Assert.Equal(["допуск поступления сверх заказа 10%", "допуск поступления сверх заказа 0%"], audit);
            (await db.Users.SingleAsync(u => u.Id == purchaser)).Activate();
            await db.SaveChangesAsync();
        }

        // Менять допуск может только «Организация: изменение» — менеджер сам себе допуск не расширяет.
        await using (var m = host.As(purchaser, f.Org.OrganizationId))
        {
            var dto = await m.PurchaseSettings.GetAsync();
            Assert.False(dto.CanEdit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.PurchaseSettings.SaveAsync(5m, dto.RowVersion));
        }
    }

    [SqlFact]
    public async Task Payment_allocations_in_closed_period_are_refused_and_skipped()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var september = new DateOnly(2026, 9, 5);
        var orderA = await ConfirmedPurchaseOrderAsync(s, f, september);
        var orderB = await ConfirmedPurchaseOrderAsync(s, f, new DateOnly(2026, 10, 5));
        var withOrder = await s.Purchases.CreatePaymentAsync(new DateOnly(2026, 9, 10), f.Supplier, orderA, 300m, null);
        var advance = await s.Purchases.CreatePaymentAsync(new DateOnly(2026, 9, 12), f.Supplier, null, 200m, null);

        // Продажи — тот же разносчик: оплата и заказ покупателя в сентябре.
        var salesOrder = await s.Sales.CreateOrderAsync(new KnitErp.Domain.Sales.SalesOrderHeader(september, f.Customer, f.Store, september, null, false, null));
        await s.Sales.SetOrderLineAsync(salesOrder, f.Item, 1, 100m, null, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
        await s.Sales.ConfirmOrderAsync(salesOrder, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
        var customerPayment = await s.Sales.CreatePaymentAsync(new DateOnly(2026, 9, 10), f.Customer, null, 50m, null);

        await s.Period.CloseAsync(new DateOnly(2026, 9, 30), null);

        // Разноска, снятие и изменение с датой (поздняя из дат оплаты и заказа) в закрытом периоде — нельзя.
        var allocation = (await s.Purchases.GetPaymentAllocationsAsync(withOrder)).Allocations.Single();
        Assert.Equal("period.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Purchases.RemovePaymentAllocationAsync(allocation.Id, allocation.RowVersion))).Code);
        Assert.Equal("period.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Purchases.AllocatePaymentAsync(advance, orderA, 100m))).Code);
        Assert.Equal("period.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.AllocatePaymentAsync(customerPayment, salesOrder, 50m))).Code);
        Assert.Equal(0m, await s.Purchases.AvailableAdvancesAsync(orderA));
        Assert.Equal(0m, await s.Purchases.ApplyAdvancesAsync(orderA));

        // Сентябрьский аванс в октябрьский заказ — дата разноски открыта: автоматическая разноска пропускает сентябрьский заказ.
        Assert.Equal(200m, await s.Purchases.AvailableAdvancesAsync(orderB));
        Assert.Equal(200m, await s.Purchases.AutoAllocatePaymentAsync(advance));
        var toB = (await s.Purchases.GetPaymentAllocationsAsync(advance)).Allocations.Single();
        Assert.Equal((orderB, 200m), (toB.OrderId, toB.Amount));
        await s.Purchases.RemovePaymentAllocationAsync(toB.Id, toB.RowVersion);

        // Период открыт с причиной — снять сентябрьскую разноску можно.
        await s.Period.ReopenAsync(null, "Исправление разноски", (await s.Period.GetAsync()).RowVersion);
        await s.Purchases.RemovePaymentAllocationAsync(allocation.Id, allocation.RowVersion);
        Assert.Empty((await s.Purchases.GetPaymentAllocationsAsync(withOrder)).Allocations);
    }

    private static async Task<long> ConfirmedPurchaseOrderAsync(Services s, Fixture f, DateOnly date)
    {
        var order = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(date, f.Supplier, f.Store, date.AddDays(5), null, true, null));
        await s.Purchases.SetOrderLineAsync(order, f.Item, 10, 100m, null, (await s.Purchases.GetOrderAsync(order)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(order, (await s.Purchases.GetOrderAsync(order)).RowVersion);
        return order;
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Item, long Supplier, long Customer);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var item = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.Material, unit, null));
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Поставщик»", null, null, true, false, null));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель»", null, null, false, true, null));
        return new Fixture(org, store, item, supplier, customer);
    }

    private static string Inn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        var inn = Inn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
