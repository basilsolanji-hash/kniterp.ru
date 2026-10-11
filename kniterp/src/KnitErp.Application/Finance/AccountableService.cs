using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

/// <summary>Расчёты с подотчётным лицом: выдано под отчёт, возвращено, отчитано (утверждённые авансовые отчёты). Debt больше нуля — долг сотрудника, меньше — перерасход (долг организации).</summary>
public sealed record AccountableBalanceDto(long EmployeeId, string Employee, string PersonnelNumber, decimal Issued, decimal Returned, decimal Reported,
    DateOnly? Deadline = null)
{
    public decimal Debt => Issued - Returned - Reported;
}

public sealed record ExpenseReportRowDto(
    long Id, string Number, DateOnly Date, long EmployeeId, string Employee, string LegalEntity, decimal Total, ExpenseReportStatus Status, string? Purpose)
{
    public string StatusName => ExpenseReport.StatusName(Status);
}

public sealed record ExpenseReportLineDto(long Id, DateOnly DocumentDate, string Document, string? Description, decimal Amount, long CashFlowItemId,
    string CashFlowItem);

public sealed record ExpenseReportDto(
    long Id, string Number, DateOnly Date, long EmployeeId, string Employee, string? Position, string PersonnelNumber, long LegalEntityId, string LegalEntity,
    string LegalEntityFull, string? Inn, string? DirectorName, string? AccountantName, string? Purpose, ExpenseReportStatus Status, string? CancelReason,
    IReadOnlyList<ExpenseReportLineDto> Lines, decimal IssuedBefore, decimal Total, decimal DebtBefore, string CreatedBy, string? ApprovedBy,
    DateTime? ApprovedAtUtc, bool CanEdit, byte[] RowVersion)
{
    public string StatusName => ExpenseReport.StatusName(Status);

    /// <summary>
    /// После отчёта: больше нуля — сотрудник вернёт остаток в кассу, меньше — перерасход, организация доплатит.
    /// DebtBefore этот отчёт не включает ни у черновика, ни у утверждённого.
    /// </summary>
    public decimal DebtAfter => DebtBefore - Total;
}

public sealed record AccountableOptionsDto(
    IReadOnlyList<KnitErp.Application.Structure.LookupDto> Employees, IReadOnlyList<KnitErp.Application.Structure.LookupDto> LegalEntities,
    IReadOnlyList<CashFlowItemDto> ExpenseItems);

