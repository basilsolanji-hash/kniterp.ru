using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Сменное задание с фактом: план — норма на смену, факт — отметки сотрудника с терминала за эту дату.</summary>
public sealed record ShiftTaskDto(
    long Id, DateOnly Date, long EmployeeId, string EmployeeName, string PersonnelNumber, long OrderId, string OrderNumber, string ItemName,
    ProductionStage Stage, decimal Planned, decimal Fact, string UnitSymbol, string? Comment, string? ShortfallReason, string? ShortfallComment,
    bool IsCancelled, byte[] RowVersion)
{
    public string StageName => ProductionFlow.StageName(Stage);
    public int Percent => ShiftTask.Percent(Planned, Fact);
    public bool Done => Fact >= Planned;
}

public sealed record ShiftEmployeeOptionDto(long Id, string Name, string PersonnelNumber);

public sealed record ShiftOrderOptionDto(long Id, string Number, string ItemName);

/// <summary>Сменные задания на дату для мастера: задания с фактом, кого и на какие заказы можно назначить.</summary>
public sealed record ShiftBoardDto(
    DateOnly Date, DateOnly Today, IReadOnlyList<ShiftTaskDto> Tasks, bool CanAssign,
    IReadOnlyList<ShiftEmployeeOptionDto> Employees, IReadOnlyList<ShiftOrderOptionDto> Orders);

/// <summary>Сколько заданий скопировано и сколько пропущено (сотрудник не работает, заказ не в работе, уже есть).</summary>
public sealed record ShiftCopyResult(int Created, int Skipped);

public sealed record ShiftTaskCommand(DateOnly Date, long EmployeeId, long OrderId, ProductionStage Stage, decimal Planned, string? Comment);

