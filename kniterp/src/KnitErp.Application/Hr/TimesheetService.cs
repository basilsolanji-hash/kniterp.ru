using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

public sealed record WorkScheduleDto(
    long Id, string Name, WorkScheduleKind Kind, decimal ShiftHours, decimal NightHours, byte WorkDays, byte RestDays, DateOnly? CycleStart,
    bool Summarized, byte[] RowVersion)
{
    public string Describe => Kind == WorkScheduleKind.FiveDay ? $"5/2, {ShiftHours:0.##} ч" : $"{WorkDays}/{RestDays}, {ShiftHours:0.##} ч";
}

public sealed record WorkScheduleCommand(
    string Name, WorkScheduleKind Kind, decimal ShiftHours, decimal NightHours, byte WorkDays, byte RestDays, DateOnly? CycleStart, bool Summarized);

public sealed record EmployeeScheduleDto(long EmployeeId, string FullName, string PersonnelNumber, long? ScheduleId, string? ScheduleName, DateOnly? ValidFrom);

public sealed record SchedulesDto(IReadOnlyList<WorkScheduleDto> Schedules, IReadOnlyList<EmployeeScheduleDto> Employees, bool CanEdit, DateOnly Today);

public sealed record DayCellDto(string Code, decimal Hours, decimal NightHours, decimal OvertimeHours, bool Manual, TimeOnly? FirstSignIn)
{
    public string Short => Hours > 0 || OvertimeHours > 0 ? $"{Code} {Hours + OvertimeHours:0.##}" : Code;
}

/// <summary>Итоги строки табеля: явки и неявки (Т-13, графы 4–6 и 7–9), часы для расчёта заработка.</summary>
public sealed record TimesheetTotalsDto(int DaysWorked, decimal Hours, decimal NightHours, decimal OvertimeHours, decimal HolidayHours,
    IReadOnlyDictionary<string, int> AbsenceDays)
{
    public static TimesheetTotalsDto Of(IEnumerable<(DateOnly Date, string Code, decimal Hours, decimal Night, decimal Overtime)> days)
    {
        var list = days.ToList();
        var worked = list.Where(d => TimesheetCodes.IsWorked(d.Code)).ToList();
        return new TimesheetTotalsDto(worked.Count, worked.Sum(d => d.Hours), worked.Sum(d => d.Night), worked.Sum(d => d.Overtime),
            worked.Where(d => d.Code == TimesheetCodes.Holiday).Sum(d => d.Hours + d.Overtime),
            list.Where(d => !TimesheetCodes.IsWorked(d.Code) && d.Code != TimesheetCodes.Weekend)
                .GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count()));
    }
}

public sealed record TimesheetRowDto(long EmployeeId, string PersonnelNumber, string FullName, string Position,
    IReadOnlyDictionary<DateOnly, DayCellDto> Cells, TimesheetTotalsDto Totals);

public sealed record TimesheetVersionDto(long Id, byte Version, TimesheetStatus Status);

public sealed record TimesheetGridDto(
    long? Id, long DepartmentId, string DepartmentName, int Year, int Month, byte Version, TimesheetStatus? Status,
    IReadOnlyList<DateOnly> Days, IReadOnlySet<DateOnly> Holidays, IReadOnlyList<TimesheetRowDto> Rows,
    bool CanEdit, bool CanClose, IReadOnlyList<TimesheetVersionDto> Versions, byte[]? RowVersion, string Organization)
{
    public bool IsOpen => Status == TimesheetStatus.Open;
    public bool CanCorrect => Status == TimesheetStatus.Closed && CanEdit && Versions.Count > 0 && Versions[^1].Id == Id;
}

/// <summary>Свой табель сотрудника за месяц (личный кабинет).</summary>
public sealed record MyTimesheetDto(string FullName, int Year, int Month, IReadOnlyList<DateOnly> Days, IReadOnlyDictionary<DateOnly, DayCellDto> Cells,
    TimesheetTotalsDto Totals, bool Closed);

