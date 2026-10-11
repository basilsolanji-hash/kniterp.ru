using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

public sealed record StaffingRowDto(
    long Id, long DepartmentId, string DepartmentName, long PositionId, string PositionName,
    decimal Rates, decimal Occupied, decimal Salary, decimal Allowance, DateOnly ValidFrom, byte[] RowVersion)
{
    public decimal Vacant => Rates - Occupied;
    public decimal MonthlyFund => Rates * (Salary + Allowance);
}

public sealed record StaffingDto(
    string Organization, IReadOnlyList<StaffingRowDto> Rows, bool CanEdit, DateOnly Today,
    IReadOnlyList<Structure.LookupDto> Departments, IReadOnlyList<Structure.LookupDto> Positions)
{
    public decimal TotalRates => Rows.Sum(r => r.Rates);
    public decimal TotalFund => Rows.Sum(r => r.MonthlyFund);
}

/// <summary>
/// Штатное расписание (Т-3, D203). Смотрят и готовят приказы — с правом «Кадровые приказы: черновик»; изменяет — с правом проведения.
/// Пока в организации нет действующих строк, приём и перевод штатом не ограничиваются (организация переходит на штат постепенно).
/// </summary>
public sealed class StaffingService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<StaffingDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var rows = await (from s in db.StaffingPositions.AsNoTracking()
                          join d in db.Departments.AsNoTracking() on s.DepartmentId equals d.Id
                          join p in db.Positions.AsNoTracking() on s.PositionId equals p.Id
                          where s.OrganizationId == ctx.OrganizationId && !s.IsArchived
                          orderby d.Name, p.Name
                          select new { s, Department = d.Name, Position = p.Name }).ToListAsync(ct);
        var occupied = await StaffingRules.OccupancyAsync(db, ctx.OrganizationId, ct);
        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => new { o.FullName, o.TimeZoneId }).SingleAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId && !d.IsArchived)
            .OrderBy(d => d.Name).Select(d => new Structure.LookupDto(d.Id, d.Name)).ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.Name).Select(p => new Structure.LookupDto(p.Id, p.Name)).ToListAsync(ct);
        return new StaffingDto(org.FullName,
            rows.Select(r => new StaffingRowDto(r.s.Id, r.s.DepartmentId, r.Department, r.s.PositionId, r.Position, r.s.Rates,
                occupied.GetValueOrDefault((r.s.DepartmentId, r.s.PositionId)), r.s.Salary, r.s.Allowance, r.s.ValidFrom, r.s.RowVersion)).ToList(),
            ctx.Permissions.Has(Permissions.HrOrderPost),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, org.TimeZoneId)), departments, positions);
    }

    public async Task<long> AddAsync(long departmentId, long positionId, decimal rates, decimal salary, decimal allowance, DateOnly validFrom,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderPost, ct);
        await RequireStructureAsync(ctx, departmentId, positionId, ct);
        if (await db.StaffingPositions.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.DepartmentId == departmentId
                                                     && s.PositionId == positionId && !s.IsArchived, ct))
        {
            throw new BusinessRuleException("hr.staffing.duplicate", "Эта должность в подразделении уже есть в штатном расписании.");
        }

        var row = StaffingPosition.Create(ctx.OrganizationId, departmentId, positionId, rates, salary, allowance, validFrom);
        db.StaffingPositions.Add(row);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        // В журнал — без сумм: журнал читают шире, чем заработок (оклад одной ставки — это оклад конкретного человека).
        Audit(ctx, row.Id, null, $"{rates:0.##} ст.", "добавлена строка");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row.Id;
    }

    /// <summary>Изменение строки. Ставок нельзя сделать меньше, чем уже занято: сначала переведите или уволите сотрудников.</summary>
    public async Task ChangeAsync(long id, decimal rates, decimal salary, decimal allowance, DateOnly validFrom, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await StaffingRules.LockAsync(db, ctx.OrganizationId, ct);
        var row = await LoadAsync(ctx, id, rowVersion, ct);
        var occupied = (await StaffingRules.OccupancyAsync(db, ctx.OrganizationId, ct)).GetValueOrDefault((row.DepartmentId, row.PositionId));
        if (rates < occupied)
        {
            throw new BusinessRuleException("hr.staffing.below_occupied", $"Занято {occupied:0.##} ст. — штат не может быть меньше.");
        }

        var (before, after) = row.Change(rates, salary, allowance, validFrom);
        Audit(ctx, id, before, after, "изменена строка");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.HrOrderPost, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await StaffingRules.LockAsync(db, ctx.OrganizationId, ct);
        var row = await LoadAsync(ctx, id, rowVersion, ct);
        var occupied = (await StaffingRules.OccupancyAsync(db, ctx.OrganizationId, ct)).GetValueOrDefault((row.DepartmentId, row.PositionId));
        if (occupied > 0)
        {
            throw new BusinessRuleException("hr.staffing.occupied", "На должности работают сотрудники — строку нельзя убрать из штата.");
        }

        row.Archive();
        Audit(ctx, id, null, null, "строка в архиве");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
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

    private async Task<StaffingPosition> LoadAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var row = await db.StaffingPositions.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Строка штатного расписания");
        return row.RowVersion.AsSpan().SequenceEqual(rowVersion) ? row : throw new ConcurrencyConflictException();
    }

    private async Task RequireStructureAsync(AccessContext ctx, long departmentId, long positionId, CancellationToken ct)
    {
        _ = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId && d.OrganizationId == ctx.OrganizationId && !d.IsArchived, ct)
            ?? throw new NotFoundException("Подразделение");
        _ = await db.Positions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == positionId && p.OrganizationId == ctx.OrganizationId && !p.IsArchived, ct)
            ?? throw new NotFoundException("Должность");
    }

    private void Audit(AccessContext ctx, long id, string? before, string? after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.StaffingChanged, nameof(StaffingPosition),
            id.ToString(), before, after, reason, currentUser.CorrelationId));
}

