using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

public sealed record OpeningBalanceRowDto(
    long Id, string Number, long WarehouseId, string WarehouseName, DateOnly AsOfDate, OpeningBalanceStatus Status,
    int LineCount, string CreatedBy, DateTime CreatedAtUtc)
{
    public string StatusName => OpeningBalance.StatusName(Status);
}

public sealed record OpeningBalanceLineDto(long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Quantity);

public sealed record OpeningBalanceDto(
    long Id, string Number, long WarehouseId, string WarehouseName, DateOnly AsOfDate, OpeningBalanceStatus Status, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? ApprovedBy, DateTime? ApprovedAtUtc, string? ReturnReason,
    IReadOnlyList<OpeningBalanceLineDto> Lines, byte[] RowVersion,
    bool CanEdit, bool CanApprove)
{
    public string StatusName => OpeningBalance.StatusName(Status);
}

public sealed record EntryItemDto(long Id, string Code, string Name, string UnitSymbol, byte Precision);

public sealed record LineImportRow(int RowNumber, string Code, string QuantityText, IReadOnlyList<string> Errors);

public sealed record LineImportResult(bool Applied, int Lines, IReadOnlyList<LineImportRow> Rows)
{
    public int ErrorRows => Rows.Count(r => r.Errors.Count > 0);
}