/// <summary>
/// Подотчётные лица и авансовые отчёты (D86). Выдача под отчёт и возврат остатка — денежные операции со статьями «Выдача под отчёт»
/// и «Возврат подотчётных сумм» и сотрудником; расход подтверждается авансовым отчётом (АО-1, постановление Госкомстата России № 55):
/// черновик → утверждён (уменьшает долг) → при ошибке отменён с причиной. Права — как у денежных операций: смотреть — «Цены и суммы»,
/// составлять и утверждать — вместе с правом записывать оплаты продаж или закупок.
/// </summary>
public sealed class AccountableService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRows = 1000;

    public async Task<IReadOnlyList<AccountableBalanceDto>> BalancesAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var rows = await BalancesAsync(db, ctx.OrganizationId, asOf, null, ct);
        if (asOf is not null)
        {
            return rows;
        }

        // D87: последний день авансового отчёта — по первой непогашенной выдаче.
        var deadlines = await MoneyAlerts.DeadlinesAsync(db, ctx.OrganizationId, ct);
        return rows.Select(r => r.Debt > 0 && deadlines.TryGetValue(r.EmployeeId, out var d) ? r with { Deadline = d } : r).ToList();
    }

    /// <summary>Напоминания по деньгам: просроченные авансовые отчёты, долг подотчётных, кассы сверх лимита (D87).</summary>
    public async Task<MoneyAlertsDto> AlertsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return await MoneyAlerts.ComputeAsync(db, ctx.OrganizationId, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz)), ct);
    }

    internal static async Task<IReadOnlyList<AccountableBalanceDto>> BalancesAsync(IKnitErpDbContext db, long org, DateOnly? asOf, long? employeeId,
        CancellationToken ct)
    {
        var ops = await db.MoneyOperations.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.EmployeeId != null && o.Status == MoneyOperationStatus.Posted && (asOf == null || o.OperationDate <= asOf)
                        && (employeeId == null || o.EmployeeId == employeeId))
            .GroupBy(o => new { o.EmployeeId, o.Kind }).Select(g => new { g.Key.EmployeeId, g.Key.Kind, Sum = g.Sum(o => o.Amount) }).ToListAsync(ct);
        var reported = await db.ExpenseReports.AsNoTracking()
            .Where(r => r.OrganizationId == org && r.Status == ExpenseReportStatus.Approved && (asOf == null || r.ReportDate <= asOf)
                        && (employeeId == null || r.EmployeeId == employeeId))
            .GroupBy(r => r.EmployeeId).Select(g => new { g.Key, Sum = g.SelectMany(r => r.Lines).Sum(l => l.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var ids = ops.Select(o => o.EmployeeId!.Value).Concat(reported.Keys).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToDictionaryAsync(e => e.Id, ct);
        return ids.Select(id => new AccountableBalanceDto(id, Name(employees[id].LastName, employees[id].FirstName, employees[id].MiddleName),
                employees[id].PersonnelNumber,
                ops.Where(o => o.EmployeeId == id && o.Kind == MoneyOperationKind.Expense).Sum(o => o.Sum),
                ops.Where(o => o.EmployeeId == id && o.Kind == MoneyOperationKind.Income).Sum(o => o.Sum),
                reported.GetValueOrDefault(id)))
            .OrderByDescending(b => b.Debt).ThenBy(b => b.Employee).ToList();
    }

    /// <summary>Долг сотрудника по подотчёту сейчас: выдано − возвращено − утверждено в отчётах.</summary>
    internal static async Task<decimal> DebtAsync(IKnitErpDbContext db, long org, long employeeId, CancellationToken ct) =>
        (await BalancesAsync(db, org, null, employeeId, ct)).SingleOrDefault()?.Debt ?? 0;

    public async Task<AccountableOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        return await OptionsAsync(db, ctx.OrganizationId, ct);
    }

    internal static async Task<AccountableOptionsDto> OptionsAsync(IKnitErpDbContext db, long org, CancellationToken ct)
    {
        var employees = (await db.Employees.AsNoTracking().Where(e => e.OrganizationId == org && e.Status != EmploymentStatus.Dismissed)
                .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName, e.PersonnelNumber }).ToListAsync(ct))
            .Select(e => new KnitErp.Application.Structure.LookupDto(e.Id, $"{Name(e.LastName, e.FirstName, e.MiddleName)} ({e.PersonnelNumber})"))
            .OrderBy(e => e.Name).ToList();
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && !e.IsArchived)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName)
            .Select(e => new KnitErp.Application.Structure.LookupDto(e.Id, e.ShortName)).ToListAsync(ct);
        var items = await db.CashFlowItems.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && i.Direction == CashFlowDirection.Out
                        && i.SystemCode != CashFlowItem.SupplierPayments && i.SystemCode != CashFlowItem.AccountableIssue)
            .OrderBy(i => i.SystemCode != null).ThenBy(i => i.Name)
            .Select(i => new CashFlowItemDto(i.Id, i.Direction, i.Name, i.SystemCode, i.IsArchived, i.RowVersion)).ToListAsync(ct);
        return new AccountableOptionsDto(employees, entities, items);
    }

    public async Task<IReadOnlyList<ExpenseReportRowDto>> ListReportsAsync(long? employeeId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var rows = await (from r in db.ExpenseReports.AsNoTracking()
                          where r.OrganizationId == ctx.OrganizationId && (employeeId == null || r.EmployeeId == employeeId)
                          join e in db.Employees.AsNoTracking() on r.EmployeeId equals e.Id
                          join le in db.LegalEntities.AsNoTracking() on r.LegalEntityId equals le.Id
                          orderby r.ReportDate descending, r.Id descending
                          select new
                          {
                              r.Id, r.Number, r.ReportDate, r.EmployeeId, e.LastName, e.FirstName, e.MiddleName, le.ShortName,
                              Total = r.Lines.Sum(l => (decimal?)l.Amount) ?? 0m, r.Status, r.Purpose,
                          })
            .Take(MaxRows).ToListAsync(ct);
        return rows.Select(r => new ExpenseReportRowDto(r.Id, r.Number, r.ReportDate, r.EmployeeId, Name(r.LastName, r.FirstName, r.MiddleName), r.ShortName,
            r.Total, r.Status, r.Purpose)).ToList();
    }

    public async Task<ExpenseReportDto> GetReportAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var r = await db.ExpenseReports.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Авансовый отчёт");
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == r.EmployeeId, ct);
        var position = await db.Positions.AsNoTracking().Where(p => p.Id == employee.PositionId).Select(p => p.Name).SingleOrDefaultAsync(ct);
        var entity = await db.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == r.LegalEntityId, ct);
        var itemIds = r.Lines.Select(l => l.CashFlowItemId).Distinct().ToList();
        var items = await db.CashFlowItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        long?[] userIds = [r.CreatedByUserId, r.ApprovedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // Получено до отчёта и долг до него — по данным на дату отчёта, без самого отчёта.
        var balance = (await BalancesAsync(db, ctx.OrganizationId, r.ReportDate, r.EmployeeId, ct)).SingleOrDefault();
        var reportedBefore = (balance?.Reported ?? 0) - (r.Status == ExpenseReportStatus.Approved ? r.Total : 0);
        var debtBefore = (balance?.Issued ?? 0) - (balance?.Returned ?? 0) - reportedBefore;
        var canEdit = r.Status == ExpenseReportStatus.Draft && CanEdit(ctx);
        return new ExpenseReportDto(r.Id, r.Number, r.ReportDate, r.EmployeeId, employee.FullName, position, employee.PersonnelNumber, r.LegalEntityId,
            entity.ShortName, entity.Name, entity.Inn, entity.DirectorName, entity.AccountantName, r.Purpose, r.Status, r.CancelReason,
            r.Lines.OrderBy(l => l.DocumentDate).ThenBy(l => l.Id)
                .Select(l => new ExpenseReportLineDto(l.Id, l.DocumentDate, l.Document, l.Description, l.Amount, l.CashFlowItemId, items[l.CashFlowItemId])).ToList(),
            balance?.Issued ?? 0, r.Total, debtBefore, users[r.CreatedByUserId], r.ApprovedByUserId is { } a ? users[a] : null, r.ApprovedAtUtc,
            canEdit, r.RowVersion);
    }

    public async Task<long> CreateReportAsync(long employeeId, DateOnly date, long? legalEntityId, string? purpose, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var entity = legalEntityId is { } eid
            ? await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eid && e.OrganizationId == ctx.OrganizationId, ct)
              ?? throw new NotFoundException("Юрлицо")
            : await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault, ct)
              ?? throw new BusinessRuleException("legal_entity.no_default", "Нет основного юрлица — добавьте его в разделе «Юрлица и счета».");
        entity.EnsureActive();
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, date, ct);
        ExpenseReport.Create(ctx.OrganizationId, "—", date, employee.Id, entity.Id, purpose, ctx.UserId, clock.UtcNow);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, ExpenseReport.NumberPrefix, ct);
        var report = ExpenseReport.Create(ctx.OrganizationId, number, date, employee.Id, entity.Id, purpose, ctx.UserId, clock.UtcNow);
        db.ExpenseReports.Add(report);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ExpenseReportCreated, report.Id, null, number, $"{number}: {employee.FullName}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return report.Id;
    }

    public async Task UpdateReportAsync(long id, DateOnly date, string? purpose, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, report) = await LoadForEditAsync(id, rowVersion, ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, date, ct);
        report.UpdateHeader(date, purpose);
        Audit(ctx, AuditActions.ExpenseReportChanged, id, null, $"{date:dd.MM.yyyy}", $"{report.Number}: шапка");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task AddLineAsync(long id, DateOnly documentDate, string? document, string? description, decimal amount, long cashFlowItemId, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var (ctx, report) = await LoadForEditAsync(id, rowVersion, ct);
        var item = await db.CashFlowItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == cashFlowItemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Статья движения денег");
        if (item.IsArchived || item.Direction != CashFlowDirection.Out || item.SystemCode is CashFlowItem.SupplierPayments or CashFlowItem.AccountableIssue)
        {
            throw new BusinessRuleException("expense_report.item", $"Статья «{item.Name}» не подходит для расходов подотчётного лица.");
        }

        var line = report.AddLine(documentDate, document, description, amount, item.Id);
        Audit(ctx, AuditActions.ExpenseReportChanged, id, null, $"{line.Amount:0.00}", $"{report.Number}: {line.Document}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveLineAsync(long id, long lineId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, report) = await LoadForEditAsync(id, rowVersion, ct);
        var line = report.Lines.SingleOrDefault(l => l.Id == lineId);
        report.RemoveLine(lineId);
        Audit(ctx, AuditActions.ExpenseReportChanged, id, line is null ? null : $"{line.Amount:0.00}", null, $"{report.Number}: {line?.Document}");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Утверждение: расходы признаны, долг сотрудника уменьшается. Если расходы больше выданного — перерасход доплачивается выдачей из кассы.</summary>
    public async Task ApproveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, report) = await LoadForEditAsync(id, rowVersion, ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, report.ReportDate, ct);
        report.Approve(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.ExpenseReportApproved, id, "Черновик", "Утверждён", $"{report.Number}: {report.Total:0.00}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task CancelAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var report = await LoadAsync(ctx, id, ct);
        report.EnsureVersion(report.RowVersion, rowVersion);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, report.ReportDate, ct);
        var before = ExpenseReport.StatusName(report.Status);
        report.Cancel(ctx.UserId, reason, clock.UtcNow);
        Audit(ctx, AuditActions.ExpenseReportCancelled, id, before, "Отменён", $"{report.Number}: {report.CancelReason}");
        await db.SaveOrConflictAsync(ct);
    }

    internal static string Name(string last, string first, string? middle) => middle is null ? $"{last} {first}" : $"{last} {first} {middle}";

    private static bool CanEdit(AccessContext ctx) =>
        ctx.Permissions.Has(Permissions.PriceView) && (ctx.Permissions.Has(Permissions.SalesEdit) || ctx.Permissions.Has(Permissions.PurchaseEdit));

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        if (!CanEdit(ctx))
        {
            await guard.DenyAsync(ctx, Permissions.SalesEdit, ct);
        }

        return ctx;
    }

    private async Task<ExpenseReport> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.ExpenseReports.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Авансовый отчёт");

    private async Task<(AccessContext, ExpenseReport)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await DemandEditAsync(ct);
        var report = await LoadAsync(ctx, id, ct);
        return (ctx, report.EnsureVersion(report.RowVersion, rowVersion));
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(ExpenseReport), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
