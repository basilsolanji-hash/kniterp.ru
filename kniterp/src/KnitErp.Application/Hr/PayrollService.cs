using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

public sealed record PieceRateDto(long Id, long OperationId, string OperationName, long? ItemId, string Unit, decimal Price, DateOnly ValidFrom);

/// <summary>Операция из выработки производства за последние месяцы и есть ли на неё действующая расценка (D215).</summary>
public sealed record WorkOperationDto(long OperationId, string OperationName, long? ItemId, string Unit, decimal Quantity, bool HasRate);

public sealed record PieceRateCommand(long OperationId, string OperationName, long? ItemId, string Unit, decimal Price, DateOnly ValidFrom);

public sealed record PayrollEmployeeDto(long EmployeeId, string PersonnelNumber, string FullName, string Position, decimal Piece, decimal Salary,
    decimal Premiums, decimal Total);

public sealed record PayrollDto(
    long? Id, int Year, int Month, PayrollStatus? Status, bool TimesheetsClosed, DateTime? CalculatedAtUtc, IReadOnlyList<PayrollEmployeeDto> Employees,
    bool CanCalculate, byte[]? RowVersion, bool OutputChanged = false)
{
    public decimal Total => Employees.Sum(e => e.Total);
}

/// <summary>Итог расчёта: число сотрудников, сумма и предупреждения (нет расценки, нет табеля).</summary>
public sealed record PayrollCalculationDto(long RunId, int Employees, decimal Total, IReadOnlyList<string> Warnings);

public sealed record PayslipLineDto(PayrollLineKind Kind, string Description, decimal Quantity, decimal Rate, decimal Amount)
{
    public string KindName => PayrollRules.KindName(Kind);
}

/// <summary>Расчётный листок (ст. 136 ТК РФ): составные части заработка за месяц. НДФЛ и удержания — в 1С ЗУП (вне объёма, D213).</summary>
public sealed record PayslipDto(
    string Organization, string FullName, string PersonnelNumber, string Department, string Position, int Year, int Month, PayrollStatus Status,
    int DaysWorked, decimal HoursWorked, IReadOnlyList<PayslipLineDto> Lines)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}

