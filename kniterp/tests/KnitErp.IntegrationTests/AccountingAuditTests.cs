using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Application.Finance;
using KnitErp.Application.Organizations;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D129 — аудит учёта 12.10.2026: отгрузка и поступление не больше заказа (и параллельно), сторно после возврата, параллельное
/// утверждение начальных остатков, отмена кассовой оплаты, повтор после конфликта версий в той же вкладке, заказ по КП, дата ставки НДС.
/// </summary>
public sealed class AccountingAuditTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 871_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Shipment_cannot_exceed_order_even_with_two_drafts_posted_in_parallel()
    {
        var f = await SetUpAsync();
        long order, first, second;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await StockAsync(s, f, 300);
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
            await s.Sales.SetOrderLineAsync(order, f.Item, 100, 100m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);

            // Повторное «Оформить отгрузку» открывает тот же черновик.
            first = await s.Sales.CreateShipmentAsync(order);
            Assert.Equal(first, await s.Sales.CreateShipmentAsync(order));

            // Второй черновик на весь заказ — вручную (или через API).
            var reason = await ReasonAsync(s, f, StockOperationKind.Shipment);
            second = await s.Documents.CreateAsync(StockOperationKind.Shipment,
                new StockDocumentHeader(f.Store, null, f.Customer, reason, Day, null, SalesOrderId: order));
            await s.Documents.SetLineAsync(second, f.Item, 100, (await s.Documents.GetAsync(second)).RowVersion);
        }

        var results = await Task.WhenAll(TryPostAsync(f, first), TryPostAsync(f, second));
        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal("sales.shipment.exceeds", results.Single(r => r is not null));

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal(100m, (await s.Sales.GetOrderAsync(order)).Lines.Single().Shipped);

            // Строка больше заказанного в одном документе — тоже отказ.
            var reason = await ReasonAsync(s, f, StockOperationKind.Shipment);
            var extra = await s.Documents.CreateAsync(StockOperationKind.Shipment,
                new StockDocumentHeader(f.Store, null, f.Customer, reason, Day, null, SalesOrderId: order));
            await s.Documents.SetLineAsync(extra, f.Item, 1, (await s.Documents.GetAsync(extra)).RowVersion);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(extra, (await s.Documents.GetAsync(extra)).RowVersion));
            Assert.Equal("sales.shipment.exceeds", ex.Code);
            Assert.Contains("осталось отгрузить 0", ex.Message);
        }
    }

    [SqlFact]
    public async Task Shipment_reversal_is_refused_while_returns_would_exceed_the_rest()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await StockAsync(s, f, 300);
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day, null, false, null));
        await s.Sales.SetOrderLineAsync(order, f.Item, 100, 100m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var big = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.SetLineAsync(big, f.Item, 60, (await s.Documents.GetAsync(big)).RowVersion);
        await s.Documents.PostAsync(big, (await s.Documents.GetAsync(big)).RowVersion);
        var small = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(small, (await s.Documents.GetAsync(small)).RowVersion); // остальные 40
        var ret = await s.Sales.CreateReturnAsync(order);
        await s.Documents.SetLineAsync(ret, f.Item, 50, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);

        // Без отгрузки 40 останется 60 ≥ 50 возвращённых — можно; без 60 осталось бы 0 < 50 — нельзя.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.ReverseAsync(big, "Ошибка", (await s.Documents.GetAsync(big)).RowVersion));
        Assert.Equal("stock.reverse.returns_exist", ex.Code);
        // D1159 (модуль «Продажи») строже: после проведённого возврата сторно отгрузки по заказу запрещено вовсе.
        Assert.Equal("sales.shipment.reverse_after_return", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.ReverseAsync(small, "Ошибка", (await s.Documents.GetAsync(small)).RowVersion))).Code);
        var line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        Assert.Equal((100m, 50m), (line.Shipped, line.Returned));

        // Сначала сторно возврата — затем и отгрузки можно сторнировать.
        await s.Documents.ReverseAsync(ret, "Ошибка", (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.ReverseAsync(small, "Ошибка", (await s.Documents.GetAsync(small)).RowVersion);
        await s.Documents.ReverseAsync(big, "Ошибка", (await s.Documents.GetAsync(big)).RowVersion);
        line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        Assert.Equal((0m, 0m), (line.Shipped, line.Returned));
    }

    [SqlFact]
    public async Task Purchase_receipt_cannot_exceed_order_and_its_reversal_respects_returns()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Поставщик»", null, null, true, false, null));
        var order = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, f.Store, Day.AddDays(5), null, true, null));
        await s.Purchases.SetOrderLineAsync(order, f.Item, 50, 100m, null, (await s.Purchases.GetOrderAsync(order)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(order, (await s.Purchases.GetOrderAsync(order)).RowVersion);

        var receipt = await s.Purchases.CreateReceiptAsync(order);
        Assert.Equal(receipt, await s.Purchases.CreateReceiptAsync(order));
        await s.Documents.SetLineAsync(receipt, f.Item, 60, (await s.Documents.GetAsync(receipt)).RowVersion);
        Assert.Equal("purchase.receipt.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion))).Code);
        await s.Documents.SetLineAsync(receipt, f.Item, 50, (await s.Documents.GetAsync(receipt)).RowVersion);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

        var ret = await s.Purchases.CreateReturnAsync(order);
        await s.Documents.SetLineAsync(ret, f.Item, 5, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
        Assert.Equal("stock.reverse.returns_exist", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.ReverseAsync(receipt, "Ошибка", (await s.Documents.GetAsync(receipt)).RowVersion))).Code);
    }

    [SqlFact]
    public async Task Parallel_approval_of_two_opening_balances_enters_the_item_once()
    {
        var f = await SetUpAsync();
        long senior;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: f.Store))).UserId;
        }

        await using (var users = host.NewDb())
        {
            (await users.Users.SingleAsync(u => u.Id == senior)).Activate();
            await users.SaveChangesAsync();
        }

        var docs = new List<long>();
        await using (var s = host.As(senior, f.Org.OrganizationId))
        {
            for (var i = 0; i < 2; i++)
            {
                var doc = await s.OpeningBalances.CreateAsync(f.Store, Day, null);
                await s.OpeningBalances.SetLineAsync(doc, f.Item, 10, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
                await s.OpeningBalances.SubmitAsync(doc, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
                docs.Add(doc);
            }
        }

        async Task<string?> ApproveAsync(long id)
        {
            await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
            try
            {
                await s.OpeningBalances.ApproveAsync(id, (await s.OpeningBalances.GetAsync(id)).RowVersion);
                return null;
            }
            catch (BusinessRuleException ex)
            {
                return ex.Code;
            }
        }

        var results = await Task.WhenAll(ApproveAsync(docs[0]), ApproveAsync(docs[1]));
        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal("stock.opening.already_entered", results.Single(r => r is not null));
        await using var db = host.NewDb();
        Assert.Equal(10m, await db.StockMovements.Where(m => m.OrganizationId == f.Org.OrganizationId && m.ItemId == f.Item).SumAsync(m => m.Quantity));
    }

    [SqlFact]
    public async Task Cancelling_cash_customer_payment_cannot_make_cash_negative()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var entity = (await s.LegalEntities.ListAsync()).Single();
        var cash = await s.LegalEntities.AddCashAsync(entity.Id, "Касса магазина");
        var payment = await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 100m, null, null, cash);
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day, MoneyOperationKind.Expense, cash, null, 80m, "Петров П. П.", "Сдача в банк", null));

        var version = (await s.Db.CustomerPayments.AsNoTracking().SingleAsync(p => p.Id == payment)).RowVersion;
        Assert.Equal("money.cash.insufficient", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.CancelPaymentAsync(payment, "Ошибка", version))).Code);

        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day, MoneyOperationKind.Income, cash, null, 100m, "Петров П. П.", "Взнос", null));
        await s.Sales.CancelPaymentAsync(payment, "Ошибка", version);
        await using var db = host.NewDb();
        Assert.Equal(CustomerPaymentStatus.Cancelled, (await db.CustomerPayments.SingleAsync(p => p.Id == payment)).Status);
        Assert.Equal(20m, (await s.Money.BalancesAsync(null)).Single(b => b.AccountId == cash).Balance);
    }

    [SqlFact]
    public async Task Same_tab_can_save_with_fresh_version_after_a_conflict()
    {
        var f = await SetUpAsync();
        await using var tab = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var supplier = await tab.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Версия»", null, null, true, false, null));
        async Task<byte[]> VersionAsync()
        {
            await using var db = host.NewDb();
            return (await db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == supplier)).RowVersion;
        }

        // Вкладка сохранила (контрагент теперь отслеживается её контекстом), затем другую правку сделали в другой вкладке.
        await tab.Counterparties.UpdateAsync(supplier, new CounterpartyCommand("ООО «Версия 2»", null, null, true, false, null), await VersionAsync());
        var mine = await VersionAsync();
        await using (var other = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await other.Counterparties.UpdateAsync(supplier, new CounterpartyCommand("ООО «Чужая правка»", null, null, true, false, null), mine);
        }

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            tab.Counterparties.UpdateAsync(supplier, new CounterpartyCommand("ООО «Версия 3»", null, null, true, false, null), mine));

        // Пользователь перечитал форму — та же вкладка сохраняет с новой версией.
        await tab.Counterparties.UpdateAsync(supplier, new CounterpartyCommand("ООО «Версия 4»", null, null, true, false, null), await VersionAsync());
        await using var check = host.NewDb();
        Assert.Equal("ООО «Версия 4»", (await check.Counterparties.SingleAsync(c => c.Id == supplier)).Name);
    }

    [SqlFact]
    public async Task Order_from_offer_is_created_once_and_not_from_expired_offer()
    {
        var f = await SetUpAsync();
        await using var crm = host.Crm(f.Org.OwnerUserId, f.Org.OrganizationId);
        var contact = await crm.Contacts.CreateAsync(new CrmContactValues("Покупатель Иван", CounterpartyId: f.Customer, Email: "ivan@example.org"));
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера", f.Customer, contact));
        await crm.Deals.SetLineAsync(deal, f.Item, 10, 1000m, (await crm.Deals.GetAsync(deal)).RowVersion);
        var offer = await crm.Offers.CreateAsync(new CrmOfferCommand(deal));
        var shortOffer = await crm.Offers.CreateAsync(new CrmOfferCommand(deal, ValidDays: 1));

        var order = await crm.Deals.CreateOrderFromOfferAsync(offer, f.Store);
        Assert.Equal(order, await crm.Deals.CreateOrderFromOfferAsync(offer, f.Store));
        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await db.CrmDealOrders.CountAsync(l => l.DealId == deal));
            Assert.Equal(offer, (await db.CrmDealOrders.SingleAsync(l => l.SalesOrderId == order)).OfferId);
        }

        var now = host.Clock.UtcNow;
        try
        {
            host.Clock.UtcNow = now.AddDays(5);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Deals.CreateOrderFromOfferAsync(shortOffer, f.Store));
            Assert.Equal("crm.offer.expired", ex.Code);
        }
        finally
        {
            host.Clock.UtcNow = now;
        }
    }

    [SqlFact]
    public async Task Price_list_vat_rate_is_taken_on_organization_local_date()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var rate = await s.VatRates.CreateAsync("Тестовая ставка", VatRateKind.Reduced, new DateOnly(2026, 1, 1), 20m);
        var dto = (await s.VatRates.ListAsync()).Rates.Single(r => r.Id == rate);
        await s.VatRates.AddPeriodAsync(rate, new DateOnly(2027, 1, 1), 24m, dto.RowVersion);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-НДС", "Шапка", ItemType.Finished, piece, null, rate));
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Прайс»", null, null, true, false, null));
        await s.Suppliers.SetAsync(hat, supplier, "P-1", null, true);

        var now = host.Clock.UtcNow;
        try
        {
            // 31.12.2026 22:00 UTC — в Москве уже 01.01.2027, действует 24%.
            host.Clock.UtcNow = new DateTime(2026, 12, 31, 22, 0, 0, DateTimeKind.Utc);
            var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
                [new SheetData("Прайс", ["Артикул", "Наименование", "Штрихкод", "Цена"], [["P-1", "Шапка", "", "124"]])]));
            var plan = await s.PriceLists.PreviewAsync(supplier, file, pricesIncludeVat: true);
            Assert.Equal(100m, plan.Rows.Single(r => r.ItemId == hat).NewPurchasePrice);
        }
        finally
        {
            host.Clock.UtcNow = now;
        }
    }

    private async Task<string?> TryPostAsync(Fixture f, long id)
    {
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        try
        {
            await s.Documents.PostAsync(id, (await s.Documents.GetAsync(id)).RowVersion);
            return null;
        }
        catch (BusinessRuleException ex)
        {
            return ex.Code;
        }
    }

    private static async Task<long?> ReasonAsync(Services s, Fixture f, StockOperationKind kind) =>
        await s.Db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstOrDefaultAsync();

    private static async Task StockAsync(Services s, Fixture f, decimal quantity)
    {
        var doc = await s.Documents.CreateAsync(StockOperationKind.Receipt,
            new StockDocumentHeader(f.Store, null, null, await ReasonAsync(s, f, StockOperationKind.Receipt), Day, null));
        await s.Documents.SetLineAsync(doc, f.Item, quantity, (await s.Documents.GetAsync(doc)).RowVersion);
        await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Item, long Customer);

    private async Task<Fixture> SetUpAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель»", null, null, false, true, null));
            return new Fixture(org, store, item, customer);
        }
    }
}
