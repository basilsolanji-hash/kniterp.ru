using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

/// <summary>Партия в реестре: остаток по видимым складам, пересчёт в кг и места.</summary>
public sealed record StockBatchRowDto
{
    public long Id { get; init; }
    public long ItemId { get; init; }
    public string ItemCode { get; init; } = "";
    public string ItemName { get; init; } = "";
    public string UnitSymbol { get; init; } = "";
    public string UnitCode { get; init; } = "";
    public decimal? ItemWeightKg { get; init; }
    public string Number { get; init; } = "";
    public string? Color { get; init; }
    public string? DyeLot { get; init; }
    public long? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public decimal? PackageWeightKg { get; init; }
    public string? Comment { get; init; }
    public bool IsArchived { get; init; }
    public decimal Balance { get; init; }
    public byte[] RowVersion { get; init; } = [];

    public string Label => StockBatch.Describe(Number, Color, DyeLot);
    public decimal? BalanceKg => WeightConversion.ToKg(Balance, UnitCode, ItemWeightKg);
    public decimal? BalancePackages => BalanceKg is { } kg ? WeightConversion.Packages(kg, PackageWeightKg) : null;
}

public sealed record BatchItemDto(long Id, string Code, string Name, string UnitSymbol, string UnitCode, byte Precision, decimal? WeightKg)
{
    public decimal? KgPerUnit => WeightConversion.KgPerUnit(UnitCode, WeightKg);
}

/// <summary>Ярлык партии (D101): что печатается рядом со штрихкодом.</summary>
public sealed record BatchLabelDto(
    long Id, string Code, string ItemCode, string ItemName, string Number, string? Color, string? DyeLot, string? SupplierName,
    decimal? PackageWeightKg, int Copies, string? DocumentNumber, DateOnly? DocumentDate);

/// <summary>Партия, найденная по коду сканера.</summary>
public sealed record BatchScanDto(long Id, long ItemId, string ItemCode, string Label, decimal? PackageWeightKg);

public sealed record StockBatchFilter(long? ItemId = null, string? Search = null, bool IncludeArchived = false, bool OnlyWithBalance = false);

/// <summary>Остаток позиции на складе с разбивкой по партиям; «без партии» = общий остаток − партии.</summary>
public sealed record BatchBalanceDto(
    long WarehouseId, string WarehouseName, long ItemId, string ItemCode, string ItemName, string UnitSymbol, string UnitCode, decimal? ItemWeightKg,
    decimal Total, IReadOnlyList<BatchBalancePartDto> Batches)
{
    public decimal WithoutBatch => Total - Batches.Sum(b => b.Quantity);
    public decimal? TotalKg => WeightConversion.ToKg(Total, UnitCode, ItemWeightKg);
}

public sealed record BatchBalancePartDto(long BatchId, string Label, decimal Quantity, decimal? Kg, decimal? Packages);

/// <summary>Партия для выбора в документе: остаток на складе-отправителе (у расходных документов).</summary>
public sealed record BatchOptionDto(long Id, string Label, decimal? Available, decimal? PackageWeightKg);

public sealed record DocumentBatchRowDto(
    long Id, long ItemId, long BatchId, string BatchLabel, decimal Quantity, int? Packages, decimal? GrossWeightKg, decimal? NetKg)
{
    /// <summary>Вес нетто одного места по этой строке, кг.</summary>
    public decimal? NetPerPackageKg => NetKg is { } kg && Packages is > 0 ? decimal.Round(kg / Packages.Value, 3) : null;

    /// <summary>Тара = брутто − нетто.</summary>
    public decimal? TareKg => NetKg is { } kg && GrossWeightKg is { } g ? g - kg : null;
}

public sealed record DocumentBatchItemDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal LineQuantity, decimal? LineKg,
    IReadOnlyList<DocumentBatchRowDto> Rows, IReadOnlyList<BatchOptionDto> Options)
{
    public decimal Allocated => Rows.Sum(r => r.Quantity);
    public bool Matches => Rows.Count == 0 || Allocated == LineQuantity;
}

/// <summary>Фактическое движение партий проведённого документа (включая подобранные автоматически).</summary>
public sealed record DocumentBatchMovementDto(string WarehouseName, string ItemCode, string BatchLabel, decimal Quantity, string UnitSymbol, decimal? Kg);

