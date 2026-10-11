using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

/// <summary>Фильтр реестра инвентаризаций: период по дате пересчёта и склад.</summary>
public sealed record InventoryListFilter(DateOnly? From = null, DateOnly? To = null, long? WarehouseId = null);

public sealed record InventoryRowDto(
    long Id, string Number, DateOnly CountDate, string WarehouseName, InventoryStatus Status, int LineCount,
    int SurplusCount, int ShortageCount, string CreatedBy)
{
    public string StatusName => InventoryCount.StatusName(Status);
}

public sealed record InventoryLineDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Book, decimal? Counted)
{
    public decimal Difference => (Counted ?? Book) - Book;
}

public sealed record InventoryDto(
    long Id, string Number, long WarehouseId, string WarehouseName, DateOnly CountDate, InventoryStatus Status, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? PostedBy, DateTime? PostedAtUtc,
    IReadOnlyList<InventoryLineDto> Lines, byte[] RowVersion, bool CanEdit, bool CanPost)
{
    public string StatusName => InventoryCount.StatusName(Status);
    public int NotCounted => Lines.Count(l => l.Counted is null);
    public IEnumerable<InventoryLineDto> Deviations => Lines.Where(l => l.Difference != 0);
}

/// <summary>
/// Инвентаризация: черновик заполняется по учёту, кладовщик вносит факт, проведение пишет разницы движениями.
/// Права — как у складских документов: черновик — warehouse.document.create, проведение — warehouse.document.post,
/// оба в области склада.
/// </summary>
public sealed class InventoryService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    private static readonly string[] ViewPermissions =
        [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    public async Task<IReadOnlyList<InventoryRowDto>> ListAsync(InventoryListFilter? filter = null, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        var q = db.InventoryCounts.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId);
        if (visible is not null)
        {
            q = q.Where(d => visible.Contains(d.WarehouseId));
        }

        if (filter?.From is { } from)
        {
            q = q.Where(d => d.CountDate >= from);
        }

        if (filter?.To is { } to)
        {
            q = q.Where(d => d.CountDate <= to);
        }

        if (filter?.WarehouseId is { } wh)
        {
            q = q.Where(d => d.WarehouseId == wh);
        }

        // Разница считается по колонкам строки: Difference — вычисляемое свойство и в SQL не переводится.
        return await (
            from d in q.OrderByDescending(d => d.CountDate).ThenByDescending(d => d.Id).Take(5000)
            join w in db.Warehouses.AsNoTracking() on d.WarehouseId equals w.Id
            join u in db.Users.AsNoTracking() on d.CreatedByUserId equals u.Id
            select new InventoryRowDto(d.Id, d.Number, d.CountDate, w.Name, d.Status, d.Lines.Count,
                d.Lines.Count(l => l.CountedQuantity != null && l.CountedQuantity > l.BookQuantity),
                d.Lines.Count(l => l.CountedQuantity != null && l.CountedQuantity < l.BookQuantity),
                u.DisplayName)).ToListAsync(ct);
    }

    public async Task<InventoryDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var doc = await db.InventoryCounts.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Инвентаризация");
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        if (visible is not null && !visible.Contains(doc.WarehouseId))
        {
            throw new NotFoundException("Инвентаризация");
        }

        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        long?[] userIds = [doc.CreatedByUserId, doc.PostedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == doc.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        var draft = doc.Status == InventoryStatus.Draft;
        return new InventoryDto(doc.Id, doc.Number, doc.WarehouseId, warehouse, doc.CountDate, doc.Status, doc.Comment,
            users[doc.CreatedByUserId], doc.CreatedAtUtc, doc.PostedByUserId is { } p ? users[p] : null, doc.PostedAtUtc,
            doc.Lines.Select(l => (Line: l, Item: items[l.ItemId]))
                .Select(x => new InventoryLineDto(x.Line.ItemId, x.Item.Code, x.Item.Name, x.Item.Symbol, x.Item.Precision,
                    x.Line.BookQuantity, x.Line.CountedQuantity))
                .OrderBy(l => l.Code).ToList(),
            doc.RowVersion,
            draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId),
            draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, doc.WarehouseId));
    }

    public async Task<IReadOnlyList<LookupWarehouseDto>> ListWarehousesForEntryAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseDocumentCreate);
        return await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && (visible == null || visible.Contains(w.Id)))
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EntryItemDto>> ListItemsForEntryAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        return await StockEntry.ActiveItems(db, ctx.OrganizationId).ToListAsync(ct);
    }

    /// <summary>Новая инвентаризация сразу заполняется по учёту — кладовщику остаётся внести факт.</summary>
    public async Task<long> CreateAsync(long warehouseId, DateOnly countDate, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == warehouseId && x.OrganizationId == ctx.OrganizationId, ct);
        if (w is null || !WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, warehouseId))
        {
            throw new NotFoundException("Склад");
        }

        if (w.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Склад «{w.Name}» в архиве.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, InventoryCount.NumberPrefix, ct);
        var doc = InventoryCount.Create(ctx.OrganizationId, number, warehouseId, countDate, comment, ctx.UserId, clock.UtcNow);
        doc.FillFromBook(await BookAsync(ctx.OrganizationId, warehouseId, null, ct));
        db.InventoryCounts.Add(doc);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.StockDocumentCreated, doc, null, $"{doc.Number} на {countDate:dd.MM.yyyy}, позиций по учёту: {doc.Lines.Count}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    public async Task UpdateHeaderAsync(long id, DateOnly countDate, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var before = $"{doc.CountDate:dd.MM.yyyy}, {doc.Comment}";
        doc.UpdateHeader(countDate, comment);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, before, $"{doc.CountDate:dd.MM.yyyy}, {doc.Comment}", "Шапка");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Обновить учётные количества (и добавить позиции, появившиеся на складе после заполнения).</summary>
    public async Task<int> RefillFromBookAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var added = doc.FillFromBook(await BookAsync(ctx.OrganizationId, doc.WarehouseId, null, ct));
        Audit(ctx, AuditActions.StockDocumentChanged, doc, null, $"учёт обновлён, добавлено позиций: {added}", "Заполнение по учёту");
        await db.SaveOrConflictAsync(ct);
        return added;
    }

    /// <summary>Факт по позиции; null — снять отметку «пересчитано». Новая позиция получает текущий учётный остаток.</summary>
    public async Task SetCountedAsync(long id, long itemId, decimal? counted, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct);
        var existing = doc.Lines.FirstOrDefault(l => l.ItemId == itemId);
        if (item is null && existing is null)
        {
            throw new NotFoundException("Номенклатура");
        }

        if (item is not null && counted is { } c)
        {
            StockEntry.EnsurePrecision(item, c);
        }

        var book = existing is null ? (await BookAsync(ctx.OrganizationId, doc.WarehouseId, itemId, ct)).GetValueOrDefault(itemId) : 0m;
        var before = existing?.CountedQuantity;
        doc.SetCounted(itemId, counted, book);
        var code = item?.Code ?? await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).SingleAsync(ct);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, before is null ? null : $"{code}: {Quantities.Format(before.Value)}",
            counted is null ? $"{code}: не пересчитано" : $"{code}: {Quantities.Format(counted.Value)}", "Факт");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveLineAsync(long id, long itemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var line = doc.Lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is { BookQuantity: not 0 })
        {
            throw new BusinessRuleException("stock.inventory.book_line", "По учёту позиция на складе есть — укажите факт (0, если её нет), а не удаляйте строку.");
        }

        doc.RemoveLine(itemId);
        var code = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).SingleAsync(ct);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, code, "строка удалена", "Строка");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Загрузка факта из Excel («Код», «Количество»): всё или ничего; строки, которых нет в файле, не меняются.</summary>
    public async Task<LineImportResult> ImportCountsAsync(long id, Stream file, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var (rows, lines) = await StockEntry.ParseLinesAsync(db, spreadsheet, ctx.OrganizationId, file, ct, allowZero: true);
        if (rows.Any(r => r.Errors.Count > 0))
        {
            return new LineImportResult(false, 0, rows);
        }

        var book = await BookAsync(ctx.OrganizationId, doc.WarehouseId, null, ct);
        foreach (var (itemId, quantity) in lines)
        {
            doc.SetCounted(itemId, quantity, book.GetValueOrDefault(itemId));
        }

        Audit(ctx, AuditActions.StockDocumentChanged, doc, null, $"загружено строк факта: {lines.Count}", "Импорт из Excel");
        await db.SaveOrConflictAsync(ct);
        return new LineImportResult(true, lines.Count, rows);
    }

    /// <summary>Бланк пересчёта: позиции документа с пустым фактом — распечатать или заполнить в Excel и загрузить обратно.</summary>
    public async Task<byte[]> CountSheetAsync(long id, CancellationToken ct = default)
    {
        var doc = await GetAsync(id, ct);
        return spreadsheet.Write(
        [
            new SheetData("Факт", StockEntry.ImportColumns,
                doc.Lines.Select(l => (IReadOnlyList<string>)[l.Code, l.Counted is { } c ? Quantities.Format(c) : ""]).ToList()),
            new SheetData("Позиции", ["Код", "Наименование", "Единица"],
                doc.Lines.Select(l => (IReadOnlyList<string>)[l.Code, l.Name, l.UnitSymbol]).ToList()),
        ]);
    }

    public async Task CancelAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        doc.Cancel();
        Audit(ctx, AuditActions.StockDocumentCancelled, doc, "Черновик", "Отменена", null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Проведение: склад блокируется, учёт берётся на момент проведения (D42), разницы пишутся движениями датой
    /// инвентаризации. Факт не бывает отрицательным, поэтому остаток после проведения тоже не отрицательный.
    /// </summary>
    public async Task PostAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        try
        {
            await PostCoreAsync(id, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges(); // отказ после doc.Post не должен оставить «проведённую» инвентаризацию в контексте вкладки
            throw;
        }
    }

    private async Task PostCoreAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var doc = await LoadAsync(ctx, id, Permissions.WarehouseDocumentPost, rowVersion, ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, doc.CountDate, ct);
        await db.LockWarehousesAsync([doc.WarehouseId], ct);
        var book = await BookAsync(ctx.OrganizationId, doc.WarehouseId, null, ct);
        var batchMoves = await StockBatchLedger.PlanInventoryAsync(db, ctx, doc, book, ct);
        var differences = doc.Post(book, ctx.UserId, clock.UtcNow);
        foreach (var (itemId, difference) in differences)
        {
            db.StockMovements.Add(StockMovement.Create(ctx.OrganizationId, doc.WarehouseId, itemId, difference, doc.CountDate,
                StockSource.Inventory, doc.Id, clock.UtcNow));
        }

        StockBatchLedger.Write(db, ctx.OrganizationId, doc.Id, doc.CountDate, batchMoves, StockSource.Inventory, clock.UtcNow);
        Audit(ctx, AuditActions.StockDocumentPosted, doc, "Черновик", "Проведена",
            $"излишков: {differences.Count(d => d.Difference > 0)}, недостач: {differences.Count(d => d.Difference < 0)}");
        await ClosedPeriod.SaveAsync(db, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Учётный остаток склада по позициям (все или одна).</summary>
    private async Task<Dictionary<long, decimal>> BookAsync(long organizationId, long warehouseId, long? itemId, CancellationToken ct) =>
        await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.WarehouseId == warehouseId && (itemId == null || m.ItemId == itemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) })
            .Where(x => x.Sum != 0)
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

    private async Task<AccessContext> DemandAnyAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ViewPermissions.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseDocumentCreate, ct);
        }

        return ctx;
    }

    private async Task<(AccessContext, InventoryCount)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        return (ctx, await LoadAsync(ctx, id, Permissions.WarehouseDocumentCreate, rowVersion, ct));
    }

    private async Task<InventoryCount> LoadAsync(AccessContext ctx, long id, string permission, byte[] rowVersion, CancellationToken ct)
    {
        var doc = await db.InventoryCounts.Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Инвентаризация");
        if (!WarehouseScope.Covers(ctx, permission, doc.WarehouseId))
        {
            throw new NotFoundException("Инвентаризация");
        }

        return doc.EnsureVersion(doc.RowVersion, rowVersion);
    }

    private void Audit(AccessContext ctx, string action, InventoryCount doc, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(InventoryCount), doc.Id.ToString(),
            before, after, reason is null ? doc.Number : $"{doc.Number}: {reason}", currentUser.CorrelationId));
}
