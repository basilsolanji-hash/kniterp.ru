using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D350 (сессия 4, Р3): цена ниже минимальной в заказе покупателя — подсветка строки. После слияния с продажами действует D1104:
/// подтверждение с ценой ниже минимальной — только с правом «цена ниже минимальной» и причиной, разрешение пишется в журнал.
/// </summary>
public sealed class CatMinPriceOrderTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 957_000_000;

    [SqlFact]
    public async Task Line_below_min_price_is_flagged_and_confirmation_warns_but_passes()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync())[0].Id;
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, vat));
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.Finished, piece, null, vat));
        foreach (var id in new[] { sweater, hat })
        {
            var trade = await s.Trade.GetAsync(id);
            await s.Trade.SaveAsync(id, new ItemTradeDetails(PaymentSubject.Goods, false, TrackingType.None, null, 1000m), trade.RowVersion);
        }

        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var day = new DateOnly(2026, 10, 1);
        // Цены заказа с НДС — как минимальная цена (вид цен по умолчанию с НДС): D1104 сравнивает цены с НДС.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(day, customer, store, day, null, true, null));
        // Свитер: 1100 со скидкой 10 % = 990 — ниже 1000; шапка: ровно 1000 — не ниже.
        await s.Sales.SetOrderLineAsync(order, sweater, 1, 1100m, 22m, 10m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderLineAsync(order, hat, 1, 1000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);

        var dto = await s.Sales.GetOrderAsync(order);
        Assert.True(dto.Lines.Single(l => l.ItemId == sweater).BelowMinimum);
        Assert.Equal(1000m, dto.Lines.Single(l => l.ItemId == sweater).MinPrice);
        Assert.False(dto.Lines.Single(l => l.ItemId == hat).BelowMinimum);

        // D1104: без причины не подтверждается, с причиной — подтверждается, позиция ниже минимальной — в журнале.
        Assert.Equal("sales.price.override_reason",
            (await Assert.ThrowsAsync<KnitErp.Domain.Common.BusinessRuleException>(() => s.Sales.ConfirmOrderAsync(order, dto.RowVersion))).Code);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion, "Распродажа");
        Assert.Equal(SalesOrderStatus.Confirmed, (await s.Sales.GetOrderAsync(order)).Status);
        await using var db = host.NewDb();
        var audit = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.SalesPriceOverride).SingleAsync();
        Assert.Contains("СВ-1", audit.Before);
        Assert.DoesNotContain("ШП-1", audit.Before);
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