/// <summary>Проверка штата при приёме и переводе (D203).</summary>
internal static class StaffingRules
{
    /// <summary>Блокировка штата организации до конца транзакции: проверка вакансии и запись назначения не перемежаются.</summary>
    public static Task LockAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        db.LockAsync($"kniterp.hr.staffing.{organizationId}", ct);

    /// <summary>
    /// Проверка уже записанного (в той же транзакции) состояния: по каждой затронутой должности занято не больше штата.
    /// Нужна массовым операциям (импорт), где вакансию по строкам не проверить заранее.
    /// </summary>
    public static async Task EnsureWithinAsync(IKnitErpDbContext db, long organizationId, IEnumerable<(long DepartmentId, long PositionId)> affected,
        CancellationToken ct)
    {
        var lines = await db.StaffingPositions.AsNoTracking().Where(s => s.OrganizationId == organizationId && !s.IsArchived).ToListAsync(ct);
        if (lines.Count == 0)
        {
            return;
        }

        var occupancy = await OccupancyAsync(db, organizationId, ct);
        foreach (var key in affected.Distinct())
        {
            var line = lines.FirstOrDefault(s => s.DepartmentId == key.DepartmentId && s.PositionId == key.PositionId)
                       ?? throw new BusinessRuleException("hr.staffing.no_position", "Этой должности нет в штатном расписании подразделения.");
            var occupied = occupancy.GetValueOrDefault(key);
            if (occupied > line.Rates)
            {
                throw new BusinessRuleException("hr.staffing.over", $"Сверх штата: по штату {line.Rates:0.##} ст., занято {occupied:0.##}.");
            }
        }
    }

    /// <summary>Занятые ставки по (подразделение, должность): ставка из последнего приёма или перевода; без истории — 1 ставка.</summary>
    public static async Task<Dictionary<(long DepartmentId, long PositionId), decimal>> OccupancyAsync(
        IKnitErpDbContext db, long organizationId, CancellationToken ct, long? exceptEmployeeId = null)
    {
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.Status != EmploymentStatus.Dismissed && e.Id != exceptEmployeeId)
            .Select(e => new { e.Id, e.DepartmentId, e.PositionId }).ToListAsync(ct);
        var rates = await db.EmploymentHistory.AsNoTracking()
            .Where(h => h.OrganizationId == organizationId && (h.Kind == PersonnelOrderKind.Hire || h.Kind == PersonnelOrderKind.Transfer))
            .GroupBy(h => h.EmployeeId)
            .Select(g => g.OrderByDescending(h => h.EffectiveFrom).ThenByDescending(h => h.Id).Select(h => new { h.EmployeeId, h.Rate }).First())
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Rate, ct);
        return employees.GroupBy(e => (e.DepartmentId, e.PositionId))
            .ToDictionary(g => g.Key, g => g.Sum(e => rates.GetValueOrDefault(e.Id, 1m)));
    }

    /// <summary>Сверх штата — отказ. Без действующих строк штата в организации проверка не выполняется.</summary>
    public static async Task EnsureVacancyAsync(IKnitErpDbContext db, long organizationId, long departmentId, long positionId, decimal rate,
        long? exceptEmployeeId, CancellationToken ct)
    {
        var lines = await db.StaffingPositions.AsNoTracking().Where(s => s.OrganizationId == organizationId && !s.IsArchived).ToListAsync(ct);
        if (lines.Count == 0)
        {
            return;
        }

        var line = lines.FirstOrDefault(s => s.DepartmentId == departmentId && s.PositionId == positionId)
                   ?? throw new BusinessRuleException("hr.staffing.no_position", "Этой должности нет в штатном расписании подразделения.");
        var occupied = (await OccupancyAsync(db, organizationId, ct, exceptEmployeeId)).GetValueOrDefault((departmentId, positionId));
        if (!line.Fits(occupied, rate))
        {
            throw new BusinessRuleException("hr.staffing.over", $"Сверх штата: по штату {line.Rates:0.##} ст., занято {occupied:0.##}.");
        }
    }
}
