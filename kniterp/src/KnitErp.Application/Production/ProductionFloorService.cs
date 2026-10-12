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
    string CreatedBy, DateTime CreatedAtUtc, string? Employee = null, decimal? Remaining = null, bool CanCorrect = false, string? Culprit = null,
    decimal Repeat = 0)
{
    public string StageName => ProductionFlow.StageName(Stage);
    public string KindName => ProductionFlow.KindName(Kind);
    public string? ReturnToName => ReturnTo is { } r ? ProductionFlow.StageName(r) : null;
}

/// <summary>Ход работ по заказу: этапы, брак, переделка, готово к выпуску, история отметок.</summary>
public sealed record ProductionProgressDto(
    long OrderId, ProductionOrderStatus Status, decimal Quantity, string UnitSymbol, byte Precision,
    IReadOnlyList<ProductionStageDto> Stages, decimal Good, decimal Defect, decimal Rework, decimal ReadyForOutput, decimal Released,
    IReadOnlyList<ProductionOperationDto> History, bool CanMark, IReadOnlyList<ShiftEmployeeOptionDto>? Employees = null,
    IReadOnlyList<string>? DefectReasons = null, IReadOnlyList<string>? ReworkReasons = null);

/// <summary>Отметка из формы: этап, вид, количество (у исправления — на сколько уменьшить, положительным числом), этап возврата переделки.</summary>
public sealed record ProductionOperationCommand(
    ProductionStage Stage, ProductionOperationKind Kind, decimal Quantity, ProductionStage? ReturnTo, string? Reason, DateOnly OccurredOn,
    long? CulpritEmployeeId = null);

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
        var employeeIds = ops.Where(o => o.EmployeeId != null).Select(o => o.EmployeeId!.Value)
            .Concat(ops.Where(o => o.CulpritEmployeeId != null).Select(o => o.CulpritEmployeeId!.Value)).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName }).ToListAsync(ct);
        var employeeNames = employees.ToDictionary(e => e.Id, e => ShopFloorService.ShortName(e.LastName, e.FirstName, e.MiddleName));
        var correctable = inWorkStatus(order) && ctx.Permissions.Has(Permissions.ProductionOrderEdit);
        var history = ops.OrderByDescending(o => o.Id)
            .Select(o =>
            {
                decimal? remaining = o.Kind == ProductionOperationKind.Done && o.EmployeeId is not null ? Remaining(o, ops) : null;
                return new ProductionOperationDto(o.Id, o.OccurredOn, o.Stage, o.Kind, o.Quantity, o.ReturnToStage, o.Reason,
                    users.GetValueOrDefault(o.CreatedByUserId) ?? "—", o.CreatedAtUtc,
                    o.EmployeeId is { } e ? employeeNames.GetValueOrDefault(e) : null, remaining, correctable && remaining > 0,
                    o.CulpritEmployeeId is { } c ? employeeNames.GetValueOrDefault(c) : null, o.RepeatQuantity);
            })
            .ToList();
        var inWork = inWorkStatus(order);
        return new ProductionProgressDto(order.Id, order.Status, order.Quantity, unit.Symbol, unit.Precision,
            flow.Route.Stages.Select(s => new ProductionStageDto(s, flow.Done(s), inWork ? flow.Available(s) : 0)).ToList(),
            flow.Done(ProductionStage.QualityControl), flow.Defect, flow.Rework, flow.ReadyForOutput, flow.Released, history,
            inWork && ctx.Permissions.Has(Permissions.ProductionStageMark),
            inWork && ctx.Permissions.Has(Permissions.ProductionStageMark) && ctx.Permissions.Has(Permissions.ProductionDefectCulprit)
                ? await CulpritOptionsAsync(db, ctx.OrganizationId, ops, ct) : null,
            inWork ? await FrequentReasonsAsync(db, ctx.OrganizationId, ProductionOperationKind.Defect, clock.UtcNow, ct) : null,
            inWork ? await FrequentReasonsAsync(db, ctx.OrganizationId, ProductionOperationKind.Rework, clock.UtcNow, ct) : null);
    }

    /// <summary>Отметка на этапе. Проверка потока и запись — под блокировкой заказа в одной транзакции.</summary>
    public async Task<long> RecordAsync(long orderId, ProductionOperationCommand command, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        return await RecordCoreAsync(ctx, orderId, command, null, ct);
    }

    /// <summary>Отметка с исполнителем (терминал цеха); право уже проверено вызывающим сервисом.</summary>
    internal async Task<long> RecordCoreAsync(AccessContext ctx, long orderId, ProductionOperationCommand command, long? employeeId, CancellationToken ct,
        long? correctsOperationId = null)
    {
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
        if (correctsOperationId is { } corrected
            && Remaining(ops.Single(o => o.Id == corrected), ops) is var left && command.Quantity > left)
        {
            throw new BusinessRuleException("production.stage.correct_exceeds",
                $"Отметку можно уменьшить не больше чем на {Quantities.Format(left)} — столько от неё осталось.");
        }

        (await FlowAsync(order, ops, ct)).EnsureCanApply(step);
        if (command.CulpritEmployeeId is not null && !ctx.Permissions.Has(Permissions.ProductionDefectCulprit))
        {
            await guard.DenyAsync(ctx, Permissions.ProductionDefectCulprit, ct);
        }

        if (command.CulpritEmployeeId is { } culprit
            && !await db.Employees.AnyAsync(e => e.Id == culprit && e.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Сотрудник");
        }

        var repeat = ProductionRepeat.Part(step, ops, correctsOperationId is { } c ? ops.Single(o => o.Id == c) : null);
        var op = ProductionOperation.Create(ctx.OrganizationId, orderId, step, command.Reason, command.OccurredOn, ctx.UserId, clock.UtcNow, employeeId,
            correctsOperationId, command.CulpritEmployeeId, repeat);
        db.ProductionOperations.Add(op);
        await db.SaveChangesAsync(ct);
        var text = $"{ProductionFlow.StageName(step.Stage)}: {ProductionFlow.KindName(step.Kind)} {Quantities.Format(step.Quantity)} {item.Symbol}"
                   + (step.ReturnTo is { } r ? $" → {ProductionFlow.StageName(r)}" : "")
                   + (repeat != 0 ? $", в т. ч. повторно после переделки {Quantities.Format(repeat)}" : "");
        if (command.CulpritEmployeeId is { } guilty)
        {
            var g = await db.Employees.AsNoTracking().Where(x => x.Id == guilty)
                .Select(x => new { x.PersonnelNumber, x.LastName, x.FirstName, x.MiddleName }).SingleAsync(ct);
            text += $"; виновник {ShopFloorService.ShortName(g.LastName, g.FirstName, g.MiddleName)} (таб. № {g.PersonnelNumber})";
        }

        if (employeeId is { } performer)
        {
            var e = await db.Employees.AsNoTracking().Where(x => x.Id == performer)
                .Select(x => new { x.PersonnelNumber, x.LastName, x.FirstName, x.MiddleName }).SingleAsync(ct);
            text += $"; исполнитель {ShopFloorService.ShortName(e.LastName, e.FirstName, e.MiddleName)} (таб. № {e.PersonnelNumber})";
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ProductionStageMarked, nameof(ProductionOrder),
            order.Id.ToString(), null, text, op.Reason is null ? order.Number : $"{order.Number}: {op.Reason}", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return op.Id;
    }

    /// <summary>
    /// Исправление отметки рабочего с терминала (D92, дополнение): уменьшает её на указанное количество с причиной.
    /// Дата — сегодня по часовому поясу организации: выработка исполнителя уменьшается в текущем периоде, а расчёт за прошлые месяцы
    /// не меняется задним числом. Свою отметку рабочий исправляет только в день отметки; чужую — с правом изменения заказов (мастер, начальник цеха).
    /// </summary>
    public async Task<long> CorrectAsync(long orderId, long operationId, decimal quantity, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        var target = await db.ProductionOperations.AsNoTracking()
                         .SingleOrDefaultAsync(o => o.Id == operationId && o.OrganizationId == ctx.OrganizationId && o.ProductionOrderId == orderId, ct)
                     ?? throw new NotFoundException("Отметка");
        if (target.Kind != ProductionOperationKind.Done || target.EmployeeId is null)
        {
            throw new BusinessRuleException("production.stage.correct_target",
                "Так исправляется только отметка «Сделано» рабочего с терминала; остальные отметки — исправлением этапа.");
        }

        var today = await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        var self = await ShopFloorService.EmployeeIdAsync(db, ctx, ct) == target.EmployeeId;
        if (!self || target.OccurredOn != today)
        {
            await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        }

        return await RecordCoreAsync(ctx, orderId,
            new ProductionOperationCommand(target.Stage, ProductionOperationKind.Correction, quantity, null, reason, today), target.EmployeeId, ct, target.Id);
    }

    /// <summary>
    /// Работающие сотрудники для выбора виновника брака (D413): сначала те, кто отмечал работу по этому заказу, — обычно виновник среди них,
    /// и искать его в списке всей фабрики не нужно. Сам виновник не подставляется: он влияет на баллы и премии, выбирает контролёр.
    /// </summary>
    internal static async Task<IReadOnlyList<ShiftEmployeeOptionDto>> CulpritOptionsAsync(IKnitErpDbContext db, long organizationId,
        IEnumerable<ProductionOperation> orderOps, CancellationToken ct)
    {
        var worked = orderOps.Where(o => o.Kind == ProductionOperationKind.Done && o.EmployeeId is not null).Select(o => o.EmployeeId!.Value).ToHashSet();
        return (await db.Employees.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.Status == KnitErp.Domain.Structure.EmploymentStatus.Working)
                .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct))
            .OrderByDescending(e => worked.Contains(e.Id)).ThenBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new ShiftEmployeeOptionDto(e.Id, ShopFloorService.ShortName(e.LastName, e.FirstName, e.MiddleName), e.PersonnelNumber,
                worked.Contains(e.Id)))
            .ToList();
    }

    /// <summary>Сколько последних дней смотреть для подсказок причин и сколько подсказок показывать.</summary>
    public const int ReasonHintDays = 180;

    public const int ReasonHintCount = 8;

    /// <summary>
    /// Частые причины брака или переделки организации за полгода (D413): подсказки в поле причины — контролёр выбирает, а не печатает.
    /// Причина всё равно свободный текст; подсказки — только из записей своей организации.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> FrequentReasonsAsync(IKnitErpDbContext db, long organizationId, ProductionOperationKind kind,
        DateTime nowUtc, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(nowUtc).AddDays(-ReasonHintDays);
        return await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.Kind == kind && o.Reason != null && o.OccurredOn >= since)
            .GroupBy(o => o.Reason!)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key).Take(ReasonHintCount).ToListAsync(ct);
    }

    internal static decimal Remaining(ProductionOperation done, IEnumerable<ProductionOperation> ops) =>
        done.Quantity + ops.Where(o => o.CorrectsOperationId == done.Id).Sum(o => o.Quantity);

    private static bool inWorkStatus(ProductionOrder order) => order.Status is ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone;

    /// <summary>Поток заказа: отметки этапов и выпущенное количество.</summary>
    internal async Task<ProductionFlow> FlowAsync(ProductionOrder order, IEnumerable<ProductionOperation> ops, CancellationToken ct) =>
        new(order.Quantity, ops.Select(o => o.Step), await ProductionLocks.ReleasedAsync(db, order, ct), order.RouteValue);
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

/// <summary>
/// Отметки этапов многих заказов, сложенные в SQL по заказу, этапу, виду и этапу возврата. Поток этапов считает только суммы, поэтому
/// результат тот же, что по всем строкам, а из базы приходит не больше 5 × 4 × 3 строк на заказ — главный экран, терминал и отчёт
/// не растут вместе с историей отметок.
/// </summary>
internal static class ProductionSteps
{
    public static async Task<ILookup<long, ProductionStep>> SummedAsync(IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long> orderIds,
        CancellationToken ct)
    {
        if (orderIds.Count == 0)
        {
            return Array.Empty<ProductionStep>().ToLookup(_ => 0L);
        }

        var ids = orderIds.ToList();
        var rows = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && ids.Contains(o.ProductionOrderId))
            .GroupBy(o => new { o.ProductionOrderId, o.Stage, o.Kind, o.ReturnToStage })
            .Select(g => new { g.Key.ProductionOrderId, g.Key.Stage, g.Key.Kind, g.Key.ReturnToStage, Quantity = g.Sum(o => o.Quantity) })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.ProductionOrderId, r => new ProductionStep(r.Stage, r.Kind, r.Quantity, r.ReturnToStage));
    }
}

