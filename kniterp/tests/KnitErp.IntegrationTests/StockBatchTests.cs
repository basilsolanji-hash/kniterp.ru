using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D89: партии пряжи — разбивка строк, партийный регистр, автоподбор при расходе, сторно, правила справочника.</summary>
public sealed class StockBatchTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 760_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Receipt_by_batches_then_explicit_and_automatic_issue()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var red1 = await s.Batches.CreateAsync(f.Wool, new StockBatchData("П-101", "бордо", "2231", f.Supplier, null, null));
        var red2 = await s.Batches.CreateAsync(f.Wool, new StockBatchData("П-102", "бордо", "2240", f.Supplier, 1.25m, null));

        var receipt = await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Wool, 30m));
        await SetBatchAsync(s, receipt, f.Wool, red1, 10m, 8, 10.6m);
        await SetBatchAsync(s, receipt, f.Wool, red2, 15m, 12, null);

        // Разбивка 25 из 30 — не проводится.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => PostAsync(s, receipt));
        Assert.Equal("stock.batch.sum", ex.Code);
        await SetBatchAsync(s, receipt, f.Wool, red2, 20m, 16, null);
        var card = await s.Batches.ForDocumentAsync(receipt);
        var row = card.Items.Single().Rows.First(r => r.BatchId == red1);
        Assert.Equal(1.25m, row.NetPerPackageKg);
        Assert.Equal(0.6m, row.TareKg);
        Assert.True(card.Items.Single().Matches);
        await PostAsync(s, receipt);

        // Партии нельзя менять в проведённом документе — и сервисом, и напрямую в базе.
        await Assert.ThrowsAsync<BusinessRuleException>(async () => await s.Batches.SetDocumentBatchAsync(receipt, f.Wool, red1, 1m, null, null,
            (await s.Batches.ForDocumentAsync(receipt)).RowVersion));
        await using (var raw = host.NewDb())
        {
            var line = await raw.StockDocumentBatchLines.FirstAsync(l => l.DocumentId == receipt);
            raw.StockDocumentBatchLines.Remove(line);
            await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
        }

        // Ещё 5 кг без партии.
        await PostAsync(s, await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Wool, 5m)));

        // Явный расход сверх остатка партии.
        var over = await DocAsync(s, f, StockOperationKind.WriteOff, f.Yarn, null, (f.Wool, 12m));
        await SetBatchAsync(s, over, f.Wool, red1, 12m, null, null);
        Assert.Equal("stock.batch.insufficient", (await Assert.ThrowsAsync<BusinessRuleException>(() => PostAsync(s, over))).Code);

        // Без разбивки: 5 «без партии», затем 3 со старейшей партии П-101.
        var auto = await DocAsync(s, f, StockOperationKind.WriteOff, f.Yarn, null, (f.Wool, 8m));
        await PostAsync(s, auto);
        var balances = (await s.Batches.BalancesAsync(null, null)).Single();
        Assert.Equal(27m, balances.Total);
        Assert.Equal(0m, balances.WithoutBatch);
        Assert.Equal(7m, balances.Batches.Single(b => b.BatchId == red1).Quantity);
        Assert.Equal(20m, balances.Batches.Single(b => b.BatchId == red2).Quantity);
        Assert.Equal(16m, balances.Batches.Single(b => b.BatchId == red2).Packages);
        Assert.Single((await s.Batches.ForDocumentAsync(auto)).Movements);

        var list = await s.Batches.ListAsync(new StockBatchFilter(f.Wool));
        Assert.Equal(20m, list.Single(b => b.Id == red2).BalanceKg);
        Assert.Equal("П-102 · бордо · кр. 2240", list.Single(b => b.Id == red2).Label);

        // Сторно поступления после расхода П-101 уведёт её в минус — запрещено; сторно расхода возвращает партию.
        Assert.Equal("stock.batch.insufficient", (await Assert.ThrowsAsync<BusinessRuleException>(() => ReverseAsync(s, receipt))).Code);
        await ReverseAsync(s, auto);
        // Отклонённое сторно не «прилипло» к документу и не ушло в базу со следующим сохранением в том же контексте.
        await using (var check = host.NewDb())
        {
            Assert.Equal(StockDocumentStatus.Posted, (await check.StockDocuments.SingleAsync(d => d.Id == receipt)).Status);
        }

        balances = (await s.Batches.BalancesAsync(null, null)).Single();
        Assert.Equal(10m, balances.Batches.Single(b => b.BatchId == red1).Quantity);
        Assert.Equal(5m, balances.WithoutBatch);
    }

    [SqlFact]
    public async Task Transfer_moves_batch_and_reversal_guards_batch_balance()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var batch = await s.Batches.CreateAsync(f.Wool, new StockBatchData("Л-7", "серый", null, null, null, null));
        var receipt = await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Wool, 10m));
        await SetBatchAsync(s, receipt, f.Wool, batch, 10m, null, null);
        await PostAsync(s, receipt);

        var transfer = await DocAsync(s, f, StockOperationKind.Transfer, f.Yarn, f.Shop, (f.Wool, 4m));
        await SetBatchAsync(s, transfer, f.Wool, batch, 4m, null, null);
        await PostAsync(s, transfer);
        var byWarehouse = await s.Batches.BalancesAsync(null, null);
        Assert.Equal(6m, byWarehouse.Single(b => b.WarehouseId == f.Yarn).Batches.Single().Quantity);
        Assert.Equal(4m, byWarehouse.Single(b => b.WarehouseId == f.Shop).Batches.Single().Quantity);
        Assert.Equal(2, (await s.Batches.ForDocumentAsync(transfer)).Movements.Count);

        // Цех израсходовал партию — сторно перемещения не проходит (общий остаток цеха тоже ушёл бы в минус).
        await PostAsync(s, await DocAsync(s, f, StockOperationKind.WriteOff, f.Shop, null, (f.Wool, 4m)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => ReverseAsync(s, transfer));

        // Архив — только пустой партии.
        var row = (await s.Batches.ListAsync(new StockBatchFilter(f.Wool))).Single();
        Assert.Equal(6m, row.Balance);
        Assert.Equal("stock.batch.has_balance",
            (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Batches.ArchiveAsync(batch, row.RowVersion))).Code);
    }

    [SqlFact]
    public async Task Batch_catalog_rules_and_isolation()
    {
        var f = await SetUpAsync();
        var other = await SetUpAsync();
        long batch;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            batch = await s.Batches.CreateAsync(f.Wool, new StockBatchData("A-1", null, null, null, null, null));
            Assert.Equal("stock.batch.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Batches.CreateAsync(f.Wool, new StockBatchData(" A-1 ", null, null, null, null, null)))).Code);
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Batches.CreateAsync(f.Wool, new StockBatchData("A-2", null, null, null, -1m, null)));

            // Партия пряжи не подходит к строке пуговиц.
            var doc = await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Buttons, 10m));
            Assert.Equal("stock.batch.other_item", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                SetBatchAsync(s, doc, f.Buttons, batch, 10m, null, null))).Code);

            var row = (await s.Batches.ListAsync(new StockBatchFilter())).Single();
            await s.Batches.UpdateAsync(batch, new StockBatchData("A-1", "синий", "77", f.Supplier, 0.5m, "с ярлыка"), row.RowVersion);
            row = (await s.Batches.ListAsync(new StockBatchFilter())).Single();
            Assert.Equal("синий", row.Color);
            await s.Batches.ArchiveAsync(batch, row.RowVersion);
            Assert.Empty(await s.Batches.ListAsync(new StockBatchFilter()));
            Assert.Single(await s.Batches.ListAsync(new StockBatchFilter(IncludeArchived: true)));
            Assert.Contains(await s.Db.AuditEntries.Where(a => a.EntityId == batch.ToString()).Select(a => a.Action).ToListAsync(),
                a => a == KnitErp.Domain.Audit.AuditActions.StockBatchArchived);
        }

        // Чужая организация не видит и не правит партию.
        await using (var s = host.As(other.Org.OwnerUserId, other.Org.OrganizationId))
        {
            Assert.Empty(await s.Batches.ListAsync(new StockBatchFilter(IncludeArchived: true)));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Batches.ArchiveAsync(batch, [0, 0, 0, 0, 0, 0, 0, 1]));
            var doc = await DocAsync(s, other, StockOperationKind.Receipt, other.Yarn, null, (other.Wool, 1m));
            await Assert.ThrowsAsync<NotFoundException>(() => SetBatchAsync(s, doc, other.Wool, batch, 1m, null, null));
        }
    }

    [SqlFact]
    public async Task Opening_balance_by_batches_is_checked_on_approval()
    {
        var f = await SetUpAsync();
        var senior = await InviteActiveAsync(f.Org, SystemRoles.SeniorStorekeeper, f.Yarn);
        long a, b, doc;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            a = await s.Batches.CreateAsync(f.Wool, new StockBatchData("Н-1", "белый", "11", null, null, null));
            b = await s.Batches.CreateAsync(f.Wool, new StockBatchData("Н-2", "белый", "12", null, null, null));
        }

        await using (var s = host.As(senior, f.Org.OrganizationId))
        {
            doc = await s.OpeningBalances.CreateAsync(f.Yarn, Day, "Перенос остатков");
            await s.OpeningBalances.SetLineAsync(doc, f.Wool, 20m, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
            await s.Breakdowns.SetOpeningBatchAsync(doc, f.Wool, a, 12m, (await s.Breakdowns.ForOpeningAsync(doc)).RowVersion);
            await s.Breakdowns.SetOpeningBatchAsync(doc, f.Wool, b, 5m, (await s.Breakdowns.ForOpeningAsync(doc)).RowVersion);
            Assert.Equal("stock.batch.over_line", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Breakdowns.SetOpeningBatchAsync(doc, f.Wool, b, 9m, (await s.Breakdowns.ForOpeningAsync(doc)).RowVersion))).Code);
            await s.OpeningBalances.SubmitAsync(doc, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal("stock.batch.sum", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.OpeningBalances.ApproveAsync(doc, (await s.OpeningBalances.GetAsync(doc)).RowVersion))).Code);
            await s.OpeningBalances.ReturnAsync(doc, "Партии не сходятся", (await s.OpeningBalances.GetAsync(doc)).RowVersion);
        }

        await using (var check = host.NewDb())
        {
            Assert.Equal(OpeningBalanceStatus.Draft, (await check.OpeningBalances.SingleAsync(d => d.Id == doc)).Status);
            Assert.Empty(await check.StockMovements.Where(m => m.SourceId == doc && m.Source == StockSource.OpeningBalance).ToListAsync());
        }

        await using (var s = host.As(senior, f.Org.OrganizationId))
        {
            await s.Breakdowns.SetOpeningBatchAsync(doc, f.Wool, b, 8m, (await s.Breakdowns.ForOpeningAsync(doc)).RowVersion);
            await s.OpeningBalances.SubmitAsync(doc, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.OpeningBalances.ApproveAsync(doc, (await s.OpeningBalances.GetAsync(doc)).RowVersion);
            var card = await s.Breakdowns.ForOpeningAsync(doc);
            Assert.False(card.CanEdit);
            Assert.Equal(2, card.Movements.Count);
            var balance = (await s.Batches.BalancesAsync(null, null)).Single();
            Assert.Equal(12m, balance.Batches.Single(x => x.BatchId == a).Quantity);
            Assert.Equal(8m, balance.Batches.Single(x => x.BatchId == b).Quantity);
            Assert.Equal(0m, balance.WithoutBatch);
        }
    }

    [SqlFact]
    public async Task Inventory_counts_by_batches_and_shortage_without_batches_takes_unbatched_first()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var a = await s.Batches.CreateAsync(f.Wool, new StockBatchData("И-1", "синий", "1", null, null, null));
        var b = await s.Batches.CreateAsync(f.Wool, new StockBatchData("И-2", "синий", "2", null, null, null));
        var receipt = await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Wool, 30m));
        await SetBatchAsync(s, receipt, f.Wool, a, 10m, null, null);
        await SetBatchAsync(s, receipt, f.Wool, b, 20m, null, null);
        await PostAsync(s, receipt);
        await PostAsync(s, await DocAsync(s, f, StockOperationKind.Receipt, f.Yarn, null, (f.Wool, 5m)));

        // 1-я инвентаризация: по партиям И-1 = 10, И-2 = 18, всего факт 33 → 5 «без партии».
        var inv = await s.Inventory.CreateAsync(f.Yarn, Day, "Пересчёт по партиям, недостача — усушка");
        await s.Inventory.RefillFromBookAsync(inv, (await s.Inventory.GetAsync(inv)).RowVersion);
        await s.Inventory.SetCountedAsync(inv, f.Wool, 33m, (await s.Inventory.GetAsync(inv)).RowVersion);
        Assert.Equal(2, await s.Breakdowns.FillInventoryBatchesAsync(inv, (await s.Inventory.GetAsync(inv)).RowVersion));
        await s.Breakdowns.SetInventoryBatchAsync(inv, f.Wool, b, 18m, (await s.Inventory.GetAsync(inv)).RowVersion);
        var card = await s.Breakdowns.ForInventoryAsync(inv);
        var item = card.Items.Single();
        Assert.True(item.CountedByBatches);
        Assert.Equal(5m, item.WithoutBatch);
        Assert.Equal(-2m, item.Rows.Single(r => r.BatchId == b).Difference(true));

        // Факт по партиям больше факта позиции — не проводится, документ остаётся черновиком.
        await s.Breakdowns.SetInventoryBatchAsync(inv, f.Wool, a, 16m, (await s.Inventory.GetAsync(inv)).RowVersion);
        Assert.Equal("stock.batch.inventory_over", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Inventory.PostAsync(inv, (await s.Inventory.GetAsync(inv)).RowVersion))).Code);
        await s.Breakdowns.SetInventoryBatchAsync(inv, f.Wool, a, 10m, (await s.Inventory.GetAsync(inv)).RowVersion);
        await s.Inventory.PostAsync(inv, (await s.Inventory.GetAsync(inv)).RowVersion);
        var balance = (await s.Batches.BalancesAsync(null, null)).Single();
        Assert.Equal(33m, balance.Total);
        Assert.Equal(18m, balance.Batches.Single(x => x.BatchId == b).Quantity);
        Assert.Equal(5m, balance.WithoutBatch);
        Assert.Single((await s.Breakdowns.ForInventoryAsync(inv)).Movements);

        // 2-я инвентаризация без партий: недостача 10 — сначала 5 «без партии», затем 5 со старейшей партии И-1.
        var inv2 = await s.Inventory.CreateAsync(f.Yarn, Day, "Недостача");
        await s.Inventory.RefillFromBookAsync(inv2, (await s.Inventory.GetAsync(inv2)).RowVersion);
        await s.Inventory.SetCountedAsync(inv2, f.Wool, 23m, (await s.Inventory.GetAsync(inv2)).RowVersion);
        await s.Inventory.PostAsync(inv2, (await s.Inventory.GetAsync(inv2)).RowVersion);
        balance = (await s.Batches.BalancesAsync(null, null)).Single();
        Assert.Equal(5m, balance.Batches.Single(x => x.BatchId == a).Quantity);
        Assert.Equal(18m, balance.Batches.Single(x => x.BatchId == b).Quantity);
        Assert.Equal(0m, balance.WithoutBatch);

        // Проведённая инвентаризация: факт по партиям не меняется ни сервисом, ни напрямую в базе.
        await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Breakdowns.SetInventoryBatchAsync(inv, f.Wool, a, 1m, (await s.Inventory.GetAsync(inv)).RowVersion));
        await using var raw = host.NewDb();
        raw.InventoryBatchCounts.Remove(await raw.InventoryBatchCounts.FirstAsync(c => c.DocumentId == inv));
        await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
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

    private sealed record Fixture(CreatedOrganization Org, long Yarn, long Shop, long Wool, long Buttons, long Supplier, IReadOnlyDictionary<StockOperationKind, long> Reasons);

    private static async Task<long> DocAsync(Services s, Fixture f, StockOperationKind kind, long warehouse, long? target, params (long Item, decimal Qty)[] lines)
    {
        var doc = await s.Documents.CreateAsync(kind, new StockDocumentHeader(warehouse, target,
            kind == StockOperationKind.Receipt ? f.Supplier : null, f.Reasons[kind], Day, null));
        foreach (var (item, qty) in lines)
        {
            await s.Documents.SetLineAsync(doc, item, qty, (await s.Documents.GetAsync(doc)).RowVersion);
        }

        return doc;
    }

    private static async Task SetBatchAsync(Services s, long doc, long item, long batch, decimal qty, int? packages, decimal? gross) =>
        await s.Batches.SetDocumentBatchAsync(doc, item, batch, qty, packages, gross, (await s.Batches.ForDocumentAsync(doc)).RowVersion);

    private static async Task PostAsync(Services s, long doc) => await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);

    private static async Task ReverseAsync(Services s, long doc) =>
        await s.Documents.ReverseAsync(doc, "Ошибка ввода", (await s.Documents.GetAsync(doc)).RowVersion);

    private async Task<Fixture> SetUpAsync()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            var shop = await s.Warehouses.CreateWarehouseAsync("Вязальный цех", null);
            var wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            var buttons = await s.Catalog.CreateItemAsync(new ItemCommand("Ф-1", "Пуговица", ItemType.Accessory, units.Single(u => u.Symbol == "шт").Id, null));
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
            var reasons = await s.Db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && !r.RequiresComment)
                .GroupBy(r => r.Kind).Select(g => new { g.Key, Id = g.Min(r => r.Id) }).ToDictionaryAsync(x => x.Key, x => x.Id);
            return new Fixture(org, yarn, shop, wool, buttons, supplier, reasons);
        }
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