/// <summary>
/// Сменные задания (D92, дополнение): мастер или начальник цеха (право изменения заказов) назначает работающему сотруднику
/// этап запущенного заказа и количество на сегодня или будущую дату; смотреть — с правом просмотра заказов. Сотрудник видит свои задания
/// на терминале и указывает причину, если сделал меньше плана. Задания не удаляются — отменяются.
/// </summary>
public sealed class ShiftTaskService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<ShiftBoardDto> BoardAsync(DateOnly? date = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var today = await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        var day = date ?? today;
        var tasks = await ListAsync(db, ctx.OrganizationId, day, null, ct);
        var canAssign = ctx.Permissions.Has(Permissions.ProductionOrderEdit);
        IReadOnlyList<ShiftEmployeeOptionDto> employees = [];
        IReadOnlyList<ShiftOrderOptionDto> orders = [];
        if (canAssign)
        {
            employees = (await db.Employees.AsNoTracking()
                    .Where(e => e.OrganizationId == ctx.OrganizationId && e.Status == EmploymentStatus.Working)
                    .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
                    .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct))
                .Select(e => new ShiftEmployeeOptionDto(e.Id, ShopFloorService.ShortName(e.LastName, e.FirstName, e.MiddleName), e.PersonnelNumber)).ToList();
            orders = await (from p in db.ProductionOrders.AsNoTracking()
                            where p.OrganizationId == ctx.OrganizationId
                                  && (p.Status == ProductionOrderStatus.Released || p.Status == ProductionOrderStatus.PartiallyDone)
                            join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                            orderby p.DueDate, p.Id
                            select new ShiftOrderOptionDto(p.Id, p.Number, i.Name)).Take(ShopFloorService.MaxTaskOrders).ToListAsync(ct);
        }

        return new ShiftBoardDto(day, today, tasks, canAssign, employees, orders);
    }

    public async Task<long> AssignAsync(ShiftTaskCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var today = await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        if (cmd.Date < today)
        {
            throw new BusinessRuleException("production.shift.past", "Задание назначается на сегодня или на будущую дату.");
        }

        var employee = await db.Employees.AsNoTracking()
                           .Where(e => e.Id == cmd.EmployeeId && e.OrganizationId == ctx.OrganizationId)
                           .Select(e => new { e.Status, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).SingleOrDefaultAsync(ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (employee.Status != EmploymentStatus.Working)
        {
            throw new BusinessRuleException("production.shop.not_working", "Сотрудник не работает (отпуск или увольнение) — отмечать работу нельзя.");
        }

        var order = await db.ProductionOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == cmd.OrderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        order.EnsureInWork();
        if (!order.RouteValue.Contains(cmd.Stage))
        {
            throw new BusinessRuleException("production.stage.not_in_route",
                $"Этапа «{ProductionFlow.StageName(cmd.Stage)}» нет в маршруте изделия: {order.RouteValue.Describe()}.");
        }

        var unit = await db.Items.AsNoTracking().Where(i => i.Id == order.ItemId)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision }).SingleAsync(ct);
        StockEntry.EnsurePrecision(new EntryItemDto(unit.Id, unit.Code, unit.Name, unit.Symbol, unit.Precision), cmd.Planned);

        // Назначения и копирование заданий на одну дату — по очереди: проверка дубля и вставка под общей блокировкой даты.
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(DateLock(ctx.OrganizationId, cmd.Date), ct);
        if (await db.ShiftTasks.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.Date == cmd.Date && t.EmployeeId == cmd.EmployeeId
                                              && t.ProductionOrderId == cmd.OrderId && t.Stage == cmd.Stage && t.CancelledAtUtc == null, ct))
        {
            throw new BusinessRuleException("production.shift.duplicate",
                "У сотрудника уже есть задание на этот заказ и этап на эту дату — отмените его, чтобы назначить другое количество.");
        }

        var task = ShiftTask.Create(ctx.OrganizationId, cmd.Date, cmd.EmployeeId, cmd.OrderId, cmd.Stage, cmd.Planned, cmd.Comment, ctx.UserId, clock.UtcNow);
        db.ShiftTasks.Add(task);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ProductionShiftTaskAssigned, task, null,
            $"{cmd.Date:dd.MM.yyyy}, {ShopFloorService.ShortName(employee.LastName, employee.FirstName, employee.MiddleName)} (таб. № {employee.PersonnelNumber}): "
            + $"{order.Number}, {ProductionFlow.StageName(cmd.Stage)} — {Quantities.Format(cmd.Planned)} {unit.Symbol}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return task.Id;
    }

    /// <summary>
    /// Повторить задания прошлой смены (D408): действующие задания даты <paramref name="from"/> переносятся на <paramref name="to"/>
    /// с тем же количеством и комментарием. Пропускаются сотрудники, которые не работают, заказы не в работе, этапы вне маршрута
    /// и задания, которые на новую дату уже есть. Всё — одной транзакцией под блокировкой организации и даты.
    /// </summary>
    public async Task<ShiftCopyResult> CopyAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var today = await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        if (to < today)
        {
            throw new BusinessRuleException("production.shift.past", "Задание назначается на сегодня или на будущую дату.");
        }

        if (from == to)
        {
            throw new BusinessRuleException("production.shift.copy_same", "Выберите другую дату, с которой копировать задания.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(DateLock(ctx.OrganizationId, to), ct);
        var source = await db.ShiftTasks.AsNoTracking()
            .Where(t => t.OrganizationId == ctx.OrganizationId && t.Date == from && t.CancelledAtUtc == null).OrderBy(t => t.Id).ToListAsync(ct);
        var existing = (await db.ShiftTasks.AsNoTracking()
                .Where(t => t.OrganizationId == ctx.OrganizationId && t.Date == to && t.CancelledAtUtc == null)
                .Select(t => new { t.EmployeeId, t.ProductionOrderId, t.Stage }).ToListAsync(ct))
            .Select(t => (t.EmployeeId, t.ProductionOrderId, t.Stage)).ToHashSet();
        var employeeIds = source.Select(t => t.EmployeeId).Distinct().ToList();
        var working = (await db.Employees.AsNoTracking()
                .Where(e => e.OrganizationId == ctx.OrganizationId && employeeIds.Contains(e.Id) && e.Status == EmploymentStatus.Working)
                .Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var orderIds = source.Select(t => t.ProductionOrderId).Distinct().ToList();
        var routes = await db.ProductionOrders.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && orderIds.Contains(o.Id)
                        && (o.Status == ProductionOrderStatus.Released || o.Status == ProductionOrderStatus.PartiallyDone))
            .ToDictionaryAsync(o => o.Id, o => o.Route, ct);

        var created = new List<ShiftTask>();
        var skipped = 0;
        foreach (var t in source)
        {
            if (!working.Contains(t.EmployeeId) || !routes.TryGetValue(t.ProductionOrderId, out var route)
                || !ProductionRoute.From(route).Contains(t.Stage) || !existing.Add((t.EmployeeId, t.ProductionOrderId, t.Stage)))
            {
                skipped++;
                continue;
            }

            created.Add(ShiftTask.Create(ctx.OrganizationId, to, t.EmployeeId, t.ProductionOrderId, t.Stage, t.PlannedQuantity, t.Comment, ctx.UserId, clock.UtcNow));
        }

        db.ShiftTasks.AddRange(created);
        await db.SaveChangesAsync(ct);
        foreach (var task in created)
        {
            Audit(ctx, AuditActions.ProductionShiftTaskAssigned, task, null, $"{to:dd.MM.yyyy}: копия задания смены {from:dd.MM.yyyy}");
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ShiftCopyResult(created.Count, skipped);
    }

    public async Task CancelAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var task = await db.ShiftTasks.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Сменное задание");
        task.EnsureVersion(task.RowVersion, rowVersion);
        task.Cancel(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.ProductionShiftTaskCancelled, task, "Действует", "Отменено");
        await SaveOrDiscardAsync(ct);
    }

    /// <summary>Причина невыполнения — сам сотрудник по своему заданию на сегодня или прошедшую дату.</summary>
    public async Task ReportShortfallAsync(long id, string reason, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        var employeeId = await ShopFloorService.EmployeeIdAsync(db, ctx, ct);
        var task = await db.ShiftTasks.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId && t.EmployeeId == employeeId, ct)
                   ?? throw new NotFoundException("Сменное задание");
        if (task.Date > await ShopFloorService.TodayAsync(db, ctx, clock, ct))
        {
            throw new BusinessRuleException("production.shift.future", "Причину указывают в день задания или позже.");
        }

        task.EnsureVersion(task.RowVersion, rowVersion);
        task.ReportShortfall(reason, comment, clock.UtcNow);
        Audit(ctx, AuditActions.ProductionShiftShortfall, task, null, task.ShortfallComment is null ? reason : $"{reason}. {task.ShortfallComment}");
        await SaveOrDiscardAsync(ct);
    }

    /// <summary>Задания организации на дату (по сотруднику — если указан) с фактом по отметкам терминала.</summary>
    internal static Task<IReadOnlyList<ShiftTaskDto>> ListAsync(IKnitErpDbContext db, long organizationId, DateOnly day, long? employeeId, CancellationToken ct) =>
        ListAsync(db, organizationId, day, day, employeeId, ct);

    /// <summary>Задания за период с фактом по каждому дню.</summary>
    internal static async Task<IReadOnlyList<ShiftTaskDto>> ListAsync(
        IKnitErpDbContext db, long organizationId, DateOnly from, DateOnly to, long? employeeId, CancellationToken ct)
    {
        var rows = await (from t in db.ShiftTasks.AsNoTracking()
                          where t.OrganizationId == organizationId && t.Date >= @from && t.Date <= to && (employeeId == null || t.EmployeeId == employeeId)
                          join e in db.Employees.AsNoTracking() on t.EmployeeId equals e.Id
                          join p in db.ProductionOrders.AsNoTracking() on t.ProductionOrderId equals p.Id
                          join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                          join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                          orderby t.Date, e.LastName, e.FirstName, t.Id
                          select new { Task = t, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber, p.Number, ItemName = i.Name, u.Symbol })
            .ToListAsync(ct);
        var employees = rows.Select(r => r.Task.EmployeeId).Distinct().ToList();
        var facts = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.OccurredOn >= from && o.OccurredOn <= to && o.EmployeeId != null && employees.Contains(o.EmployeeId.Value)
                        && (o.Kind == ProductionOperationKind.Done || o.Kind == ProductionOperationKind.Correction))
            .GroupBy(o => new { EmployeeId = o.EmployeeId!.Value, o.ProductionOrderId, o.Stage, o.OccurredOn })
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Quantity) })
            .ToListAsync(ct);
        var fact = facts.ToDictionary(f => (f.Key.EmployeeId, f.Key.ProductionOrderId, f.Key.Stage, f.Key.OccurredOn), f => f.Sum);
        return rows.Select(r => new ShiftTaskDto(r.Task.Id, r.Task.Date, r.Task.EmployeeId,
                ShopFloorService.ShortName(r.LastName, r.FirstName, r.MiddleName), r.PersonnelNumber, r.Task.ProductionOrderId, r.Number, r.ItemName,
                r.Task.Stage, r.Task.PlannedQuantity, fact.GetValueOrDefault((r.Task.EmployeeId, r.Task.ProductionOrderId, r.Task.Stage, r.Task.Date)), r.Symbol,
                r.Task.Comment, r.Task.ShortfallReason, r.Task.ShortfallComment, r.Task.IsCancelled, r.Task.RowVersion))
            .ToList();
    }

    private static string DateLock(long organizationId, DateOnly date) => $"kniterp.production.shift.{organizationId}.{date:yyyyMMdd}";

    /// <summary>Конфликт версий возвращается пользователю, а изменённая строка не остаётся в контексте вкладки.</summary>
    private async Task SaveOrDiscardAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private void Audit(AccessContext ctx, string action, ShiftTask task, string? before, string? after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(ShiftTask), task.Id.ToString(),
            before, after, null, currentUser.CorrelationId));
}
