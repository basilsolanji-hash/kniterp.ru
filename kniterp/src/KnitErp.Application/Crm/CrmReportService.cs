using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmReportFilter(DateOnly From, DateOnly To, long? PipelineId = null, long? ResponsibleUserId = null);

/// <summary>Шаг воронки: сколько сделок периода дошло до этапа, их сумма, конверсия в следующий этап (%).</summary>
public sealed record CrmFunnelStepDto(string Stage, string Color, int Reached, decimal? Amount, decimal? ConversionToNext, decimal AvgDays);

public sealed record CrmReasonRowDto(string Reason, int Deals, decimal? Amount);

public sealed record CrmSourceRowDto(string Source, int Leads, int Deals, int Won, decimal? Revenue);

public sealed record CrmManagerRowDto(long UserId, string Manager, int Activities, int Deals, int Won, decimal? WonAmount, decimal? Plan, decimal? Fact);

/// <summary>
/// Отчёты CRM (D630): воронка с конверсией этап → этап и лид → сделка → выигрыш, скорость (дни на этапе, цикл сделки),
/// причины проигрыша, источники (выручка — оплаты заказов из сделок), менеджеры с планом и фактом. Суммы — null без «Цены и суммы».
/// </summary>
public sealed record CrmReportDto(
    CrmReportFilter Filter, string Pipeline, IReadOnlyList<CrmFunnelStepDto> Funnel, int Leads, int QualifiedLeads, int DealsFromLeads, int WonFromLeads,
    int DealsCreated, int Won, int Lost, decimal? WonAmount, decimal AvgCycleDays, IReadOnlyList<CrmReasonRowDto> LossReasons,
    IReadOnlyList<CrmSourceRowDto> Sources, IReadOnlyList<CrmManagerRowDto> Managers, decimal? Plan, decimal? Fact, CrmPlanFact FactSource, bool CanSeeAmounts)
{
    public decimal? LeadToDeal => Leads == 0 ? null : Math.Round(100m * QualifiedLeads / Leads, 1);
    public decimal? DealToWon => DealsCreated == 0 ? null : Math.Round(100m * Won / DealsCreated, 1);
}

/// <summary>Блок «CRM» главного экрана (D632): в работе и сумма, взвешенная по вероятности, выигрыш месяца против плана, просрочки.</summary>
public sealed record CrmDashboardData(
    int OpenDeals, decimal? OpenAmount, decimal? WeightedAmount, decimal? WonThisMonth, decimal? PlanThisMonth, int OverdueActivities, int DealsWithoutNextStep,
    string Href = "crm/summary");

/// <summary>
/// Источник блока «CRM» главного экрана (ТЗ CRM §6, по образцу IProductionDashboardSource D111): база подключает его к
/// DashboardBlocks.Crm по запросу в docs/sessions/crm.md. Без crm.view — null. Пока блок не добавлен в базу, те же данные — на странице «CRM → Сводка».
/// </summary>
public interface ICrmDashboardSource
{
    Task<CrmDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct);
}

public sealed class CrmDashboardSource(IClock clock) : ICrmDashboardSource
{
    public async Task<CrmDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.CrmView))
        {
            return null;
        }

        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var open = await CrmAccess.Deals(db, ctx).AsNoTracking().Where(d => d.Outcome == CrmDealOutcome.Open)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(d => d.Amount), Weighted = g.Sum(d => d.Amount * d.Probability / 100m) })
            .SingleOrDefaultAsync(ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var (fact, plan) = await CrmReportService.PlanFactAsync(db, ctx, monthStart, monthStart.AddMonths(1).AddDays(-1), CrmAccess.SeesAll(ctx) ? null : ctx.UserId,
            null, ct);
        var overdue = await db.CrmActivities.AsNoTracking()
            .CountAsync(a => a.OrganizationId == ctx.OrganizationId && a.AssigneeUserId == ctx.UserId && !a.IsDone && a.DueAtUtc < clock.UtcNow, ct);
        var noStep = await CrmActivityService.WithoutNextStepQuery(db, ctx).CountAsync(ct);
        return new CrmDashboardData(open?.Count ?? 0, amounts ? open?.Sum ?? 0 : null, amounts ? Math.Round(open?.Weighted ?? 0, 2) : null,
            amounts ? fact : null, amounts ? plan : null, overdue, noStep);
    }
}

