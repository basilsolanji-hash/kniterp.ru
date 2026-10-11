using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

/// <summary>Партия в инвентаризации: учёт (остаток партии на складе сейчас) и факт, если указан.</summary>
public sealed record InventoryBatchRowDto(long? Id, long BatchId, string Label, decimal Book, decimal? Counted)
{
    /// <summary>Разница при проведении: партия без факта у позиции, пересчитанной по партиям, списывается до нуля.</summary>
    public decimal Difference(bool countedByBatches) => countedByBatches ? (Counted ?? 0) - Book : 0;
}

public sealed record InventoryBatchItemDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal? Counted, IReadOnlyList<InventoryBatchRowDto> Rows,
    IReadOnlyList<BatchOptionDto> Options)
{
    public bool CountedByBatches => Rows.Any(r => r.Counted is not null);
    public decimal BatchesCounted => Rows.Sum(r => r.Counted ?? 0);

    /// <summary>Сколько останется «без партии» после проведения (факт позиции − факт партий).</summary>
    public decimal? WithoutBatch => CountedByBatches && Counted is { } c ? c - BatchesCounted : null;
}

public sealed record InventoryBatchesDto(
    bool CanEdit, IReadOnlyList<InventoryBatchItemDto> Items, IReadOnlyList<DocumentBatchMovementDto> Movements, byte[] RowVersion);

/// <summary>
/// Партии в начальных остатках и инвентаризации (D100): разбивка строк, факт по партиям, движения партий после утверждения
/// или проведения. Права и область склада — как у самих документов; правится только черновик.
/// </summary>
public sealed class StockBatchBreakdownService(IKnitErpDbContext db, IAccessGuard guard)
{
    // ── Начальные остатки ─────────────────────────────────────────────

