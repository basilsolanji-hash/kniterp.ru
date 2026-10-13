using System.Globalization;
using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Разрешение кредитного контроля в карточке заказа (D1111): кто, когда, причина, сумма и снимок долга.</summary>
public sealed record SalesCreditApprovalDto(string By, DateTime AtUtc, string Reason, decimal Amount, decimal Debt, decimal? Limit, decimal Overdue);

/// <summary>
/// Кредитный контроль заказа для карточки (D1105, D1111): проверка суммы заказа (черновик) или неотгруженного остатка
/// (подтверждённый); Covered — последнее разрешение покрывает сумму заказа; CanApprove — можно разрешить (право и блокировка).
/// </summary>
public sealed record SalesCreditDto(long OrderId, decimal OrderTotal, SalesCreditCheckDto Check, SalesCreditApprovalDto? Approval, bool Covered, bool CanApprove)
{
    public bool Blocked => Check.Blocked && !Covered;
}

/// <summary>Отбор дебиторки: на дату (по умолчанию — сегодня) и менеджер (0 — без менеджера).</summary>
public sealed record ReceivablesFilter(DateOnly? AsOf = null, long? ManagerId = null);

/// <summary>Покупатель в дебиторке (D1112): долг и его разбивка по срокам просрочки; OverdueDays — самая давняя просрочка.</summary>
public sealed record ReceivablesRowDto(
    long CustomerId, string Customer, long? ManagerId, string? Manager, decimal Debt, decimal NotDue, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90, int OverdueDays)
{
    public decimal Overdue => Days1To30 + Days31To60 + Days61To90 + Over90;
}

/// <summary>Покупатели одного менеджера (null — без менеджера) с итогами.</summary>
public sealed record ReceivablesGroupDto(long? ManagerId, string? Manager, IReadOnlyList<ReceivablesRowDto> Rows)
{
    public decimal Debt => Rows.Sum(r => r.Debt);
    public decimal NotDue => Rows.Sum(r => r.NotDue);
    public decimal Days1To30 => Rows.Sum(r => r.Days1To30);
    public decimal Days31To60 => Rows.Sum(r => r.Days31To60);
    public decimal Days61To90 => Rows.Sum(r => r.Days61To90);
    public decimal Over90 => Rows.Sum(r => r.Over90);
    public decimal Overdue => Rows.Sum(r => r.Overdue);
}

/// <summary>Дебиторка на дату (D1112): группы по менеджерам, список менеджеров для отбора; OwnOnly — видны только свои покупатели.</summary>
public sealed record ReceivablesReportDto(DateOnly AsOf, IReadOnlyList<ReceivablesGroupDto> Groups, IReadOnlyList<LookupDto> Managers, bool OwnOnly)
{
    public IEnumerable<ReceivablesRowDto> Rows => Groups.SelectMany(g => g.Rows);
}

/// <summary>Блок главного экрана «Просроченная дебиторка» (D1114): итоги и самые большие просрочки.</summary>
public sealed record SalesOverdueSummaryDto(decimal Debt, decimal Overdue, int Customers, IReadOnlyList<ReceivablesRowDto> Top);

/// <summary>Напоминание менеджеру о просрочке покупателя (D1113) — одно на покупателя в день.</summary>
public sealed record SalesOverdueAlert(long CustomerId, string Customer, decimal Overdue, int Days, DateOnly Today)
{
    public string Text => $"Просрочена оплата: {Customer} — {Overdue.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}, до {Days} дн.";
    public string Key => $"sales-overdue:{CustomerId}:{Today:yyyyMMdd}";
}

/// <summary>Дебиторка по срокам и менеджерам (D1112–D1114) — общая часть отчёта, напоминаний и главного экрана.</summary>
public static class SalesReceivables
{
    public const int MaxAlerts = 20;

    /// <summary>
    /// Менеджер покупателя (D1112): закреплённый в условиях продаж, иначе ответственный последнего (не отменённого) заказа, иначе — нет.
    /// </summary>
    public static async Task<Dictionary<long, long?>> ManagersAsync(IKnitErpDbContext db, long org, IReadOnlyCollection<long> customers, CancellationToken ct)
    {
        var result = await db.CustomerSalesProfiles.AsNoTracking()
            .Where(p => p.OrganizationId == org && customers.Contains(p.CounterpartyId) && p.ManagerUserId != null)
            .ToDictionaryAsync(p => p.CounterpartyId, p => p.ManagerUserId, ct);
        var rest = customers.Where(c => !result.ContainsKey(c)).ToList();
        if (rest.Count > 0)
        {
            var last = await db.SalesOrders.AsNoTracking()
                .Where(o => o.OrganizationId == org && rest.Contains(o.CustomerId) && o.Status != SalesOrderStatus.Cancelled)
                .GroupBy(o => o.CustomerId)
                .Select(g => g.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Select(o => new { o.CustomerId, o.ResponsibleUserId }).First())
                .ToListAsync(ct);
            foreach (var l in last)
            {
                result[l.CustomerId] = l.ResponsibleUserId;
            }
        }

        foreach (var c in customers)
        {
            result.TryAdd(c, null);
        }

        return result;
    }

