using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Показатели сотрудника за период: дисциплина (сменные задания) и качество (брак по его вине), баллы — по настройкам организации.</summary>
public sealed record EmployeeScoreDto(
    long EmployeeId, string Name, string PersonnelNumber, int TasksDone, int MissedWithReason, int MissedNoReason, int InProgress,
    int AveragePercent, decimal DefectUnits, int? Points);

public sealed record ScoreBoardDto(
    DateOnly From, DateOnly To, bool PointsEnabled, IReadOnlyList<EmployeeScoreDto> Rows, ProductionSettingsDto Rules);

/// <summary>
/// Баллы и показатели качества и дисциплины (D92, дополнение; правила — в настройках производства организации).
/// Задание выполнено — факт не меньше плана в любой день; не выполнено — день прошёл, факт меньше плана (с причиной или без);
/// сегодняшнее невыполненное — «в работе», баллов не меняет. Брак — единицы, где сотрудник указан виновником на контроле качества.
/// Мастер видит всех (право просмотра заказов), сотрудник на терминале — только себя.
/// </summary>
public sealed class ProductionScoreService(IKnitErpDbContext db, IAccessGuard guard, IClock clock)
{
    /// <summary>Самый длинный период отчёта — чтобы запрос не уходил на годы.</summary>
    public const int MaxDays = 93;

    public async Task<ScoreBoardDto> BoardAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        return await BuildAsync(ctx, from, to, null, ct);
    }

    /// <summary>Свои показатели за месяц, в котором <paramref name="day"/> (по умолчанию — текущий).</summary>
    public async Task<EmployeeScoreDto?> MineAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionStageMark, ct);
        var employee = await ShopFloorService.EmployeeIdAsync(db, ctx, ct);
        if (employee is null)
        {
            return null;
        }

        var d = day ?? await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        var from = new DateOnly(d.Year, d.Month, 1);
        var board = await BuildAsync(ctx, from, from.AddMonths(1).AddDays(-1), employee, ct);
        return board.Rows.SingleOrDefault();
    }

    private async Task<ScoreBoardDto> BuildAsync(AccessContext ctx, DateOnly from, DateOnly to, long? employeeId, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxDays)
        {
            throw new BusinessRuleException("production.score.period", "Период — от одного дня до трёх месяцев.");
        }

        var settings = await ProductionSettingsService.LoadAsync(db, ctx.OrganizationId, ct);
        var today = await ShopFloorService.TodayAsync(db, ctx, clock, ct);
        var tasks = (await ShiftTaskService.ListAsync(db, ctx.OrganizationId, from, to, employeeId, ct)).Where(t => !t.IsCancelled).ToList();
        var defects = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.Kind == ProductionOperationKind.Defect && o.CulpritEmployeeId != null
                        && o.OccurredOn >= from && o.OccurredOn <= to && (employeeId == null || o.CulpritEmployeeId == employeeId))
            .GroupBy(o => o.CulpritEmployeeId!.Value).Select(g => new { g.Key, Sum = g.Sum(o => o.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        // Свои показатели видны и с нулями — сотрудник понимает, что считать нечего, а не что расчёт сломан.
        var people = tasks.Select(t => t.EmployeeId).Concat(defects.Keys).Concat(employeeId is { } me ? [me] : []).Distinct().ToList();
        var names = (await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && people.Contains(e.Id))
                .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct))
            .ToDictionary(e => e.Id, e => (Name: ShopFloorService.ShortName(e.LastName, e.FirstName, e.MiddleName), e.PersonnelNumber));

        var rows = people.Where(names.ContainsKey).Select(id =>
            {
                var mine = tasks.Where(t => t.EmployeeId == id).ToList();
                var done = mine.Count(t => t.Done);
                var missed = mine.Where(t => !t.Done && t.Date < today).ToList();
                var withReason = missed.Count(t => t.ShortfallReason is not null);
                var noReason = missed.Count - withReason;
                var inProgress = mine.Count(t => !t.Done && t.Date >= today);
                var closed = mine.Where(t => t.Done || t.Date < today).ToList();
                var average = closed.Count == 0 ? 0 : (int)Math.Round(closed.Average(t => (double)t.Percent), MidpointRounding.AwayFromZero);
                var defectUnits = defects.GetValueOrDefault(id);
                return new EmployeeScoreDto(id, names[id].Name, names[id].PersonnelNumber, done, withReason, noReason, inProgress, average, defectUnits,
                    settings.PointsEnabled ? settings.Points(done, withReason, noReason, defectUnits) : null);
            })
            .OrderByDescending(r => r.Points ?? 0).ThenBy(r => r.Name).ToList();
        var rules = new ProductionSettingsDto(settings.TolerancePercent, settings.PointsEnabled, settings.PointsTaskDone, settings.PointsTaskMissedWithReason,
            settings.PointsTaskMissedNoReason, settings.PointsPerDefectUnit, null, settings.UpdatedAtUtc, null, false);
        return new ScoreBoardDto(from, to, settings.PointsEnabled, rows, rules);
    }
}
