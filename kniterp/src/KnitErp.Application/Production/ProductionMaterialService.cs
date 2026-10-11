using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Материал заказа в балансе; Warning — отклонение расхода от нормы больше допуска или остаток ушёл в минус.</summary>
public sealed record MaterialBalanceDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Planned, decimal Issued, decimal Consumed, decimal Waste,
    decimal Returned, decimal Remaining, decimal? Normative, decimal? DeviationPercent, bool Warning);

public sealed record ProductionDocumentDto(long Id, string Number, DateOnly DocumentDate, ProductionDocumentPurpose Purpose, StockDocumentStatus Status)
{
    public string PurposeName => ProductionOrderDocument.PurposeName(Purpose);
    public string StatusName => StockDocument.StatusName(Status);
}

/// <summary>Материалы заказа: баланс, предупреждения, документы и справочники формы движения.</summary>
public sealed record ProductionMaterialsDto(
    long OrderId, ProductionOrderStatus Status, long WipWarehouseId, string WipWarehouseName, IReadOnlyList<MaterialBalanceDto> Lines,
    IReadOnlyList<ProductionDocumentDto> Documents, IReadOnlyList<string> Warnings, decimal TolerancePercent, bool CanMove,
    IReadOnlyList<LookupWarehouseDto> Warehouses, IReadOnlyList<ReasonLookupDto> WasteReasons);

public sealed record MaterialLineCommand(long ItemId, decimal Quantity);

/// <summary>
/// Движение материалов по заказу: выдача (со склада <c>WarehouseId</c> в цех), возврат (из цеха на склад <c>WarehouseId</c>)
/// или отходы (списание из цеха с причиной <c>ReasonId</c>).
/// </summary>
public sealed record MaterialMoveCommand(
    ProductionDocumentPurpose Purpose, long? WarehouseId, long? ReasonId, string? Comment, DateOnly DocumentDate, IReadOnlyList<MaterialLineCommand> Lines);

/// <summary>
/// Материалы производственного заказа (D93, D94). Каждое движение — настоящий складской документ, проведённый в той же транзакции:
/// выдача в цех — перемещение на склад «Цех (НЗП)», возврат остатка — обратное перемещение, отходы — списание из цеха с причиной.
/// Возврат и отходы — не больше остатка материала этого заказа в цехе; все движения заказа идут под блокировкой заказа,
/// поэтому двойного списания нет. Баланс считается по проведённым документам: выдано = израсходовано + отходы + возвращено + остаток.
/// Право — «Производство: выдача, возврат и списание материалов, выпуск» в области склада и права складских документов на склад-отправитель.
/// </summary>
public sealed class ProductionMaterialService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StockDocumentService documents)
{
    public const string IssueReasonName = "Выдача в производство";
    public const string ReturnReasonName = "Возврат из производства";
    public const string WasteReasonName = "Отходы производства";

