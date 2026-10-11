using KnitErp.Application.Common;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Finance;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

/// <summary>Сотрудник, не отчитавшийся вовремя: Amount — его долг по подотчёту, Deadline — последний день для авансового отчёта.</summary>
public sealed record OverdueAdvanceDto(long EmployeeId, string Employee, decimal Amount, DateOnly Deadline);

/// <summary>Касса, где остаток выше установленного лимита.</summary>
public sealed record CashOverLimitDto(long AccountId, string Cash, string LegalEntity, decimal Balance, decimal Limit);

public sealed record MoneyAlertsDto(IReadOnlyList<OverdueAdvanceDto> OverdueAdvances, decimal AccountableDebt, IReadOnlyList<CashOverLimitDto> CashOverLimit);

/// <summary>
/// Напоминания по деньгам (D87). Срок авансового отчёта — 3 рабочих дня после срока, на который выдан аванс (п. 6.3 Указания
/// Банка России № 3210-У); срок не указан при выдаче — 30 дней (допущение D87). Долг погашается по порядку выдач (ФИФО):
/// возвраты и утверждённые отчёты закрывают сначала самую раннюю выдачу, и срок отчёта считается от первой непогашенной.
/// </summary>
public static class MoneyAlerts
{
    public const int DefaultAdvanceDays = 30;
    public const int ReportWorkingDays = 3;

    private sealed record AdvanceOp(long EmployeeId, MoneyOperationKind Kind, DateOnly Date, DateOnly? Due, decimal Amount, long Id);

    /// <summary>
    /// Все напоминания за несколько запросов: выдачи и возвраты подотчётных, утверждённые отчёты, страна, лимиты касс; остатки касс —
    /// только если хоть у одной кассы задан лимит; имена — только просрочивших.
    /// </summary>
    internal static async Task<MoneyAlertsDto> ComputeAsync(IKnitErpDbContext db, long org, DateOnly today, CancellationToken ct)
    {
        var (ops, reported, country) = await LoadAsync(db, org, ct);
        var deadlines = Deadlines(ops, reported, country);
        var debts = ops.GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key,
            g => g.Where(o => o.Kind == MoneyOperationKind.Expense).Sum(o => o.Amount) - g.Where(o => o.Kind == MoneyOperationKind.Income).Sum(o => o.Amount)
                 - reported.GetValueOrDefault(g.Key));
        var overdueIds = debts.Where(d => d.Value > 0 && deadlines.TryGetValue(d.Key, out var dl) && dl < today).Select(d => d.Key).ToList();
        var names = overdueIds.Count == 0
            ? []
            : (await db.Employees.AsNoTracking().Where(e => overdueIds.Contains(e.Id)).Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName })
                .ToListAsync(ct)).ToDictionary(e => e.Id, e => AccountableService.Name(e.LastName, e.FirstName, e.MiddleName));
        var overdue = overdueIds.Select(id => new OverdueAdvanceDto(id, names[id], debts[id], deadlines[id])).OrderBy(o => o.Deadline).ToList();

        var limits = await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.CashLimit != null && !a.IsArchived)
            .Select(a => new { a.Id, a.CashLimit }).ToDictionaryAsync(a => a.Id, a => a.CashLimit!.Value, ct);
        var over = limits.Count == 0
            ? []
            : (await MoneyService.BalancesAsync(db, org, today, includeArchived: false, ct))
            .Where(b => b.AccountId is { } id && limits.TryGetValue(id, out var limit) && b.Balance > limit)
            .Select(b => new CashOverLimitDto(b.AccountId!.Value, b.Name, b.LegalEntity, b.Balance, limits[b.AccountId!.Value])).ToList();

        return new MoneyAlertsDto(overdue, debts.Values.Where(d => d > 0).Sum(), over);
    }

    /// <summary>Последний день авансового отчёта по каждому сотруднику с непогашенной выдачей.</summary>
    internal static async Task<Dictionary<long, DateOnly>> DeadlinesAsync(IKnitErpDbContext db, long org, CancellationToken ct)
    {
        var (ops, reported, country) = await LoadAsync(db, org, ct);
        return Deadlines(ops, reported, country);
    }

    private static async Task<(List<AdvanceOp> Ops, Dictionary<long, decimal> Reported, string Country)> LoadAsync(IKnitErpDbContext db, long org,
        CancellationToken ct)
    {
        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CountryCode).SingleAsync(ct);
        var ops = (await db.MoneyOperations.AsNoTracking()
                .Where(o => o.OrganizationId == org && o.EmployeeId != null && o.Status == MoneyOperationStatus.Posted)
                .Select(o => new { EmployeeId = o.EmployeeId!.Value, o.Kind, o.OperationDate, o.AdvanceDueDate, o.Amount, o.Id })
                .ToListAsync(ct))
            .Select(o => new AdvanceOp(o.EmployeeId, o.Kind, o.OperationDate, o.AdvanceDueDate, o.Amount, o.Id)).ToList();
        var reported = ops.Count == 0
            ? []
            : await db.ExpenseReports.AsNoTracking()
                .Where(r => r.OrganizationId == org && r.Status == ExpenseReportStatus.Approved)
                .GroupBy(r => r.EmployeeId).Select(g => new { g.Key, Sum = g.SelectMany(r => r.Lines).Sum(l => l.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return (ops, reported, country);
    }

    private static Dictionary<long, DateOnly> Deadlines(List<AdvanceOp> ops, Dictionary<long, decimal> reported, string country)
    {
        var result = new Dictionary<long, DateOnly>();
        foreach (var employee in ops.GroupBy(o => o.EmployeeId))
        {
            var covered = employee.Where(o => o.Kind == MoneyOperationKind.Income).Sum(o => o.Amount) + reported.GetValueOrDefault(employee.Key);
            var issued = 0m;
            foreach (var issue in employee.Where(o => o.Kind == MoneyOperationKind.Expense).OrderBy(o => o.Date).ThenBy(o => o.Id))
            {
                issued += issue.Amount;
                if (issued > covered)
                {
                    var due = issue.Due ?? issue.Date.AddDays(DefaultAdvanceDays);
                    result[employee.Key] = ProductionCalendar.AddWorkingDays(country, due, ReportWorkingDays);
                    break;
                }
            }
        }

        return result;
    }
}