    public async Task<DocumentBatchesDto> ForOpeningAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        string[] view = [Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView];
        if (!view.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.OpeningBalanceCreate, ct);
        }

        var doc = await db.OpeningBalances.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!view.Any(p => WarehouseScope.Covers(ctx, p, doc.WarehouseId)))
        {
            throw new NotFoundException("Документ");
        }

        var canEdit = doc.Status == OpeningBalanceStatus.Draft && WarehouseScope.Covers(ctx, Permissions.OpeningBalanceCreate, doc.WarehouseId);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await ItemsAsync(itemIds, ct);
        var rows = await (from r in db.OpeningBalanceBatchLines.AsNoTracking()
                          join b in db.StockBatches.AsNoTracking() on r.BatchId equals b.Id
                          where r.DocumentId == id && r.OrganizationId == ctx.OrganizationId
                          orderby r.Id
                          select new { r.Id, r.ItemId, r.BatchId, b.Number, b.Color, b.DyeLot, r.Quantity }).ToListAsync(ct);
        var options = canEdit ? await OptionsAsync(ctx, itemIds, null, ct) : [];
        var result = doc.Lines.OrderBy(l => items[l.ItemId].Code).Select(l =>
        {
            var item = items[l.ItemId];
            return new DocumentBatchItemDto(l.ItemId, item.Code, item.Name, item.Symbol, item.Precision, l.Quantity,
                WeightConversion.ToKg(l.Quantity, item.UnitCode, item.WeightKg),
                rows.Where(r => r.ItemId == l.ItemId).Select(r => new DocumentBatchRowDto(r.Id, r.ItemId, r.BatchId,
                    StockBatch.Describe(r.Number, r.Color, r.DyeLot), r.Quantity, null, null,
                    WeightConversion.ToKg(r.Quantity, item.UnitCode, item.WeightKg))).ToList(),
                options.Where(o => o.ItemId == l.ItemId).Select(o => o.Option).ToList());
        }).ToList();
        var movements = doc.Status == OpeningBalanceStatus.Approved ? await MovementsAsync(ctx, StockSource.OpeningBalance, id, ct) : [];
        return new DocumentBatchesDto(canEdit, true, result, movements, doc.RowVersion);
    }

    public async Task SetOpeningBatchAsync(long id, long itemId, long batchId, decimal quantity, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadOpeningAsync(id, rowVersion, ct);
        var line = doc.Lines.FirstOrDefault(l => l.ItemId == itemId)
                   ?? throw new BusinessRuleException("stock.document.line_missing", "Такой позиции в документе нет.");
        await RequireBatchAsync(ctx, itemId, batchId, ct);
        var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Позиция");
        StockEntry.EnsurePrecision(item, quantity);
        var others = await db.OpeningBalanceBatchLines.AsNoTracking()
            .Where(r => r.DocumentId == id && r.ItemId == itemId && r.BatchId != batchId).SumAsync(r => (decimal?)r.Quantity, ct) ?? 0;
        if (others + quantity > line.Quantity)
        {
            throw new BusinessRuleException("stock.batch.over_line",
                $"По партиям {Quantities.Format(others + quantity)} — больше количества строки {Quantities.Format(line.Quantity)}.");
        }

        var row = await db.OpeningBalanceBatchLines.SingleOrDefaultAsync(r => r.DocumentId == id && r.ItemId == itemId && r.BatchId == batchId, ct);
        if (row is null)
        {
            db.OpeningBalanceBatchLines.Add(OpeningBalanceBatchLine.Create(ctx.OrganizationId, id, itemId, batchId, quantity));
        }
        else
        {
            row.SetQuantity(quantity);
        }

        doc.MarkBatchesChanged();
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveOpeningBatchAsync(long id, long rowId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (_, doc) = await LoadOpeningAsync(id, rowVersion, ct);
        var row = await db.OpeningBalanceBatchLines.SingleOrDefaultAsync(r => r.Id == rowId && r.DocumentId == id, ct)
                  ?? throw new NotFoundException("Строка партии");
        db.OpeningBalanceBatchLines.Remove(row);
        doc.MarkBatchesChanged();
        await db.SaveChangesAsync(ct);
    }

    // ── Инвентаризация ────────────────────────────────────────────────

    public async Task<InventoryBatchesDto> ForInventoryAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        string[] view = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];
        if (!view.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseDocumentCreate, ct);
        }

        var doc = await db.InventoryCounts.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Инвентаризация");
        if (!view.Any(p => WarehouseScope.Covers(ctx, p, doc.WarehouseId)))
        {
            throw new NotFoundException("Инвентаризация");
        }

        var canEdit = doc.Status == InventoryStatus.Draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await ItemsAsync(itemIds, ct);
        var counts = await db.InventoryBatchCounts.AsNoTracking().Where(c => c.DocumentId == id && c.OrganizationId == ctx.OrganizationId)
            .ToListAsync(ct);
        var balances = doc.Status == InventoryStatus.Draft
            ? await db.StockBatchMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId))
                .GroupBy(m => new { m.ItemId, m.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
                .Where(x => x.Sum != 0).ToListAsync(ct)
            : [];
        var batchIds = counts.Select(c => c.BatchId).Concat(balances.Select(b => b.BatchId)).Distinct().ToList();
        var labels = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Number, b.Color, b.DyeLot }).ToDictionaryAsync(b => b.Id, b => StockBatch.Describe(b.Number, b.Color, b.DyeLot), ct);
        var options = canEdit ? await OptionsAsync(ctx, itemIds, null, ct) : [];
        var result = doc.Lines.OrderBy(l => items[l.ItemId].Code).Select(l =>
        {
            var item = items[l.ItemId];
            var itemCounts = counts.Where(c => c.ItemId == l.ItemId).ToList();
            var rows = itemCounts.Select(c => c.BatchId).Union(balances.Where(b => b.ItemId == l.ItemId).Select(b => b.BatchId))
                .Select(b => new InventoryBatchRowDto(itemCounts.FirstOrDefault(c => c.BatchId == b)?.Id, b, labels[b],
                    balances.FirstOrDefault(x => x.ItemId == l.ItemId && x.BatchId == b)?.Sum ?? 0,
                    itemCounts.FirstOrDefault(c => c.BatchId == b)?.CountedQuantity))
                .OrderBy(r => r.BatchId).ToList();
            return new InventoryBatchItemDto(l.ItemId, item.Code, item.Name, item.Symbol, item.Precision, l.CountedQuantity, rows,
                options.Where(o => o.ItemId == l.ItemId && rows.All(r => r.BatchId != o.Option.Id)).Select(o => o.Option).ToList());
        }).ToList();
        var movements = doc.Status == InventoryStatus.Posted ? await MovementsAsync(ctx, StockSource.Inventory, id, ct) : [];
        return new InventoryBatchesDto(canEdit, result, movements, doc.RowVersion);
    }

    /// <summary>Факт по партии; null — убрать факт (партия вернётся к учёту).</summary>
    public async Task SetInventoryBatchAsync(long id, long itemId, long batchId, decimal? counted, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadInventoryAsync(id, rowVersion, ct);
        if (doc.Lines.All(l => l.ItemId != itemId))
        {
            throw new BusinessRuleException("stock.document.line_missing", "Такой позиции в документе нет.");
        }

        var batch = await db.StockBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId && b.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Партия");
        if (batch.ItemId != itemId)
        {
            throw new BusinessRuleException("stock.batch.other_item", $"Партия {batch.Number} относится к другой позиции.");
        }

        var row = await db.InventoryBatchCounts.SingleOrDefaultAsync(r => r.DocumentId == id && r.ItemId == itemId && r.BatchId == batchId, ct);
        if (counted is not { } value)
        {
            if (row is not null)
            {
                db.InventoryBatchCounts.Remove(row);
            }
        }
        else
        {
            var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Позиция");
            if (value > 0)
            {
                StockEntry.EnsurePrecision(item, value);
            }

            if (row is null)
            {
                db.InventoryBatchCounts.Add(InventoryBatchCount.Create(ctx.OrganizationId, id, itemId, batchId, value));
            }
            else
            {
                row.SetCounted(value);
            }
        }

        doc.MarkBatchesChanged();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Заполнить факт по партиям учётными остатками — для позиций, где по партиям ещё ничего не указано.</summary>
    public async Task<int> FillInventoryBatchesAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadInventoryAsync(id, rowVersion, ct);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var counted = await db.InventoryBatchCounts.AsNoTracking().Where(c => c.DocumentId == id).Select(c => c.ItemId).Distinct().ToListAsync(ct);
        var balances = await db.StockBatchMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId) && !counted.Contains(m.ItemId))
            .GroupBy(m => new { m.ItemId, m.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
            .Where(x => x.Sum > 0).ToListAsync(ct);
        foreach (var b in balances)
        {
            db.InventoryBatchCounts.Add(InventoryBatchCount.Create(ctx.OrganizationId, id, b.ItemId, b.BatchId, b.Sum));
        }

        if (balances.Count > 0)
        {
            doc.MarkBatchesChanged();
            await db.SaveChangesAsync(ct);
        }

        return balances.Count;
    }

    /// <summary>
    /// Сканирование ярлыка при пересчёте (D101): каждое сканирование прибавляет к факту партии одно место — вес места партии
    /// для весовой позиции или одну единицу для штучной. Возвращает новый факт партии.
    /// </summary>
    public async Task<(string Label, decimal Counted)> ScanInventoryAsync(long id, string? scanned, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadInventoryAsync(id, rowVersion, ct);
        var text = scanned?.Trim() ?? "";
        var batchId = StockBatch.ParseCode(text) ?? throw new BusinessRuleException("stock.batch.scan_unknown", $"«{text}» — не ярлык партии knitERP.");
        var batch = await db.StockBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId && b.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new BusinessRuleException("stock.batch.scan_unknown", $"Партия «{text}» не найдена.");
        if (doc.Lines.All(l => l.ItemId != batch.ItemId))
        {
            throw new BusinessRuleException("stock.batch.scan_not_in_document", $"Позиции партии {batch.Number} нет в инвентаризации — добавьте строку.");
        }

        var item = await (from i in db.Items.AsNoTracking()
                          join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                          where i.Id == batch.ItemId
                          select new { u.Code, u.Precision, i.WeightKg }).SingleAsync(ct);
        decimal step;
        if (WeightConversion.IsWeightUnit(item.Code))
        {
            step = batch.PackageWeightKg is { } kg
                ? WeightConversion.FromKg(kg, item.Code, item.WeightKg, item.Precision) ?? 0
                : throw new BusinessRuleException("stock.batch.scan_no_weight", $"У партии {batch.Number} не указан вес места — введите факт вручную.");
        }
        else
        {
            step = 1;
        }

        var row = await db.InventoryBatchCounts.SingleOrDefaultAsync(r => r.DocumentId == id && r.ItemId == batch.ItemId && r.BatchId == batchId, ct);
        if (row is null)
        {
            row = InventoryBatchCount.Create(ctx.OrganizationId, id, batch.ItemId, batchId, step);
            db.InventoryBatchCounts.Add(row);
        }
        else
        {
            row.SetCounted(row.CountedQuantity + step);
        }

        doc.MarkBatchesChanged();
        await db.SaveChangesAsync(ct);
        return (StockBatch.Describe(batch.Number, batch.Color, batch.DyeLot), row.CountedQuantity);
    }

    // ── Общее ─────────────────────────────────────────────────────────

    private async Task<(AccessContext, OpeningBalance)> LoadOpeningAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        var doc = await db.OpeningBalances.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!WarehouseScope.Covers(ctx, Permissions.OpeningBalanceCreate, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        if (doc.Status != OpeningBalanceStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Документ уже не черновик — изменения запрещены.");
        }

        return (ctx, doc.EnsureVersion(doc.RowVersion, rowVersion));
    }

    private async Task<(AccessContext, InventoryCount)> LoadInventoryAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var doc = await db.InventoryCounts.Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Инвентаризация");
        if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId))
        {
            throw new NotFoundException("Инвентаризация");
        }

        if (doc.Status != InventoryStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Документ уже не черновик — изменения запрещены.");
        }

        return (ctx, doc.EnsureVersion(doc.RowVersion, rowVersion));
    }

    private async Task RequireBatchAsync(AccessContext ctx, long itemId, long batchId, CancellationToken ct)
    {
        var batch = await db.StockBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == batchId && b.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Партия");
        if (batch.ItemId != itemId)
        {
            throw new BusinessRuleException("stock.batch.other_item", $"Партия {batch.Number} относится к другой позиции.");
        }

        if (batch.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Партия {batch.Number} в архиве.");
        }
    }

    private sealed record ItemInfo(long Id, string Code, string Name, string Symbol, string UnitCode, byte Precision, decimal? WeightKg);

    private async Task<Dictionary<long, ItemInfo>> ItemsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct) =>
        await (from i in db.Items.AsNoTracking()
               join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
               where itemIds.Contains(i.Id)
               select new ItemInfo(i.Id, i.Code, i.Name, u.Symbol, u.Code, u.Precision, i.WeightKg)).ToDictionaryAsync(x => x.Id, ct);

    private async Task<List<(long ItemId, BatchOptionDto Option)>> OptionsAsync(
        AccessContext ctx, IReadOnlyCollection<long> itemIds, long? warehouseId, CancellationToken ct) =>
        (await db.StockBatches.AsNoTracking()
            .Where(b => b.OrganizationId == ctx.OrganizationId && itemIds.Contains(b.ItemId) && !b.IsArchived)
            .OrderByDescending(b => b.Id).Take(StockBatchService.MaxRows)
            .Select(b => new { b.Id, b.ItemId, b.Number, b.Color, b.DyeLot, b.PackageWeightKg }).ToListAsync(ct))
        .Select(b => (b.ItemId, new BatchOptionDto(b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), null, b.PackageWeightKg))).ToList();

    private async Task<IReadOnlyList<DocumentBatchMovementDto>> MovementsAsync(AccessContext ctx, StockSource source, long sourceId, CancellationToken ct) =>
        (await (from m in db.StockBatchMovements.AsNoTracking()
                join b in db.StockBatches.AsNoTracking() on m.BatchId equals b.Id
                join w in db.Warehouses.AsNoTracking() on m.WarehouseId equals w.Id
                join i in db.Items.AsNoTracking() on m.ItemId equals i.Id
                join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                where m.OrganizationId == ctx.OrganizationId && m.Source == source && m.SourceId == sourceId
                orderby m.Id
                select new { WarehouseName = w.Name, i.Code, b.Number, b.Color, b.DyeLot, m.Quantity, u.Symbol, UnitCode = u.Code, i.WeightKg })
            .ToListAsync(ct))
        .Select(m => new DocumentBatchMovementDto(m.WarehouseName, m.Code, StockBatch.Describe(m.Number, m.Color, m.DyeLot), m.Quantity, m.Symbol,
            WeightConversion.ToKg(m.Quantity, m.UnitCode, m.WeightKg))).ToList();
}