    public async Task<ProductionMaterialsDto> MaterialsAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials)
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        var balance = await BalanceAsync(order, ct);
        var ids = balance.Select(b => b.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        var lines = balance.Select(b => new MaterialBalanceDto(b.ItemId, items[b.ItemId].Code, items[b.ItemId].Name, items[b.ItemId].Symbol,
                items[b.ItemId].Precision, b.Planned, b.Issued, b.Consumed, b.Waste, b.Returned, b.Remaining, b.Normative, b.DeviationPercent,
                b.OverTolerance(MaterialBalance.TolerancePercent) || b.Remaining < 0))
            .OrderBy(l => l.Code).ToList();
        var warnings = new List<string>();
        foreach (var l in lines)
        {
            if (l.Remaining < 0)
            {
                warnings.Add($"{l.Code}: расхождение — израсходовано, списано и возвращено больше выданного на {Quantities.Format(-l.Remaining)} {l.UnitSymbol} (сторнирован документ выдачи?).");
            }

            if (l.DeviationPercent is { } d && Math.Abs(d) > MaterialBalance.TolerancePercent)
            {
                warnings.Add($"{l.Code}: расход с отходами {(d > 0 ? "больше" : "меньше")} нормы на {Math.Abs(d):0.##}% — допуск {MaterialBalance.TolerancePercent:0.##}%.");
            }
        }

        var docs = await (from link in db.ProductionOrderDocuments.AsNoTracking()
                          where link.OrganizationId == ctx.OrganizationId && link.ProductionOrderId == orderId
                          join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
                          orderby d.DocumentDate descending, d.Id descending
                          select new ProductionDocumentDto(d.Id, d.Number, d.DocumentDate, link.Purpose, d.Status)).ToListAsync(ct);
        var wipName = await db.Warehouses.AsNoTracking().Where(w => w.Id == order.WipWarehouseId).Select(w => w.Name).SingleAsync(ct);
        var canMove = order.Status is ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone
                      && ctx.Permissions.Has(Permissions.ProductionMaterialMove);
        IReadOnlyList<LookupWarehouseDto> warehouses = [];
        IReadOnlyList<ReasonLookupDto> reasons = [];
        if (canMove)
        {
            warehouses = await db.Warehouses.AsNoTracking()
                .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && w.Id != order.WipWarehouseId)
                .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
            reasons = await db.OperationReasons.AsNoTracking()
                .Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Kind == StockOperationKind.WriteOff)
                .OrderBy(r => r.Name).Select(r => new ReasonLookupDto(r.Id, r.Name, r.RequiresComment)).ToListAsync(ct);
        }

        return new ProductionMaterialsDto(order.Id, order.Status, order.WipWarehouseId, wipName, lines, docs, warnings,
            MaterialBalance.TolerancePercent, canMove, warehouses, reasons);
    }

    /// <summary>Выдача, возврат или отходы: проверки, складской документ, проведение и связь с заказом — в одной транзакции.</summary>
    public async Task<long> MoveAsync(long orderId, MaterialMoveCommand command, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionMaterialMove, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var order = await ProductionLocks.LockOrderAsync(db, ctx, orderId, ct);
            order.EnsureInWork();
            var lines = await CheckLinesAsync(ctx, order, command.Lines, ct);
            var codes = await CodesAsync(lines.Select(l => l.ItemId), ct);
            var (kind, header) = command.Purpose switch
            {
                ProductionDocumentPurpose.Issue => (StockOperationKind.Transfer, new StockDocumentHeader(
                    RequireWarehouse(command.WarehouseId, order), order.WipWarehouseId, null,
                    await ProductionReasons.FindOrCreateAsync(db, ctx, currentUser, clock, StockOperationKind.Transfer, IssueReasonName, ct),
                    command.DocumentDate, Comment(order, command.Comment))),
                ProductionDocumentPurpose.Return => (StockOperationKind.Transfer, new StockDocumentHeader(
                    order.WipWarehouseId, RequireWarehouse(command.WarehouseId, order), null,
                    await ProductionReasons.FindOrCreateAsync(db, ctx, currentUser, clock, StockOperationKind.Transfer, ReturnReasonName, ct),
                    command.DocumentDate, Comment(order, command.Comment))),
                ProductionDocumentPurpose.Waste => (StockOperationKind.WriteOff, new StockDocumentHeader(
                    order.WipWarehouseId, null, null, command.ReasonId ?? throw new BusinessRuleException("production.material.reason_required", "Укажите причину списания отходов."),
                    command.DocumentDate, Comment(order, command.Comment))),
                _ => throw new BusinessRuleException("production.material.purpose", "Выберите операцию: выдача, возврат или отходы."),
            };

            // Область права производства — по складу, с которого уходит материал; права складских документов проверит складской сервис.
            if (!WarehouseScope.Covers(ctx, Permissions.ProductionMaterialMove, header.WarehouseId))
            {
                throw new NotFoundException("Склад");
            }

            // Номер заказа пишется в комментарий сам, поэтому пояснение причины («Брак», «Порча») проверяется по тексту пользователя.
            if (command.Purpose == ProductionDocumentPurpose.Waste && string.IsNullOrWhiteSpace(command.Comment)
                && await db.OperationReasons.AsNoTracking().AnyAsync(r => r.Id == header.ReasonId && r.OrganizationId == ctx.OrganizationId && r.RequiresComment, ct))
            {
                throw new BusinessRuleException("stock.document.comment_required", "Для этой причины нужно пояснение в комментарии.");
            }

            if (command.Purpose != ProductionDocumentPurpose.Issue)
            {
                MaterialBalance.EnsureWithinRemaining(await BalanceAsync(order, ct), lines, id => codes[id]);
            }

            var documentId = await ProductionPosting.PostAsync(db, documents, ctx, clock, order, kind, header, lines, command.Purpose, ct);
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ProductionMaterialMoved, nameof(ProductionOrder),
                order.Id.ToString(), null, $"{ProductionOrderDocument.PurposeName(command.Purpose)}: "
                                           + string.Join(", ", lines.Select(l => $"{codes[l.ItemId]} {Quantities.Format(l.Quantity)}")),
                order.Number, currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return documentId;
        }
        catch
        {
            db.DiscardChanges(); // отказ на любом шаге не оставляет черновик документа или связь в контексте вкладки
            throw;
        }
    }

    /// <summary>Баланс материалов заказа по проведённым документам.</summary>
    internal async Task<IReadOnlyList<MaterialBalanceLine>> BalanceAsync(ProductionOrder order, CancellationToken ct)
    {
        var moves = await ProductionPosting.MovementsAsync(db, order, ct);
        var released = await ProductionLocks.ReleasedAsync(db, order, ct);
        var defect = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == order.OrganizationId && o.ProductionOrderId == order.Id && o.Kind == ProductionOperationKind.Defect)
            .SumAsync(o => (decimal?)o.Quantity, ct) ?? 0m;
        return MaterialBalance.Calculate(order.Materials, order.Quantity, moves, released, defect);
    }

    /// <summary>Строки — материалы снимка норм заказа, положительные, с точностью единицы; одна позиция — одна строка.</summary>
    private async Task<IReadOnlyList<(long ItemId, decimal Quantity)>> CheckLinesAsync(
        AccessContext ctx, ProductionOrder order, IReadOnlyList<MaterialLineCommand> lines, CancellationToken ct)
    {
        var filled = lines.Where(l => l.Quantity != 0).ToList();
        if (filled.Count == 0)
        {
            throw new BusinessRuleException("production.material.empty", "Укажите количество хотя бы одного материала.");
        }

        if (filled.Select(l => l.ItemId).Distinct().Count() != filled.Count)
        {
            throw new BusinessRuleException("stock.document.duplicate_item", "Позиция повторяется в документе.");
        }

        var ids = filled.Select(l => l.ItemId).ToList();
        if (ids.Any(id => order.Materials.All(m => m.ItemId != id)))
        {
            throw new BusinessRuleException("production.material.not_in_order", "Материала нет в нормах заказа — выдаются только материалы техкарты.");
        }

        foreach (var l in filled)
        {
            Quantities.EnsurePositive(l.Quantity);
            var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, l.ItemId).SingleOrDefaultAsync(ct)
                       ?? throw new BusinessRuleException("catalog.archived", "Материал в архиве.");
            StockEntry.EnsurePrecision(item, l.Quantity);
        }

        return filled.Select(l => (l.ItemId, l.Quantity)).ToList();
    }

    private async Task<Dictionary<long, string>> CodesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => list.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
    }

    private static long RequireWarehouse(long? warehouseId, ProductionOrder order)
    {
        if (warehouseId is not { } id)
        {
            throw new BusinessRuleException("production.material.warehouse_required", "Выберите склад.");
        }

        if (id == order.WipWarehouseId)
        {
            throw new BusinessRuleException("production.material.same_warehouse", "Выберите склад, отличный от склада цеха.");
        }

        return id;
    }

    private static string Comment(ProductionOrder order, string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? $"Производственный заказ {order.Number}" : $"Производственный заказ {order.Number}: {comment.Trim()}";
}

