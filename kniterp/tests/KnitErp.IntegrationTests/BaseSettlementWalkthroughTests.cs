using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D143: акт сверки с покупателем и с поставщиком — по шагам: вводный остаток, аванс, отгрузка (поступление), возврат, зачёт переплаты
/// и возврат денег. После каждого шага сальдо акта на конец совпадает с расчётами (BalancesAsync), входящее сальдо — со строками до периода.
/// </summary>
public sealed class BaseSettlementWalkthroughTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 961_000_000;

    [SqlFact]
    public async Task Customer_statement_follows_every_step()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель сверки»", null, null, false, true, null));
        var t = f.Today;

        async Task StepAsync(decimal opening, decimal closing, int rows)
        {
            var act = await s.Settlements.BuildAsync(customer, f.Entity, t.AddDays(-15), t);
            Assert.Equal((opening, closing, rows), (act.Opening, act.Closing, act.Rows.Count));
            Assert.Equal(closing, (await s.Sales.BalancesAsync(t)).SingleOrDefault(b => b.CustomerId == customer)?.Debt ?? 0m);
            var whole = await s.Settlements.BuildAsync(customer, f.Entity, t.AddDays(-60), t);
            Assert.Equal((0m, closing), (whole.Opening, whole.Closing)); // всё в периоде — то же сальдо на конец
        }

        // 1. Вводный остаток 5 000 (долг покупателя) — до периода: во входящем сальдо.
        await s.Sales.AddOpeningBalanceAsync(customer, f.Entity, t.AddDays(-30), 5_000m, null);
        await StepAsync(5_000m, 5_000m, 0);

        // 2. Аванс 2 000 без заказа — кредит.
        await s.Sales.CreatePaymentAsync(t.AddDays(-10), customer, null, 2_000m, null);
        await StepAsync(5_000m, 3_000m, 1);

        // 3. Заказ 10 × 1 000, оплата 10 000 по заказу, отгрузка всего — дебет 10 000, кредит 10 000.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(t.AddDays(-8), customer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, 10, 1_000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.CreatePaymentAsync(t.AddDays(-7), customer, order, 10_000m, null);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await RedateAsync(s, ship, t.AddDays(-6));
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        await StepAsync(5_000m, 3_000m, 3);

        // 4. Возврат 3 шт. — кредит 3 000: покупатель переплатил.
        var ret = await s.Sales.CreateReturnAsync(order);
        await RedateAsync(s, ret, t.AddDays(-4));
        await s.Documents.SetLineAsync(ret, f.Sweater, 3, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
        await StepAsync(5_000m, 0m, 4);

        // 5. Зачёт переплаты в аванс — только разноски, в акте ничего не меняется.
        Assert.Equal(3_000m, await s.Sales.ReleaseOverpaymentAsync(order));
        await StepAsync(5_000m, 0m, 4);

        // 6. Возврат денег 1 000 — дебет: долг покупателя снова 1 000.
        await s.Sales.RefundAsync(order, new RefundCommand(t.AddDays(-2), 1_000m, f.Cash, null));
        await StepAsync(5_000m, 1_000m, 5);
        var act = await s.Settlements.BuildAsync(customer, f.Entity, t.AddDays(-15), t);
        Assert.Equal((11_000m, 15_000m), (act.DebitTotal, act.CreditTotal));
        Assert.Equal(act.Rows.OrderBy(r => r.Date).Select(r => r.Date), act.Rows.Select(r => r.Date));
    }

    [SqlFact]
    public async Task Supplier_statement_follows_every_step()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Поставщик сверки»", null, null, true, false, null));
        var t = f.Today;

        async Task StepAsync(decimal opening, decimal closing, int rows)
        {
            var act = await s.Settlements.BuildAsync(supplier, f.Entity, t.AddDays(-15), t);
            Assert.Equal((opening, closing, rows), (act.Opening, act.Closing, act.Rows.Count));
            // Сальдо акта — со стороны контрагента (минус — наш долг), долг в расчётах с поставщиками — наш долг.
            Assert.Equal(-closing, (await s.Purchases.BalancesAsync(t)).SingleOrDefault(b => b.SupplierId == supplier)?.Debt ?? 0m);
        }

        // 1. Вводный остаток: наш долг 7 000.
        await s.Purchases.AddOpeningBalanceAsync(supplier, f.Entity, t.AddDays(-30), 7_000m, null);
        await StepAsync(-7_000m, -7_000m, 0);

        // 2. Аванс поставщику 2 000 без заказа — дебет.
        await s.Purchases.CreatePaymentAsync(t.AddDays(-10), supplier, null, 2_000m, null);
        await StepAsync(-7_000m, -5_000m, 1);

        // 3. Заказ 5 × 1 000, поступление всего — кредит 5 000.
        var order = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(t.AddDays(-8), supplier, f.Store, null, null, true, null));
        await s.Purchases.SetOrderLineAsync(order, f.Wool, 5, 1_000m, null, (await s.Purchases.GetOrderAsync(order)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(order, (await s.Purchases.GetOrderAsync(order)).RowVersion);
        var receipt = await s.Purchases.CreateReceiptAsync(order);
        await RedateAsync(s, receipt, t.AddDays(-6));
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        await StepAsync(-7_000m, -10_000m, 2);

        // 4. Возврат поставщику 1 — дебет 1 000.
        var back = await s.Purchases.CreateReturnAsync(order);
        await RedateAsync(s, back, t.AddDays(-4));
        await s.Documents.SetLineAsync(back, f.Wool, 1, (await s.Documents.GetAsync(back)).RowVersion);
        await s.Documents.PostAsync(back, (await s.Documents.GetAsync(back)).RowVersion);
        await StepAsync(-7_000m, -9_000m, 3);

        // 5. Оплата по заказу 4 000 — дебет; аванс 2 000 остаётся авансом, долг 3 000.
        await s.Purchases.CreatePaymentAsync(t.AddDays(-2), supplier, order, 4_000m, null);
        await StepAsync(-7_000m, -5_000m, 4);
    }

    /// <summary>Дата документа по заказу — шапка прежняя, меняется только дата.</summary>
    private static async Task RedateAsync(Services s, long id, DateOnly date)
    {
        var d = await s.Documents.GetAsync(id);
        await s.Documents.UpdateHeaderAsync(id, new StockDocumentHeader(d.WarehouseId, d.TargetWarehouseId, d.CounterpartyId, d.ReasonId, date, d.Comment,
            d.PurchaseOrderId, d.SalesOrderId), d.RowVersion);
    }

    private sealed record Fixture(CreatedOrganization Org, DateOnly Today, long Entity, long Store, long Sweater, long Wool, long Cash);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(host.Clock.UtcNow, "Europe/Moscow"));
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
        var wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
        var entity = (await s.LegalEntities.ListAsync()).Single().Id;
        var cash = await s.LegalEntities.AddCashAsync(entity, "Касса");
        long reason;
        await using (var db = host.NewDb())
        {
            reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt).Select(r => r.Id).FirstAsync();
        }

        var stock = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, today.AddDays(-20), null));
        await s.Documents.SetLineAsync(stock, sweater, 20, (await s.Documents.GetAsync(stock)).RowVersion);
        await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);

        // Касса с деньгами для возврата покупателю — приход наличными от другого покупателя.
        var walkIn = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Розница»", null, null, false, true, null));
        await s.Sales.CreatePaymentAsync(today.AddDays(-20), walkIn, null, 5_000m, null, null, cash);
        return new Fixture(org, today, entity, store, sweater, wool, cash);
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