public sealed record DocumentBatchesDto(
    bool CanEdit, bool IsIncoming, IReadOnlyList<DocumentBatchItemDto> Items, IReadOnlyList<DocumentBatchMovementDto> Movements, byte[] RowVersion,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Партии, отгруженные по заказу покупателя (D106): нетто отгрузок минус возвраты.</summary>
public sealed record SalesOrderBatchDto(long ItemId, string ItemCode, string ItemName, string BatchLabel, string? DyeLot, decimal Quantity, string UnitSymbol);

public sealed record SalesOrderBatchesDto(IReadOnlyList<SalesOrderBatchDto> Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// Партии (D89): справочник партий, остатки по партиям и разбивка строк складских документов. Партийный регистр пишется
/// при проведении и сторно складского документа (<see cref="StockBatchLedger"/>), общий регистр остатков при этом не меняется.
/// Просмотр — у кого есть просмотр остатков или складских документов, правка — создание складских документов.
/// </summary>
public sealed class StockBatchService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private static readonly string[] ViewPermissions =
        [Permissions.WarehouseReportView, Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost];

    public const int MaxRows = 2000;

    public async Task<IReadOnlyList<StockBatchRowDto>> ListAsync(StockBatchFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        var q = db.StockBatches.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && (filter.IncludeArchived || !b.IsArchived));
        if (filter.ItemId is { } itemId)
        {
            q = q.Where(b => b.ItemId == itemId);
        }

        var rows = from b in q
                   join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                   join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                   join s in db.Counterparties.AsNoTracking() on b.SupplierId equals s.Id into sj
                   from s in sj.DefaultIfEmpty()
                   select new { b, i.Code, i.Name, u.Symbol, UnitCode = u.Code, i.WeightKg, Supplier = s == null ? null : s.Name };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            rows = rows.Where(r => r.b.Number.Contains(text) || (r.b.Color != null && r.b.Color.Contains(text))
                                   || (r.b.DyeLot != null && r.b.DyeLot.Contains(text)) || r.Code.Contains(text) || r.Name.Contains(text));
        }

        var list = await rows.OrderBy(r => r.Code).ThenByDescending(r => r.b.Id).Take(MaxRows)
            .Select(r => new StockBatchRowDto
            {
                Id = r.b.Id, ItemId = r.b.ItemId, ItemCode = r.Code, ItemName = r.Name, UnitSymbol = r.Symbol, UnitCode = r.UnitCode,
                ItemWeightKg = r.WeightKg, Number = r.b.Number, Color = r.b.Color, DyeLot = r.b.DyeLot, SupplierId = r.b.SupplierId,
                SupplierName = r.Supplier, PackageWeightKg = r.b.PackageWeightKg, Comment = r.b.Comment, IsArchived = r.b.IsArchived,
                RowVersion = r.b.RowVersion,
            })
            .ToListAsync(ct);
        var ids = list.Select(b => b.Id).ToList();
        var balances = await db.StockBatchMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && ids.Contains(m.BatchId) && (visible == null || visible.Contains(m.WarehouseId)))
            .GroupBy(m => m.BatchId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var result = list.Select(b => b with { Balance = balances.GetValueOrDefault(b.Id) });
        return (filter.OnlyWithBalance ? result.Where(b => b.Balance != 0) : result).ToList();
    }

    /// <summary>Остатки по складам с разбивкой по партиям — только позиции, у которых партии есть.</summary>
    public async Task<IReadOnlyList<BatchBalanceDto>> BalancesAsync(long? warehouseId, string? search, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseReportView, ct);
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseReportView);
        var batchMoves = db.StockBatchMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && (visible == null || visible.Contains(m.WarehouseId))
                        && (warehouseId == null || m.WarehouseId == warehouseId));
        var parts = await batchMoves.GroupBy(m => new { m.WarehouseId, m.ItemId, m.BatchId })
            .Select(g => new { g.Key.WarehouseId, g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
            .Where(x => x.Sum != 0).ToListAsync(ct);
        var itemIds = parts.Select(p => p.ItemId).Distinct().ToList();
        if (itemIds.Count == 0)
        {
            return [];
        }

        var totals = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && itemIds.Contains(m.ItemId) && (visible == null || visible.Contains(m.WarehouseId))
                        && (warehouseId == null || m.WarehouseId == warehouseId))
            .GroupBy(m => new { m.WarehouseId, m.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Sum = g.Sum(m => m.Quantity) })
            .ToListAsync(ct);
        var items = await (from i in db.Items.AsNoTracking()
                           join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                           where itemIds.Contains(i.Id)
                           select new { i.Id, i.Code, i.Name, u.Symbol, UnitCode = u.Code, i.WeightKg }).ToDictionaryAsync(x => x.Id, ct);
        var batchIds = parts.Select(p => p.BatchId).Distinct().ToList();
        var batches = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Number, b.Color, b.DyeLot, b.PackageWeightKg }).ToDictionaryAsync(b => b.Id, ct);
        var warehouseIds = parts.Select(p => p.WarehouseId).Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var text = search?.Trim();
        return parts.GroupBy(p => (p.WarehouseId, p.ItemId))
            .Select(g =>
            {
                var item = items[g.Key.ItemId];
                return new BatchBalanceDto(g.Key.WarehouseId, warehouses[g.Key.WarehouseId], item.Id, item.Code, item.Name, item.Symbol, item.UnitCode,
                    item.WeightKg, totals.FirstOrDefault(t => t.WarehouseId == g.Key.WarehouseId && t.ItemId == g.Key.ItemId)?.Sum ?? 0,
                    g.OrderBy(p => p.BatchId).Select(p =>
                    {
                        var b = batches[p.BatchId];
                        var kg = WeightConversion.ToKg(p.Sum, item.UnitCode, item.WeightKg);
                        return new BatchBalancePartDto(b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), p.Sum, kg,
                            kg is { } k ? WeightConversion.Packages(k, b.PackageWeightKg) : null);
                    }).ToList());
            })
            .Where(r => string.IsNullOrEmpty(text) || r.ItemCode.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                  || r.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                  || r.Batches.Any(b => b.Label.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.WarehouseName).ThenBy(r => r.ItemCode).ToList();
    }

    /// <summary>Позиции для новой партии и пересчёта: действующая номенклатура с единицей и весом единицы.</summary>
    public async Task<IReadOnlyList<BatchItemDto>> ItemsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        return await (from i in db.Items.AsNoTracking()
                      join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                      where i.OrganizationId == ctx.OrganizationId && !i.IsArchived
                      orderby i.Code
                      select new BatchItemDto(i.Id, i.Code, i.Name, u.Symbol, u.Code, u.Precision, i.WeightKg)).Take(5000).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LookupDto>> SuppliersAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        return await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.IsSupplier)
            .OrderBy(c => c.Name).Select(c => new LookupDto(c.Id, c.Name)).ToListAsync(ct);
    }

    public async Task<long> CreateAsync(long itemId, StockBatchData data, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        await RequireItemAsync(ctx, itemId, ct);
        await RequireSupplierAsync(ctx, data.SupplierId, ct);
        var number = data.Number?.Trim() ?? "";
        await EnsureUniqueAsync(ctx, itemId, number, null, ct);
        var batch = StockBatch.Create(ctx.OrganizationId, itemId, data, clock.UtcNow);
        db.StockBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.StockBatchCreated, batch.Id, null, batch.Number, StockBatch.Describe(batch.Number, batch.Color, batch.DyeLot));
        await db.SaveChangesAsync(ct);
        return batch.Id;
    }

    public async Task UpdateAsync(long id, StockBatchData data, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var batch = await LoadAsync(ctx, id, rowVersion, ct);
        await RequireSupplierAsync(ctx, data.SupplierId, ct);
        var number = data.Number?.Trim() ?? "";
        await EnsureUniqueAsync(ctx, batch.ItemId, number, id, ct);
        var changes = batch.Update(data);
        if (changes.Count > 0)
        {
            Audit(ctx, AuditActions.StockBatchChanged, id, string.Join("; ", changes.Select(c => $"{c.Field}: {c.Before}")),
                string.Join("; ", changes.Select(c => $"{c.Field}: {c.After}")), batch.Number);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var batch = await LoadAsync(ctx, id, rowVersion, ct);
        // Остаток по всем складам организации, а не только по видимым пользователю.
        var balance = await db.StockBatchMovements.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId && m.BatchId == id)
            .SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
        batch.Archive(balance);
        Audit(ctx, AuditActions.StockBatchArchived, id, "Действует", "В архиве", batch.Number);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public const int MaxLabels = 2000;

    /// <summary>
    /// Ярлыки партий (D101): по списку партий (copies штук каждой) или по складскому документу — по одному на место
    /// (число мест из разбивки строки, если не указано — один ярлык).
    /// </summary>
    public async Task<IReadOnlyList<BatchLabelDto>> LabelsAsync(IReadOnlyList<long> ids, int copies, long? documentId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var perBatch = new Dictionary<long, int>();
        string? docNumber = null;
        DateOnly? docDate = null;
        if (documentId is { } docId)
        {
            var doc = await db.StockDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == docId && d.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Документ");
            var visible = WarehouseScope.Visible(ctx, ViewPermissions);
            if (visible is not null && !visible.Contains(doc.WarehouseId) && !(doc.TargetWarehouseId is { } t && visible.Contains(t)))
            {
                throw new NotFoundException("Документ");
            }

            docNumber = doc.Number;
            docDate = doc.DocumentDate;
            foreach (var r in await db.StockDocumentBatchLines.AsNoTracking().Where(r => r.DocumentId == docId).ToListAsync(ct))
            {
                perBatch[r.BatchId] = perBatch.GetValueOrDefault(r.BatchId) + (r.Packages ?? 1);
            }
        }
        else
        {
            var n = Math.Clamp(copies, 1, 100);
            foreach (var id in ids.Distinct().Take(200))
            {
                perBatch[id] = n;
            }
        }

        if (perBatch.Values.Sum() > MaxLabels)
        {
            throw new BusinessRuleException("stock.batch.labels_limit", $"Не больше {MaxLabels} ярлыков за раз.");
        }

        var batchIds = perBatch.Keys.ToList();
        var rows = await (from b in db.StockBatches.AsNoTracking()
                          join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                          join s in db.Counterparties.AsNoTracking() on b.SupplierId equals s.Id into sj
                          from s in sj.DefaultIfEmpty()
                          where b.OrganizationId == ctx.OrganizationId && batchIds.Contains(b.Id)
                          orderby i.Code, b.Id
                          select new { b.Id, i.Code, i.Name, b.Number, b.Color, b.DyeLot, Supplier = s == null ? null : s.Name, b.PackageWeightKg })
            .ToListAsync(ct);
        return rows.Select(r => new BatchLabelDto(r.Id, StockBatch.Code(r.Id), r.Code, r.Name, r.Number, r.Color, r.DyeLot, r.Supplier,
            r.PackageWeightKg, perBatch[r.Id], docNumber, docDate)).ToList();
    }

    /// <summary>Партия по коду со сканера (KB…) или по номеру партии, если он однозначен; чужая организация — «не найдено».</summary>
    public async Task<BatchScanDto> FindByCodeAsync(string? scanned, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var text = scanned?.Trim() ?? "";
        var q = db.StockBatches.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && !b.IsArchived);
        q = StockBatch.ParseCode(text) is { } id ? q.Where(b => b.Id == id) : q.Where(b => b.Number == text);
        var found = await q.Join(db.Items.AsNoTracking(), b => b.ItemId, i => i.Id,
                (b, i) => new { b.Id, b.ItemId, i.Code, b.Number, b.Color, b.DyeLot, b.PackageWeightKg })
            .Take(2).ToListAsync(ct);
        if (found.Count == 0)
        {
            throw new BusinessRuleException("stock.batch.scan_unknown", $"Партия «{text}» не найдена.");
        }

        if (found.Count > 1)
        {
            throw new BusinessRuleException("stock.batch.scan_ambiguous", $"Номер «{text}» есть у нескольких позиций — сканируйте ярлык knitERP.");
        }

        var f = found[0];
        return new BatchScanDto(f.Id, f.ItemId, f.Code, StockBatch.Describe(f.Number, f.Color, f.DyeLot), f.PackageWeightKg);
    }

    /// <summary>Какие партии и крашения ушли покупателю по заказу (D106): отгрузки минус возвраты, с предупреждением о смешивании.</summary>
    public async Task<SalesOrderBatchesDto> ForSalesOrderAsync(long salesOrderId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesView) && !ViewPermissions.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        if (!await db.SalesOrders.AnyAsync(o => o.Id == salesOrderId && o.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Заказ");
        }

        var docIds = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == salesOrderId
                        && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Reversed))
            .Select(d => d.Id).ToListAsync(ct);
        var moves = await (from m in db.StockBatchMovements.AsNoTracking()
                           join b in db.StockBatches.AsNoTracking() on m.BatchId equals b.Id
                           join i in db.Items.AsNoTracking() on m.ItemId equals i.Id
                           join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                           where m.OrganizationId == ctx.OrganizationId && docIds.Contains(m.SourceId)
                                 && (m.Source == StockSource.StockDocument || m.Source == StockSource.StockDocumentReversal)
                           group m by new { m.ItemId, i.Code, i.Name, BatchId = b.Id, b.Number, b.Color, b.DyeLot, u.Symbol } into g
                           select new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToListAsync(ct);
        var rows = moves.Where(m => m.Sum != 0)
            .Select(m => new SalesOrderBatchDto(m.Key.ItemId, m.Key.Code, m.Key.Name, StockBatch.Describe(m.Key.Number, m.Key.Color, m.Key.DyeLot),
                m.Key.DyeLot, -m.Sum, m.Key.Symbol))
            .Where(r => r.Quantity > 0).OrderBy(r => r.ItemCode).ThenBy(r => r.BatchLabel).ToList();
        var warnings = rows.GroupBy(r => r.ItemId)
            .Where(g => g.Select(r => r.DyeLot ?? r.BatchLabel).Distinct().Count() > 1)
            .Select(g => $"{g.First().ItemCode}: покупателю ушли разные крашения ({string.Join(", ", g.Select(r => r.DyeLot ?? r.BatchLabel).Distinct())}).")
            .ToList();
        return new SalesOrderBatchesDto(rows, warnings);
    }

    /// <summary>Разбивка строк документа по партиям и фактические движения партий (после проведения).</summary>
    public async Task<DocumentBatchesDto> ForDocumentAsync(long documentId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        if (visible is not null && !visible.Contains(doc.WarehouseId) && !(doc.TargetWarehouseId is { } t && visible.Contains(t)))
        {
            throw new NotFoundException("Документ");
        }

        var itemIds = doc.ItemTotals().Select(l => l.ItemId).ToList();
        var items = await (from i in db.Items.AsNoTracking()
                           join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                           where itemIds.Contains(i.Id)
                           select new { i.Id, i.Code, i.Name, u.Symbol, UnitCode = u.Code, u.Precision, i.WeightKg }).ToDictionaryAsync(x => x.Id, ct);
        var rows = await (from r in db.StockDocumentBatchLines.AsNoTracking()
                          join b in db.StockBatches.AsNoTracking() on r.BatchId equals b.Id
                          where r.DocumentId == documentId && r.OrganizationId == ctx.OrganizationId
                          orderby r.Id
                          select new { r.Id, r.ItemId, r.BatchId, b.Number, b.Color, b.DyeLot, r.Quantity, r.Packages, r.GrossWeightKg })
            .ToListAsync(ct);
        var incoming = !StockDocument.IsOutgoing(doc.Kind);
        var canEdit = doc.Status == StockDocumentStatus.Draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId);

        var options = new Dictionary<long, List<BatchOptionDto>>();
        if (canEdit)
        {
            var batches = await db.StockBatches.AsNoTracking()
                .Where(b => b.OrganizationId == ctx.OrganizationId && itemIds.Contains(b.ItemId) && !b.IsArchived)
                .OrderByDescending(b => b.Id).Take(MaxRows)
                .Select(b => new { b.Id, b.ItemId, b.Number, b.Color, b.DyeLot, b.PackageWeightKg }).ToListAsync(ct);
            Dictionary<long, decimal>? available = null;
            if (!incoming)
            {
                available = await db.StockBatchMovements.AsNoTracking()
                    .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId))
                    .GroupBy(m => m.BatchId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            }

            foreach (var b in batches)
            {
                var have = available?.GetValueOrDefault(b.Id);
                if (have is <= 0)
                {
                    continue; // в расходе предлагаются только партии с остатком на складе
                }

                if (!options.TryGetValue(b.ItemId, out var list))
                {
                    options[b.ItemId] = list = [];
                }

                list.Add(new BatchOptionDto(b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), have, b.PackageWeightKg));
            }
        }

        var result = doc.ItemTotals().OrderBy(l => items[l.ItemId].Code).Select(l =>
        {
            var item = items[l.ItemId];
            return new DocumentBatchItemDto(l.ItemId, item.Code, item.Name, item.Symbol, item.Precision, l.Quantity,
                WeightConversion.ToKg(l.Quantity, item.UnitCode, item.WeightKg),
                rows.Where(r => r.ItemId == l.ItemId).Select(r => new DocumentBatchRowDto(r.Id, r.ItemId, r.BatchId,
                    StockBatch.Describe(r.Number, r.Color, r.DyeLot), r.Quantity, r.Packages, r.GrossWeightKg,
                    WeightConversion.ToKg(r.Quantity, item.UnitCode, item.WeightKg))).ToList(),
                options.GetValueOrDefault(l.ItemId) is { } o ? o : []);
        }).ToList();

        var movements = new List<DocumentBatchMovementDto>();
        if (doc.Status is StockDocumentStatus.Posted or StockDocumentStatus.Reversed)
        {
            var moves = await (from m in db.StockBatchMovements.AsNoTracking()
                               join b in db.StockBatches.AsNoTracking() on m.BatchId equals b.Id
                               join w in db.Warehouses.AsNoTracking() on m.WarehouseId equals w.Id
                               join i in db.Items.AsNoTracking() on m.ItemId equals i.Id
                               join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                               where m.OrganizationId == ctx.OrganizationId && m.SourceId == documentId
                                     && (m.Source == StockSource.StockDocument || m.Source == StockSource.StockDocumentReversal)
                               orderby m.Id
                               select new { WarehouseName = w.Name, i.Code, b.Number, b.Color, b.DyeLot, m.Quantity, u.Symbol, UnitCode = u.Code, i.WeightKg })
                .ToListAsync(ct);
            movements.AddRange(moves.Select(m => new DocumentBatchMovementDto(m.WarehouseName, m.Code, StockBatch.Describe(m.Number, m.Color, m.DyeLot),
                m.Quantity, m.Symbol, WeightConversion.ToKg(m.Quantity, m.UnitCode, m.WeightKg))));
        }

        // D106: в отгрузке по заказу — предупреждения о смешивании крашений.
        var warnings = new List<string>();
        if (doc.Kind == StockOperationKind.Shipment)
        {
            var shipped = doc.SalesOrderId is { } so && doc.Status == StockDocumentStatus.Draft
                ? await StockBatchLedger.ShippedLotsAsync(db, ctx.OrganizationId, so, doc.Id, ct)
                : [];
            foreach (var item in result)
            {
                var lots = rows.Where(r => r.ItemId == item.ItemId).Select(r => (Key: BatchAllocation.LotKey(r.BatchId, r.DyeLot), Text: r.DyeLot ?? r.Number))
                    .DistinctBy(x => x.Key).ToList();
                if (lots.Count > 1)
                {
                    warnings.Add($"{item.Code}: в отгрузке разные крашения ({string.Join(", ", lots.Select(l => l.Text))}) — тон изделий может отличаться.");
                }

                if (shipped.GetValueOrDefault(item.ItemId) is { Count: > 0 } before && lots.Any(l => !before.Contains(l.Key)))
                {
                    warnings.Add($"{item.Code}: по заказу уже отгружено другое крашение — проверьте, что новый тон подходит покупателю.");
                }
            }
        }

        return new DocumentBatchesDto(canEdit, incoming, result, movements, doc.RowVersion, warnings);
    }

    /// <summary>Строка разбивки: партия и количество по позиции документа (новая или замена — одна партия один раз).</summary>
    public async Task SetDocumentBatchAsync(
        long documentId, long itemId, long batchId, decimal quantity, int? packages, decimal? grossWeightKg, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadDocumentAsync(documentId, rowVersion, ct);
        // D352 (сессия 4, Р2): позиция может быть в нескольких строках (отдельно и в комплектах) — разбивка на их сумму.
        var line = doc.ItemTotals().Where(l => l.ItemId == itemId).Select(l => ((long ItemId, decimal Quantity)?)l).SingleOrDefault()
                   ?? throw new BusinessRuleException("stock.document.line_missing", "Такой позиции в документе нет.");
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

        var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Позиция");
        StockEntry.EnsurePrecision(item, quantity);
        var row = await db.StockDocumentBatchLines.SingleOrDefaultAsync(r => r.DocumentId == documentId && r.ItemId == itemId && r.BatchId == batchId, ct);
        if (row is null)
        {
            row = StockDocumentBatchLine.Create(ctx.OrganizationId, documentId, itemId, batchId, quantity, packages, grossWeightKg);
            db.StockDocumentBatchLines.Add(row);
        }
        else
        {
            row.Set(quantity, packages, grossWeightKg);
        }

        var otherRows = await db.StockDocumentBatchLines.AsNoTracking()
            .Where(r => r.DocumentId == documentId && r.ItemId == itemId && r.BatchId != batchId).SumAsync(r => (decimal?)r.Quantity, ct) ?? 0;
        if (otherRows + quantity > line.Quantity)
        {
            throw new BusinessRuleException("stock.batch.over_line",
                $"По партиям {Quantities.Format(otherRows + quantity)} — больше количества строки {Quantities.Format(line.Quantity)}.");
        }

        doc.MarkLinesChanged();
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveDocumentBatchAsync(long documentId, long rowId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (_, doc) = await LoadDocumentAsync(documentId, rowVersion, ct);
        var row = await db.StockDocumentBatchLines.SingleOrDefaultAsync(r => r.Id == rowId && r.DocumentId == documentId, ct)
                  ?? throw new NotFoundException("Строка партии");
        db.StockDocumentBatchLines.Remove(row);
        doc.MarkLinesChanged();
        await db.SaveChangesAsync(ct);
    }

    private async Task<(AccessContext, StockDocument)> LoadDocumentAsync(long documentId, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var doc = await db.StockDocuments.Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        if (doc.Status != StockDocumentStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Документ уже не черновик — изменения запрещены.");
        }

        return (ctx, doc.EnsureVersion(doc.RowVersion, rowVersion));
    }

    private async Task<StockBatch> LoadAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var batch = await db.StockBatches.SingleOrDefaultAsync(b => b.Id == id && b.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Партия");
        return batch.EnsureVersion(batch.RowVersion, rowVersion);
    }

    private async Task RequireItemAsync(AccessContext ctx, long itemId, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Позиция");
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Позиция {item.Code} в архиве.");
        }

        if (!ItemTypes.IsStocked(item.Type))
        {
            // D300, D302 (сессия 4): у услуги и комплекта нет партий.
            throw item.Type == ItemType.Service ? ServiceRules.NotStocked([item.Code]) : ServiceRules.BundleNotStocked([item.Code]);
        }
    }

    private async Task RequireSupplierAsync(AccessContext ctx, long? supplierId, CancellationToken ct)
    {
        if (supplierId is not { } id)
        {
            return;
        }

        var c = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Контрагент");
        if (!c.IsSupplier)
        {
            throw new BusinessRuleException("stock.document.not_supplier", $"«{c.Name}» не отмечен как поставщик.");
        }
    }

    private async Task EnsureUniqueAsync(AccessContext ctx, long itemId, string number, long? exceptId, CancellationToken ct)
    {
        if (await db.StockBatches.AnyAsync(b => b.OrganizationId == ctx.OrganizationId && b.ItemId == itemId && b.Number == number && b.Id != exceptId, ct))
        {
            throw new BusinessRuleException("stock.batch.duplicate", $"Партия {number} у этой позиции уже есть.");
        }
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ViewPermissions.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseReportView, ct);
        }

        return ctx;
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(StockBatch), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}

