using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Tagging;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D117: точки подключения модулей. Объект модуля (значение 30–99) получает теги через <see cref="ObjectDirectory.Register"/>,
/// база пропускает его CHECK; подтверждение заказа покупателя вызывает обработчики модулей в той же транзакции;
/// сделка CRM хранится в заказе.
/// </summary>
public sealed class ModuleExtensionTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const TaggedEntity FakeDeal = (TaggedEntity)59;
    private static int _innSeed = 850_000_000;

    [SqlFact]
    public async Task Module_object_gets_tags_through_registered_describer_only_when_visible()
    {
        var org = await CreateOrgAsync();
        ObjectDirectory.Register(FakeDeal, (_, ctx, ids, _) => Task.FromResult<IReadOnlyList<TaggedObjectDto>>(
            ids.Where(id => id < 1000).Select(id => new TaggedObjectDto(FakeDeal, id, $"Сделка {id} · орг. {ctx.OrganizationId}", null, $"crm/deals/{id}")).ToList()));

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Tags.AssignAsync(FakeDeal, 7, null, "Горячая");
            await s.Discussions.AddAsync(FakeDeal, 7, "Обсудим цену с клиентом", null);
            // Объект, которого модуль не показывает пользователю, — «не найдено».
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.AssignAsync(FakeDeal, 5000, null, "Горячая"));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await db.TagLinks.CountAsync(l => l.OrganizationId == org.OrganizationId && l.Entity == FakeDeal));
            Assert.Equal(1, await db.DiscussionComments.CountAsync(c => c.OrganizationId == org.OrganizationId && c.Target == FakeDeal));
        }

        // Встроенный объект переопределить нельзя, значение вне диапазона модулей — тоже.
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjectDirectory.Register(TaggedEntity.SalesOrder, (_, _, _, _) => Task.FromResult<IReadOnlyList<TaggedObjectDto>>([])));
        Assert.Throws<InvalidOperationException>(() => ObjectDirectory.Register(FakeDeal, (_, _, _, _) => Task.FromResult<IReadOnlyList<TaggedObjectDto>>([])));
    }

    [SqlFact]
    public async Task Confirming_sales_order_calls_module_handlers_in_the_same_transaction_and_keeps_crm_deal()
    {
        var org = await CreateOrgAsync();
        long order;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Клиент»", null, null, false, true, null));
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(DateOnly.FromDateTime(host.Clock.UtcNow), customer, store, null, null, true, null));
            await s.Sales.SetOrderLineAsync(order, item, 2, 1000, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
            var tracked = await s.Db.SalesOrders.SingleAsync(o => o.Id == order);
            tracked.LinkCrmDeal(77);
            await s.Db.SaveChangesAsync();
        }

        // Обработчик модуля падает — подтверждение откатывается целиком.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var failing = Sales(s, org, new Handler(fail: true));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await failing.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(KnitErp.Domain.Sales.SalesOrderStatus.Draft, (await db.SalesOrders.SingleAsync(o => o.Id == order)).Status);
        }

        var handler = new Handler(fail: false);
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await Sales(s, org, handler).ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        }

        Assert.Equal([order], handler.Confirmed);
        await using (var db = host.NewDb())
        {
            var saved = await db.SalesOrders.SingleAsync(o => o.Id == order);
            Assert.Equal((KnitErp.Domain.Sales.SalesOrderStatus.Confirmed, 77L), (saved.Status, saved.CrmDealId));
        }
    }

    private SalesService Sales(Services s, CreatedOrganization org, ISalesOrderEvents handler)
    {
        var user = new TestUser { UserId = org.OwnerUserId, OrganizationId = org.OrganizationId };
        var guard = new AccessGuard(s.Db, user, host.Clock);
        return new SalesService(s.Db, guard, user, host.Clock, new StockDocumentService(s.Db, guard, SqlTestHost.Spreadsheet, user, host.Clock), [handler]);
    }

    private sealed class Handler(bool fail) : ISalesOrderEvents
    {
        public List<long> Confirmed { get; } = [];

        public async Task OnConfirmedAsync(IKnitErpDbContext db, AccessContext ctx, long salesOrderId, CancellationToken ct)
        {
            // Статус уже сохранён в этой транзакции.
            var status = await db.SalesOrders.Where(o => o.Id == salesOrderId).Select(o => o.Status).SingleAsync(ct);
            Assert.Equal(KnitErp.Domain.Sales.SalesOrderStatus.Confirmed, status);
            if (fail)
            {
                throw new InvalidOperationException("модуль недоступен");
            }

            Confirmed.Add(salesOrderId);
        }
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
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}", "RU"));
    }
}