public sealed class CrmReportService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ISpreadsheetFormat spreadsheet)
{
    public const int MaxDeals = 10_000;

    public async Task<CrmDashboardData?> DashboardAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var today = CrmEvents.Today(clock.UtcNow, await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct));
        return await new CrmDashboardSource(clock).GetAsync(db, ctx, today, ct);
    }

    public async Task<CrmReportDto> BuildAsync(CrmReportFilter f, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        if (f.To < f.From || f.To.DayNumber - f.From.DayNumber > 3660)
        {
            throw new Domain.Common.BusinessRuleException("crm.report.period", "Период отчёта указан неверно (не больше 10 лет).");
        }

        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        var fromUtc = CrmEvents.ToUtc(f.From.ToDateTime(TimeOnly.MinValue), tz);
        var toUtc = CrmEvents.ToUtc(f.To.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var pipelineId = f.PipelineId ?? await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.Id).FirstAsync(ct);
        var pipeline = await db.CrmPipelines.AsNoTracking().Where(p => p.Id == pipelineId && p.OrganizationId == ctx.OrganizationId).Select(p => p.Name)
                           .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Воронка");
        var stages = CrmSettingsService.Ordered(await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == pipelineId).ToListAsync(ct)).ToList();

        var deals = CrmAccess.Deals(db, ctx).AsNoTracking();
        if (f.ResponsibleUserId is { } r)
        {
            deals = deals.Where(d => d.ResponsibleUserId == r);
        }

        // Воронка и скорость: сделки воронки, созданные в периоде, и их история этапов.
        var created = await deals.Where(d => d.PipelineId == pipelineId && d.CreatedAtUtc >= fromUtc && d.CreatedAtUtc < toUtc)
            .Select(d => new { d.Id, d.Amount, d.Outcome, d.StageId, d.CreatedAtUtc, d.ClosedAtUtc, d.LeadId, d.SourceId, d.ResponsibleUserId })
            .Take(MaxDeals).ToListAsync(ct);
        var ids = created.Select(d => d.Id).ToList();
        var history = await db.CrmDealStageHistory.AsNoTracking().Where(h => h.OrganizationId == ctx.OrganizationId && ids.Contains(h.DealId))
            .Select(h => new { h.DealId, h.FromStageId, h.ToStageId, h.DaysInStage }).ToListAsync(ct);
        var order = stages.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var won = stages.FirstOrDefault(s => s.Kind == CrmStageKind.Won);
        var openStages = stages.Where(s => s.Kind == CrmStageKind.Open && !s.IsArchived).ToList();

        // Сделка «дошла» до рабочего этапа, если была на нём или на рабочем этапе дальше; выигранная — прошла все рабочие.
        var maxReached = created.ToDictionary(d => d.Id, d =>
        {
            if (d.Outcome == CrmDealOutcome.Won)
            {
                return int.MaxValue;
            }

            return history.Where(h => h.DealId == d.Id).Select(h => h.ToStageId).Append(d.StageId)
                .Where(id => stages.Any(s => s.Id == id && s.Kind == CrmStageKind.Open)).Select(id => order.GetValueOrDefault(id, -1)).DefaultIfEmpty(-1).Max();
        });
        var funnel = new List<CrmFunnelStepDto>();
        for (var i = 0; i < openStages.Count; i++)
        {
            var s = openStages[i];
            var idx = order[s.Id];
            var reached = created.Where(d => maxReached[d.Id] >= idx).ToList();
            var next = i + 1 < openStages.Count ? created.Count(d => maxReached[d.Id] >= order[openStages[i + 1].Id]) : created.Count(d => d.Outcome == CrmDealOutcome.Won);
            var days = history.Where(h => h.FromStageId == s.Id).Select(h => h.DaysInStage).DefaultIfEmpty(0).Average();
            funnel.Add(new CrmFunnelStepDto(s.Name, s.Color, reached.Count, amounts ? reached.Sum(d => d.Amount) : null,
                reached.Count == 0 ? null : Math.Round(100m * next / reached.Count, 1), Math.Round(days, 1)));
        }

        if (won is not null)
        {
            var w = created.Where(d => d.Outcome == CrmDealOutcome.Won).ToList();
            funnel.Add(new CrmFunnelStepDto(won.Name, won.Color, w.Count, amounts ? w.Sum(d => d.Amount) : null, null, 0));
        }

        // Закрытые в периоде (любые даты создания): выигрыши, проигрыши, причины, цикл.
        var closed = await deals.Where(d => d.PipelineId == pipelineId && d.ClosedAtUtc >= fromUtc && d.ClosedAtUtc < toUtc)
            .Select(d => new { d.Id, d.Amount, d.Outcome, d.CreatedAtUtc, d.ClosedAtUtc, d.LossReasonId, d.ResponsibleUserId }).Take(MaxDeals).ToListAsync(ct);
        var wonClosed = closed.Where(d => d.Outcome == CrmDealOutcome.Won).ToList();
        var lost = closed.Where(d => d.Outcome == CrmDealOutcome.Lost).ToList();
        var reasonIds = lost.Select(d => d.LossReasonId).OfType<long>().Distinct().ToList();
        var reasonNames = await db.CrmReferences.AsNoTracking().Where(x => reasonIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var reasons = lost.GroupBy(d => d.LossReasonId).Select(g => new CrmReasonRowDto(g.Key is { } k && reasonNames.TryGetValue(k, out var n) ? n : "—", g.Count(),
            amounts ? g.Sum(d => d.Amount) : null)).OrderByDescending(x => x.Deals).ToList();

        // Лиды периода: квалифицировано, сделки из лидов и их выигрыши.
        var leads = CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => l.CreatedAtUtc >= fromUtc && l.CreatedAtUtc < toUtc);
        if (f.ResponsibleUserId is { } lr)
        {
            leads = leads.Where(l => l.ResponsibleUserId == lr);
        }

        var leadRows = await leads.Select(l => new { l.Id, l.Status, l.SourceId, l.DealId }).Take(MaxDeals).ToListAsync(ct);
        var leadDealIds = leadRows.Select(l => l.DealId).OfType<long>().ToList();
        var leadDeals = await db.CrmDeals.AsNoTracking().Where(d => leadDealIds.Contains(d.Id)).Select(d => new { d.Id, d.Outcome }).ToListAsync(ct);

        // Источники: лиды и сделки периода, выигрыши, выручка — оплаты по заказам из выигранных сделок источника.
        var sourceIds = leadRows.Select(l => l.SourceId).Concat(created.Select(d => d.SourceId)).OfType<long>().Distinct().ToList();
        var sourceNames = await db.CrmReferences.AsNoTracking().Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var wonIds = created.Where(d => d.Outcome == CrmDealOutcome.Won).Select(d => d.Id).ToList();
        var revenue = amounts ? await PaidByDealAsync(ctx, wonIds, null, null, ct) : [];
        var sources = sourceIds.Select(id => (long?)id).Append(null).Select(sid => new CrmSourceRowDto(
                sid is { } x ? sourceNames[x] : "Не указан",
                leadRows.Count(l => l.SourceId == sid), created.Count(d => d.SourceId == sid), created.Count(d => d.SourceId == sid && d.Outcome == CrmDealOutcome.Won),
                amounts ? created.Where(d => d.SourceId == sid).Sum(d => revenue.GetValueOrDefault(d.Id)) : null))
            .Where(r => r.Leads + r.Deals > 0).OrderByDescending(r => r.Deals).ThenByDescending(r => r.Leads).ToList();

        // Менеджеры: выполненные активности, новые сделки, выигрыши, план и факт периода.
        var acts = await db.CrmActivities.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.IsDone && a.DoneAtUtc >= fromUtc && a.DoneAtUtc < toUtc)
            .GroupBy(a => a.DoneByUserId).Select(g => new { UserId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var userIds = created.Select(d => d.ResponsibleUserId).Concat(wonClosed.Select(d => d.ResponsibleUserId)).Concat(acts.Select(a => a.UserId).OfType<long>())
            .Distinct().ToList();
        if (!CrmAccess.SeesAll(ctx))
        {
            userIds = userIds.Where(u => u == ctx.UserId).ToList();
        }

        if (f.ResponsibleUserId is { } fr)
        {
            userIds = userIds.Where(u => u == fr).ToList();
        }

        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var managers = new List<CrmManagerRowDto>();
        foreach (var uid in userIds.OrderBy(u => users.GetValueOrDefault(u)))
        {
            var (fact, plan) = await PlanFactAsync(db, ctx, f.From, f.To, uid, f.PipelineId, ct);
            managers.Add(new CrmManagerRowDto(uid, users.GetValueOrDefault(uid) ?? "—", acts.Where(a => a.UserId == uid).Sum(a => a.Count),
                created.Count(d => d.ResponsibleUserId == uid), wonClosed.Count(d => d.ResponsibleUserId == uid),
                amounts ? wonClosed.Where(d => d.ResponsibleUserId == uid).Sum(d => d.Amount) : null, amounts ? plan : null, amounts ? fact : null));
        }

        var (totalFact, totalPlan) = await PlanFactAsync(db, ctx, f.From, f.To, f.ResponsibleUserId, f.PipelineId, ct);
        var settings = await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct);
        return new CrmReportDto(f with { PipelineId = pipelineId }, pipeline, funnel, leadRows.Count, leadRows.Count(l => l.Status == CrmLeadStatus.Qualified),
            leadDeals.Count, leadDeals.Count(d => d.Outcome == CrmDealOutcome.Won), created.Count, wonClosed.Count, lost.Count,
            amounts ? wonClosed.Sum(d => d.Amount) : null,
            wonClosed.Count == 0 ? 0 : Math.Round((decimal)wonClosed.Average(d => (d.ClosedAtUtc!.Value - d.CreatedAtUtc).TotalDays), 1),
            reasons, sources, managers, amounts ? totalPlan : null, amounts ? totalFact : null, settings.PlanFact, amounts);
    }

    /// <summary>Отчёт в Excel (D630): листы «Воронка», «Причины», «Источники», «Менеджеры». Выгрузка — в журнал.</summary>
    public async Task<byte[]> ExportAsync(CrmReportFilter f, CancellationToken ct = default)
    {
        var r = await BuildAsync(f, ct);
        var ctx = await guard.CurrentAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmExported, "CrmReport", null, null,
            $"отчёт CRM {f.From:dd.MM.yyyy}–{f.To:dd.MM.yyyy}", r.Pipeline, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        static string N(decimal? v) => v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return spreadsheet.Write([
            new SheetData("Воронка", ["Этап", "Дошло сделок", "Сумма", "Конверсия в следующий, %", "Среднее дней на этапе"],
                r.Funnel.Select(s => (IReadOnlyList<string>)[s.Stage, s.Reached.ToString(), N(s.Amount), N(s.ConversionToNext), N(s.AvgDays)]).ToList(),
                new HashSet<int> { 1, 2, 3, 4 }),
            new SheetData("Причины", ["Причина проигрыша", "Сделок", "Сумма"],
                r.LossReasons.Select(x => (IReadOnlyList<string>)[x.Reason, x.Deals.ToString(), N(x.Amount)]).ToList(), new HashSet<int> { 1, 2 }),
            new SheetData("Источники", ["Источник", "Лидов", "Сделок", "Выиграно", "Выручка (оплаты)"],
                r.Sources.Select(x => (IReadOnlyList<string>)[x.Source, x.Leads.ToString(), x.Deals.ToString(), x.Won.ToString(), N(x.Revenue)]).ToList(),
                new HashSet<int> { 1, 2, 3, 4 }),
            new SheetData("Менеджеры", ["Менеджер", "Активностей", "Сделок", "Выиграно", "Сумма выигрышей", "План", "Факт"],
                r.Managers.Select(x => (IReadOnlyList<string>)[x.Manager, x.Activities.ToString(), x.Deals.ToString(), x.Won.ToString(), N(x.WonAmount), N(x.Plan),
                    N(x.Fact)]).ToList(), new HashSet<int> { 1, 2, 3, 4, 5, 6 }),
        ]);
    }

    /// <summary>
    /// План и факт за период (D631). План — сумма месячных планов, полностью попадающих в период (по менеджеру; без менеджера — общий план
    /// или сумма планов менеджеров). Факт — по настройке: оплаты по заказам из сделок (по дате оплаты) или выигранные сделки (по дате закрытия).
    /// </summary>
    internal static async Task<(decimal Fact, decimal Plan)> PlanFactAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly from, DateOnly to, long? userId,
        long? pipelineId, CancellationToken ct)
    {
        var settings = await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        var deals = db.CrmDeals.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId);
        if (userId is { } u)
        {
            deals = deals.Where(d => d.ResponsibleUserId == u);
        }

        if (pipelineId is { } p)
        {
            deals = deals.Where(d => d.PipelineId == p);
        }

        var (periodFrom, periodTo) = (from, to);
        decimal fact;
        if (settings.PlanFact == CrmPlanFact.WonDeals)
        {
            var fromUtc = CrmEvents.ToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
            var toUtc = CrmEvents.ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
            fact = await deals.Where(d => d.Outcome == CrmDealOutcome.Won && d.ClosedAtUtc >= fromUtc && d.ClosedAtUtc < toUtc).SumAsync(d => d.Amount, ct);
        }
        else
        {
            fact = await (from l in db.CrmDealOrders.AsNoTracking()
                          join d in deals on l.DealId equals d.Id
                          join a in db.CustomerPaymentAllocations.AsNoTracking() on l.SalesOrderId equals a.OrderId
                          join pay in db.CustomerPayments.AsNoTracking() on a.PaymentId equals pay.Id
                          where a.RemovedAtUtc == null && pay.Status == CustomerPaymentStatus.Posted && pay.PaymentDate >= periodFrom && pay.PaymentDate <= periodTo
                          select a.Amount).SumAsync(ct);
        }

        var months = new List<(int Y, int M)>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            if (m >= from && m.AddMonths(1).AddDays(-1) <= to)
            {
                months.Add((m.Year, m.Month));
            }
        }

        var plans = await db.CrmPlans.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.PipelineId == pipelineId).ToListAsync(ct);
        plans = plans.Where(x => months.Contains((x.Year, x.Month))).ToList();
        var plan = userId is { } uid
            ? plans.Where(x => x.UserId == uid).Sum(x => x.Amount)
            : plans.Any(x => x.UserId == null) ? plans.Where(x => x.UserId == null).Sum(x => x.Amount) : plans.Sum(x => x.Amount);
        return (fact, plan);
    }

    /// <summary>Оплачено по заказам каждой сделки (разнесения оплат базы, D85).</summary>
    private async Task<Dictionary<long, decimal>> PaidByDealAsync(AccessContext ctx, IReadOnlyCollection<long> dealIds, DateOnly? fromDate, DateOnly? toDate, CancellationToken ct) =>
        (await (from l in db.CrmDealOrders.AsNoTracking()
                join a in db.CustomerPaymentAllocations.AsNoTracking() on l.SalesOrderId equals a.OrderId
                join pay in db.CustomerPayments.AsNoTracking() on a.PaymentId equals pay.Id
                where l.OrganizationId == ctx.OrganizationId && dealIds.Contains(l.DealId) && a.RemovedAtUtc == null && pay.Status == CustomerPaymentStatus.Posted
                      && (fromDate == null || pay.PaymentDate >= fromDate) && (toDate == null || pay.PaymentDate <= toDate)
                select new { l.DealId, a.Amount }).ToListAsync(ct))
        .GroupBy(x => x.DealId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
}