    /// <summary>
    /// D1110: чьи заказы видит пользователь — null (все) или ответственные: сам он («Свои») либо сотрудники его подразделения с вложенными
    /// («Своего подразделения»). Действует самое мягкое среди ролей (как D1103); роль без настройки — «Все»; без ролей — «Все».
    /// Подразделение — у карточки сотрудника, привязанной к пользователю (уволенные не учитываются); нет карточки — только свои.
    /// </summary>
    public static async Task<HashSet<long>?> VisibleManagersAsync(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var roleIds = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == ctx.UserId && a.RoleId != null && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= nowUtc && (a.ValidToUtc == null || a.ValidToUtc > nowUtc))
            .Select(a => a.RoleId!.Value).Distinct().ToListAsync(ct);
        if (roleIds.Count == 0)
        {
            return null;
        }

        var settings = await db.SalesRoleSettings.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && roleIds.Contains(s.RoleId)).Select(s => new { s.RoleId, s.Visibility }).ToListAsync(ct);
        if (settings.Count < roleIds.Count || settings.Any(s => s.Visibility == SalesOrderVisibility.All))
        {
            return null;
        }

        HashSet<long> users = [ctx.UserId];
        if (settings.Any(s => s.Visibility == SalesOrderVisibility.Department))
        {
            var own = await db.Employees.AsNoTracking()
                .Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId && e.Status != KnitErp.Domain.Structure.EmploymentStatus.Dismissed)
                .Select(e => e.DepartmentId).ToListAsync(ct);
            if (own.Count > 0)
            {
                var tree = await KnitErp.Application.Structure.StructureScope.LoadTreeAsync(db, ctx.OrganizationId, ct);
                var departments = tree.WithDescendants(own).ToList();
                users.UnionWith(await db.Employees.AsNoTracking()
                    .Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId != null && departments.Contains(e.DepartmentId)
                                && e.Status != KnitErp.Domain.Structure.EmploymentStatus.Dismissed)
                    .Select(e => e.UserId!.Value).ToListAsync(ct));
            }
        }

        return users;
    }

    /// <summary>Отчёт без проверки прав — права проверяет вызывающий (сервис, главный экран).</summary>
    public static async Task<ReceivablesReportDto> BuildAsync(
        IKnitErpDbContext db, AccessContext ctx, ReceivablesFilter filter, DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var asOf = filter.AsOf ?? today;
        var ledgers = await SalesCredit.LedgersAsync(db, ctx.OrganizationId, null, asOf, ct);
        var debtors = ledgers.Values.Where(l => l.Debt > 0).ToList();
        var managers = await ManagersAsync(db, ctx.OrganizationId, debtors.Select(d => d.CustomerId).ToList(), ct);
        var visible = await VisibleManagersAsync(db, ctx, nowUtc, ct);
        var ownOnly = visible is not null;
        if (visible is not null)
        {
            // D1110: свои покупатели или покупатели менеджеров своего подразделения.
            debtors = debtors.Where(d => managers[d.CustomerId] is { } m && visible.Contains(m)).ToList();
        }

        var managerIds = debtors.Select(d => managers[d.CustomerId]).Where(m => m is not null).Select(m => m!.Value).Distinct().ToList();
        var userNames = await db.Users.AsNoTracking().Where(u => managerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var options = userNames.OrderBy(u => u.Value, StringComparer.CurrentCulture).Select(u => new LookupDto(u.Key, u.Value)).ToList();
        if (filter.ManagerId is { } managerFilter)
        {
            debtors = debtors.Where(d => managerFilter == 0 ? managers[d.CustomerId] is null : managers[d.CustomerId] == managerFilter).ToList();
        }

        var ids = debtors.Select(d => d.CustomerId).ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var rows = debtors.Select(d =>
        {
            var b = d.Buckets(asOf);
            var manager = managers[d.CustomerId];
            return new ReceivablesRowDto(d.CustomerId, names[d.CustomerId], manager, manager is { } m ? userNames.GetValueOrDefault(m) : null, d.Debt,
                b[0], b[1], b[2], b[3], b[4], Math.Max(0, d.Overdue(asOf, onlyWithTerms: false).Days));
        }).ToList();
        var groups = rows.GroupBy(r => r.ManagerId)
            .OrderBy(g => g.Key is null).ThenBy(g => g.First().Manager, StringComparer.CurrentCulture)
            .Select(g => new ReceivablesGroupDto(g.Key, g.First().Manager,
                g.OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Debt).ThenBy(r => r.Customer, StringComparer.CurrentCulture).ToList()))
            .ToList();
        return new ReceivablesReportDto(asOf, groups, options, ownOnly);
    }

    /// <summary>
    /// D1113: напоминания менеджеру — покупатели, у которых он менеджер (D1112) и есть просроченный долг. Одно на покупателя в день
    /// (ключ с датой): скрытое пользователем вернётся завтра, если просрочка не погашена. Без прав на продажи и цены — ничего.
    /// </summary>
    public static async Task<IReadOnlyList<SalesOverdueAlert>> AlertsAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.SalesView) || !ctx.Permissions.Has(Permissions.PriceView))
        {
            return [];
        }

        var org = ctx.OrganizationId;
        var candidates = await db.CustomerSalesProfiles.AsNoTracking().Where(p => p.OrganizationId == org && p.ManagerUserId == ctx.UserId)
            .Select(p => p.CounterpartyId)
            .Union(db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.ResponsibleUserId == ctx.UserId).Select(o => o.CustomerId))
            .Distinct().Take(5000).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        var managers = await ManagersAsync(db, org, candidates, ct);
        var mine = candidates.Where(c => managers[c] == ctx.UserId).ToList();
        var ledgers = await SalesCredit.LedgersAsync(db, org, mine, today, ct);
        var late = ledgers.Values.Where(l => l.Debt > 0).Select(l => (l.CustomerId, Overdue: l.Overdue(today, onlyWithTerms: false)))
            .Where(x => x.Overdue.Amount > 0).OrderByDescending(x => x.Overdue.Amount).Take(MaxAlerts).ToList();
        var ids = late.Select(l => l.CustomerId).ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return late.Select(l => new SalesOverdueAlert(l.CustomerId, names[l.CustomerId], l.Overdue.Amount, l.Overdue.Days, today)).ToList();
    }

    /// <summary>
    /// D1114: блок главного экрана — своим коротким контекстом (у страницы главного экрана свой контекст вкладки). Без права
    /// «Продажи: отчёты» или «Цены и суммы» — null, блок не выводится; с настройкой роли «Свои» — только свои покупатели.
    /// </summary>
    public static async Task<SalesOverdueSummaryDto?> DashboardAsync(IKnitErpDbContextFactory factory, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, user, clock).CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesReportView) || !ctx.Permissions.Has(Permissions.PriceView))
        {
            return null;
        }

        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        var report = await BuildAsync(db, ctx, new ReceivablesFilter(), today, clock.UtcNow, ct);
        var rows = report.Rows.ToList();
        var late = rows.Where(r => r.Overdue > 0).ToList();
        return new SalesOverdueSummaryDto(rows.Sum(r => r.Debt), late.Sum(r => r.Overdue), late.Count,
            late.OrderByDescending(r => r.Overdue).Take(5).ToList());
    }
}

