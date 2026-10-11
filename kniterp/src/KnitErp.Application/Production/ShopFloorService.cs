using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Почему на терминале нельзя отметить работу.</summary>
public enum ShopHint : byte
{
    None = 0,
    NoEmployee = 1,
    NotWorking = 2,
    NotInWork = 3,
    NothingAvailable = 4,
}

/// <summary>Операция (этап заказа) на терминале цеха: что сделано, сколько можно отметить, сколько я сделал сегодня.</summary>
public sealed record ShopOperationDto(
    long OrderId, string OrderNumber, string ItemCode, string ItemName, ProductionStage Stage, string Code,
    decimal OrderQuantity, decimal Done, decimal Available, decimal MyToday, string UnitSymbol, byte Precision, DateOnly? DueDate,
    DateOnly Today, string? EmployeeName, bool CanMark, ShopHint Hint,
    IReadOnlyList<ProductionStage>? ReworkStages = null, IReadOnlyList<ShiftEmployeeOptionDto>? Culprits = null)
{
    public string StageName => ProductionFlow.StageName(Stage);

    /// <summary>Контроль качества: кроме годного — брак и переделка.</summary>
    public bool IsQualityControl => Stage == ProductionStage.QualityControl;
}

/// <summary>Отметка контроля качества с терминала: брак (причина, виновник) или переделка (этап возврата).</summary>
public sealed record ShopQualityCommand(ProductionOperationKind Kind, decimal Quantity, string? Reason, long? CulpritEmployeeId, ProductionStage? ReturnTo);

/// <summary>Отметка с терминала: сколько сделано, почему не всё (из списка), комментарий.</summary>
public sealed record ShopMarkCommand(decimal Quantity, string? ShortfallReason, string? Comment);

public sealed record ShopEntryDto(
    long Id, DateTime CreatedAtUtc, long OrderId, string OrderNumber, string ItemName, ProductionStage Stage, ProductionOperationKind Kind,
    decimal Quantity, string UnitSymbol, string? Reason, TimeOnly LocalTime = default, decimal? Remaining = null, bool CanCorrect = false)
{
    public string StageName => ProductionFlow.StageName(Stage);
    public string KindName => ProductionFlow.KindName(Kind);
}

/// <summary>Работа, которую можно взять: этап заказа, на который уже пришло с предыдущего этапа.</summary>
public sealed record ShopTaskDto(long OrderId, string OrderNumber, string ItemName, ProductionStage Stage, decimal Available, string UnitSymbol, DateOnly? DueDate)
{
    public string StageName => ProductionFlow.StageName(Stage);
}

/// <summary>Мой день на терминале: мои отметки за дату и доступная работа.</summary>
public sealed record ShopDayDto(
    DateOnly Date, DateOnly Today, string? EmployeeName, ShopHint Hint, IReadOnlyList<ShopEntryDto> Entries, IReadOnlyList<ShopTaskDto> Tasks,
    IReadOnlyList<ShiftTaskDto>? Shift = null);

/// <summary>
/// Терминал цеха (телефон рабочего, допущение D92, дополнение): по коду операции с маршрутного листа рабочий открывает этап заказа
/// и отмечает сделанное целиком или частично, с причиной и комментарием. Исполнитель — сотрудник, привязанный к учётной записи;
/// дата — сегодня по часовому поясу организации. Отметка идёт через те же правила потока и блокировку заказа, что и с карточки.
/// Право — «Производство: отметка этапов и контроль качества» (рабочему — отдельной выдачей); цены не показываются.
/// </summary>
public sealed class ShopFloorService(IKnitErpDbContext db, IAccessGuard guard, IClock clock, ProductionFloorService floor)
{
    /// <summary>Сколько открытых заказов показывать в «доступной работе».</summary>
    public const int MaxTaskOrders = 100;

    public const int CommentMaxLength = 300;