/// <summary>
/// Начальные остатки склада: черновик (Старший кладовщик) → на утверждении → утверждён (Владелец или Руководитель,
/// не автор). Утверждение создаёт движения регистра. Все действия проверяют право и область склада на сервере.
/// </summary>
public sealed class OpeningBalanceService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    public static IReadOnlyList<string> ImportColumns => StockEntry.ImportColumns;

    public async Task<IReadOnlyList<OpeningBalanceRowDto>> ListAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var visible = WarehouseScope.Visible(ctx, Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView);
        var q = db.OpeningBalances.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId);
        if (visible is not null)
        {
            q = q.Where(d => visible.Contains(d.WarehouseId));
        }

        return await q.OrderByDescending(d => d.Id)
            .Join(db.Warehouses.AsNoTracking(), d => d.WarehouseId, w => w.Id, (d, w) => new { d, w.Name })
            .Join(db.Users.AsNoTracking(), x => x.d.CreatedByUserId, u => u.Id, (x, u) => new OpeningBalanceRowDto(
                x.d.Id, x.d.Number, x.d.WarehouseId, x.Name, x.d.AsOfDate, x.d.Status, x.d.Lines.Count, u.DisplayName, x.d.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<OpeningBalanceDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var doc = await db.OpeningBalances.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        var visible = WarehouseScope.Visible(ctx, Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView);
        if (visible is not null && !visible.Contains(doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        var users = await db.Users.AsNoTracking().Where(u => u.Id == doc.CreatedByUserId || u.Id == doc.ApprovedByUserId)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == doc.WarehouseId).Select(w => w.Name).SingleAsync(ct);

        var canEdit = doc.Status == OpeningBalanceStatus.Draft && WarehouseScope.Covers(ctx, Permissions.OpeningBalanceCreate, doc.WarehouseId);
        var canApprove = doc.Status == OpeningBalanceStatus.Submitted && doc.CreatedByUserId != ctx.UserId
                         && WarehouseScope.Covers(ctx, Permissions.OpeningBalanceApprove, doc.WarehouseId);
        return new OpeningBalanceDto(doc.Id, doc.Number, doc.WarehouseId, warehouse, doc.AsOfDate, doc.Status, doc.Comment,
            users[doc.CreatedByUserId], doc.CreatedAtUtc, doc.ApprovedByUserId is { } a ? users[a] : null, doc.ApprovedAtUtc, doc.ReturnReason,
            doc.Lines.Select(l => (Line: l, Item: items[l.ItemId]))
                .Select(x => new OpeningBalanceLineDto(x.Line.ItemId, x.Item.Code, x.Item.Name, x.Item.Symbol, x.Item.Precision, x.Line.Quantity))
                .OrderBy(l => l.Code).ToList(),
            doc.RowVersion, canEdit, canApprove);
    }

    /// <summary>Склады, где текущий пользователь может заводить начальные остатки.</summary>
    public async Task<IReadOnlyList<LookupWarehouseDto>> ListWarehousesForEntryAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        var visible = WarehouseScope.Visible(ctx, Permissions.OpeningBalanceCreate);
        return await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && (visible == null || visible.Contains(w.Id)))
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EntryItemDto>> ListItemsForEntryAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        return await StockEntry.ActiveItems(db, ctx.OrganizationId, stockedOnly: true).ToListAsync(ct);
    }

    public async Task<long> CreateAsync(long warehouseId, DateOnly asOfDate, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        await RequireWarehouseAsync(ctx, warehouseId, Permissions.OpeningBalanceCreate, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, OpeningBalance.NumberPrefix, ct);
        var doc = OpeningBalance.Create(ctx.OrganizationId, number, warehouseId, asOfDate, comment, ctx.UserId, clock.UtcNow);
        db.OpeningBalances.Add(doc);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.StockDocumentCreated, doc, null, $"{doc.Number} на {asOfDate:dd.MM.yyyy}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    public async Task UpdateHeaderAsync(long id, DateOnly asOfDate, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var before = doc.AsOfDate;
        doc.UpdateHeader(asOfDate, comment);
        if (before != doc.AsOfDate)
        {
            Audit(ctx, AuditActions.StockDocumentChanged, doc, before.ToString("dd.MM.yyyy"), doc.AsOfDate.ToString("dd.MM.yyyy"), "Дата остатков");
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetLineAsync(long id, long itemId, decimal quantity, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var item = await ActiveItems(ctx, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Номенклатура");
        await StockEntry.EnsureStockedAsync(db, ctx.OrganizationId, [itemId], ct); // D300 (сессия 4)
        StockEntry.EnsurePrecision(item, quantity);
        var before = doc.Lines.FirstOrDefault(l => l.ItemId == itemId)?.Quantity;
        doc.SetLine(itemId, quantity);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, before is null ? null : $"{item.Code}: {Quantities.Format(before.Value)}",
            $"{item.Code}: {Quantities.Format(quantity)} {item.UnitSymbol}", "Строка");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveLineAsync(long id, long itemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var line = doc.Lines.FirstOrDefault(l => l.ItemId == itemId);
        doc.RemoveLine(itemId);
        var code = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).SingleAsync(ct);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, $"{code}: {Quantities.Format(line!.Quantity)}", "строка удалена", "Строка");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Загрузка строк из Excel (столбцы «Код», «Количество»): заменяет все строки черновика. При любой ошибке
    /// не меняется ничего, а протокол показывает, что исправить.
    /// </summary>
    public async Task<LineImportResult> ImportLinesAsync(long id, Stream file, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var (rows, lines) = await StockEntry.ParseLinesAsync(db, spreadsheet, ctx.OrganizationId, file, ct);
        if (rows.Any(r => r.Errors.Count > 0))
        {
            return new LineImportResult(false, 0, rows);
        }

        doc.ReplaceLines(lines);
        Audit(ctx, AuditActions.StockDocumentChanged, doc, null, $"загружено строк: {lines.Count}", "Импорт из Excel");
        await db.SaveOrConflictAsync(ct);
        return new LineImportResult(true, lines.Count, rows);
    }

    /// <summary>Шаблон строк: «Код», «Количество» и лист с действующей номенклатурой для подсказки.</summary>
    public async Task<byte[]> LinesTemplateAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        return await StockEntry.LinesTemplateAsync(db, spreadsheet, ctx.OrganizationId, "Остатки", ct);
    }

    public async Task SubmitAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        doc.Submit(clock.UtcNow);
        Audit(ctx, AuditActions.StockDocumentSubmitted, doc, "Черновик", "На утверждении", $"строк: {doc.Lines.Count}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task CancelAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        doc.Cancel();
        Audit(ctx, AuditActions.StockDocumentCancelled, doc, "Черновик", "Отменён", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task ReturnAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForApprovalAsync(id, rowVersion, ct);
        doc.ReturnToDraft(reason);
        Audit(ctx, AuditActions.StockDocumentReturned, doc, "На утверждении", "Черновик", doc.ReturnReason);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Утверждение: проверки и движения регистра в одной транзакции. Начальный остаток позиции на складе
    /// вводится один раз (допущение D37): повторное утверждение той же позиции отклоняется.
    /// </summary>
    public async Task ApproveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        try
        {
            await ApproveCoreAsync(id, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges(); // отказ после doc.Approve не должен оставить «утверждённый» документ в контексте вкладки
            throw;
        }
    }

    private async Task ApproveCoreAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var (ctx, doc) = await LoadForApprovalAsync(id, rowVersion, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, doc.AsOfDate, ct);

        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var already = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && m.Source == StockSource.OpeningBalance
                        && itemIds.Contains(m.ItemId))
            .Join(db.Items.AsNoTracking(), m => m.ItemId, i => i.Id, (m, i) => i.Code)
            .Distinct().ToListAsync(ct);
        if (already.Count > 0)
        {
            throw new BusinessRuleException("stock.opening.already_entered",
                $"Начальный остаток уже утверждён для: {string.Join(", ", already.Take(10))}{(already.Count > 10 ? "…" : "")}. Уберите эти строки.");
        }

        var archived = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsArchived).Select(i => i.Code).ToListAsync(ct);
        await StockEntry.EnsureStockedAsync(db, ctx.OrganizationId, itemIds, ct); // D300 (сессия 4)
        if (archived.Count > 0)
        {
            throw new BusinessRuleException("stock.opening.archived_item", $"Позиции в архиве: {string.Join(", ", archived.Take(10))}.");
        }

        var batchMoves = await StockBatchLedger.PlanOpeningAsync(db, ctx, doc, ct);
        doc.Approve(ctx.UserId, clock.UtcNow);
        foreach (var line in doc.Lines)
        {
            db.StockMovements.Add(StockMovement.Create(ctx.OrganizationId, doc.WarehouseId, line.ItemId, line.Quantity, doc.AsOfDate,
                StockSource.OpeningBalance, doc.Id, clock.UtcNow));
        }

        StockBatchLedger.Write(db, ctx.OrganizationId, doc.Id, doc.AsOfDate, batchMoves, StockSource.OpeningBalance, clock.UtcNow);

        Audit(ctx, AuditActions.StockDocumentApproved, doc, "На утверждении", "Утверждён", $"движений: {doc.Lines.Count}");
        await ClosedPeriod.SaveAsync(db, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<AccessContext> DemandAnyAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.OpeningBalanceCreate) && !ctx.Permissions.Has(Permissions.OpeningBalanceApprove)
            && !ctx.Permissions.Has(Permissions.WarehouseReportView))
        {
            await guard.DenyAsync(ctx, Permissions.OpeningBalanceCreate, ct);
        }

        return ctx;
    }

    private async Task<(AccessContext, OpeningBalance)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceCreate, ct);
        return (ctx, await LoadAsync(ctx, id, Permissions.OpeningBalanceCreate, rowVersion, ct));
    }

    private async Task<(AccessContext, OpeningBalance)> LoadForApprovalAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OpeningBalanceApprove, ct);
        return (ctx, await LoadAsync(ctx, id, Permissions.OpeningBalanceApprove, rowVersion, ct));
    }

    /// <summary>Документ своей организации и своей области склада; иначе «не найдено».</summary>
    private async Task<OpeningBalance> LoadAsync(AccessContext ctx, long id, string permission, byte[] rowVersion, CancellationToken ct)
    {
        var doc = await db.OpeningBalances.Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!WarehouseScope.Covers(ctx, permission, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        return doc.EnsureVersion(doc.RowVersion, rowVersion);
    }

    private async Task RequireWarehouseAsync(AccessContext ctx, long warehouseId, string permission, CancellationToken ct)
    {
        var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == warehouseId && x.OrganizationId == ctx.OrganizationId, ct);
        if (w is null || !WarehouseScope.Covers(ctx, permission, warehouseId))
        {
            throw new NotFoundException("Склад");
        }

        if (w.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Склад в архиве.");
        }
    }

    private IQueryable<EntryItemDto> ActiveItems(AccessContext ctx, long? itemId = null) => StockEntry.ActiveItems(db, ctx.OrganizationId, itemId);

    private void Audit(AccessContext ctx, string action, OpeningBalance doc, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(OpeningBalance), doc.Id.ToString(),
            before, after, reason is null ? doc.Number : $"{doc.Number}: {reason}", currentUser.CorrelationId));
}