/// <summary>Кредитный контроль и дебиторка (D1105, D1111–D1114) — часть сервиса продаж.</summary>
public sealed partial class SalesService
{
    /// <summary>Кредитный контроль заказа для карточки: лимит, долг, просрочка, превышение и разрешение. Только с правом на цены.</summary>
    public async Task<SalesCreditDto> GetCreditAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var (check, approval, covered) = await CreditStateAsync(ctx, order, ct);
        SalesCreditApprovalDto? approvalDto = null;
        if (approval is not null)
        {
            var by = await db.Users.AsNoTracking().Where(u => u.Id == approval.ApprovedByUserId).Select(u => u.DisplayName).SingleAsync(ct);
            approvalDto = new SalesCreditApprovalDto(by, approval.ApprovedAtUtc, approval.Reason, approval.Amount, approval.Debt, approval.Limit, approval.Overdue);
        }

        var open = order.Status is SalesOrderStatus.Draft or SalesOrderStatus.Confirmed;
        return new SalesCreditDto(order.Id, Money.Round(order.Total), check, approvalDto, covered,
            open && check.Blocked && !covered && ctx.Permissions.Has(Permissions.SalesCreditOverride));
    }

    /// <summary>
    /// D1111: разрешить заказ сверх кредитного лимита или при просрочке — право «Продажи: отгрузка сверх кредитного лимита и при
    /// просрочке», причина обязательна, запись в журнал аудита. Разрешение действует на подтверждение и отгрузки этого заказа
    /// в пределах его суммы.
    /// </summary>
    public async Task ApproveCreditAsync(long orderId, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        await guard.DemandAsync(Permissions.SalesCreditOverride, ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        order.EnsureVersion(order.RowVersion, rowVersion);
        if (order.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
        {
            throw new BusinessRuleException("sales.credit.status", "Разрешение даётся черновику или подтверждённому заказу.");
        }

        var (check, _, covered) = await CreditStateAsync(ctx, order, ct);
        if (!check.Blocked || covered)
        {
            throw new BusinessRuleException("sales.credit.not_needed", "Кредитный контроль этот заказ не останавливает — разрешение не нужно.");
        }

        var approval = SalesCreditApproval.Create(ctx.OrganizationId, order.Id, order.Total, check.Debt, check.Limit, check.Overdue, reason, ctx.UserId,
            clock.UtcNow);
        db.SalesCreditApprovals.Add(approval);
        Audit(ctx, AuditActions.SalesCreditOverride, nameof(SalesOrder), order.Id,
            FormattableString.Invariant($"долг {check.Debt:0.00}, лимит {(check.Limit is { } l ? l.ToString("0.00", CultureInfo.InvariantCulture) : "—")}, просрочено {check.Overdue:0.00}, заказ {approval.Amount:0.00}"),
            approval.Reason, order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// D1105: крючок подтверждения (после проверки цен D1104) — долг + сумма заказа в пределах лимита и нет просрочки по отсрочке,
    /// иначе — только по разрешению (D1111), покрывающему сумму заказа. Отказ снимает несохранённые изменения вкладки.
    /// </summary>
    private async Task EnsureCreditOnConfirmAsync(AccessContext ctx, SalesOrder order, CancellationToken ct)
    {
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        var check = await SalesCredit.CheckAsync(db, ctx.OrganizationId, order.CustomerId, order.AgreementId, order.Total, today, ct);
        if (!check.Blocked || await SalesCredit.LastApprovalAsync(db, ctx.OrganizationId, order.Id, ct) is { } a && a.Amount >= Money.Round(order.Total))
        {
            return;
        }

        db.DiscardChanges();
        if (check.Overdue > 0)
        {
            throw new BusinessRuleException("sales.credit.overdue",
                $"У покупателя просрочена оплата на {check.Overdue:N2} (до {check.OverdueDays} дн.). Подтвердить заказ можно после оплаты или с разрешением руководителя.");
        }

        throw new BusinessRuleException("sales.credit.limit",
            $"Кредитный лимит превышен на {check.Excess:N2}: долг {check.Debt:N2} + заказ {check.Amount:N2} больше лимита {check.Limit:N2}. Подтвердить заказ можно после оплаты или с разрешением руководителя.");
    }

    /// <summary>Проверка для карточки и разрешения: черновик — вся сумма заказа, подтверждённый — ещё не отгруженное.</summary>
    private async Task<(SalesCreditCheckDto Check, SalesCreditApproval? Approval, bool Covered)> CreditStateAsync(
        AccessContext ctx, SalesOrder order, CancellationToken ct)
    {
        var total = Money.Round(order.Total);
        var amount = order.Status switch
        {
            SalesOrderStatus.Draft => total,
            SalesOrderStatus.Confirmed => Math.Max(0, total - await SalesCredit.ShippedValueAsync(db, ctx.OrganizationId, order, ct)),
            _ => 0m,
        };
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        var check = await SalesCredit.CheckAsync(db, ctx.OrganizationId, order.CustomerId, order.AgreementId, amount, today, ct);
        var approval = await SalesCredit.LastApprovalAsync(db, ctx.OrganizationId, order.Id, ct);
        return (check, approval, approval is not null && total > 0 && approval.Amount >= total);
    }

    /// <summary>Дебиторская задолженность (D1112): права «Продажи: отчёты» и «Цены и суммы»; роль «Свои» — только свои покупатели.</summary>
    public async Task<ReceivablesReportDto> ReceivablesAsync(ReceivablesFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        return await SalesReceivables.BuildAsync(db, ctx, filter, today, clock.UtcNow, ct);
    }

    /// <summary>Дебиторка в Excel — те же отбор и права, что на экране.</summary>
    public async Task<byte[]> ExportReceivablesAsync(ReceivablesFilter filter, ISpreadsheetFormat spreadsheet, CancellationToken ct = default)
    {
        var report = await ReceivablesAsync(filter, ct);
        static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var g in report.Groups)
        {
            var manager = g.Manager ?? "Без менеджера";
            rows.AddRange(g.Rows.Select(r => (IReadOnlyList<string>)[manager, r.Customer, N(r.Debt), N(r.NotDue), N(r.Days1To30), N(r.Days31To60), N(r.Days61To90),
                N(r.Over90), r.OverdueDays > 0 ? r.OverdueDays.ToString(CultureInfo.InvariantCulture) : ""]));
            rows.Add([manager, "Итого по менеджеру", N(g.Debt), N(g.NotDue), N(g.Days1To30), N(g.Days31To60), N(g.Days61To90), N(g.Over90), ""]);
        }

        var all = report.Groups;
        rows.Add(["", "Итого", N(all.Sum(g => g.Debt)), N(all.Sum(g => g.NotDue)), N(all.Sum(g => g.Days1To30)), N(all.Sum(g => g.Days31To60)),
            N(all.Sum(g => g.Days61To90)), N(all.Sum(g => g.Over90)), ""]);
        return spreadsheet.Write([new SheetData("Дебиторка", ["Менеджер", "Покупатель", "Долг", "Не просрочено", "1–30 дней", "31–60 дней", "61–90 дней",
            "Более 90 дней", "Просрочка, дней", $"На {report.AsOf:dd.MM.yyyy}"], rows, new HashSet<int> { 2, 3, 4, 5, 6, 7, 8 })]);
    }

    /// <summary>
    /// D1113: текст письма покупателю о долге — только для копирования (рассылки из системы нет до решения владельца). Просроченные
    /// отгрузки — со сроками оплаты; подпись — пользователь и юрлицо по умолчанию.
    /// </summary>
    public async Task<string> OverdueLetterAsync(long customerId, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var org = ctx.OrganizationId;
        var customer = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId && c.OrganizationId == org, ct)
                       ?? throw new NotFoundException("Контрагент");
        if (await SalesReceivables.VisibleManagersAsync(db, ctx, clock.UtcNow, ct) is { } visible
            && !((await SalesReceivables.ManagersAsync(db, org, [customerId], ct))[customerId] is { } manager && visible.Contains(manager)))
        {
            throw new NotFoundException("Контрагент");
        }

        var on = asOf ?? await SalesCredit.TodayAsync(db, org, clock.UtcNow, ct);
        var ledger = (await SalesCredit.LedgersAsync(db, org, [customerId], on, ct)).GetValueOrDefault(customerId);
        if (ledger is null || ledger.Debt <= 0)
        {
            throw new BusinessRuleException("sales.credit.no_debt", "У покупателя нет долга — напоминать не о чем.");
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CurrencyCode).SingleAsync(ct);
        var seller = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && e.IsDefault).Select(e => e.ShortName).SingleOrDefaultAsync(ct)
                     ?? await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.ShortName).SingleAsync(ct);
        var me = await db.Users.AsNoTracking().Where(u => u.Id == ctx.UserId).Select(u => u.DisplayName).SingleAsync(ct);
        string M(decimal v) => $"{v.ToString("N2", ru)} {currency}";
        var late = ledger.Open.Where(o => o.OverdueDays(on) > 0).ToList();
        var text = new StringBuilder();
        text.AppendLine("Добрый день!").AppendLine();
        text.AppendLine($"По нашим данным на {on:dd.MM.yyyy} задолженность {customer.Name} перед {seller} составляет {M(ledger.Debt)}"
                        + (late.Count > 0 ? $", в том числе просроченная — {M(Money.Round(late.Sum(o => o.Left)))}:" : "."));
        foreach (var o in late)
        {
            text.AppendLine($"— отгрузка № {o.Number} от {o.Date:dd.MM.yyyy} (заказ {o.OrderNumber}), срок оплаты {o.Due:dd.MM.yyyy}, к оплате {M(Money.Round(o.Left))};");
        }

        text.AppendLine();
        text.AppendLine(late.Count > 0
            ? "Просим погасить просроченную задолженность или сообщить ожидаемую дату оплаты."
            : "Просим оплатить задолженность в установленные сроки.");
        text.AppendLine("Если оплата уже произведена, пришлите, пожалуйста, копию платёжного поручения — мы сверим расчёты. При необходимости направим акт сверки.");
        text.AppendLine().AppendLine("С уважением,").AppendLine(me).Append(seller);
        return text.ToString();
    }
}
