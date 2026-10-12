using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Поступление, перемещение, списание: проведение, контроль остатка, сторно, области складов.</summary>
public sealed class StockDocumentFlowTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 710_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Receipt_transfer_and_write_off_move_stock()
    {
        var f = await SetUpAsync();
        long receipt, transfer, writeOff;
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(f.Yarn, null, f.Supplier, f.Reason("Закупка у поставщика"), Day, "Счёт 15"));
            Assert.Equal("ПТ-000001", (await s.Documents.GetAsync(receipt)).Number);
            await AddLinesAsync(s, receipt, (f.Wool, 50m), (f.Buttons, 200m));
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            transfer = await s.Documents.CreateAsync(StockOperationKind.Transfer,
                new StockDocumentHeader(f.Yarn, f.Shop, null, f.Reason("Перемещение между складами"), Day, null));
            await AddLinesAsync(s, transfer, (f.Wool, 20m));
            var card = await s.Documents.GetAsync(transfer);
            Assert.Equal(50m, card.Lines.Single().Available);
            Assert.Empty(card.Shortages);
            await s.Documents.PostAsync(transfer, card.RowVersion);

            writeOff = await s.Documents.CreateAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(f.Yarn, null, null, f.Reason("Передача в производство"), Day, null));
            await AddLinesAsync(s, writeOff, (f.Wool, 12.5m), (f.Buttons, 200m));
            await s.Documents.PostAsync(writeOff, (await s.Documents.GetAsync(writeOff)).RowVersion);
            Assert.Equal(StockDocumentStatus.Posted, (await s.Documents.GetAsync(writeOff)).Status);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var balances = await s.Stock.BalancesAsync(new StockFilter());
            Assert.Equal(17.5m, balances.Single(b => b.WarehouseId == f.Yarn && b.ItemId == f.Wool).Quantity);
            Assert.Equal(20m, balances.Single(b => b.WarehouseId == f.Shop && b.ItemId == f.Wool).Quantity);
            Assert.DoesNotContain(balances, b => b.ItemId == f.Buttons);
            var list = await s.Documents.ListAsync(new StockDocumentListFilter(StockOperationKind.Transfer));
            Assert.Equal("ПМ-000001", list.Single().Number);

            // Реестр: период, склад (в том числе получатель перемещения), поиск по номеру и контрагенту.
            Assert.Equal(3, (await s.Documents.ListAsync(new StockDocumentListFilter(From: Day, To: Day))).Count);
            Assert.Empty(await s.Documents.ListAsync(new StockDocumentListFilter(From: Day.AddDays(1))));
            Assert.Equal(["ПМ-000001"], (await s.Documents.ListAsync(new StockDocumentListFilter(WarehouseId: f.Shop))).Select(d => d.Number));
            Assert.Equal(["ПТ-000001"], (await s.Documents.ListAsync(new StockDocumentListFilter(Search: "Пряжа"))).Select(d => d.Number));
            Assert.Equal(["СП-000001"], (await s.Documents.ListAsync(new StockDocumentListFilter(Search: "СП-0"))).Select(d => d.Number));
            Assert.Equal(2, (await s.Documents.ListVisibleWarehousesAsync()).Count);
        }

        await using var db = host.NewDb();
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.Source == StockSource.StockDocument && m.SourceId == transfer));
        Assert.True(await db.AuditEntries.AnyAsync(e => e.Action == AuditActions.StockDocumentPosted && e.EntityId == writeOff.ToString()));
    }

    [SqlFact]
    public async Task Write_off_cannot_take_more_than_balance_and_storekeeper_cannot_post()
    {
        var f = await SetUpAsync();
        await PostedReceiptAsync(f, f.Yarn, (f.Wool, 10m));

        long doc;
        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            doc = await s.Documents.CreateAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(f.Yarn, null, null, f.Reason("Передача в производство"), Day, null));
            await AddLinesAsync(s, doc, (f.Wool, 12m));
            var card = await s.Documents.GetAsync(doc);
            Assert.False(card.CanPost);
            Assert.Equal(12m, card.Shortages.Single().Quantity);
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => s.Documents.PostAsync(doc, card.RowVersion));
            Assert.Equal(Permissions.WarehouseDocumentPost, denied.PermissionCode);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var card = await s.Documents.GetAsync(doc);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Documents.PostAsync(doc, card.RowVersion));
            Assert.Equal("stock.balance.insufficient", ex.Code);
            Assert.Contains("нужно 12, есть 10", ex.Message);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var card = await s.Documents.GetAsync(doc);
            Assert.Equal(StockDocumentStatus.Draft, card.Status);
            await s.Documents.SetLineAsync(doc, f.Wool, 10m, card.RowVersion);
            await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
        }

        await using var db = host.NewDb();
        Assert.Equal(0m, await db.StockMovements.Where(m => m.WarehouseId == f.Yarn && m.ItemId == f.Wool).SumAsync(m => m.Quantity));
    }

    [SqlFact]
    public async Task Reversal_returns_stock_but_not_below_zero()
    {
        var f = await SetUpAsync();
        var receipt = await PostedReceiptAsync(f, f.Yarn, (f.Wool, 10m));
        long writeOff;
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            writeOff = await s.Documents.CreateAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(f.Yarn, null, null, f.Reason("Брак"), Day, null));
            await AddLinesAsync(s, writeOff, (f.Wool, 4m));
            var draft = await s.Documents.GetAsync(writeOff);
            Assert.Equal("stock.document.comment_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Documents.PostAsync(writeOff, draft.RowVersion))).Code);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var card = await s.Documents.GetAsync(writeOff);
            await s.Documents.UpdateHeaderAsync(writeOff,
                new StockDocumentHeader(f.Yarn, null, null, f.Reason("Брак"), Day, "Пятна на мотках"), card.RowVersion);
            await s.Documents.PostAsync(writeOff, (await s.Documents.GetAsync(writeOff)).RowVersion);

            // Приход уже частично списан: его сторно увело бы остаток в минус.
            var posted = await s.Documents.GetAsync(receipt);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Documents.ReverseAsync(receipt, "Не тот склад", posted.RowVersion));
            Assert.Equal("stock.balance.insufficient", ex.Code);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Documents.ReverseAsync(writeOff, "Брак не подтвердился", (await s.Documents.GetAsync(writeOff)).RowVersion);
            await s.Documents.ReverseAsync(receipt, "Не тот склад", (await s.Documents.GetAsync(receipt)).RowVersion);
            var card = await s.Documents.GetAsync(receipt);
            Assert.Equal((StockDocumentStatus.Reversed, "Не тот склад", false), (card.Status, card.ReversalReason, card.CanReverse));
            Assert.Empty(await s.Stock.BalancesAsync(new StockFilter()));
        }

        await using var db = host.NewDb();
        Assert.Equal(1, await db.StockMovements.CountAsync(m => m.Source == StockSource.StockDocumentReversal && m.SourceId == receipt));
    }

    [SqlFact]
    public async Task Warehouse_scope_and_reference_checks()
    {
        var f = await SetUpAsync();
        long customerOnly;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            customerOnly = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель»", null, null, false, true, null));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Documents.CreateAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(f.Shop, null, null, null, Day, null)));
            Assert.Equal("stock.document.reason_kind", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Documents.CreateAsync(
                StockOperationKind.Receipt, new StockDocumentHeader(f.Yarn, null, null, f.Reason("Брак"), Day, null)))).Code);
            Assert.Equal("stock.document.not_supplier", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Documents.CreateAsync(
                StockOperationKind.Receipt, new StockDocumentHeader(f.Yarn, null, customerOnly, null, Day, null)))).Code);
            Assert.Equal([f.Yarn], (await s.Documents.GetOptionsAsync(StockOperationKind.Transfer)).Warehouses.Select(w => w.Id));
        }

        await PostedReceiptAsync(f, f.Yarn, (f.Wool, 5m));
        long transfer;
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            transfer = await s.Documents.CreateAsync(StockOperationKind.Transfer,
                new StockDocumentHeader(f.Yarn, f.Shop, null, f.Reason("Перемещение между складами"), Day, null));
            await AddLinesAsync(s, transfer, (f.Wool, 5m));
        }

        // Кладовщик склада-получателя видит перемещение, но не правит его.
        var shopKeeper = await InviteActiveAsync(f.Org, SystemRoles.Storekeeper, f.Shop);
        await using (var s = host.As(shopKeeper, f.Org.OrganizationId))
        {
            var card = await s.Documents.GetAsync(transfer);
            Assert.False(card.CanEdit);
            Assert.Contains(await s.Documents.ListAsync(new StockDocumentListFilter()), d => d.Id == transfer);
            await Assert.ThrowsAsync<NotFoundException>(() => s.Documents.CancelAsync(transfer, card.RowVersion));
            Assert.DoesNotContain(await s.Documents.ListAsync(new StockDocumentListFilter()), d => d.Kind == StockOperationKind.Receipt);
            Assert.Equal([f.Shop], (await s.Documents.ListVisibleWarehousesAsync()).Select(w => w.Id));

            // D118 (AUTHZ-03): карточка позиции показывает остаток только по своим складам — 5 на складе пряжи кладовщику магазина не видны.
            var item = await s.Nomenclature.GetItemAsync(f.Wool);
            Assert.Equal((0m, false), (item.Stock, item.StockAllWarehouses));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var item = await s.Nomenclature.GetItemAsync(f.Wool);
            Assert.Equal((5m, true), (item.Stock, item.StockAllWarehouses));
        }
    }

    [SqlFact]
    public async Task Parallel_write_offs_cannot_overdraw()
    {
        var f = await SetUpAsync();
        await PostedReceiptAsync(f, f.Yarn, (f.Wool, 10m));
        var docs = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            await using var s = host.As(f.Senior, f.Org.OrganizationId);
            var doc = await s.Documents.CreateAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(f.Yarn, null, null, f.Reason("Передача в производство"), Day, null));
            await AddLinesAsync(s, doc, (f.Wool, 4m));
            docs.Add(doc);
        }

        var results = await Task.WhenAll(docs.Select(async doc =>
        {
            await using var s = host.As(f.Senior, f.Org.OrganizationId);
            try
            {
                await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
                return true;
            }
            catch (BusinessRuleException ex) when (ex.Code == "stock.balance.insufficient")
            {
                return false;
            }
        }));

        Assert.Equal(2, results.Count(r => r));
        await using var db = host.NewDb();
        Assert.Equal(2m, await db.StockMovements.Where(m => m.WarehouseId == f.Yarn && m.ItemId == f.Wool).SumAsync(m => m.Quantity));
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Yarn, long Shop, long Wool, long Buttons, long Supplier, long Senior, long Keeper,
        IReadOnlyDictionary<string, long> Reasons)
    {
        public long Reason(string name) => Reasons[name];
    }

    private async Task<Fixture> SetUpAsync()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        long yarn, shop, wool, buttons, supplier;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            shop = await s.Warehouses.CreateWarehouseAsync("Вязальный цех", null);
            wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            buttons = await s.Catalog.CreateItemAsync(new ItemCommand("Ф-1", "Пуговица", ItemType.Accessory, units.Single(u => u.Symbol == "шт").Id, null));
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
        }

        await using var db = host.NewDb();
        var reasons = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId).ToDictionaryAsync(r => r.Name, r => r.Id);
        var senior = await InviteActiveAsync(org, SystemRoles.SeniorStorekeeper, yarn);
        var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper, yarn);
        return new Fixture(org, yarn, shop, wool, buttons, supplier, senior, keeper, reasons);
    }

    private async Task<long> PostedReceiptAsync(Fixture f, long warehouse, params (long Item, decimal Qty)[] lines)
    {
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var doc = await s.Documents.CreateAsync(StockOperationKind.Receipt,
            new StockDocumentHeader(warehouse, null, f.Supplier, f.Reason("Закупка у поставщика"), Day, null));
        await AddLinesAsync(s, doc, lines);
        await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
        return doc;
    }

    private static async Task AddLinesAsync(Services s, long doc, params (long Item, decimal Qty)[] lines)
    {
        foreach (var (item, qty) in lines)
        {
            await s.Documents.SetLineAsync(doc, item, qty, (await s.Documents.GetAsync(doc)).RowVersion);
        }
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode, long? warehouseId)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, null, WarehouseId: warehouseId))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private static string NextValidInn()
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
}