/// <summary>
/// Табель учёта рабочего времени (Т-12/Т-13, D207–D209): графики работы, заполнение по графику и отпускам из приказов, ручные правки
/// с журналом, закрытие и исправительный табель. Заполняет — право «Табель: заполнение» (мастер — своё подразделение),
/// закрывает — право «Табель: закрытие». Сотрудник видит только свой табель.
/// </summary>
public sealed class TimesheetService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ISpreadsheetFormat spreadsheet)
{
    // ——— Графики работы ———

    public async Task<SchedulesDto> SchedulesAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var visible = await VisibleAsync(ctx, ct);
        var schedules = await db.WorkSchedules.AsNoTracking().Where(s => s.OrganizationId == ctx.OrganizationId && !s.IsArchived)
            .OrderBy(s => s.Name)
            .Select(s => new WorkScheduleDto(s.Id, s.Name, s.Kind, s.ShiftHours, s.NightHours, s.WorkDays, s.RestDays, s.CycleStart, s.Summarized, s.RowVersion))
            .ToListAsync(ct);
        var today = await TodayAsync(ctx, ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.Status != EmploymentStatus.Dismissed && (visible == null || visible.Contains(e.DepartmentId)))
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var current = (await db.EmployeeSchedules.AsNoTracking()
                .Where(s => s.OrganizationId == ctx.OrganizationId && ids.Contains(s.EmployeeId) && s.ValidFrom <= today).ToListAsync(ct))
            .GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.MaxBy(s => s.ValidFrom)!);
        var names = schedules.ToDictionary(s => s.Id, s => s.Name);
        return new SchedulesDto(schedules,
            employees.Select(e => new EmployeeScheduleDto(e.Id, Name(e.LastName, e.FirstName, e.MiddleName), e.PersonnelNumber,
                current.GetValueOrDefault(e.Id)?.ScheduleId, current.TryGetValue(e.Id, out var s) ? names.GetValueOrDefault(s.ScheduleId) : null,
                current.GetValueOrDefault(e.Id)?.ValidFrom)).ToList(),
            ctx.Permissions.Has(Permissions.TimesheetEdit) && visible is null, today);
    }

    /// <summary>Новый график или изменение существующего (id). Справочник графиков общий для организации — нужна полная область.</summary>
    public async Task<long> SaveScheduleAsync(long? id, WorkScheduleCommand cmd, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandFullEditAsync(ct);
        WorkSchedule schedule;
        if (id is { } existing)
        {
            schedule = await db.WorkSchedules.SingleOrDefaultAsync(s => s.Id == existing && s.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("График работы");
            if (rowVersion is null || !schedule.RowVersion.AsSpan().SequenceEqual(rowVersion))
            {
                throw new ConcurrencyConflictException();
            }

            schedule.Change(cmd.Name, cmd.Kind, cmd.ShiftHours, cmd.NightHours, cmd.WorkDays, cmd.RestDays, cmd.CycleStart, cmd.Summarized);
        }
        else
        {
            schedule = WorkSchedule.Create(ctx.OrganizationId, cmd.Name, cmd.Kind, cmd.ShiftHours, cmd.NightHours, cmd.WorkDays, cmd.RestDays,
                cmd.CycleStart, cmd.Summarized);
            db.WorkSchedules.Add(schedule);
        }

        if (await db.WorkSchedules.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.Name == schedule.Name && !s.IsArchived && s.Id != schedule.Id, ct))
        {
            db.DiscardChanges();
            throw new BusinessRuleException("hr.schedule.duplicate", "График с таким названием уже есть.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ScheduleChanged, nameof(WorkSchedule),
            schedule.Id.ToString(), null, $"{schedule.Name}: {(schedule.Kind == WorkScheduleKind.FiveDay ? "5/2" : $"{schedule.WorkDays}/{schedule.RestDays}")}, {schedule.ShiftHours:0.##} ч",
            id is null ? "новый график" : "изменён график", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return schedule.Id;
    }

    /// <summary>График сотруднику с даты; повтор той же даты заменяет назначение только если оно ещё не наступило.</summary>
    public async Task AssignScheduleAsync(long employeeId, long scheduleId, DateOnly validFrom, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var visible = await VisibleAsync(ctx, ct, Permissions.TimesheetEdit);
        if (visible is not null && !visible.Contains(employee.DepartmentId))
        {
            throw new NotFoundException("Сотрудник");
        }

        var schedule = await db.WorkSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scheduleId && s.OrganizationId == ctx.OrganizationId && !s.IsArchived, ct)
                       ?? throw new NotFoundException("График работы");
        if (await db.EmployeeSchedules.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.EmployeeId == employeeId && s.ValidFrom == validFrom, ct))
        {
            throw new BusinessRuleException("hr.schedule.assigned", "С этой даты сотруднику уже назначен график.");
        }

        db.EmployeeSchedules.Add(EmployeeSchedule.Create(ctx.OrganizationId, employeeId, scheduleId, validFrom));
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ScheduleChanged, nameof(Employee),
            employeeId.ToString(), null, $"{schedule.Name} с {validFrom:dd.MM.yyyy}", employee.FullName, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    // ——— Табель ———

    /// <summary>Подразделения, табели которых пользователь видит (область «заполнение» + «закрытие»).</summary>
    public async Task<IReadOnlyList<LookupDto>> DepartmentsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var visible = await VisibleAsync(ctx, ct);
        return await db.Departments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && !d.IsArchived && (visible == null || visible.Contains(d.Id)))
            .OrderBy(d => d.Name).Select(d => new LookupDto(d.Id, d.Name)).ToListAsync(ct);
    }

    /// <summary>Табель подразделения за месяц: последняя версия (или пустая сетка, если табель ещё не составлен).</summary>
    public async Task<TimesheetGridDto> GetAsync(long departmentId, int year, int month, long? versionId = null, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var department = await DepartmentAsync(ctx, departmentId, ct);
        var versions = await db.Timesheets.AsNoTracking()
            .Where(t => t.OrganizationId == ctx.OrganizationId && t.DepartmentId == departmentId && t.Year == year && t.Month == month)
            .OrderBy(t => t.Version).ToListAsync(ct);
        var ts = versionId is { } vid ? versions.SingleOrDefault(v => v.Id == vid) ?? throw new NotFoundException("Табель") : versions.LastOrDefault();
        return await GridAsync(ctx, department, year, month, ts, versions, ct);
    }

    /// <summary>Табель по идентификатору — для печати Т-13.</summary>
    public async Task<TimesheetGridDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var ts = await db.Timesheets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                 ?? throw new NotFoundException("Табель");
        return await GetAsync(ts.DepartmentId, ts.Year, ts.Month, id, ct);
    }

    /// <summary>Составление табеля: строки по сотрудникам подразделения, заполненные по графику и отпускам.</summary>
    public async Task<long> CreateAsync(long departmentId, int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        var department = await DepartmentAsync(ctx, departmentId, ct, Permissions.TimesheetEdit);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.hr.timesheet.{ctx.OrganizationId}.{departmentId}.{year}.{month}", ct);
        if (await db.Timesheets.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.DepartmentId == departmentId && t.Year == year && t.Month == month, ct))
        {
            throw new BusinessRuleException("hr.timesheet.exists", "Табель за этот месяц уже составлен.");
        }

        var ts = Timesheet.Create(ctx.OrganizationId, departmentId, year, month, ctx.UserId, clock.UtcNow);
        db.Timesheets.Add(ts);
        await db.SaveChangesAsync(ct);
        await FillAsync(ctx, ts, ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.TimesheetCreated, nameof(Timesheet),
            ts.Id.ToString(), null, $"{department.Name}, {month:00}.{year}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ts.Id;
    }

    /// <summary>Перезаполнить по графику: ручные правки сохраняются, добавляются новые сотрудники подразделения.</summary>
    public async Task RefillAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        var ts = await LoadAsync(ctx, id, rowVersion, Permissions.TimesheetEdit, ct);
        ts.EnsureOpen();
        await using var tx = await db.BeginTransactionAsync(ct);
        await FillAsync(ctx, ts, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Ручная правка дня. В журнал — «было → стало» с ФИО и датой.</summary>
    public async Task SetDayAsync(long id, long employeeId, DateOnly date, string code, decimal hours, decimal nightHours, decimal overtimeHours,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        var ts = await db.Timesheets.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Табель");
        await DepartmentAsync(ctx, ts.DepartmentId, ct, Permissions.TimesheetEdit);
        ts.EnsureOpen();
        ts.EnsureDay(date);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (date < employee.HiredOn || (employee.DismissedOn is { } off && date > off))
        {
            throw new BusinessRuleException("hr.timesheet.not_employed", "В этот день сотрудник не работал в организации.");
        }

        var inTimesheet = employee.DepartmentId == ts.DepartmentId
                          || await db.TimesheetDays.AnyAsync(d => d.TimesheetId == id && d.EmployeeId == employeeId, ct);
        if (!inTimesheet)
        {
            throw new NotFoundException("Сотрудник");
        }

        var day = await db.TimesheetDays.SingleOrDefaultAsync(d => d.TimesheetId == id && d.EmployeeId == employeeId && d.Date == date, ct);
        string before;
        try
        {
            if (day is null)
            {
                day = TimesheetDay.Create(ctx.OrganizationId, id, employeeId, date, code, hours, nightHours, overtimeHours, manual: true);
                db.TimesheetDays.Add(day);
                before = "—";
            }
            else
            {
                before = day.Set(code, hours, nightHours, overtimeHours, manual: true);
            }
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.TimesheetDayChanged, nameof(Timesheet), id.ToString(),
            before, day.Describe(), $"{employee.FullName}, {date:dd.MM.yyyy}", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    public async Task CloseAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetClose, ct);
        var ts = await LoadAsync(ctx, id, rowVersion, Permissions.TimesheetClose, ct);
        ts.Close(ctx.UserId, clock.UtcNow);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.TimesheetClosed, nameof(Timesheet), id.ToString(),
            "открыт", "закрыт", ts.Version == 0 ? null : $"исправительный № {ts.Version}", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Исправительный табель к закрытому: копия строк, открыт для правки.</summary>
    public async Task<long> CorrectAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        var ts = await db.Timesheets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                 ?? throw new NotFoundException("Табель");
        await DepartmentAsync(ctx, ts.DepartmentId, ct, Permissions.TimesheetEdit);
        if (await db.Timesheets.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.DepartmentId == ts.DepartmentId && t.Year == ts.Year
                                              && t.Month == ts.Month && t.Version > ts.Version, ct))
        {
            throw new BusinessRuleException("hr.timesheet.not_latest", "Исправляется только последняя версия табеля.");
        }

        var correction = ts.Correction(ctx.UserId, clock.UtcNow);
        await using var tx = await db.BeginTransactionAsync(ct);
        db.Timesheets.Add(correction);
        await db.SaveChangesAsync(ct);
        var days = await db.TimesheetDays.AsNoTracking().Where(d => d.TimesheetId == id).ToListAsync(ct);
        db.TimesheetDays.AddRange(days.Select(d =>
            TimesheetDay.Create(ctx.OrganizationId, correction.Id, d.EmployeeId, d.Date, d.Code, d.Hours, d.NightHours, d.OvertimeHours, d.IsManual)));
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.TimesheetCreated, nameof(Timesheet),
            correction.Id.ToString(), null, $"исправительный № {correction.Version} к табелю {ts.Month:00}.{ts.Year}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return correction.Id;
    }

    /// <summary>Свой табель: строки последних версий табелей месяца, где есть сотрудник с учётной записью пользователя.</summary>
    public async Task<MyTimesheetDto> MyAsync(int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.HasOwnOnly(Permissions.TimesheetEdit))
        {
            await guard.DenyAsync(ctx, Permissions.TimesheetEdit, ct);
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var latest = await LatestVersionsAsync(ctx.OrganizationId, new DateOnly(year, month, 1), new DateOnly(year, month, 1), ct);
        var ids = latest.Select(t => t.Id).ToList();
        var days = await db.TimesheetDays.AsNoTracking().Where(d => ids.Contains(d.TimesheetId) && d.EmployeeId == employee.Id).ToListAsync(ct);
        var from = new DateOnly(year, month, 1);
        return new MyTimesheetDto(employee.FullName, year, month, Enumerable.Range(0, DateTime.DaysInMonth(year, month)).Select(from.AddDays).ToList(),
            days.ToDictionary(d => d.Date, d => new DayCellDto(d.Code, d.Hours, d.NightHours, d.OvertimeHours, d.IsManual, null)),
            TimesheetTotalsDto.Of(days.Select(d => (d.Date, d.Code, d.Hours, d.NightHours, d.OvertimeHours))),
            latest.Count > 0 && latest.All(t => t.Status == TimesheetStatus.Closed));
    }

    /// <summary>Выгрузка табеля в Excel: строка на сотрудника, по столбцу на день и итоги.</summary>
    public async Task<(string FileName, byte[] Content)> ExportAsync(long departmentId, int year, int month, CancellationToken ct = default)
    {
        var grid = await GetAsync(departmentId, year, month, null, ct);
        var header = new List<string> { "Таб. №", "ФИО", "Должность" };
        header.AddRange(grid.Days.Select(d => d.Day.ToString()));
        header.AddRange(["Дней", "Часов", "Ночных", "Сверхурочных", "Праздничных", "Неявки"]);
        var numeric = new HashSet<int>(Enumerable.Range(3 + grid.Days.Count, 5));
        var rows = grid.Rows.Select(r =>
        {
            var row = new List<string> { r.PersonnelNumber, r.FullName, r.Position };
            row.AddRange(grid.Days.Select(d => r.Cells.TryGetValue(d, out var c) ? c.Short : ""));
            row.AddRange([r.Totals.DaysWorked.ToString(), Inv(r.Totals.Hours), Inv(r.Totals.NightHours), Inv(r.Totals.OvertimeHours), Inv(r.Totals.HolidayHours),
                string.Join(", ", r.Totals.AbsenceDays.Select(a => $"{a.Key} {a.Value}"))]);
            return (IReadOnlyList<string>)row;
        }).ToList();
        var content = spreadsheet.Write([new SheetData($"Табель {month:00}.{year}", header, rows, numeric)]);
        return ($"timesheet-{year}-{month:00}.xlsx", content);
    }

    /// <summary>
    /// Итоги табеля по сотрудникам за период — для расчёта заработка (этап 4). Берётся последняя версия табеля каждого подразделения
    /// и месяца; Closed — все ли эти версии закрыты.
    /// </summary>
    internal async Task<(Dictionary<long, TimesheetTotalsDto> Totals, bool Closed)> TotalsAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var latest = await LatestVersionsAsync(organizationId, from, to, ct);
        var ids = latest.Select(t => t.Id).ToList();
        var days = await db.TimesheetDays.AsNoTracking().Where(d => ids.Contains(d.TimesheetId) && d.Date >= from && d.Date <= to).ToListAsync(ct);
        return (days.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key,
                g => TimesheetTotalsDto.Of(g.Select(d => (d.Date, d.Code, d.Hours, d.NightHours, d.OvertimeHours)))),
            latest.Count > 0 && latest.All(t => t.Status == TimesheetStatus.Closed));
    }

    /// <summary>Дни последних версий табелей за период — для расчёта заработка; Closed — все ли версии закрыты.</summary>
    internal async Task<(List<TimesheetDay> Days, bool Closed)> DaysAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var latest = await LatestVersionsAsync(organizationId, from, to, ct);
        var ids = latest.Select(t => t.Id).ToList();
        var days = await db.TimesheetDays.AsNoTracking().Where(d => ids.Contains(d.TimesheetId) && d.Date >= from && d.Date <= to).ToListAsync(ct);
        return (days, latest.Count > 0 && latest.All(t => t.Status == TimesheetStatus.Closed));
    }

    /// <summary>
    /// Норма часов месяца по графику сотрудника (без учёта отпусков и дат приёма), умноженная на ставку, — знаменатель оплаты по окладу (D212).
    /// </summary>
    internal async Task<Dictionary<long, decimal>> NormHoursAsync(long organizationId, IReadOnlyCollection<long> employeeIds, DateOnly from, DateOnly to,
        IReadOnlyDictionary<long, decimal> rates, CancellationToken ct)
    {
        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.CountryCode).SingleAsync(ct);
        var holidays = ProductionCalendar.Holidays(country, from, to.AddDays(1)).Select(h => h.Date).ToHashSet();
        var assignments = await (from a in db.EmployeeSchedules.AsNoTracking()
                                 join s in db.WorkSchedules.AsNoTracking() on a.ScheduleId equals s.Id
                                 where a.OrganizationId == organizationId && employeeIds.Contains(a.EmployeeId) && a.ValidFrom <= to
                                 select new { a.EmployeeId, a.ValidFrom, Schedule = s }).ToListAsync(ct);
        var result = new Dictionary<long, decimal>();
        foreach (var id in employeeIds)
        {
            var own = assignments.Where(a => a.EmployeeId == id).OrderBy(a => a.ValidFrom).ToList();
            decimal sum = 0;
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var plan = own.LastOrDefault(a => a.ValidFrom <= date)?.Schedule.PlanFor(date, holidays) ?? WorkSchedule.DefaultPlan(date, holidays);
                sum += plan.Work ? plan.Hours : 0;
            }

            result[id] = decimal.Round(sum * rates.GetValueOrDefault(id, 1m), 2);
        }

        return result;
    }

    private async Task<List<Timesheet>> LatestVersionsAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var fromKey = from.Year * 12 + from.Month;
        var toKey = to.Year * 12 + to.Month;
        var all = await db.Timesheets.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.Year * 12 + t.Month >= fromKey && t.Year * 12 + t.Month <= toKey).ToListAsync(ct);
        return all.GroupBy(t => (t.DepartmentId, t.Year, t.Month)).Select(g => g.MaxBy(t => t.Version)!).ToList();
    }

    private async Task<TimesheetGridDto> GridAsync(AccessContext ctx, Department department, int year, int month, Timesheet? ts,
        IReadOnlyList<Timesheet> versions, CancellationToken ct)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => new { o.FullName, o.CountryCode, o.TimeZoneId }).SingleAsync(ct);
        var holidays = ProductionCalendar.Holidays(org.CountryCode, from, to.AddDays(1)).Select(h => h.Date).ToHashSet();
        var days = Enumerable.Range(0, to.Day).Select(from.AddDays).ToList();
        var rows = new List<TimesheetRowDto>();
        if (ts is not null)
        {
            var entries = await db.TimesheetDays.AsNoTracking().Where(d => d.TimesheetId == ts.Id).ToListAsync(ct);
            var employeeIds = entries.Select(d => d.EmployeeId).Distinct().ToList();
            var employees = await (from e in db.Employees.AsNoTracking()
                                   join p in db.Positions.AsNoTracking() on e.PositionId equals p.Id
                                   where employeeIds.Contains(e.Id)
                                   select new { e.Id, e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName, e.UserId, Position = p.Name }).ToListAsync(ct);
            var signIns = await SignInsAsync(ctx.OrganizationId, employees.Where(e => e.UserId != null).Select(e => e.UserId!.Value).ToList(), from, to, org.TimeZoneId, ct);
            foreach (var e in employees.OrderBy(e => e.LastName).ThenBy(e => e.FirstName))
            {
                var own = entries.Where(d => d.EmployeeId == e.Id).ToList();
                rows.Add(new TimesheetRowDto(e.Id, e.PersonnelNumber, Name(e.LastName, e.FirstName, e.MiddleName), e.Position,
                    own.ToDictionary(d => d.Date, d => new DayCellDto(d.Code, d.Hours, d.NightHours, d.OvertimeHours, d.IsManual,
                        e.UserId is { } uid && signIns.TryGetValue((uid, d.Date), out var t) ? t : null)),
                    TimesheetTotalsDto.Of(own.Select(d => (d.Date, d.Code, d.Hours, d.NightHours, d.OvertimeHours)))));
            }
        }

        var editVisible = ctx.Permissions.Has(Permissions.TimesheetEdit) ? await VisibleAsync(ctx, ct, Permissions.TimesheetEdit) : [];
        var closeVisible = ctx.Permissions.Has(Permissions.TimesheetClose) ? await VisibleAsync(ctx, ct, Permissions.TimesheetClose) : [];
        var isLatest = ts is not null && versions.Count > 0 && versions[^1].Id == ts.Id;
        return new TimesheetGridDto(ts?.Id, department.Id, department.Name, year, month, ts?.Version ?? 0, ts?.Status, days, holidays, rows,
            ctx.Permissions.Has(Permissions.TimesheetEdit) && (editVisible is null || editVisible.Contains(department.Id)) && (ts is null || isLatest),
            ctx.Permissions.Has(Permissions.TimesheetClose) && (closeVisible is null || closeVisible.Contains(department.Id)) && isLatest,
            versions.Select(v => new TimesheetVersionDto(v.Id, v.Version, v.Status)).ToList(), ts?.RowVersion, org.FullName);
    }

    /// <summary>Отметки «Рабочего дня»: первый вход пользователя за день по журналу входов (только чтение).</summary>
    private async Task<Dictionary<(long UserId, DateOnly Date), TimeOnly>> SignInsAsync(long organizationId, IReadOnlyList<long> userIds, DateOnly from, DateOnly to,
        string timeZoneId, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var ids = userIds.Select(u => u.ToString()).ToList();
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.SignedIn && a.OccurredAtUtc >= fromUtc && a.OccurredAtUtc < toUtc
                        && ((a.ActorUserId != null && userIds.Contains(a.ActorUserId.Value)) || (a.EntityType == "UserAccount" && a.EntityId != null && ids.Contains(a.EntityId))))
            .Select(a => new { a.ActorUserId, a.EntityId, a.OccurredAtUtc }).ToListAsync(ct);
        return entries
            .Select(a => (UserId: a.ActorUserId ?? long.Parse(a.EntityId!), Local: TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(a.OccurredAtUtc, DateTimeKind.Utc), zone)))
            .GroupBy(a => (a.UserId, DateOnly.FromDateTime(a.Local)))
            .ToDictionary(g => g.Key, g => TimeOnly.FromDateTime(g.Min(x => x.Local)));
    }

    /// <summary>Заполнение по графику и отпускам. Ручные правки не трогает.</summary>
    private async Task FillAsync(AccessContext ctx, Timesheet ts, CancellationToken ct)
    {
        var from = ts.From;
        var to = ts.To;
        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
        var holidays = ProductionCalendar.Holidays(country, from, to.AddDays(1)).Select(h => h.Date).ToHashSet();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.DepartmentId == ts.DepartmentId && e.HiredOn <= to
                        && (e.DismissedOn == null || e.DismissedOn >= from)).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var assignments = await (from a in db.EmployeeSchedules.AsNoTracking()
                                 join s in db.WorkSchedules.AsNoTracking() on a.ScheduleId equals s.Id
                                 where a.OrganizationId == ctx.OrganizationId && ids.Contains(a.EmployeeId) && a.ValidFrom <= to
                                 select new { a.EmployeeId, a.ValidFrom, Schedule = s }).ToListAsync(ct);
        var history = await db.EmploymentHistory.AsNoTracking()
            .Where(h => h.OrganizationId == ctx.OrganizationId && ids.Contains(h.EmployeeId) && h.EffectiveFrom <= to).ToListAsync(ct);
        var existing = await db.TimesheetDays.Where(d => d.TimesheetId == ts.Id).ToListAsync(ct);
        var byKey = existing.ToDictionary(d => (d.EmployeeId, d.Date));

        foreach (var e in employees)
        {
            var leaves = history.Where(h => h.EmployeeId == e.Id && h.Kind == PersonnelOrderKind.Leave && h.EffectiveTo >= from).ToList();
            var rate = history.Where(h => h.EmployeeId == e.Id && h.Kind is PersonnelOrderKind.Hire or PersonnelOrderKind.Transfer)
                .OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id).Select(h => (decimal?)h.Rate).FirstOrDefault() ?? 1m;
            var schedules = assignments.Where(a => a.EmployeeId == e.Id).OrderBy(a => a.ValidFrom).ToList();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (date < e.HiredOn || (e.DismissedOn is { } off && date > off))
                {
                    continue;
                }

                string code;
                decimal hours = 0, night = 0;
                var leave = leaves.FirstOrDefault(l => l.EffectiveFrom <= date && l.EffectiveTo >= date);
                if (leave?.LeaveKind is { } kind)
                {
                    code = LeaveKinds.TimesheetCode(kind);
                }
                else
                {
                    var schedule = schedules.LastOrDefault(a => a.ValidFrom <= date)?.Schedule;
                    var plan = schedule?.PlanFor(date, holidays) ?? WorkSchedule.DefaultPlan(date, holidays);
                    if (plan.Work)
                    {
                        // Неполная ставка — часы пропорционально (D209); смена в праздник — код «РВ».
                        hours = decimal.Round(plan.Hours * rate, 2);
                        night = Math.Min(hours, decimal.Round(plan.NightHours * rate, 2));
                        code = holidays.Contains(date) ? TimesheetCodes.Holiday : TimesheetCodes.Day;
                    }
                    else
                    {
                        code = TimesheetCodes.Weekend;
                    }
                }

                if (byKey.TryGetValue((e.Id, date), out var day))
                {
                    if (!day.IsManual)
                    {
                        day.Set(code, hours, night, 0, manual: false);
                    }
                }
                else
                {
                    db.TimesheetDays.Add(TimesheetDay.Create(ctx.OrganizationId, ts.Id, e.Id, date, code, hours, night, 0, manual: false));
                }
            }
        }
    }

    private async Task<Timesheet> LoadAsync(AccessContext ctx, long id, byte[] rowVersion, string permission, CancellationToken ct)
    {
        var ts = await db.Timesheets.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Табель");
        await DepartmentAsync(ctx, ts.DepartmentId, ct, permission);
        return ts.RowVersion.AsSpan().SequenceEqual(rowVersion) ? ts : throw new ConcurrencyConflictException();
    }

    /// <summary>Подразделение своей организации в области пользователя по праву; иначе «не найдено».</summary>
    private async Task<Department> DepartmentAsync(AccessContext ctx, long departmentId, CancellationToken ct, string? permission = null)
    {
        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId && d.OrganizationId == ctx.OrganizationId, ct)
                         ?? throw new NotFoundException("Подразделение");
        var visible = await VisibleAsync(ctx, ct, permission);
        return visible is null || visible.Contains(departmentId) ? department : throw new NotFoundException("Подразделение");
    }

    /// <summary>Видимые подразделения: по одному праву или объединение «заполнение» + «закрытие». null — все.</summary>
    private async Task<HashSet<long>?> VisibleAsync(AccessContext ctx, CancellationToken ct, string? permission = null)
    {
        if (permission is not null)
        {
            return await StructureScope.VisibleDepartmentsAsync(db, ctx, permission, ct);
        }

        var edit = ctx.Permissions.Has(Permissions.TimesheetEdit) ? await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.TimesheetEdit, ct) : [];
        var close = ctx.Permissions.Has(Permissions.TimesheetClose) ? await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.TimesheetClose, ct) : [];
        return edit is null || close is null ? null : [.. edit, .. close];
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.TimesheetEdit) && !ctx.Permissions.Has(Permissions.TimesheetClose))
        {
            await guard.DenyAsync(ctx, Permissions.TimesheetEdit, ct);
        }

        return ctx;
    }

    private async Task<AccessContext> DemandFullEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.TimesheetEdit, ct);
        if (await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.TimesheetEdit, ct) is not null)
        {
            throw new BusinessRuleException("hr.schedule.scope", "Графики работы общие для организации: их меняет пользователь с правом на все подразделения.");
        }

        return ctx;
    }

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private static string Inv(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Name(string last, string first, string? middle) => middle is null ? $"{last} {first}" : $"{last} {first} {middle}";
}
