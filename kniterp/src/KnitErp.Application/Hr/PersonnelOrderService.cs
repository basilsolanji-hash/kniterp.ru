using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

public sealed record PersonnelOrderRowDto(
    long Id, string Number, PersonnelOrderKind Kind, PersonnelOrderStatus Status, DateOnly OrderDate, DateOnly EffectiveFrom,
    DateOnly? EffectiveTo, long? EmployeeId, string EmployeeName, string? PersonnelNumber)
{
    public string KindName => PersonnelOrder.KindName(Kind);
    public string FormName => PersonnelOrder.FormName(Kind);
    public string StatusName => PersonnelOrder.StatusName(Status);
}

public sealed record PersonnelOrderDto(
    long Id, string Number, PersonnelOrderStatus Status, PersonnelOrderValues Values, string EmployeeName, string? CurrentDepartment,
    string? CurrentPosition, string? NewDepartment, string? NewPosition, bool CanEdit, bool CanPost, byte[] RowVersion)
{
    public string StatusName => PersonnelOrder.StatusName(Status);
}

public sealed record PersonnelOrderOptionsDto(
    IReadOnlyList<Structure.LookupDto> Employees, IReadOnlyList<Structure.LookupDto> Departments, IReadOnlyList<Structure.LookupDto> Positions,
    DateOnly Today);

/// <summary>Данные для печати приказа по форме Т-1, Т-5, Т-6 или Т-8.</summary>
public sealed record PersonnelOrderPrintDto(
    string Organization, string? Okpo, string Number, PersonnelOrderKind Kind, PersonnelOrderStatus Status, DateOnly OrderDate,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, string EmployeeName, string PersonnelNumber,
    string? FromDepartment, string? FromPosition, string? ToDepartment, string? ToPosition, decimal? Rate, decimal? Salary, byte? TrialMonths,
    LeaveKind? LeaveKind, int? LeaveDays, DismissalGround? Ground, DateOnly? HiredOn, string? Basis, string? DirectorName, string? DirectorPosition);

/// <summary>Строка истории назначений для карточки сотрудника. Оклад — только тем, кто видит приказы или заработок.</summary>
public sealed record EmploymentRecordDto(
    long? OrderId, string? OrderNumber, PersonnelOrderKind Kind, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Department, string Position,
    decimal Rate, decimal? Salary, string? Note)
{
    public string KindName => PersonnelOrder.KindName(Kind);
}

public sealed record PersonnelOrderFilter(PersonnelOrderKind? Kind = null, PersonnelOrderStatus? Status = null, long? EmployeeId = null);