    public async Task<ShopOperationDto> OperationAsync(long orderId, ProductionStage stage, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        EnsureStage(stage);
        var order = await db.ProductionOrders.AsNoTracking()
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        if (order.Status != ProductionOrderStatus.Draft && !order.RouteValue.Contains(stage))
        {
            // Этапа нет в маршруте изделия (D401): QR-кода на него на маршрутном листе нет.
            throw new NotFoundException("Этап производства");
        }

        var item = await db.Items.AsNoTracking().Where(i => i.Id == order.ItemId)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Code, i.Name, u.Symbol, u.Precision }).SingleAsync(ct);
        var ops = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.ProductionOrderId == orderId).ToListAsync(ct);
        var flow = await floor.FlowAsync(order, ops, ct);
        var today = await TodayAsync(ctx, ct);
        var employee = await EmployeeAsync(ctx, ct);
        var inWork = order.Status is ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone;
        var available = inWork ? flow.Available(stage) : 0m;
        var mine = employee is null
            ? 0m
            : ops.Where(o => o.EmployeeId == employee.Id && o.Stage == stage && o.OccurredOn == today
                             && o.Kind is ProductionOperationKind.Done or ProductionOperationKind.Correction).Sum(o => o.Quantity);
        var hint = employee is null ? ShopHint.NoEmployee
            : employee.Status != EmploymentStatus.Working ? ShopHint.NotWorking
            : !inWork ? ShopHint.NotInWork
            : available <= 0 ? ShopHint.NothingAvailable
            : ShopHint.None;
        IReadOnlyList<ProductionStage>? rework = null;
        IReadOnlyList<ShiftEmployeeOptionDto>? culprits = null;
        if (stage == ProductionStage.QualityControl && hint == ShopHint.None)
        {
            rework = flow.Route.Stages.Where(s => s < ProductionStage.QualityControl).ToList();
            culprits = (await db.Employees.AsNoTracking()
                    .Where(e => e.OrganizationId == ctx.OrganizationId && e.Status == EmploymentStatus.Working)
                    .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
                    .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct))
                .Select(e => new ShiftEmployeeOptionDto(e.Id, ShortName(e.LastName, e.FirstName, e.MiddleName), e.PersonnelNumber)).ToList();
        }

        return new ShopOperationDto(order.Id, order.Number, item.Code, item.Name, stage, ShopFloorCode.Text(order.Id, stage),
            order.Quantity, flow.Done(stage), available, mine, item.Symbol, item.Precision, order.DueDate, today,
            employee?.ShortName, hint == ShopHint.None, hint, rework, culprits);
    }

    /// <summary>Отметка «сделано» от имени своего сотрудника. Частично — меньше доступного; причина — из списка.</summary>
    public async Task<long> MarkAsync(long orderId, ProductionStage stage, ShopMarkCommand command, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        EnsureStage(stage);
        var employee = await EmployeeAsync(ctx, ct)
                       ?? throw new BusinessRuleException("production.shop.no_employee",
                           "Учётная запись не привязана к сотруднику — обратитесь к кадровику или администратору.");
        if (employee.Status != EmploymentStatus.Working)
        {
            throw new BusinessRuleException("production.shop.not_working", "Сотрудник не работает (отпуск или увольнение) — отмечать работу нельзя.");
        }

        if (command.ShortfallReason is not null && !ShopFloorShortfall.Reasons.Contains(command.ShortfallReason))
        {
            throw new BusinessRuleException("production.shop.reason", "Выберите причину из списка.");
        }

        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
        if (comment is { Length: > CommentMaxLength })
        {
            throw new BusinessRuleException("production.shop.comment", "Комментарий — не длиннее 300 символов.");
        }

        var reason = command.ShortfallReason is null
            ? comment
            : $"Не выполнено полностью: {command.ShortfallReason}" + (comment is null ? "" : $". {comment}");
        var today = await TodayAsync(ctx, ct);
        return await floor.RecordCoreAsync(ctx, orderId,
            new ProductionOperationCommand(stage, ProductionOperationKind.Done, command.Quantity, null, reason, today), employee.Id, ct);
    }

    /// <summary>
    /// Брак или переделка на контроле качества с терминала (D407): исполнитель — контролёр (свой сотрудник), дата — сегодня;
    /// у брака причина обязательна, виновник — необязательно (показатели качества); у переделки — этап возврата из маршрута.
    /// Правила потока и блокировка — те же, что в карточке заказа.
    /// </summary>
    public async Task<long> MarkQualityAsync(long orderId, ShopQualityCommand command, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        if (command.Kind is not (ProductionOperationKind.Defect or ProductionOperationKind.Rework))
        {
            throw new BusinessRuleException("production.stage.kind", "Выберите вид отметки.");
        }

        var employee = await EmployeeAsync(ctx, ct)
                       ?? throw new BusinessRuleException("production.shop.no_employee",
                           "Учётная запись не привязана к сотруднику — обратитесь к кадровику или администратору.");
        if (employee.Status != EmploymentStatus.Working)
        {
            throw new BusinessRuleException("production.shop.not_working", "Сотрудник не работает (отпуск или увольнение) — отмечать работу нельзя.");
        }

        var today = await TodayAsync(ctx, ct);
        return await floor.RecordCoreAsync(ctx, orderId, new ProductionOperationCommand(ProductionStage.QualityControl, command.Kind, command.Quantity,
                command.Kind == ProductionOperationKind.Rework ? command.ReturnTo : null, command.Reason, today,
                command.Kind == ProductionOperationKind.Defect ? command.CulpritEmployeeId : null),
            employee.Id, ct);
    }

    /// <summary>Мои отметки за день (по умолчанию сегодня) и работа, доступная на этапах открытых заказов.</summary>
    public async Task<ShopDayDto> MyDayAsync(DateOnly? date = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        var zone = await ZoneAsync(ctx, ct);
        var today = DateOnly.FromDateTime(Local(clock.UtcNow, zone));
        var day = date ?? today;
        var employee = await EmployeeAsync(ctx, ct);

        var entries = new List<ShopEntryDto>();
        if (employee is not null)
        {
            var loaded = await (from o in db.ProductionOperations.AsNoTracking()
                                where o.OrganizationId == ctx.OrganizationId && o.EmployeeId == employee.Id && o.OccurredOn == day
                                join p in db.ProductionOrders.AsNoTracking() on o.ProductionOrderId equals p.Id
                                join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                                join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                                orderby o.Id descending
                                select new
                                {
                                    Entry = new ShopEntryDto(o.Id, o.CreatedAtUtc, p.Id, p.Number, i.Name, o.Stage, o.Kind, o.Quantity, u.Symbol, o.Reason),
                                    InWork = p.Status == ProductionOrderStatus.Released || p.Status == ProductionOrderStatus.PartiallyDone,
                                })
                .ToListAsync(ct);
            entries = await CorrectableAsync(loaded.Select(x => (x.Entry, x.InWork)).ToList(),
                day == today && employee.Status == EmploymentStatus.Working, zone, ct);
        }

        var orders = await (from p in db.ProductionOrders.AsNoTracking()
                            where p.OrganizationId == ctx.OrganizationId
                                  && (p.Status == ProductionOrderStatus.Released || p.Status == ProductionOrderStatus.PartiallyDone)
                            join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                            orderby p.DueDate, p.Id
                            select new { Order = p, i.Name, u.Symbol })
            .Take(MaxTaskOrders).ToListAsync(ct);
        var ids = orders.Select(o => o.Order.Id).ToList();
        var ops = (await db.ProductionOperations.AsNoTracking()
                .Where(o => o.OrganizationId == ctx.OrganizationId && ids.Contains(o.ProductionOrderId)).ToListAsync(ct))
            .ToLookup(o => o.ProductionOrderId);
        var released = await ReleasedAsync(ctx, ids, ct);
        var tasks = new List<ShopTaskDto>();
        foreach (var o in orders)
        {
            var flow = new ProductionFlow(o.Order.Quantity, ops[o.Order.Id].Select(x => x.Step), released.GetValueOrDefault(o.Order.Id), o.Order.RouteValue);
            tasks.AddRange(flow.Route.Stages.Where(s => flow.Available(s) > 0)
                .Select(s => new ShopTaskDto(o.Order.Id, o.Order.Number, o.Name, s, flow.Available(s), o.Symbol, o.Order.DueDate)));
        }

        var hint = employee is null ? ShopHint.NoEmployee : employee.Status != EmploymentStatus.Working ? ShopHint.NotWorking : ShopHint.None;
        var shift = employee is null
            ? []
            : (await ShiftTaskService.ListAsync(db, ctx.OrganizationId, day, employee.Id, ct)).Where(t => !t.IsCancelled).ToList();
        return new ShopDayDto(day, today, employee?.ShortName, hint, entries, tasks, shift);
    }

    /// <summary>Местное время, остаток каждой отметки «Сделано» после исправлений и можно ли её уменьшить сейчас.</summary>
    private async Task<List<ShopEntryDto>> CorrectableAsync(List<(ShopEntryDto Entry, bool InWork)> loaded, bool editable, TimeZoneInfo zone, CancellationToken ct)
    {
        var ids = loaded.Where(x => x.Entry.Kind == ProductionOperationKind.Done).Select(x => (long?)x.Entry.Id).ToList();
        var corrected = await db.ProductionOperations.AsNoTracking().Where(o => ids.Contains(o.CorrectsOperationId))
            .GroupBy(o => o.CorrectsOperationId!.Value).Select(g => new { g.Key, Sum = g.Sum(o => o.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return loaded.Select(x =>
        {
            decimal? remaining = x.Entry.Kind == ProductionOperationKind.Done ? x.Entry.Quantity + corrected.GetValueOrDefault(x.Entry.Id) : null;
            return x.Entry with
            {
                LocalTime = TimeOnly.FromDateTime(Local(x.Entry.CreatedAtUtc, zone)),
                Remaining = remaining,
                CanCorrect = editable && x.InWork && remaining > 0,
            };
        }).ToList();
    }

    /// <summary>Фамилия и инициалы: «Иванова А. П.».</summary>
    public static string ShortName(string lastName, string firstName, string? middleName) =>
        $"{lastName} {firstName[..1]}." + (string.IsNullOrEmpty(middleName) ? "" : $" {middleName[..1]}.");

    private static void EnsureStage(ProductionStage stage)
    {
        if (!ProductionFlow.Stages.Contains(stage))
        {
            throw new NotFoundException("Этап производства");
        }
    }

    private sealed record Performer(long Id, EmploymentStatus Status, string ShortName);

    private async Task<Performer?> EmployeeAsync(AccessContext ctx, CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking()
            .Where(x => x.OrganizationId == ctx.OrganizationId && x.UserId == ctx.UserId)
            .OrderBy(x => x.Status).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.LastName, x.FirstName, x.MiddleName })
            .FirstOrDefaultAsync(ct);
        return e is null ? null : new Performer(e.Id, e.Status, ShortName(e.LastName, e.FirstName, e.MiddleName));
    }

    private Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct) => TodayAsync(db, ctx, clock, ct);

    private Task<TimeZoneInfo> ZoneAsync(AccessContext ctx, CancellationToken ct) => ZoneAsync(db, ctx, ct);

    /// <summary>Сегодня по часовому поясу организации — дата отметок терминала и исправлений выработки.</summary>
    internal static async Task<DateOnly> TodayAsync(IKnitErpDbContext db, AccessContext ctx, IClock clock, CancellationToken ct) =>
        DateOnly.FromDateTime(Local(clock.UtcNow, await ZoneAsync(db, ctx, ct)));

    private static async Task<TimeZoneInfo> ZoneAsync(IKnitErpDbContext db, AccessContext ctx, CancellationToken ct) =>
        TimeZoneInfo.FindSystemTimeZoneById(
            await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct));

    /// <summary>Сотрудник, привязанный к учётной записи (сначала работающий).</summary>
    internal static async Task<long?> EmployeeIdAsync(IKnitErpDbContext db, AccessContext ctx, CancellationToken ct) =>
        await db.Employees.AsNoTracking()
            .Where(x => x.OrganizationId == ctx.OrganizationId && x.UserId == ctx.UserId)
            .OrderBy(x => x.Status).ThenByDescending(x => x.Id)
            .Select(x => (long?)x.Id).FirstOrDefaultAsync(ct);

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    private async Task<Dictionary<long, decimal>> ReleasedAsync(AccessContext ctx, List<long> ids, CancellationToken ct) =>
        await (from link in db.ProductionOrderDocuments.AsNoTracking()
               where link.OrganizationId == ctx.OrganizationId && ids.Contains(link.ProductionOrderId) && link.Purpose == ProductionDocumentPurpose.Output
               join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
               where d.Status == KnitErp.Domain.Warehousing.StockDocumentStatus.Posted
               join p in db.ProductionOrders.AsNoTracking() on link.ProductionOrderId equals p.Id
               from l in d.Lines
               where l.ItemId == p.ItemId
               group l.Quantity by link.ProductionOrderId into g
               select new { g.Key, Sum = g.Sum() }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
}