/// <summary>
/// Заработок (ТЗ «Сотрудники», этап 4; D211–D213): сдельные расценки, расчёт за месяц по выработке из производства
/// (<see cref="IWorkOutputSource"/>) и табелю, доплаты по ст. 152–154 ТК РФ, утверждение, расчётный листок.
/// Считает Бухгалтер или Владелец; сотрудник видит только свой листок; каждый просмотр чужого листка — в журнал.
/// </summary>
public sealed class PayrollService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, TimesheetService timesheets, IWorkOutputSource output,
    ISpreadsheetFormat spreadsheet)
{
    /// <summary>
    /// Расчёт месяца в Excel для сверки с данными фабрики (приёмка, D216): строка на каждое начисление — табельный номер, ФИО, вид,
    /// расчёт, количество, ставка, сумма. Суммы видны только с правом просмотра заработка; выгрузка — в журнал.
    /// </summary>
    public async Task<(string FileName, byte[] Content)> ExportAsync(int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollView, ct);
        var run = await db.PayrollRuns.AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Year == year && r.Month == month, ct)
                  ?? throw new NotFoundException("Расчёт");
        var rows = await (from l in db.PayrollLines.AsNoTracking()
                          join e in db.Employees.AsNoTracking() on l.EmployeeId equals e.Id
                          where l.RunId == run.Id
                          orderby e.LastName, e.FirstName, l.Kind, l.Id
                          select new { e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName, l.Kind, l.Description, l.Quantity, l.Rate, l.Amount }).ToListAsync(ct);
        static string Inv(decimal v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        var content = spreadsheet.Write([new SheetData($"Заработок {month:00}.{year}",
            ["Таб. №", "ФИО", "Вид начисления", "Расчёт", "Количество", "Ставка", "Сумма"],
            rows.Select(r => (IReadOnlyList<string>)[r.PersonnelNumber, Name(r.LastName, r.FirstName, r.MiddleName), PayrollRules.KindName(r.Kind),
                r.Description, Inv(r.Quantity), Inv(r.Rate), Inv(r.Amount)]).ToList(), new HashSet<int> { 4, 5, 6 })]);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PayrollExported, nameof(PayrollRun), run.Id.ToString(),
            null, $"{month:00}.{year}: строк {rows.Count}", "Excel для сверки", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return ($"payroll-{year}-{month:00}.xlsx", content);
    }

    public async Task<IReadOnlyList<PieceRateDto>> RatesAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        return await db.PieceRates.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId)
            .OrderBy(r => r.OperationName).ThenBy(r => r.ItemId).ThenByDescending(r => r.ValidFrom)
            .Select(r => new PieceRateDto(r.Id, r.OperationId, r.OperationName, r.ItemId, r.Unit, r.Price, r.ValidFrom)).ToListAsync(ct);
    }

    /// <summary>
    /// Операции, по которым производство отметило выработку за 3 последних месяца, — подсказка для расценок: код операции и единица
    /// берутся из производства, а не вводятся вручную. HasRate — есть ли расценка изделия или общая расценка операции на сегодня.
    /// </summary>
    public async Task<IReadOnlyList<WorkOperationDto>> OperationsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
        var rows = await output.OutputAsync(ctx.OrganizationId, new DateOnly(today.Year, today.Month, 1).AddMonths(-2), today, ct);
        var rates = await db.PieceRates.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId).ToListAsync(ct);
        return rows.GroupBy(r => (r.OperationId, r.ItemId, Unit: PieceRate.NormalizeUnit(r.Unit)))
            .Select(g => new WorkOperationDto(g.Key.OperationId, g.First().OperationName, g.Key.ItemId, g.Key.Unit, g.Sum(r => r.Quantity),
                PieceRate.Find(rates, g.Key.OperationId, g.Key.ItemId, today) is { } rate && PieceRate.NormalizeUnit(rate.Unit) == g.Key.Unit))
            .OrderBy(o => o.HasRate).ThenBy(o => o.OperationId).ThenBy(o => o.ItemId).ToList();
    }

    public async Task<long> AddRateAsync(PieceRateCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollCalculate, ct);
        var rate = PieceRate.Create(ctx.OrganizationId, cmd.OperationId, cmd.OperationName, cmd.ItemId, cmd.Unit, cmd.Price, cmd.ValidFrom, ctx.UserId, clock.UtcNow);
        if (await db.PieceRates.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && r.OperationId == rate.OperationId && r.ItemId == rate.ItemId
                                              && r.ValidFrom == rate.ValidFrom, ct))
        {
            throw new BusinessRuleException("hr.rate.duplicate", "Расценка на эту операцию с этой даты уже есть: новую цену укажите с другой даты.");
        }

        db.PieceRates.Add(rate);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PieceRateAdded, nameof(PieceRate), rate.Id.ToString(),
            null, $"{rate.OperationName}: {rate.Price:0.####} руб./{rate.Unit} с {rate.ValidFrom:dd.MM.yyyy}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rate.Id;
    }

    public async Task<PayrollDto> GetAsync(int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollView, ct);
        var run = await db.PayrollRuns.AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Year == year && r.Month == month, ct);
        var rows = new List<PayrollEmployeeDto>();
        if (run is not null)
        {
            var lines = await db.PayrollLines.AsNoTracking().Where(l => l.RunId == run.Id).ToListAsync(ct);
            var ids = lines.Select(l => l.EmployeeId).Distinct().ToList();
            var employees = await (from e in db.Employees.AsNoTracking()
                                   join p in db.Positions.AsNoTracking() on e.PositionId equals p.Id
                                   where ids.Contains(e.Id)
                                   select new { e.Id, e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName, Position = p.Name }).ToListAsync(ct);
            rows = employees.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).Select(e =>
            {
                var own = lines.Where(l => l.EmployeeId == e.Id).ToList();
                var piece = own.Where(l => l.Kind == PayrollLineKind.Piece).Sum(l => l.Amount);
                var salary = own.Where(l => l.Kind == PayrollLineKind.Salary).Sum(l => l.Amount);
                return new PayrollEmployeeDto(e.Id, e.PersonnelNumber, Name(e.LastName, e.FirstName, e.MiddleName), e.Position, piece, salary,
                    own.Sum(l => l.Amount) - piece - salary, own.Sum(l => l.Amount));
            }).ToList();
        }

        var outputChanged = run is not null && !run.OutputMatches(await OutputFingerprintAsync(ctx.OrganizationId, run, ct));
        return new PayrollDto(run?.Id, year, month, run?.Status, run?.TimesheetsClosed ?? false, run?.CalculatedAtUtc, rows,
            ctx.Permissions.Has(Permissions.PayrollCalculate) && run?.Status != PayrollStatus.Approved, run?.RowVersion, outputChanged);
    }

    private async Task<byte[]> OutputFingerprintAsync(long org, PayrollRun run, CancellationToken ct) =>
        WorkOutputFingerprint.Of((await output.OutputAsync(org, run.From, run.To, ct)).Where(o => o.Date >= run.From && o.Date <= run.To));

    /// <summary>
    /// Расчёт (или пересчёт черновика) за месяц: сдельно = Σ выработка × расценка на дату; оклад × отработано / норма;
    /// доплаты: ночные, сверхурочные (по дням), выходные и праздники. Часовая ставка — оклад / норма, у сдельщика без оклада —
    /// сдельный заработок / отработанные часы.
    /// </summary>
    public async Task<PayrollCalculationDto> CalculateAsync(int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollCalculate, ct);
        var org = ctx.OrganizationId;
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.hr.payroll.{org}.{year}.{month}", ct);
        var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.OrganizationId == org && r.Year == year && r.Month == month, ct);
        if (run is null)
        {
            run = PayrollRun.Create(org, year, month, ctx.UserId, clock.UtcNow);
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            run.EnsureDraft();
            await db.PayrollLines.Where(l => l.RunId == run.Id).ExecuteDeleteAsync(ct);
        }

        var from = run.From;
        var to = run.To;
        var warnings = new List<string>();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == org && e.HiredOn <= to && (e.DismissedOn == null || e.DismissedOn >= from)).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToHashSet();
        var (days, closed) = await timesheets.DaysAsync(org, from, to, ct);
        var periodOutput = (await output.OutputAsync(org, from, to, ct)).Where(o => o.Date >= from && o.Date <= to).ToList();
        var outputs = periodOutput.Where(o => ids.Contains(o.EmployeeId)).ToList();
        var rates = await db.PieceRates.AsNoTracking().Where(r => r.OrganizationId == org && r.ValidFrom <= to).ToListAsync(ct);
        var assignments = (await db.EmploymentHistory.AsNoTracking()
                .Where(h => h.OrganizationId == org && ids.Contains(h.EmployeeId) && h.EffectiveFrom <= to
                            && (h.Kind == PersonnelOrderKind.Hire || h.Kind == PersonnelOrderKind.Transfer)).ToListAsync(ct))
            .GroupBy(h => h.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id).First());
        var norms = await timesheets.NormAsync(org, ids, from, to, assignments.ToDictionary(a => a.Key, a => a.Value.Rate), ct);
        var settings = (await HrSettingsService.ForOrganizationAsync(db, org, ct)).Payroll; // D216: доплаты и база оклада организации
        var nightShare = settings.NightPercent / 100m;
        if (days.Count == 0)
        {
            warnings.Add($"За {month:00}.{year} нет табелей: оплата по окладу и доплаты не начислены.");
        }

        decimal total = 0;
        var paid = 0;
        foreach (var e in employees.OrderBy(e => e.LastName))
        {
            var lines = new List<PayrollLine>();
            var own = days.Where(d => d.EmployeeId == e.Id).ToList();
            var worked = own.Where(d => TimesheetCodes.IsWorked(d.Code)).ToList();
            var workedHours = worked.Sum(d => d.Hours);

            decimal piece = 0;
            foreach (var g in outputs.Where(o => o.EmployeeId == e.Id).GroupBy(o => (o.OperationId, o.ItemId, Unit: PieceRate.NormalizeUnit(o.Unit))))
            {
                foreach (var byRate in g.GroupBy(o => PieceRate.Find(rates, o.OperationId, o.ItemId, o.Date)))
                {
                    var qty = byRate.Sum(o => o.Quantity);
                    if (byRate.Key is not { } rate)
                    {
                        warnings.Add($"{e.FullName}: нет расценки на «{g.First().OperationName}» — {qty:0.###} {g.Key.Unit} не оплачено.");
                        continue;
                    }

                    if (PieceRate.NormalizeUnit(rate.Unit) != g.Key.Unit)
                    {
                        warnings.Add($"{e.FullName}: «{rate.OperationName}» — выработка в «{g.Key.Unit}», расценка в «{rate.Unit}»: не оплачено.");
                        continue;
                    }

                    var amount = Money.Round(qty * rate.Price);
                    piece += amount;
                    lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Piece, $"{rate.OperationName}, {rate.Unit}", qty, rate.Price, amount));
                }
            }

            var salary = assignments.TryGetValue(e.Id, out var a) ? a.Salary ?? 0 : 0;
            var (norm, normDays) = norms.GetValueOrDefault(e.Id);
            if (salary > 0 && workedHours > 0)
            {
                if (settings.SalaryBase == SalaryBase.Days)
                {
                    lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Salary, $"Оклад {salary:0.##} × {worked.Count} дн. / {normDays} дн.", worked.Count,
                        normDays > 0 ? salary / normDays : 0, PayrollRules.SalaryPart(salary, worked.Count, normDays)));
                }
                else
                {
                    lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Salary, $"Оклад {salary:0.##} × {workedHours:0.##} ч / {norm:0.##} ч", workedHours,
                        norm > 0 ? salary / norm : 0, PayrollRules.SalaryPart(salary, workedHours, norm)));
                }
            }

            // Часовая ставка для доплат: оклад / норма; у сдельщика — тарифная ставка из настроек, иначе сдельный заработок / часы.
            var hourRate = salary > 0 && norm > 0 ? salary / norm
                : settings.PieceHourlyRate is { } tariff ? tariff
                : workedHours > 0 ? piece / workedHours : 0;
            var night = worked.Sum(d => d.NightHours);
            if (night > 0 && hourRate > 0)
            {
                lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Night, $"Ночные часы × {settings.NightPercent:0.##} % часовой ставки", night,
                    hourRate * nightShare, night * hourRate * nightShare));
            }

            var overtime = worked.Where(d => d.OvertimeHours > 0).ToList();
            if (overtime.Count > 0 && hourRate > 0)
            {
                lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Overtime,
                    $"Первые 2 часа в день × {settings.OvertimeFirst:0.##}, далее × {settings.OvertimeNext:0.##}", overtime.Sum(d => d.OvertimeHours), hourRate,
                    overtime.Sum(d => PayrollRules.OvertimeDay(d.OvertimeHours, hourRate, settings.OvertimeFirst, settings.OvertimeNext))));
            }

            var holiday = worked.Where(d => d.Code == TimesheetCodes.Holiday).Sum(d => d.Hours);
            if (holiday > 0 && hourRate > 0)
            {
                var extra = settings.HolidayMultiplier - 1;
                lines.Add(PayrollLine.Create(org, run.Id, e.Id, PayrollLineKind.Holiday, $"Доплата до {settings.HolidayMultiplier:0.##}-кратного размера за часы в праздники и выходные",
                    holiday, hourRate * extra, holiday * hourRate * extra));
            }

            if (lines.Count > 0)
            {
                db.PayrollLines.AddRange(lines);
                total += lines.Sum(l => l.Amount);
                paid++;
            }
        }

        run.Recalculated(closed, WorkOutputFingerprint.Of(periodOutput), ctx.UserId, clock.UtcNow);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.PayrollCalculated, nameof(PayrollRun), run.Id.ToString(), null,
            $"{month:00}.{year}: сотрудников {paid}", closed ? null : "табели не закрыты", currentUser.CorrelationId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }

        await tx.CommitAsync(ct);
        return new PayrollCalculationDto(run.Id, paid, total, warnings);
    }

    public async Task ApproveAsync(long runId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollCalculate, ct);
        var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Расчёт");
        if (!run.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            throw new ConcurrencyConflictException();
        }

        // Табели могли измениться после расчёта (исправительный табель, новое закрытие) — тогда суммы уже не те (D212).
        var (closed, changedAt) = await timesheets.StateAsync(ctx.OrganizationId, run.From, run.To, ct);
        if (closed && run.TimesheetsClosed && changedAt > run.CalculatedAtUtc || !closed && run.TimesheetsClosed)
        {
            db.DiscardChanges();
            throw new BusinessRuleException("hr.payroll.stale", "Табели месяца изменились после расчёта: пересчитайте и утвердите снова.");
        }

        // Выработку производства исправили после расчёта — сдельные суммы уже не те (D217).
        if (!run.OutputMatches(await OutputFingerprintAsync(ctx.OrganizationId, run, ct)))
        {
            db.DiscardChanges();
            throw new BusinessRuleException("hr.payroll.output_changed", "Выработка месяца изменилась после расчёта: пересчитайте и утвердите снова.");
        }

        try
        {
            run.Approve(ctx.UserId, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PayrollApproved, nameof(PayrollRun), runId.ToString(),
            "черновик", "утверждён", $"{run.Month:00}.{run.Year}", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Расчётный листок сотрудника. Свой — с уровнем «С»; чужой — с правом просмотра заработка, каждый просмотр в журнал.</summary>
    public async Task<PayslipDto> PayslipAsync(int year, int month, long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var own = employee.UserId == ctx.UserId;
        if (!ctx.Permissions.Has(Permissions.PayrollView))
        {
            if (!own || !ctx.Permissions.HasOwnOnly(Permissions.PayrollView))
            {
                // Чужой листок без права — как несуществующий: не подтверждаем, что такой сотрудник есть.
                if (ctx.Permissions.HasOwnOnly(Permissions.PayrollView))
                {
                    throw new NotFoundException("Сотрудник");
                }

                await guard.DenyAsync(ctx, Permissions.PayrollView, ct);
            }
        }

        var run = await db.PayrollRuns.AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Year == year && r.Month == month, ct)
                  ?? throw new NotFoundException("Расчёт");
        if (own && !ctx.Permissions.Has(Permissions.PayrollView) && run.Status != PayrollStatus.Approved)
        {
            // Сотрудник видит листок только после утверждения: черновик ещё может измениться (D213).
            throw new NotFoundException("Расчёт");
        }

        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.RunId == run.Id && l.EmployeeId == employeeId).OrderBy(l => l.Kind).ThenBy(l => l.Id)
            .Select(l => new PayslipLineDto(l.Kind, l.Description, l.Quantity, l.Rate, l.Amount)).ToListAsync(ct);
        var names = await (from d in db.Departments.AsNoTracking()
                           from p in db.Positions.AsNoTracking()
                           where d.Id == employee.DepartmentId && p.Id == employee.PositionId
                           select new { Department = d.Name, Position = p.Name }).SingleAsync(ct);
        // Утверждённый листок — по тем версиям табелей, по которым считали; черновик — по последним.
        var (days, _) = await timesheets.DaysAsync(ctx.OrganizationId, run.From, run.To, ct,
            run.Status == PayrollStatus.Approved ? run.CalculatedAtUtc : null);
        var worked = days.Where(d => d.EmployeeId == employeeId && TimesheetCodes.IsWorked(d.Code)).ToList();
        var orgName = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.FullName).SingleAsync(ct);
        if (!own)
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PayslipViewed, nameof(Employee), employeeId.ToString(),
                null, $"{month:00}.{year}", employee.FullName, currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
        }

        return new PayslipDto(orgName, employee.FullName, employee.PersonnelNumber, names.Department, names.Position, year, month, run.Status,
            worked.Count, worked.Sum(d => d.Hours + d.OvertimeHours), lines);
    }

    /// <summary>Свой расчётный листок (личный кабинет).</summary>
    public async Task<PayslipDto> MyPayslipAsync(int year, int month, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var id = await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId).Select(e => (long?)e.Id)
                     .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Сотрудник");
        return await PayslipAsync(year, month, id, ct);
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.PayrollView) && !ctx.Permissions.Has(Permissions.PayrollCalculate))
        {
            await guard.DenyAsync(ctx, Permissions.PayrollView, ct);
        }

        return ctx;
    }

    private static string Name(string last, string first, string? middle) => middle is null ? $"{last} {first}" : $"{last} {first} {middle}";
}