/// <summary>
/// Кадровые приказы (D204): приём (Т-1), перевод (Т-5), отпуск (Т-6), увольнение (Т-8). Черновик готовит пользователь с правом
/// «Кадровые приказы: черновик», проводит — с правом проведения. Проведение в одной транзакции меняет карточку сотрудника и пишет
/// историю назначений; проведённый приказ не меняется (триггер в базе).
/// </summary>
public sealed class PersonnelOrderService(
    IKnitErpDbContext db, IAccessGuard guard, UserAccessService userAccess, ICurrentUser currentUser, IClock clock)
{
    public const string NumberKind = "hr-order";

    public async Task<IReadOnlyList<PersonnelOrderRowDto>> ListAsync(PersonnelOrderFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var q = db.PersonnelOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId);
        if (filter.Kind is { } kind)
        {
            q = q.Where(o => o.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            q = q.Where(o => o.Status == status);
        }

        if (filter.EmployeeId is { } emp)
        {
            q = q.Where(o => o.EmployeeId == emp);
        }

        return await (from o in q
                      join e in db.Employees.AsNoTracking() on o.EmployeeId equals e.Id into es
                      from e in es.DefaultIfEmpty()
                      orderby o.OrderDate descending, o.Id descending
                      select new PersonnelOrderRowDto(o.Id, o.Number, o.Kind, o.Status, o.OrderDate, o.EffectiveFrom, o.EffectiveTo, o.EmployeeId,
                          e == null
                              ? (o.MiddleName == null ? o.LastName + " " + o.FirstName : o.LastName + " " + o.FirstName + " " + o.MiddleName)
                              : (e.MiddleName == null ? e.LastName + " " + e.FirstName : e.LastName + " " + e.FirstName + " " + e.MiddleName),
                          e == null ? o.PersonnelNumber : e.PersonnelNumber))
            .Take(500).ToListAsync(ct);
    }

    /// <summary>
    /// История назначений сотрудника (D214): видна тем, кто видит карточку (область подразделений или своя). Ссылка на приказ — только
    /// с правом на приказы; оклад — с правом на приказы или заработок.
    /// </summary>
    public async Task<IReadOnlyList<EmploymentRecordDto>> HistoryAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (ctx.Permissions.Has(Permissions.EmployeeView))
        {
            var visible = await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
            if (visible is not null && !visible.Contains(employee.DepartmentId))
            {
                throw new NotFoundException("Сотрудник");
            }
        }
        else if (!(ctx.Permissions.HasOwnOnly(Permissions.EmployeeView) && employee.UserId == ctx.UserId))
        {
            throw new NotFoundException("Сотрудник");
        }

        var orders = ctx.Permissions.Has(Permissions.HrOrderCreate) || ctx.Permissions.Has(Permissions.HrOrderPost);
        var salary = orders || ctx.Permissions.Has(Permissions.PayrollView)
                     || (ctx.Permissions.HasOwnOnly(Permissions.PayrollView) && employee.UserId == ctx.UserId);
        var rows = await (from h in db.EmploymentHistory.AsNoTracking()
                          join d in db.Departments.AsNoTracking() on h.DepartmentId equals d.Id
                          join p in db.Positions.AsNoTracking() on h.PositionId equals p.Id
                          join o in db.PersonnelOrders.AsNoTracking() on h.OrderId equals o.Id into os
                          from o in os.DefaultIfEmpty()
                          where h.OrganizationId == ctx.OrganizationId && h.EmployeeId == employeeId
                          orderby h.EffectiveFrom, h.Id
                          select new { h, Department = d.Name, Position = p.Name, Number = o == null ? null : o.Number }).ToListAsync(ct);
        return rows.Select(r => new EmploymentRecordDto(orders ? r.h.OrderId : null, r.Number, r.h.Kind, r.h.EffectiveFrom, r.h.EffectiveTo, r.Department,
            r.Position, r.h.Rate, salary ? r.h.Salary : null, r.h.Note)).ToList();
    }

    public async Task<PersonnelOrderOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderCreate, ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.Status != EmploymentStatus.Dismissed)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new Structure.LookupDto(e.Id, e.PersonnelNumber + " " + e.LastName + " " + e.FirstName + (e.MiddleName == null ? "" : " " + e.MiddleName)))
            .ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId && !d.IsArchived)
            .OrderBy(d => d.Name).Select(d => new Structure.LookupDto(d.Id, d.Name)).ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.Name).Select(p => new Structure.LookupDto(p.Id, p.Name)).ToListAsync(ct);
        return new PersonnelOrderOptionsDto(employees, departments, positions, await TodayAsync(ctx, ct));
    }

    public async Task<PersonnelOrderDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var o = await db.PersonnelOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Приказ");
        var employee = o.EmployeeId is { } eid ? await db.Employees.AsNoTracking().SingleAsync(e => e.Id == eid, ct) : null;
        var names = await NamesAsync(ctx, [o.DepartmentId, employee?.DepartmentId], [o.PositionId, employee?.PositionId], ct);
        var draft = o.Status == PersonnelOrderStatus.Draft;
        return new PersonnelOrderDto(o.Id, o.Number, o.Status, ToValues(o), employee?.FullName ?? o.FullName,
            o.Kind == PersonnelOrderKind.Hire ? null : names.Departments.GetValueOrDefault(employee?.DepartmentId ?? 0),
            o.Kind == PersonnelOrderKind.Hire ? null : names.Positions.GetValueOrDefault(employee?.PositionId ?? 0),
            names.Departments.GetValueOrDefault(o.DepartmentId ?? 0), names.Positions.GetValueOrDefault(o.PositionId ?? 0),
            draft && ctx.Permissions.Has(Permissions.HrOrderCreate), draft && ctx.Permissions.Has(Permissions.HrOrderPost), o.RowVersion);
    }

    public async Task<long> CreateAsync(PersonnelOrderValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderCreate, ct);
        await ValidateReferencesAsync(ctx, values, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, NumberKind, ct);
        var order = PersonnelOrder.Create(ctx.OrganizationId, number, values, ctx.UserId, clock.UtcNow);
        db.PersonnelOrders.Add(order);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.HrOrderCreated, order, null, $"{PersonnelOrder.FormName(order.Kind)} № {order.Number}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order.Id;
    }

    public async Task UpdateAsync(long id, PersonnelOrderValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderCreate, ct);
        var order = await LoadAsync(ctx, id, rowVersion, ct);
        await ValidateReferencesAsync(ctx, values, ct);
        try
        {
            order.Edit(values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.HrOrderChanged, order, null, null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task CancelAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderCreate, ct);
        var order = await LoadAsync(ctx, id, rowVersion, ct);
        order.Cancel();
        Audit(ctx, AuditActions.HrOrderCancelled, order, PersonnelOrder.StatusName(PersonnelOrderStatus.Draft), PersonnelOrder.StatusName(order.Status));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Проведение. Приём создаёт карточку сотрудника, перевод меняет подразделение и должность, отпуск с датой начала не позже сегодня
    /// ставит статус «В отпуске», увольнение закрывает карточку. Приём и перевод сверх штата запрещены (D203).
    /// </summary>
    public async Task PostAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.hr.staffing.{ctx.OrganizationId}", ct);
        var order = await LoadAsync(ctx, id, rowVersion, ct);
        try
        {
            await ApplyAsync(ctx, order, ct);
            order.Post(ctx.UserId, clock.UtcNow);
            Audit(ctx, AuditActions.HrOrderPosted, order, PersonnelOrder.StatusName(PersonnelOrderStatus.Draft), PersonnelOrder.StatusName(order.Status));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch
        {
            db.DiscardChanges(); // отказ после изменения карточки не должен оставить «проведённый» приказ в контексте вкладки
            throw;
        }
    }

    public async Task<PersonnelOrderPrintDto> PrintAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var o = await db.PersonnelOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Приказ");
        var employee = o.EmployeeId is { } eid ? await db.Employees.AsNoTracking().SingleAsync(e => e.Id == eid, ct) : null;

        // «Откуда» для перевода — назначение до приказа: из истории (для проведённого) или текущая карточка (для черновика).
        long? fromDep = employee?.DepartmentId, fromPos = employee?.PositionId;
        if (o.Kind == PersonnelOrderKind.Transfer && o.Status == PersonnelOrderStatus.Posted)
        {
            var history = await db.EmploymentHistory.AsNoTracking()
                .Where(h => h.EmployeeId == o.EmployeeId && (h.Kind == PersonnelOrderKind.Hire || h.Kind == PersonnelOrderKind.Transfer))
                .OrderBy(h => h.Id).ToListAsync(ct);
            var index = history.FindIndex(h => h.OrderId == o.Id);
            var before = index > 0 ? history[index - 1] : null;
            fromDep = before?.DepartmentId;
            fromPos = before?.PositionId;
        }

        var names = await NamesAsync(ctx, [o.DepartmentId, fromDep], [o.PositionId, fromPos], ct);
        var org = await db.Organizations.AsNoTracking().Where(x => x.Id == ctx.OrganizationId).Select(x => x.FullName).SingleAsync(ct);
        var director = await db.LegalEntities.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId)
            .OrderBy(l => l.Id).Select(l => new { l.DirectorName, l.DirectorPosition }).FirstOrDefaultAsync(ct);
        return new PersonnelOrderPrintDto(org, null, o.Number, o.Kind, o.Status, o.OrderDate, o.EffectiveFrom, o.EffectiveTo,
            employee?.FullName ?? o.FullName, employee?.PersonnelNumber ?? o.PersonnelNumber ?? string.Empty,
            o.Kind == PersonnelOrderKind.Hire ? null : names.Departments.GetValueOrDefault(fromDep ?? 0),
            o.Kind == PersonnelOrderKind.Hire ? null : names.Positions.GetValueOrDefault(fromPos ?? 0),
            names.Departments.GetValueOrDefault(o.DepartmentId ?? 0), names.Positions.GetValueOrDefault(o.PositionId ?? 0),
            o.Rate, o.Salary, o.TrialMonths, o.LeaveKind, o.LeaveDays, DismissalGrounds.Find(o.DismissalGround), employee?.HiredOn, o.Basis,
            director?.DirectorName, director?.DirectorPosition);
    }

    private async Task ApplyAsync(AccessContext ctx, PersonnelOrder order, CancellationToken ct)
    {
        var now = clock.UtcNow;
        switch (order.Kind)
        {
            case PersonnelOrderKind.Hire:
            {
                await RequireAssignableAsync(ctx, order.DepartmentId!.Value, order.PositionId!.Value, ct);
                if (await db.Employees.AnyAsync(e => e.OrganizationId == ctx.OrganizationId && e.PersonnelNumber == order.PersonnelNumber, ct))
                {
                    throw new BusinessRuleException("hr.employee.number_taken", $"Табельный номер {order.PersonnelNumber} уже занят.");
                }

                await StaffingRules.EnsureVacancyAsync(db, ctx.OrganizationId, order.DepartmentId.Value, order.PositionId.Value, order.Rate!.Value, null, ct);
                var employee = Employee.Hire(ctx.OrganizationId, order.PersonnelNumber!, order.LastName!, order.FirstName!, order.MiddleName,
                    order.DepartmentId.Value, order.PositionId.Value, order.EffectiveFrom);
                db.Employees.Add(employee);
                await db.SaveChangesAsync(ct);
                order.AttachHiredEmployee(employee.Id);
                db.EmploymentHistory.Add(EmploymentRecord.From(order, employee.Id, employee.DepartmentId, employee.PositionId, order.Rate.Value,
                    order.Salary, null, now));
                db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeHired, nameof(Employee),
                    employee.Id.ToString(), null, $"{employee.PersonnelNumber} {employee.FullName}", $"Приказ № {order.Number}", currentUser.CorrelationId));
                break;
            }

            case PersonnelOrderKind.Transfer:
            {
                var employee = await EmployeeAsync(ctx, order, ct);
                await RequireAssignableAsync(ctx, order.DepartmentId!.Value, order.PositionId!.Value, ct);
                await StaffingRules.EnsureVacancyAsync(db, ctx.OrganizationId, order.DepartmentId.Value, order.PositionId.Value, order.Rate!.Value,
                    employee.Id, ct);
                var names = await NamesAsync(ctx, [employee.DepartmentId, order.DepartmentId], [employee.PositionId, order.PositionId], ct);
                var from = $"{names.Departments[employee.DepartmentId]}, {names.Positions[employee.PositionId]}";
                employee.Transfer(order.DepartmentId.Value, order.PositionId.Value);
                db.EmploymentHistory.Add(EmploymentRecord.From(order, employee.Id, employee.DepartmentId, employee.PositionId, order.Rate.Value,
                    order.Salary, null, now));
                db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeChanged, nameof(Employee),
                    employee.Id.ToString(), from, $"{names.Departments[employee.DepartmentId]}, {names.Positions[employee.PositionId]}",
                    $"Перевод, приказ № {order.Number}", currentUser.CorrelationId));
                break;
            }

            case PersonnelOrderKind.Leave:
            {
                var employee = await EmployeeAsync(ctx, order, ct);
                if (order.EffectiveFrom < employee.HiredOn)
                {
                    throw new BusinessRuleException("hr.order.before_hire", "Дата приказа раньше даты приёма сотрудника.");
                }

                // Допущение D204: статус «В отпуске» ставится при проведении, если отпуск уже начался; возврат — кнопкой на карточке.
                var today = await TodayAsync(ctx, ct);
                if (order.EffectiveFrom <= today && order.EffectiveTo >= today && employee.Status == EmploymentStatus.Working)
                {
                    var change = employee.SetOnLeave(true);
                    db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeChanged, nameof(Employee),
                        employee.Id.ToString(), change.Before, change.After, $"Отпуск, приказ № {order.Number}", currentUser.CorrelationId));
                }

                db.EmploymentHistory.Add(EmploymentRecord.From(order, employee.Id, employee.DepartmentId, employee.PositionId,
                    await CurrentRateAsync(employee.Id, ct), null, LeaveKinds.Name(order.LeaveKind!.Value), now));
                break;
            }

            case PersonnelOrderKind.Dismissal:
            {
                var employee = await EmployeeAsync(ctx, order, ct);
                if (employee.UserId is { } userId
                    && await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId
                                                                  && m.Status == MembershipStatus.Active, ct))
                {
                    // Допущение D206: доступ уволенного блокируется в той же операции; для этого нужно право управления пользователями.
                    if (!ctx.Permissions.Has(Permissions.UserManage))
                    {
                        throw new BusinessRuleException("hr.order.user_active",
                            "У сотрудника есть учётная запись: заблокировать её может администратор. Попросите его провести приказ или сначала заблокировать вход.");
                    }

                    await userAccess.BlockAsync(userId, $"Увольнение, приказ № {order.Number}", ct);
                }

                var ground = DismissalGrounds.Find(order.DismissalGround)!;
                var change = employee.Dismiss(order.EffectiveFrom);
                db.EmploymentHistory.Add(EmploymentRecord.From(order, employee.Id, employee.DepartmentId, employee.PositionId,
                    await CurrentRateAsync(employee.Id, ct), null, $"{ground.Title} ({ground.Article} ТК РФ)", now));
                db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDismissed, nameof(Employee),
                    employee.Id.ToString(), change.Before, change.After, $"{ground.Title}, приказ № {order.Number}", currentUser.CorrelationId));
                break;
            }
        }
    }

    private async Task<decimal> CurrentRateAsync(long employeeId, CancellationToken ct) =>
        await db.EmploymentHistory.AsNoTracking()
            .Where(h => h.EmployeeId == employeeId && (h.Kind == PersonnelOrderKind.Hire || h.Kind == PersonnelOrderKind.Transfer))
            .OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id).Select(h => (decimal?)h.Rate).FirstOrDefaultAsync(ct) ?? 1m;

    private async Task<Employee> EmployeeAsync(AccessContext ctx, PersonnelOrder order, CancellationToken ct)
    {
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == order.EmployeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (employee.Status == EmploymentStatus.Dismissed)
        {
            throw new BusinessRuleException("hr.employee.dismissed", "Сотрудник уволен, карточка закрыта для изменений.");
        }

        if (order.Kind == PersonnelOrderKind.Dismissal && order.EffectiveFrom < employee.HiredOn)
        {
            throw new BusinessRuleException("hr.employee.dismiss_before_hire", "Дата увольнения раньше даты приёма.");
        }

        return employee;
    }

    /// <summary>Ссылки черновика — на объекты своей организации; чужие и несуществующие — «не найдено».</summary>
    private async Task ValidateReferencesAsync(AccessContext ctx, PersonnelOrderValues v, CancellationToken ct)
    {
        if (v.EmployeeId is { } eid && !await db.Employees.AnyAsync(e => e.Id == eid && e.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Сотрудник");
        }

        if (v.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep && d.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Подразделение");
        }

        if (v.PositionId is { } pos && !await db.Positions.AnyAsync(p => p.Id == pos && p.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Должность");
        }
    }

    private async Task RequireAssignableAsync(AccessContext ctx, long departmentId, long positionId, CancellationToken ct)
    {
        if (await db.Departments.AnyAsync(d => d.Id == departmentId && d.OrganizationId == ctx.OrganizationId && d.IsArchived, ct))
        {
            throw new BusinessRuleException("structure.department.archived", "Подразделение в архиве.");
        }

        if (await db.Positions.AnyAsync(p => p.Id == positionId && p.OrganizationId == ctx.OrganizationId && p.IsArchived, ct))
        {
            throw new BusinessRuleException("structure.position.archived", "Должность в архиве.");
        }
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.HrOrderCreate) && !ctx.Permissions.Has(Permissions.HrOrderPost))
        {
            await guard.DenyAsync(ctx, Permissions.HrOrderCreate, ct);
        }

        return ctx;
    }

    private async Task<PersonnelOrder> LoadAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var order = await db.PersonnelOrders.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Приказ");
        return order.RowVersion.AsSpan().SequenceEqual(rowVersion) ? order : throw new ConcurrencyConflictException();
    }

    private async Task<(Dictionary<long, string> Departments, Dictionary<long, string> Positions)> NamesAsync(
        AccessContext ctx, long?[] departmentIds, long?[] positionIds, CancellationToken ct)
    {
        var deps = departmentIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var poss = positionIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        return (await db.Departments.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId && deps.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct),
            await db.Positions.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && poss.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct));
    }

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private static PersonnelOrderValues ToValues(PersonnelOrder o) => new(o.Kind, o.OrderDate, o.EffectiveFrom, o.EmployeeId, o.PersonnelNumber,
        o.LastName, o.FirstName, o.MiddleName, o.DepartmentId, o.PositionId, o.Rate, o.Salary, o.TrialMonths, o.EffectiveTo, o.LeaveKind,
        o.DismissalGround, o.Basis);

    private void Audit(AccessContext ctx, string action, PersonnelOrder order, string? before, string? after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(PersonnelOrder), order.Id.ToString(),
            before, after ?? $"{PersonnelOrder.FormName(order.Kind)} № {order.Number}", null, currentUser.CorrelationId));
}