/// <summary>Проведение складского документа по заказу и чтение движений заказа — общее для материалов и выпуска.</summary>
internal static class ProductionPosting
{
    /// <summary>Черновик со строками, проведение складским сервисом (его права, области, остатки и партии) и связь с заказом.</summary>
    public static async Task<long> PostAsync(
        IKnitErpDbContext db, StockDocumentService documents, AccessContext ctx, IClock clock, ProductionOrder order, StockOperationKind kind,
        StockDocumentHeader header, IReadOnlyList<(long ItemId, decimal Quantity)> lines, ProductionDocumentPurpose purpose, CancellationToken ct)
    {
        var id = await documents.CreateWithLinesAsync(kind, header, lines, ct);
        var version = await db.StockDocuments.AsNoTracking().Where(d => d.Id == id).Select(d => d.RowVersion).SingleAsync(ct);
        await documents.PostAsync(id, version, ct);
        db.ProductionOrderDocuments.Add(ProductionOrderDocument.Create(ctx.OrganizationId, order.Id, id, purpose, ctx.UserId, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Строки проведённых документов заказа (кроме выпуска — он про изделие, а не материалы).</summary>
    public static async Task<IReadOnlyList<MaterialMovement>> MovementsAsync(IKnitErpDbContext db, ProductionOrder order, CancellationToken ct) =>
        await (from link in db.ProductionOrderDocuments.AsNoTracking()
               where link.OrganizationId == order.OrganizationId && link.ProductionOrderId == order.Id && link.Purpose != ProductionDocumentPurpose.Output
               join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
               where d.Status == StockDocumentStatus.Posted
               from l in d.Lines
               select new MaterialMovement(l.ItemId, link.Purpose, l.Quantity)).ToListAsync(ct);
}

/// <summary>Служебные причины операций производства: находятся по названию или заводятся один раз (с записью в журнал).</summary>
internal static class ProductionReasons
{
    public static async Task<long> FindOrCreateAsync(
        IKnitErpDbContext db, AccessContext ctx, ICurrentUser user, IClock clock, StockOperationKind kind, string name, CancellationToken ct)
    {
        var id = await db.OperationReasons.AsNoTracking()
            .Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == kind && r.Name == name && !r.IsArchived)
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (id is { } found)
        {
            return found;
        }

        var reason = OperationReason.Create(ctx.OrganizationId, kind, name, requiresComment: false);
        db.OperationReasons.Add(reason);
        await db.SaveChangesAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogCreated, nameof(OperationReason),
            reason.Id.ToString(), null, $"{StockOperationKinds.Name(kind)}: {name}", "Причина для производства", user.CorrelationId));
        return reason.Id;
    }
}