/// <summary>
/// Партийный регистр при проведении и сторно складского документа (D89). План считается внутри транзакции проведения до любых
/// изменений (отказ не оставляет в контексте полупроведённый документ), под блокировкой складов расхода; записывается после
/// общих движений. Запросы остатков идут в базу — видят состояние до этого документа.
/// </summary>
internal static class StockBatchLedger
{
    public sealed record Planned(long WarehouseId, long ItemId, long BatchId, decimal Quantity);

    public static async Task<IReadOnlyList<Planned>> PlanPostAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, CancellationToken ct)
    {
        var rows = await db.StockDocumentBatchLines.AsNoTracking().Where(r => r.DocumentId == doc.Id && r.OrganizationId == ctx.OrganizationId)
            .ToListAsync(ct);
        var outgoing = StockDocument.IsOutgoing(doc.Kind);
        if (rows.Count == 0 && !outgoing)
        {
            return [];
        }

        var itemIds = doc.ItemTotals().Select(l => l.ItemId).ToList();
        var batchIds = rows.Select(r => r.BatchId).Distinct().ToList();
        var batches = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        if (rows.FirstOrDefault(r => !itemIds.Contains(r.ItemId)) is { } stray)
        {
            throw new BusinessRuleException("stock.batch.stray", $"Разбивка по партии {batches[stray.BatchId].Number} — для позиции, которой нет в строках.");
        }

        foreach (var line in doc.ItemTotals())
        {
            var sum = rows.Where(r => r.ItemId == line.ItemId).Sum(r => r.Quantity);
            if (sum != 0 && sum != line.Quantity)
            {
                throw new BusinessRuleException("stock.batch.sum",
                    $"{codes[line.ItemId]}: по партиям {Quantities.Format(sum)}, в строке {Quantities.Format(line.Quantity)} — разбивка должна сойтись.");
            }
        }

        if (!outgoing && rows.FirstOrDefault(r => batches[r.BatchId].IsArchived) is { } archived)
        {
            throw new BusinessRuleException("catalog.archived", $"Партия {batches[archived.BatchId].Number} в архиве.");
        }

        // Сколько списать с каждой партии: явная разбивка или автоматический подбор (сначала «без партии», затем старые партии).
        var allocations = rows.Select(r => (r.ItemId, r.BatchId, r.Quantity)).ToList();
        if (outgoing)
        {
            await db.LockWarehousesAsync([doc.WarehouseId], ct);
            var balances = await db.StockBatchMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId))
                .GroupBy(m => new { m.ItemId, m.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct);
            var shortages = rows.Select(r => (r, Have: balances.FirstOrDefault(b => b.BatchId == r.BatchId)?.Sum ?? 0m))
                .Where(x => x.Have < x.r.Quantity).ToList();
            if (shortages.Count > 0)
            {
                throw new BusinessRuleException("stock.batch.insufficient", "Не хватает остатка партии — " + string.Join("; ", shortages.Take(10).Select(x =>
                    $"{codes[x.r.ItemId]}, партия {batches[x.r.BatchId].Number}: нужно {Quantities.Format(x.r.Quantity)}, есть {Quantities.Format(x.Have)}")) + ".");
            }

            var auto = doc.ItemTotals().Where(l => rows.All(r => r.ItemId != l.ItemId)).ToList();
            if (auto.Count > 0 && balances.Any(b => b.Sum > 0 && auto.Any(l => l.ItemId == b.ItemId)))
            {
                var autoIds = auto.Select(l => l.ItemId).ToList();
                var totals = await db.StockMovements.AsNoTracking()
                    .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && autoIds.Contains(m.ItemId))
                    .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
                // D106: отгрузка по заказу покупателя — одно крашение на весь заказ, если остатка хватает.
                Dictionary<long, HashSet<string>>? shippedLots = null;
                Dictionary<long, string?>? lotOf = null;
                if (doc.Kind == StockOperationKind.Shipment && doc.SalesOrderId is { } salesOrderId)
                {
                    shippedLots = await ShippedLotsAsync(db, ctx.OrganizationId, salesOrderId, doc.Id, ct);
                    var candidateIds = balances.Where(b => b.Sum > 0).Select(b => b.BatchId).Distinct().ToList();
                    lotOf = await db.StockBatches.AsNoTracking().Where(b => candidateIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.DyeLot, ct);
                }

                foreach (var line in auto)
                {
                    var itemBalances = balances.Where(b => b.ItemId == line.ItemId).ToList();
                    var single = lotOf is null
                        ? null
                        : BatchAllocation.SingleLotAllocate(line.Quantity, itemBalances.Select(b => (b.BatchId, b.Sum, BatchAllocation.LotKey(b.BatchId, lotOf.GetValueOrDefault(b.BatchId)))),
                            shippedLots!.GetValueOrDefault(line.ItemId));
                    allocations.AddRange((single ?? BatchAllocation.AutoAllocate(line.Quantity, totals.GetValueOrDefault(line.ItemId),
                            itemBalances.Select(b => (b.BatchId, b.Sum))))
                        .Select(a => (line.ItemId, a.BatchId, a.Quantity)));
                }
            }
        }

        var planned = new List<Planned>();
        foreach (var (itemId, batchId, quantity) in allocations)
        {
            switch (doc.Kind)
            {
                case StockOperationKind.Receipt:
                case StockOperationKind.CustomerReturn:
                    planned.Add(new(doc.WarehouseId, itemId, batchId, quantity));
                    break;
                case StockOperationKind.Transfer:
                    planned.Add(new(doc.WarehouseId, itemId, batchId, -quantity));
                    planned.Add(new(doc.TargetWarehouseId!.Value, itemId, batchId, quantity));
                    break;
                default:
                    planned.Add(new(doc.WarehouseId, itemId, batchId, -quantity));
                    break;
            }
        }

        return planned;
    }

    /// <summary>Сторно: гасятся фактически записанные движения партий документа; партия не уходит в минус.</summary>
    public static async Task<IReadOnlyList<Planned>> PlanReverseAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, CancellationToken ct)
    {
        var deltas = (await db.StockBatchMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.Source == StockSource.StockDocument && m.SourceId == doc.Id)
                .GroupBy(m => new { m.WarehouseId, m.ItemId, m.BatchId })
                .Select(g => new { g.Key.WarehouseId, g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct))
            .Where(d => d.Sum != 0).ToList();
        if (deltas.Count == 0)
        {
            return [];
        }

        var takeBack = deltas.Where(d => d.Sum > 0).ToList();
        if (takeBack.Count > 0)
        {
            await db.LockWarehousesAsync(takeBack.Select(d => d.WarehouseId), ct);
            var batchIds = takeBack.Select(d => d.BatchId).Distinct().ToList();
            var balances = await db.StockBatchMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && batchIds.Contains(m.BatchId))
                .GroupBy(m => new { m.WarehouseId, m.BatchId }).Select(g => new { g.Key.WarehouseId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct);
            var short_ = takeBack.Where(d => (balances.FirstOrDefault(b => b.WarehouseId == d.WarehouseId && b.BatchId == d.BatchId)?.Sum ?? 0) < d.Sum)
                .ToList();
            if (short_.Count > 0)
            {
                var names = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Number, ct);
                throw new BusinessRuleException("stock.batch.insufficient",
                    "Сторно уведёт партию в минус — она уже израсходована: " + string.Join(", ", short_.Take(10).Select(d => names[d.BatchId])) + ".");
            }
        }

        return deltas.Select(d => new Planned(d.WarehouseId, d.ItemId, d.BatchId, -d.Sum)).ToList();
    }

    public static void Write(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, IReadOnlyList<Planned> planned, StockSource source, DateTime nowUtc) =>
        Write(db, ctx.OrganizationId, doc.Id, doc.DocumentDate, planned, source, nowUtc);

    public static void Write(
        IKnitErpDbContext db, long organizationId, long sourceId, DateOnly date, IReadOnlyList<Planned> planned, StockSource source, DateTime nowUtc)
    {
        foreach (var p in planned)
        {
            db.StockBatchMovements.Add(StockBatchMovement.Create(organizationId, p.WarehouseId, p.ItemId, p.BatchId, p.Quantity, date, source, sourceId,
                nowUtc));
        }
    }

    /// <summary>Крашения, уже отгруженные по заказу покупателя (D106): позиция → ключи крашений. Документ excludeId не учитывается.</summary>
    public static async Task<Dictionary<long, HashSet<string>>> ShippedLotsAsync(
        IKnitErpDbContext db, long organizationId, long salesOrderId, long? excludeId, CancellationToken ct)
    {
        var docIds = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.SalesOrderId == salesOrderId && d.Kind == StockOperationKind.Shipment
                        && d.Status == StockDocumentStatus.Posted && d.Id != excludeId)
            .Select(d => d.Id).ToListAsync(ct);
        if (docIds.Count == 0)
        {
            return [];
        }

        var rows = await (from m in db.StockBatchMovements.AsNoTracking()
                          join b in db.StockBatches.AsNoTracking() on m.BatchId equals b.Id
                          where m.OrganizationId == organizationId && m.Source == StockSource.StockDocument && docIds.Contains(m.SourceId)
                          select new { m.ItemId, b.Id, b.DyeLot }).ToListAsync(ct);
        return rows.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Select(r => BatchAllocation.LotKey(r.Id, r.DyeLot)).ToHashSet());
    }

    /// <summary>Начальные остатки (D100): разбивка по партиям, если указана, равна количеству строки; партии получают приход.</summary>
    public static async Task<IReadOnlyList<Planned>> PlanOpeningAsync(IKnitErpDbContext db, AccessContext ctx, OpeningBalance doc, CancellationToken ct)
    {
        var rows = await db.OpeningBalanceBatchLines.AsNoTracking().Where(r => r.DocumentId == doc.Id && r.OrganizationId == ctx.OrganizationId)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var batchIds = rows.Select(r => r.BatchId).Distinct().ToList();
        var batches = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var itemIds = rows.Select(r => r.ItemId).Concat(doc.Lines.Select(l => l.ItemId)).Distinct().ToList();
        var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        if (rows.FirstOrDefault(r => doc.Lines.All(l => l.ItemId != r.ItemId)) is { } stray)
        {
            throw new BusinessRuleException("stock.batch.stray", $"Разбивка по партии {batches[stray.BatchId].Number} — для позиции, которой нет в строках.");
        }

        foreach (var line in doc.Lines)
        {
            var sum = rows.Where(r => r.ItemId == line.ItemId).Sum(r => r.Quantity);
            if (sum != 0 && sum != line.Quantity)
            {
                throw new BusinessRuleException("stock.batch.sum",
                    $"{codes[line.ItemId]}: по партиям {Quantities.Format(sum)}, в строке {Quantities.Format(line.Quantity)} — разбивка должна сойтись.");
            }
        }

        if (rows.FirstOrDefault(r => batches[r.BatchId].IsArchived) is { } archived)
        {
            throw new BusinessRuleException("catalog.archived", $"Партия {batches[archived.BatchId].Number} в архиве.");
        }

        return rows.Select(r => new Planned(doc.WarehouseId, r.ItemId, r.BatchId, r.Quantity)).ToList();
    }

    /// <summary>
    /// Инвентаризация (D100). Позиция с фактом по партиям пересчитана по партиям полностью: разница по каждой партии = факт − учёт,
    /// партия с остатком без факта списывается до нуля, сумма партий не больше факта позиции. Позиция без фактов по партиям:
    /// недостача списывается как расход без партии (сначала «без партии», затем старые партии), излишек — «без партии».
    /// </summary>
    public static async Task<IReadOnlyList<Planned>> PlanInventoryAsync(
        IKnitErpDbContext db, AccessContext ctx, InventoryCount doc, IReadOnlyDictionary<long, decimal> book, CancellationToken ct)
    {
        var counts = await db.InventoryBatchCounts.AsNoTracking().Where(c => c.DocumentId == doc.Id && c.OrganizationId == ctx.OrganizationId)
            .ToListAsync(ct);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var balances = await db.StockBatchMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId))
            .GroupBy(m => new { m.ItemId, m.BatchId }).Select(g => new { g.Key.ItemId, g.Key.BatchId, Sum = g.Sum(m => m.Quantity) })
            .ToListAsync(ct);
        if (counts.Count == 0 && balances.All(b => b.Sum == 0))
        {
            return [];
        }

        var batchIds = counts.Select(c => c.BatchId).Distinct().ToList();
        var batches = await db.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        if (counts.FirstOrDefault(c => !itemIds.Contains(c.ItemId)) is { } stray)
        {
            throw new BusinessRuleException("stock.batch.stray", $"Факт по партии {batches[stray.BatchId].Number} — для позиции, которой нет в строках.");
        }

        var planned = new List<Planned>();
        foreach (var line in doc.Lines)
        {
            if (line.CountedQuantity is not { } counted)
            {
                continue; // непересчитанную позицию отклонит проведение
            }

            var itemCounts = counts.Where(c => c.ItemId == line.ItemId).ToList();
            var itemBalances = balances.Where(b => b.ItemId == line.ItemId).ToList();
            if (itemCounts.Count > 0)
            {
                var sum = itemCounts.Sum(c => c.CountedQuantity);
                if (sum > counted)
                {
                    throw new BusinessRuleException("stock.batch.inventory_over",
                        $"{codes[line.ItemId]}: по партиям {Quantities.Format(sum)} — больше факта позиции {Quantities.Format(counted)}.");
                }

                if (itemCounts.FirstOrDefault(c => c.CountedQuantity > 0 && batches[c.BatchId].IsArchived) is { } archived)
                {
                    throw new BusinessRuleException("catalog.archived", $"Партия {batches[archived.BatchId].Number} в архиве.");
                }

                foreach (var batchId in itemCounts.Select(c => c.BatchId).Union(itemBalances.Where(b => b.Sum != 0).Select(b => b.BatchId)))
                {
                    var diff = (itemCounts.FirstOrDefault(c => c.BatchId == batchId)?.CountedQuantity ?? 0)
                               - (itemBalances.FirstOrDefault(b => b.BatchId == batchId)?.Sum ?? 0);
                    if (diff != 0)
                    {
                        planned.Add(new(doc.WarehouseId, line.ItemId, batchId, diff));
                    }
                }
            }
            else if (counted < book.GetValueOrDefault(line.ItemId))
            {
                var total = book.GetValueOrDefault(line.ItemId);
                planned.AddRange(BatchAllocation.AutoAllocate(total - counted, total, itemBalances.Select(b => (b.BatchId, b.Sum)))
                    .Select(a => new Planned(doc.WarehouseId, line.ItemId, a.BatchId, -a.Quantity)));
            }
        }

        return planned;
    }
}
