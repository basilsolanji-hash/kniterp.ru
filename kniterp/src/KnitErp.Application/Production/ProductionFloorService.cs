using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Этап заказа: сколько сделано, сколько можно отметить сейчас.</summary>
public sealed record ProductionStageDto(ProductionStage Stage, decimal Done, decimal Available)
{
    public string Name => ProductionFlow.StageName(Stage);
}

public sealed record ProductionOperationDto(
    long Id, DateOnly OccurredOn, ProductionStage Stage, ProductionOperationKind Kind, decimal Quantity, ProductionStage? ReturnTo, string? Reason,
    string CreatedBy, DateTime CreatedAtUtc)
{
    public string StageName => ProductionFlow.StageName(Stage);
    public string KindName => ProductionFlow.KindName(Kind);
    public string? ReturnToName => ReturnTo is { } r ? ProductionFlow.StageName(r) : null;
}

/// <summary>Ход работ по заказу: этапы, брак, переделка, готово к выпуску, история отметок.</summary>
public sealed record ProductionProgressDto(
    long OrderId, ProductionOrderStatus Status, decimal Quantity, string UnitSymbol, byte Precision,
    IReadOnlyList<ProductionStageDto> Stages, decimal Good, decimal Defect, decimal Rework, decimal ReadyForOutput, decimal Released,
    IReadOnlyList<ProductionOperationDto> History, bool CanMark);

/// <summary>Отметка из формы: этап, вид, количество (у исправления — на сколько уменьшить, положительным числом), этап возврата переделки.</summary>
public sealed record ProductionOperationCommand(
    ProductionStage Stage, ProductionOperationKind Kind, decimal Quantity, ProductionStage? ReturnTo, string? Reason, DateOnly OccurredOn);

/// <summary>
/// Работа цеха по производственному заказу (D92): отметка этапов вязание → сборка → ВТО → контроль качества → упаковка,
/// годное, переделка и брак на контроле качества. Отметки — только у запущенного заказа и не больше пришедшего с предыдущего
/// этапа; все отметки заказа проверяются и пишутся под одной блокировкой заказа — одновременные отметки не превысят поток.
/// Право — «Производство: отметка этапов и контроль качества», смотреть — «Производство: просмотр заказов».
/// </summary>
public sealed class ProductionFloorService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<ProductionProgressDto> ProgressAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var order = await db.ProductionOrders.AsNoTracking()
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        var unit = await db.Items.AsNoTracking().Where(i => i.Id == order.ItemId)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { u.Symbol, u.Precision }).SingleAsync(ct);
        var ops = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.ProductionOrderId == orderId)
            .OrderBy(o => o.Id).ToListAsync(ct);
        var flow = await FlowAsync(order, ops, ct);
        var userIds = ops.Select(o => o.CreatedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var history = ops.OrderByDescending(o => o.Id)
            .Select(o => new ProductionOperationDto(o.Id, o.OccurredOn, o.Stage, o.Kind, o.Quantity, o.ReturnToStage, o.Reason,
                users.GetValueOrDefault(o.CreatedByUserId) ?? "—", o.CreatedAtUtc))
            .ToList();
        var inWork = order.Status is ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone;
        return new ProductionProgressDto(order.Id, order.Status, order.Quantity, unit.Symbol, unit.Precision,
            ProductionFlow.Stages.Select(s => new ProductionStageDto(s, flow.Done(s), inWork ? flow.Available(s) : 0)).ToList(),
            flow.Done(ProductionStage.QualityControl), flow.Defect, flow.Rework, flow.ReadyForOutput, flow.Released, history,
            inWork && ctx.Permissions.Has(Permissions.ProductionStageMark));
    }

    /// <summary>Отметка на этапе. Проверка потока и запись — под блокировкой заказа в одной транзакции.</summary>
    public async Task<long> RecordAsync(long orderId, ProductionOperationCommand command, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var order = await ProductionLocks.LockOrderAsync(db, ctx, orderId, ct);
        order.EnsureInWork();
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, command.OccurredOn, ct);
        var item = await db.Items.AsNoTracking().Where(i => i.Id == order.ItemId)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision }).SingleAsync(ct);
        Quantities.EnsurePositive(command.Quantity);
        StockEntry.EnsurePrecision(
            new EntryItemDto(item.Id, item.Code, item.Name, item.Symbol, item.Precision), command.Quantity);

        var step = new ProductionStep(command.Stage, command.Kind,
            command.Kind == ProductionOperationKind.Correction ? -command.Quantity : command.Quantity, command.ReturnTo);
        var ops = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.ProductionOrderId == orderId).ToListAsync(ct);
        (await FlowAsync(order, ops, ct)).EnsureCanApply(step);
        var op = ProductionOperation.Create(ctx.OrganizationId, orderId, step, command.Reason, command.OccurredOn, ctx.UserId, clock.UtcNow);
        db.ProductionOperations.Add(op);
        await db.SaveChangesAsync(ct);
        var text = $"{ProductionFlow.StageName(step.Stage)}: {ProductionFlow.KindName(step.Kind)} {Quantities.Format(step.Quantity)} {item.Symbol}"
                   + (step.ReturnTo is { } r ? $" → {ProductionFlow.StageName(r)}" : "");
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ProductionStageMarked, nameof(ProductionOrder),
            order.Id.ToString(), null, text, op.Reason is null ? order.Number : $"{order.Number}: {op.Reason}", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op.Id;
    }

    /// <summary>Поток заказа: отметки этапов и выпущенное количество.</summary>
    private async Task<ProductionFlow> FlowAsync(ProductionOrder order, IEnumerable<ProductionOperation> ops, CancellationToken ct) =>
        new(order.Quantity, ops.Select(o => o.Step), await ProductionLocks.ReleasedAsync(db, order, ct));
}

/// <summary>Общие для сервисов производства блокировка заказа и выпущенное количество.</summary>
internal static class ProductionLocks
{
    /// <summary>Заказ своей организации под блокировкой до конца транзакции: этапы, материалы и выпуск одного заказа идут по очереди.</summary>
    public static async Task<ProductionOrder> LockOrderAsync(IKnitErpDbContext db, AccessContext ctx, long orderId, CancellationToken ct)
    {
        var exists = await db.ProductionOrders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct);
        if (!exists)
        {
            throw new NotFoundException("Производственный заказ");
        }

        await db.LockAsync($"kniterp.production.order.{orderId}", ct);
        return await db.ProductionOrders.Include(o => o.Materials).SingleAsync(o => o.Id == orderId, ct);
    }

    /// <summary>Выпущено по заказу: изделие в проведённых документах выпуска (сторнированный выпуск не считается).</summary>
    public static async Task<decimal> ReleasedAsync(IKnitErpDbContext db, ProductionOrder order, CancellationToken ct) =>
        await (from link in db.ProductionOrderDocuments.AsNoTracking()
               where link.OrganizationId == order.OrganizationId && link.ProductionOrderId == order.Id && link.Purpose == ProductionDocumentPurpose.Output
               join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
               where d.Status == KnitErp.Domain.Warehousing.StockDocumentStatus.Posted
               from l in d.Lines
               where l.ItemId == order.ItemId
               select (decimal?)l.Quantity).SumAsync(ct) ?? 0m;
}
