using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

public sealed record StockDocumentRowDto(
    long Id, string Number, StockOperationKind Kind, DateOnly DocumentDate, string WarehouseName, string? TargetWarehouseName,
    string? CounterpartyName, string? ReasonName, StockDocumentStatus Status, int LineCount, string CreatedBy)
{
    public string KindName => StockDocument.KindName(Kind);
    public string StatusName => StockDocument.StatusName(Status);
}

public sealed record StockDocumentLineDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Quantity, decimal? Available);

public sealed record StockDocumentDto(
    long Id, string Number, StockOperationKind Kind, StockDocumentStatus Status, DateOnly DocumentDate,
    long WarehouseId, string WarehouseName, long? TargetWarehouseId, string? TargetWarehouseName,
    long? CounterpartyId, string? CounterpartyName, long? ReasonId, string? ReasonName, bool ReasonRequiresComment, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? PostedBy, DateTime? PostedAtUtc,
    string? ReversedBy, DateTime? ReversedAtUtc, string? ReversalReason,
    IReadOnlyList<StockDocumentLineDto> Lines, byte[] RowVersion, bool CanEdit, bool CanPost, bool CanReverse,
    long? PurchaseOrderId = null, string? PurchaseOrderNumber = null, long? SalesOrderId = null, string? SalesOrderNumber = null)
{
    public string KindName => StockDocument.KindName(Kind);
    public string StatusName => StockDocument.StatusName(Status);

    /// <summary>Строки, которых не хватает на складе-отправителе (для расходных документов в черновике).</summary>
    public IEnumerable<StockDocumentLineDto> Shortages => Lines.Where(l => l.Available is { } a && a < l.Quantity);
}

public sealed record ReasonLookupDto(long Id, string Name, bool RequiresComment);

/// <summary>Справочники формы документа: склады из области пользователя, все склады для получателя, поставщики, причины вида.</summary>
public sealed record StockDocumentOptions(
    IReadOnlyList<LookupWarehouseDto> Warehouses, IReadOnlyList<LookupWarehouseDto> TargetWarehouses,
    IReadOnlyList<LookupDto> Suppliers, IReadOnlyList<ReasonLookupDto> Reasons);

/// <summary>Фильтр реестра документов: вид, статус, период по дате документа, склад (отправитель или получатель), поиск по номеру и контрагенту.</summary>
public sealed record StockDocumentListFilter(
    StockOperationKind? Kind = null, StockDocumentStatus? Status = null, DateOnly? From = null, DateOnly? To = null,
    long? WarehouseId = null, string? Search = null, bool? ByOrder = null, long? TagId = null);