public sealed record LookupWarehouseDto(long Id, string Name);

/// <summary>
/// Область склада для складских прав: null — все склады; иначе склады из назначений роли.
/// Назначение с подразделением, но без склада, складов не даёт (допущение D38).
/// </summary>
internal static class WarehouseScope
{
    public static HashSet<long>? Visible(AccessContext ctx, params string[] permissionCodes)
    {
        var result = new HashSet<long>();
        foreach (var code in permissionCodes)
        {
            if (!ctx.Permissions.Has(code))
            {
                continue;
            }

            var scope = ctx.Permissions.ScopeOf(code);
            if (scope.All)
            {
                return null;
            }

            result.UnionWith(scope.WarehouseIds);
        }

        return result;
    }

    /// <summary>Права, дающие видеть остатки (D118): создание и проведение складских документов, складские отчёты.</summary>
    public static readonly string[] StockViews = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    /// <summary>
    /// D118 (аудит AUTHZ-03): движения склада, которые пользователь вправе видеть, — только склады его области; без складских прав — null
    /// (остаток не показывается). Так карточки справочников не раскрывают остатки чужих складов.
    /// </summary>
    public static IQueryable<KnitErp.Domain.Warehousing.StockMovement>? VisibleMovements(IKnitErpDbContext db, AccessContext ctx)
    {
        if (!StockViews.Any(ctx.Permissions.Has))
        {
            return null;
        }

        var visible = Visible(ctx, StockViews);
        var q = db.StockMovements.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId);
        return visible is null ? q : q.Where(m => visible.Contains(m.WarehouseId));
    }

    /// <summary>Видит ли пользователь все склады (для подписи «на всех складах» / «на ваших складах»).</summary>
    public static bool SeesAllWarehouses(AccessContext ctx) => StockViews.Any(ctx.Permissions.Has) && Visible(ctx, StockViews) is null;

    public static bool Covers(AccessContext ctx, string permissionCode, long warehouseId) =>
        ctx.Permissions.Has(permissionCode) && ctx.Permissions.ScopeOf(permissionCode).CoversWarehouse(warehouseId);
}