/// <summary>
/// Поступление, списание и перемещение: черновик → проведение (движения регистра) → при ошибке сторно.
/// Черновик — право <c>warehouse.document.create</c>, проведение и сторно — <c>warehouse.document.post</c>;
/// оба в области склада-отправителя (у поступления — склада документа). Проведение и сторно не уводят
/// остаток в минус (допущение D40) — склады блокируются на время проверки и записи.
/// </summary>
public sealed class StockDocumentService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    private static readonly string[] ViewPermissions =
        [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    public const int MaxListRows = 1000;
    public const int MaxRegistryRows = 20000;

    /// <summary>Реестр документов: на экране — последние <see cref="MaxListRows"/>, в выгрузке — до <see cref="MaxRegistryRows"/>.</summary>
    public async Task<IReadOnlyList<StockDocumentRowDto>> ListAsync(StockDocumentListFilter filter, CancellationToken ct = default) =>
        await ListAsync(filter, MaxListRows, ct);

    public async Task<IReadOnlyList<StockDocumentRowDto>> ListAsync(StockDocumentListFilter filter, int take, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        var q = db.StockDocuments.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId);
        if (filter.TagId is { } tagId)
        {
            // D104: отбор по тегу — в запросе, чтобы находились и объекты за пределами первых строк списка.
            q = q.Where(d => db.TagLinks.Any(l => l.TagId == tagId && l.Entity == KnitErp.Domain.Tagging.TaggedEntity.StockDocument && l.EntityId == d.Id));
        }

        if (visible is not null)
        {
            q = q.Where(d => visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value)));
        }

        if (filter.Kind is { } kind)
        {
            q = q.Where(d => d.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            q = q.Where(d => d.Status == status);
        }

        if (filter.From is { } from)
        {
            q = q.Where(d => d.DocumentDate >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(d => d.DocumentDate <= to);
        }

        if (filter.WarehouseId is { } wh)
        {
            q = q.Where(d => d.WarehouseId == wh || d.TargetWarehouseId == wh);
        }

        // «Приёмка» — поступления по заказу поставщику, «Оприходования» — без заказа (выпуск, излишки, прочее).
        if (filter.ByOrder is { } byOrder)
        {
            q = byOrder ? q.Where(d => d.PurchaseOrderId != null || d.SalesOrderId != null) : q.Where(d => d.PurchaseOrderId == null && d.SalesOrderId == null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            q = q.Where(d => d.Number.Contains(text)
                             || db.Counterparties.Any(c => c.Id == d.CounterpartyId && (c.Name.Contains(text) || c.Inn == text)));
        }

        var rows = await (
            from d in q.OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.Id).Take(Math.Clamp(take, 1, MaxRegistryRows))
            join w in db.Warehouses.AsNoTracking() on d.WarehouseId equals w.Id
            join u in db.Users.AsNoTracking() on d.CreatedByUserId equals u.Id
            join t in db.Warehouses.AsNoTracking() on d.TargetWarehouseId equals t.Id into tj
            from t in tj.DefaultIfEmpty()
            join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            join r in db.OperationReasons.AsNoTracking() on d.ReasonId equals r.Id into rj
            from r in rj.DefaultIfEmpty()
            select new StockDocumentRowDto(d.Id, d.Number, d.Kind, d.DocumentDate, w.Name, t == null ? null : t.Name,
                c == null ? null : c.Name, r == null ? null : r.Name, d.Status, d.Lines.Count, u.DisplayName)).ToListAsync(ct);
        return rows.OrderByDescending(r => r.DocumentDate).ThenByDescending(r => r.Id).ToList();
    }

    /// <summary>Склады для фильтра реестров (документы, инвентаризации): те, что пользователь видит по своим правам склада.</summary>
    public async Task<IReadOnlyList<LookupWarehouseDto>> ListVisibleWarehousesAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        return await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == ctx.OrganizationId && (visible == null || visible.Contains(w.Id)))
            .OrderBy(w => w.IsArchived).ThenBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
    }

    public async Task<StockDocumentDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        var visible = WarehouseScope.Visible(ctx, ViewPermissions);
        if (visible is not null && !visible.Contains(doc.WarehouseId) && !(doc.TargetWarehouseId is { } t && visible.Contains(t)))
        {
            throw new NotFoundException("Документ");
        }

        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        long?[] userIds = [doc.CreatedByUserId, doc.PostedByUserId, doc.ReversedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == doc.WarehouseId || w.Id == doc.TargetWarehouseId).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var counterparty = doc.CounterpartyId is { } cid
            ? await db.Counterparties.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).SingleAsync(ct)
            : null;
        var reason = doc.ReasonId is { } rid
            ? await db.OperationReasons.AsNoTracking().Where(r => r.Id == rid).Select(r => new { r.Name, r.RequiresComment }).SingleAsync(ct)
            : null;
        var orderNumber = doc.PurchaseOrderId is { } oid
            ? await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == oid).Select(o => o.Number).SingleAsync(ct)
            : null;
        var salesNumber = doc.SalesOrderId is { } sid
            ? await db.SalesOrders.AsNoTracking().Where(o => o.Id == sid).Select(o => o.Number).SingleAsync(ct)
            : null;

        // Остаток на складе-отправителе — подсказка в черновике расходного документа.
        Dictionary<long, decimal>? available = null;
        if (doc.Status == StockDocumentStatus.Draft && StockDocument.IsOutgoing(doc.Kind))
        {
            available = await db.StockMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == doc.WarehouseId && itemIds.Contains(m.ItemId))
                .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        }

        var lines = doc.Lines.Select(l => (Line: l, Item: items[l.ItemId]))
            .Select(x => new StockDocumentLineDto(x.Line.ItemId, x.Item.Code, x.Item.Name, x.Item.Symbol, x.Item.Precision, x.Line.Quantity,
                available is null ? null : available.GetValueOrDefault(x.Line.ItemId)))
            .OrderBy(l => l.Code).ToList();

        var canEdit = doc.Status == StockDocumentStatus.Draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId);
        var canPost = doc.Status == StockDocumentStatus.Draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, doc.WarehouseId);
        var canReverse = doc.Status == StockDocumentStatus.Posted && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, doc.WarehouseId);
        return new StockDocumentDto(doc.Id, doc.Number, doc.Kind, doc.Status, doc.DocumentDate,
            doc.WarehouseId, warehouses[doc.WarehouseId], doc.TargetWarehouseId,
            doc.TargetWarehouseId is { } tw ? warehouses[tw] : null,
            doc.CounterpartyId, counterparty, doc.ReasonId, reason?.Name, reason?.RequiresComment ?? false, doc.Comment,
            users[doc.CreatedByUserId], doc.CreatedAtUtc,
            doc.PostedByUserId is { } p ? users[p] : null, doc.PostedAtUtc,
            doc.ReversedByUserId is { } r ? users[r] : null, doc.ReversedAtUtc, doc.ReversalReason,
            lines, doc.RowVersion, canEdit, canPost, canReverse, doc.PurchaseOrderId, orderNumber, doc.SalesOrderId, salesNumber);
    }

    /// <summary>Справочники формы. Склады документа — из области права на черновик, получатель — любой действующий склад.</summary>
    public async Task<StockDocumentOptions> GetOptionsAsync(StockOperationKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseDocumentCreate);
        var all = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived)
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
        var own = all.Where(w => visible is null || visible.Contains(w.Id)).ToList();
        var customers = StockDocument.WithCustomer(kind);
        var suppliers = StockDocument.WithSupplier(kind) || customers
            ? await db.Counterparties.AsNoTracking()
                .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && (customers ? c.IsCustomer : c.IsSupplier))
                .OrderBy(c => c.Name).Select(c => new LookupDto(c.Id, c.Name)).ToListAsync(ct)
            : [];
        var reasons = await db.OperationReasons.AsNoTracking()
            .Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Kind == kind)
            .OrderBy(r => r.Name).Select(r => new ReasonLookupDto(r.Id, r.Name, r.RequiresComment)).ToListAsync(ct);
        return new StockDocumentOptions(own, kind == StockOperationKind.Transfer ? all : [], suppliers, reasons);
    }

    public async Task<IReadOnlyList<EntryItemDto>> ListItemsForEntryAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        return await StockEntry.ActiveItems(db, ctx.OrganizationId).ToListAsync(ct);
    }

    public async Task<long> CreateAsync(StockOperationKind kind, StockDocumentHeader header, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var prefix = StockDocument.NumberPrefix(kind);
        await ValidateHeaderAsync(ctx, kind, header, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, prefix, ct);
        var doc = StockDocument.Create(ctx.OrganizationId, number, kind, header, ctx.UserId, clock.UtcNow);
        db.StockDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.StockDocumentCreated, doc, null, $"{StockDocument.KindName(kind)} от {header.DocumentDate:dd.MM.yyyy}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    /// <summary>
    /// Документ сразу со строками — для оформления по заказу поставщику (поступление остатка, возврат).
    /// Права и области — те же, что у обычного черновика.
    /// </summary>
    public async Task<long> CreateWithLinesAsync(
        StockOperationKind kind, StockDocumentHeader header, IReadOnlyList<(long ItemId, decimal Quantity)> lines, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        await ValidateHeaderAsync(ctx, kind, header, ct);
        // Аудит 11.10.2026 (D109): строки из API и из заказов — позиция своей организации, количество по точности единицы.
        // Иначе в документ попала бы номенклатура другой организации. Архивная позиция допустима: заказ мог быть до архива.
        var ids = lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && ids.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new EntryItemDto(i.Id, i.Code, i.Name, u.Symbol, u.Precision))
            .ToDictionaryAsync(i => i.Id, ct);
        foreach (var (itemId, quantity) in lines)
        {
            StockEntry.EnsurePrecision(items.GetValueOrDefault(itemId) ?? throw new NotFoundException("Номенклатура"), quantity);
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, StockDocument.NumberPrefix(kind), ct);
        var doc = StockDocument.Create(ctx.OrganizationId, number, kind, header, ctx.UserId, clock.UtcNow);
        doc.ReplaceLines(lines);
        db.StockDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.StockDocumentCreated, doc, null, $"{StockDocument.KindName(kind)} от {header.DocumentDate:dd.MM.yyyy}",
            $"строк: {lines.Count}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    public async Task UpdateHeaderAsync(long id, StockDocumentHeader header, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        await ValidateHeaderAsync(ctx, doc.Kind, header, ct);
        var before = await DescribeHeaderAsync(doc.WarehouseId, doc.TargetWarehouseId, doc.CounterpartyId, doc.ReasonId, doc.DocumentDate, ct);
        doc.UpdateHeader(header);
        var after = await DescribeHeaderAsync(doc.WarehouseId, doc.TargetWarehouseId, doc.CounterpartyId, doc.ReasonId, doc.DocumentDate, ct);
        if (before != after)
        {
            Audit(ctx, AuditActions.StockDocumentChanged, doc, before, after, "Шапка");
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetLineAsync(long id, long itemId, decimal quantity, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Номенклатура");
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

    /// <summary>Загрузка строк из Excel: заменяет все строки черновика; при любой ошибке не меняется ничего.</summary>
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

    public async Task<byte[]> LinesTemplateAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        return await StockEntry.LinesTemplateAsync(db, spreadsheet, ctx.OrganizationId, "Строки", ct);
    }

    public async Task CancelAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc) = await LoadForEditAsync(id, rowVersion, ct);
        doc.Cancel();
        Audit(ctx, AuditActions.StockDocumentCancelled, doc, "Черновик", "Отменён", null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Проведение: повторная проверка справочников, блокировка складов, проверка остатка и запись движений —
    /// в одной транзакции. Автор может провести свой документ сам (допущение D39).
    /// </summary>
    public async Task PostAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        try
        {
            await PostCoreAsync(id, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges(); // отказ после doc.Post не должен оставить «проведённый» документ в контексте вкладки
            throw;
        }
    }

    private async Task PostCoreAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var doc = await LoadAsync(ctx, id, Permissions.WarehouseDocumentPost, rowVersion, ct);
        if (doc.Status != StockDocumentStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Провести можно только черновик.");
        }

        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, doc.DocumentDate, ct);
        await ValidateHeaderAsync(ctx, doc.Kind, Header(doc), ct);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var archived = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsArchived).Select(i => i.Code).ToListAsync(ct);
        if (archived.Count > 0)
        {
            throw new BusinessRuleException("stock.document.archived_item", $"Позиции в архиве: {string.Join(", ", archived.Take(10))}.");
        }

        var requiresComment = doc.ReasonId is { } rid
                              && await db.OperationReasons.AsNoTracking().Where(r => r.Id == rid).Select(r => r.RequiresComment).SingleAsync(ct);
        // Аудит 10.10.2026 (п. 2): проверка лимита возврата и проведение — под блокировкой заказа до конца транзакции,
        // иначе два параллельных возврата видят одну и ту же отгрузку и вместе превышают её.
        if (doc.Kind == StockOperationKind.ReturnToSupplier && doc.PurchaseOrderId is { } orderId)
        {
            await db.LockAsync($"kniterp.order.purchase.{orderId}", ct);
            await EnsureReturnWithinReceivedAsync(ctx, doc, orderId, ct);
        }

        if (doc.Kind == StockOperationKind.CustomerReturn && doc.SalesOrderId is { } salesOrderId)
        {
            await db.LockAsync($"kniterp.order.sales.{salesOrderId}", ct);
            await EnsureReturnWithinShippedAsync(ctx, doc, salesOrderId, ct);
        }

        var batchMoves = await StockBatchLedger.PlanPostAsync(db, ctx, doc, ct);
        doc.Post(ctx.UserId, requiresComment, clock.UtcNow);
        await WriteMovementsAsync(ctx, doc, doc.MovementDeltas(), StockSource.StockDocument, ct);
        StockBatchLedger.Write(db, ctx, doc, batchMoves, StockSource.StockDocument, clock.UtcNow);
        Audit(ctx, AuditActions.StockDocumentPosted, doc, "Черновик", "Проведён", $"строк: {doc.Lines.Count}");
        await ClosedPeriod.SaveAsync(db, ct);
        // D110: отгрузка по заказу покупателя двигает его этап («Отгружен частично / полностью»).
        if (doc.Kind == StockOperationKind.Shipment && doc.SalesOrderId is { } shippedOrder)
        {
            await KnitErp.Application.Sales.StageAutomation.ApplyAsync(db, ctx, [shippedOrder], clock.UtcNow, currentUser.CorrelationId, ct);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Сторно: движения с обратным знаком той же датой — документ как будто не проводился (допущение D41).
    /// Нельзя, если товар уже ушёл со склада и остаток стал бы отрицательным.
    /// </summary>
    public async Task ReverseAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        try
        {
            await ReverseCoreAsync(id, reason, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task ReverseCoreAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var doc = await LoadAsync(ctx, id, Permissions.WarehouseDocumentPost, rowVersion, ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, doc.DocumentDate, ct);
        var batchMoves = doc.Status == StockDocumentStatus.Posted ? await StockBatchLedger.PlanReverseAsync(db, ctx, doc, ct) : [];
        doc.Reverse(ctx.UserId, reason, clock.UtcNow);

        // Аудит 10.10.2026 (п. 1): сторно гасит фактически записанные движения документа, а не пересчёт по строкам —
        // остаток после сторно ровно такой, как до проведения, даже если строки как-то разошлись с движениями.
        var deltas = (await db.StockMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.Source == StockSource.StockDocument && m.SourceId == doc.Id)
                .GroupBy(m => new { m.WarehouseId, m.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct))
            .Where(d => d.Sum != 0).Select(d => (d.WarehouseId, d.ItemId, -d.Sum)).ToList();
        await WriteMovementsAsync(ctx, doc, deltas, StockSource.StockDocumentReversal, ct);
        StockBatchLedger.Write(db, ctx, doc, batchMoves, StockSource.StockDocumentReversal, clock.UtcNow);
        Audit(ctx, AuditActions.StockDocumentReversed, doc, "Проведён", "Сторнирован", doc.ReversalReason);
        await ClosedPeriod.SaveAsync(db, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Расход не должен увести остаток склада ниже нуля (D03 открыт, допущение D40). Склады заблокированы до конца транзакции.</summary>
    private async Task WriteMovementsAsync(
        AccessContext ctx, StockDocument doc, IReadOnlyList<(long WarehouseId, long ItemId, decimal Quantity)> deltas, StockSource source,
        CancellationToken ct)
    {
        var net = deltas.GroupBy(d => (d.WarehouseId, d.ItemId)).Select(g => (g.Key.WarehouseId, g.Key.ItemId, Quantity: g.Sum(d => d.Quantity))).ToList();
        var outgoing = net.Where(d => d.Quantity < 0).ToList();
        if (outgoing.Count > 0)
        {
            await db.LockWarehousesAsync(outgoing.Select(d => d.WarehouseId), ct);
            var warehouseIds = outgoing.Select(d => d.WarehouseId).Distinct().ToList();
            var itemIds = outgoing.Select(d => d.ItemId).Distinct().ToList();
            var balances = await db.StockMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && warehouseIds.Contains(m.WarehouseId) && itemIds.Contains(m.ItemId))
                .GroupBy(m => new { m.WarehouseId, m.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct);
            var shortages = outgoing
                .Select(d => (d.WarehouseId, d.ItemId, Need: -d.Quantity,
                    Have: balances.FirstOrDefault(b => b.WarehouseId == d.WarehouseId && b.ItemId == d.ItemId)?.Sum ?? 0m))
                .Where(x => x.Have < x.Need).ToList();
            if (shortages.Count > 0)
            {
                var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
                var names = await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
                var text = string.Join("; ", shortages.Take(10).Select(s =>
                    $"{codes[s.ItemId]} на складе «{names[s.WarehouseId]}»: нужно {Quantities.Format(s.Need)}, есть {Quantities.Format(s.Have)}"));
                throw new BusinessRuleException("stock.balance.insufficient",
                    $"Не хватает остатка — {text}{(shortages.Count > 10 ? "…" : "")}.");
            }
        }

        foreach (var (warehouseId, itemId, quantity) in deltas)
        {
            db.StockMovements.Add(StockMovement.Create(ctx.OrganizationId, warehouseId, itemId, quantity, doc.DocumentDate, source, doc.Id, clock.UtcNow));
        }
    }

    /// <summary>Возврат по заказу — не больше, чем поступило по этому заказу за вычетом прежних возвратов (D64).</summary>
    private async Task EnsureReturnWithinReceivedAsync(AccessContext ctx, StockDocument doc, long orderId, CancellationToken ct)
    {
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.PurchaseOrderId == orderId && d.Status == StockDocumentStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { d.Kind, l.ItemId, l.Quantity }))
            .Where(x => itemIds.Contains(x.ItemId))
            .ToListAsync(ct);
        var over = doc.Lines.Select(l => (l.ItemId, l.Quantity,
                Left: moved.Where(m => m.ItemId == l.ItemId && m.Kind == StockOperationKind.Receipt).Sum(m => m.Quantity)
                      - moved.Where(m => m.ItemId == l.ItemId && m.Kind == StockOperationKind.ReturnToSupplier).Sum(m => m.Quantity)))
            .Where(x => x.Quantity > x.Left).ToList();
        if (over.Count > 0)
        {
            var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
            throw new BusinessRuleException("purchase.return.exceeds",
                "Возврат больше полученного по заказу — " + string.Join("; ", over.Take(10).Select(x =>
                    $"{codes[x.ItemId]}: возвращается {Quantities.Format(x.Quantity)}, получено {Quantities.Format(x.Left)}")) + ".");
        }
    }

    /// <summary>Возврат от покупателя по заказу — не больше отгруженного по этому заказу за вычетом прежних возвратов (D65).</summary>
    private async Task EnsureReturnWithinShippedAsync(AccessContext ctx, StockDocument doc, long orderId, CancellationToken ct)
    {
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == orderId && d.Status == StockDocumentStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { d.Kind, l.ItemId, l.Quantity }))
            .Where(x => itemIds.Contains(x.ItemId))
            .ToListAsync(ct);
        var over = doc.Lines.Select(l => (l.ItemId, l.Quantity,
                Left: moved.Where(m => m.ItemId == l.ItemId && m.Kind == StockOperationKind.Shipment).Sum(m => m.Quantity)
                      - moved.Where(m => m.ItemId == l.ItemId && m.Kind == StockOperationKind.CustomerReturn).Sum(m => m.Quantity)))
            .Where(x => x.Quantity > x.Left).ToList();
        if (over.Count > 0)
        {
            var codes = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
            throw new BusinessRuleException("sales.return.exceeds",
                "Возврат больше отгруженного по заказу — " + string.Join("; ", over.Take(10).Select(x =>
                    $"{codes[x.ItemId]}: возвращается {Quantities.Format(x.Quantity)}, отгружено {Quantities.Format(x.Left)}")) + ".");
        }
    }

    /// <summary>Склады, поставщик и причина — своей организации и действующие; склад документа — в области пользователя.</summary>
    private async Task ValidateHeaderAsync(AccessContext ctx, StockOperationKind kind, StockDocumentHeader h, CancellationToken ct)
    {
        await RequireWarehouseAsync(ctx, h.WarehouseId, inScope: true, ct);
        if (h.TargetWarehouseId is { } target)
        {
            await RequireWarehouseAsync(ctx, target, inScope: false, ct);
        }

        if (h.CounterpartyId is { } cid)
        {
            var c = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Контрагент");
            if (c.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Контрагент «{c.Name}» в архиве.");
            }

            if (StockDocument.WithCustomer(kind) && !c.IsCustomer)
            {
                throw new BusinessRuleException("stock.document.not_customer", $"«{c.Name}» не отмечен как покупатель.");
            }

            if (StockDocument.WithSupplier(kind) && !c.IsSupplier)
            {
                throw new BusinessRuleException("stock.document.not_supplier", $"«{c.Name}» не отмечен как поставщик.");
            }
        }

        if (h.PurchaseOrderId is { } orderId)
        {
            var order = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ поставщику");
            var allowed = kind == StockOperationKind.Receipt
                ? order.Status == KnitErp.Domain.Purchasing.PurchaseOrderStatus.Confirmed
                : order.Status is KnitErp.Domain.Purchasing.PurchaseOrderStatus.Confirmed or KnitErp.Domain.Purchasing.PurchaseOrderStatus.Closed;
            if (!allowed)
            {
                throw new BusinessRuleException("purchase.order.not_confirmed", $"Заказ {order.Number} не подтверждён или закрыт.");
            }

            if (h.CounterpartyId != order.SupplierId)
            {
                throw new BusinessRuleException("purchase.order.supplier", $"Поставщик документа не совпадает с поставщиком заказа {order.Number}.");
            }
        }

        if (h.SalesOrderId is { } salesId)
        {
            var order = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == salesId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            var allowed = kind == StockOperationKind.Shipment
                ? order.Status == KnitErp.Domain.Sales.SalesOrderStatus.Confirmed
                : order.Status is KnitErp.Domain.Sales.SalesOrderStatus.Confirmed or KnitErp.Domain.Sales.SalesOrderStatus.Closed;
            if (!allowed)
            {
                throw new BusinessRuleException("sales.order.not_confirmed", $"Заказ {order.Number} не подтверждён или закрыт.");
            }

            if (h.CounterpartyId != order.CustomerId)
            {
                throw new BusinessRuleException("sales.order.customer", $"Покупатель документа не совпадает с покупателем заказа {order.Number}.");
            }
        }

        if (h.ReasonId is { } rid)
        {
            var r = await db.OperationReasons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rid && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Причина");
            if (r.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Причина «{r.Name}» в архиве.");
            }

            if (r.Kind != kind)
            {
                throw new BusinessRuleException("stock.document.reason_kind", $"Причина «{r.Name}» относится к другому виду операций.");
            }
        }
    }

    private async Task RequireWarehouseAsync(AccessContext ctx, long warehouseId, bool inScope, CancellationToken ct)
    {
        var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == warehouseId && x.OrganizationId == ctx.OrganizationId, ct);
        if (w is null || (inScope && !WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, warehouseId)
                                  && !WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, warehouseId)))
        {
            throw new NotFoundException("Склад");
        }

        if (w.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Склад «{w.Name}» в архиве.");
        }
    }

    private async Task<string> DescribeHeaderAsync(long warehouseId, long? targetId, long? counterpartyId, long? reasonId, DateOnly date, CancellationToken ct)
    {
        var names = await db.Warehouses.AsNoTracking().Where(w => w.Id == warehouseId || w.Id == targetId).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var parts = new List<string> { date.ToString("dd.MM.yyyy"), names[warehouseId] };
        if (targetId is { } t)
        {
            parts.Add($"→ {names[t]}");
        }

        if (counterpartyId is { } c)
        {
            parts.Add(await db.Counterparties.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).SingleAsync(ct));
        }

        if (reasonId is { } r)
        {
            parts.Add(await db.OperationReasons.AsNoTracking().Where(x => x.Id == r).Select(x => x.Name).SingleAsync(ct));
        }

        return string.Join(", ", parts);
    }

    private static StockDocumentHeader Header(StockDocument d) =>
        new(d.WarehouseId, d.TargetWarehouseId, d.CounterpartyId, d.ReasonId, d.DocumentDate, d.Comment, d.PurchaseOrderId, d.SalesOrderId);

    private async Task<AccessContext> DemandAnyAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ViewPermissions.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseDocumentCreate, ct);
        }

        return ctx;
    }

    private async Task<(AccessContext, StockDocument)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        return (ctx, await LoadAsync(ctx, id, Permissions.WarehouseDocumentCreate, rowVersion, ct));
    }

    /// <summary>Документ своей организации и области склада-отправителя; иначе «не найдено».</summary>
    private async Task<StockDocument> LoadAsync(AccessContext ctx, long id, string permission, byte[] rowVersion, CancellationToken ct)
    {
        var doc = await db.StockDocuments.Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!WarehouseScope.Covers(ctx, permission, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        return doc.EnsureVersion(doc.RowVersion, rowVersion);
    }

    private void Audit(AccessContext ctx, string action, StockDocument doc, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(StockDocument), doc.Id.ToString(),
            before, after, reason is null ? doc.Number : $"{doc.Number}: {reason}", currentUser.CorrelationId));
}
