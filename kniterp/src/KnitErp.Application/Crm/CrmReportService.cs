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

/// <summary>Ячейка воронки менеджера (D685): дошло сделок, их сумма (null без «Цены и суммы»), конверсия в следующий этап.</summary>
public sealed record CrmFunnelCellDto(int Reached, decimal? Amount, decimal? ConversionToNext);

/// <summary>Воронка менеджера: ячейки в том же порядке, что и шаги общей воронки (рабочие этапы, затем «Выиграна»).</summary>
public sealed record CrmManagerFunnelDto(long UserId, string Manager, IReadOnlyList<CrmFunnelCellDto> Steps);

public sealed record CrmReasonRowDto(string Reason, int Deals, decimal? Amount);

/// <summary>
/// Конверсия лид → сделка → выигрыш (D687) — строка «вся организация», менеджер или источник. Лиды — созданные в периоде; из них со сделкой
/// и с выигранной сделкой. Сделки — созданные в периоде в выбранной воронке; выиграно — из них (на дату отчёта). Выручка — оплаты заказов из выигранных сделок.
/// </summary>
public sealed record CrmConversionRowDto(long? Id, string Name, int Leads, int LeadDeals, int LeadWon, int Deals, int Won, decimal? WonAmount, decimal? Revenue)
{
    public decimal? LeadToDeal => Percent(LeadDeals, Leads);
    public decimal? LeadToWon => Percent(LeadWon, Leads);
    public decimal? DealToWon => Percent(Won, Deals);

    internal static decimal? Percent(decimal part, decimal total) => total == 0 ? null : Math.Round(100m * part / total, 1);
}

public sealed record CrmManagerRowDto(long UserId, string Manager, int Activities, int Deals, int Won, decimal? WonAmount, decimal? Plan, decimal? Fact);

/// <summary>Длительность (D686): сколько значений (переходов или сделок), среднее и медиана дней.</summary>
public sealed record CrmDurationDto(int Count, decimal? AvgDays, decimal? MedianDays)
{
    public static CrmDurationDto Of(IEnumerable<decimal> days)
    {
        var sorted = days.Order().ToList();
        if (sorted.Count == 0)
        {
            return new CrmDurationDto(0, null, null);
        }

        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        return new CrmDurationDto(sorted.Count, Math.Round(sorted.Average(), 1), Math.Round(median, 1));
    }
}

public sealed record CrmStageDurationDto(string Stage, string Color, CrmDurationDto Days);

/// <summary>Длительность этапов менеджера: ячейки в порядке <see cref="CrmReportDto.Durations"/>; цикл — выигранные в периоде сделки.</summary>
public sealed record CrmManagerDurationDto(long UserId, string Manager, IReadOnlyList<CrmDurationDto> Stages, CrmDurationDto Cycle);

/// <summary>План и факт месяца (D688); выполнение — null без плана.</summary>
public sealed record CrmPlanFactMonthDto(int Year, int Month, decimal Plan, decimal Fact)
{
    public decimal? Done => CrmConversionRowDto.Percent(Fact, Plan);
}

public sealed record CrmManagerPlanFactDto(long UserId, string Manager, IReadOnlyList<CrmPlanFactMonthDto> Months)
{
    public decimal Plan => Months.Sum(m => m.Plan);
    public decimal Fact => Months.Sum(m => m.Fact);
    public decimal? Done => CrmConversionRowDto.Percent(Fact, Plan);
}

/// <summary>
/// Отчёты CRM (D630, D685–D688): воронка с конверсией этап → этап — общая и по менеджерам; конверсия лид → сделка → выигрыш — общая,
/// по менеджерам и источникам; длительность этапов (среднее и медиана) и цикл выигранной сделки; причины проигрыша; план-факт по месяцам.
/// Суммы — null без «Цены и суммы»; без crm.all строки менеджеров — только свои.
/// </summary>
public sealed record CrmReportDto(
    CrmReportFilter Filter, string Pipeline, IReadOnlyList<CrmFunnelStepDto> Funnel, int Leads, int QualifiedLeads, int DealsFromLeads, int WonFromLeads,
    int DealsCreated, int Won, int Lost, decimal? WonAmount, decimal AvgCycleDays, IReadOnlyList<CrmReasonRowDto> LossReasons,
    IReadOnlyList<CrmConversionRowDto> Sources, IReadOnlyList<CrmManagerRowDto> Managers, decimal? Plan, decimal? Fact, CrmPlanFact FactSource, bool CanSeeAmounts,
    IReadOnlyList<CrmManagerFunnelDto> ManagerFunnel, CrmConversionRowDto Conversion, IReadOnlyList<CrmConversionRowDto> ManagerConversion,
    IReadOnlyList<CrmStageDurationDto> Durations, CrmDurationDto Cycle, IReadOnlyList<CrmManagerDurationDto> ManagerDurations,
    IReadOnlyList<CrmPlanFactMonthDto> PlanFactMonths, IReadOnlyList<CrmManagerPlanFactDto> ManagerPlanFact)
{
    public decimal? LeadToDeal => Conversion.LeadToDeal;
    public decimal? DealToWon => Conversion.DealToWon;
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
    /// <summary>Предел сделок периода, разбираемых в памяти по этапам (лёгкая проекция: id, менеджер, источник, сумма, итог, этап).</summary>
    public const int MaxDeals = 50_000;

    private const string Total = "Вся организация";

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

        var org = ctx.OrganizationId;
        await CrmSettingsService.EnsureDefaultsAsync(db, org, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, org, ct);
        var fromUtc = CrmEvents.ToUtc(f.From.ToDateTime(TimeOnly.MinValue), tz);
        var toUtc = CrmEvents.ToUtc(f.To.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var seesAll = CrmAccess.SeesAll(ctx);
        var pipelineId = f.PipelineId ?? await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == org && !p.IsArchived)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.Id).FirstAsync(ct);
        var pipeline = await db.CrmPipelines.AsNoTracking().Where(p => p.Id == pipelineId && p.OrganizationId == org).Select(p => p.Name)
                           .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Воронка");
        var stages = CrmSettingsService.Ordered(await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == pipelineId && s.OrganizationId == org).ToListAsync(ct))
            .ToList();

        // Строки менеджеров: без crm.all — только свои (D685), с отбором менеджера — только он.
        bool Visible(long userId) => (seesAll || userId == ctx.UserId) && (f.ResponsibleUserId is null || userId == f.ResponsibleUserId);

        var deals = CrmAccess.Deals(db, ctx).AsNoTracking();
        if (f.ResponsibleUserId is { } r)
        {
            deals = deals.Where(d => d.ResponsibleUserId == r);
        }

        var inPipeline = deals.Where(d => d.PipelineId == pipelineId);

        // D685: воронка — сделки воронки, созданные в периоде; «дошла до этапа» — была на нём или на рабочем этапе дальше (история этапов),
        // выигранная прошла все рабочие. Из базы — только лёгкие строки сделок и пары «сделка — этап» без повторов.
        var cohort = inPipeline.Where(d => d.CreatedAtUtc >= fromUtc && d.CreatedAtUtc < toUtc);
        var created = await cohort.OrderBy(d => d.Id)
            .Select(d => new DealRow(d.Id, d.ResponsibleUserId, d.SourceId, d.Amount, d.Outcome, d.StageId)).Take(MaxDeals).ToListAsync(ct);
        var visited = (await (from h in db.CrmDealStageHistory.AsNoTracking()
                              join d in cohort on h.DealId equals d.Id
                              where h.OrganizationId == org
                              select new { h.DealId, h.ToStageId }).Distinct().ToListAsync(ct))
            .ToLookup(x => x.DealId, x => x.ToStageId);
        var order = stages.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var openKind = stages.Where(s => s.Kind == CrmStageKind.Open).Select(s => s.Id).ToHashSet();
        var won = stages.FirstOrDefault(s => s.Kind == CrmStageKind.Won);
        var openStages = stages.Where(s => s.Kind == CrmStageKind.Open && !s.IsArchived).ToList();
        var maxReached = created.ToDictionary(d => d.Id, d => d.Outcome == CrmDealOutcome.Won
            ? int.MaxValue
            : visited[d.Id].Append(d.StageId).Where(openKind.Contains).Select(id => order[id]).DefaultIfEmpty(-1).Max());

        List<CrmFunnelCellDto> FunnelOf(IReadOnlyCollection<DealRow> rows)
        {
            var cells = new List<CrmFunnelCellDto>();
            for (var i = 0; i < openStages.Count; i++)
            {
                var idx = order[openStages[i].Id];
                var reached = rows.Where(d => maxReached[d.Id] >= idx).ToList();
                var next = i + 1 < openStages.Count
                    ? rows.Count(d => maxReached[d.Id] >= order[openStages[i + 1].Id])
                    : rows.Count(d => d.Outcome == CrmDealOutcome.Won);
                cells.Add(new CrmFunnelCellDto(reached.Count, amounts ? reached.Sum(d => d.Amount) : null, CrmConversionRowDto.Percent(next, reached.Count)));
            }

            if (won is not null)
            {
                var w = rows.Where(d => d.Outcome == CrmDealOutcome.Won).ToList();
                cells.Add(new CrmFunnelCellDto(w.Count, amounts ? w.Sum(d => d.Amount) : null, null));
            }

            return cells;
        }

        // D686: длительность этапа — выходы с этапа в периоде (запись истории с прежним этапом), дни на этапе записаны при переходе.
        var exits = await (from h in db.CrmDealStageHistory.AsNoTracking()
                           join d in inPipeline on h.DealId equals d.Id
                           where h.OrganizationId == org && h.FromStageId != null && h.ChangedAtUtc >= fromUtc && h.ChangedAtUtc < toUtc
                           select new { StageId = h.FromStageId!.Value, h.DaysInStage, d.ResponsibleUserId }).ToListAsync(ct);
        var exitsByStage = exits.ToLookup(x => x.StageId, x => x.DaysInStage);
        var durations = openStages.Select(s => new CrmStageDurationDto(s.Name, s.Color, CrmDurationDto.Of(exitsByStage[s.Id]))).ToList();

        var totalCells = FunnelOf(created);
        var funnel = openStages.Select((s, i) => new CrmFunnelStepDto(s.Name, s.Color, totalCells[i].Reached, totalCells[i].Amount, totalCells[i].ConversionToNext,
            durations[i].Days.AvgDays ?? 0)).ToList();
        if (won is not null)
        {
            funnel.Add(new CrmFunnelStepDto(won.Name, won.Color, totalCells[^1].Reached, totalCells[^1].Amount, null, 0));
        }

        // Закрытые в периоде (любые даты создания): выигрыши, проигрыши, причины, цикл выигранной сделки.
        var closed = await inPipeline.Where(d => d.ClosedAtUtc >= fromUtc && d.ClosedAtUtc < toUtc)
            .Select(d => new { d.Id, d.Amount, d.Outcome, d.CreatedAtUtc, d.ClosedAtUtc, d.LossReasonId, d.ResponsibleUserId }).Take(MaxDeals).ToListAsync(ct);
        var wonClosed = closed.Where(d => d.Outcome == CrmDealOutcome.Won)
            .Select(d => new { d.ResponsibleUserId, d.Amount, Days = Math.Max(0, (decimal)(d.ClosedAtUtc!.Value - d.CreatedAtUtc).TotalDays) }).ToList();
        var lost = closed.Where(d => d.Outcome == CrmDealOutcome.Lost).ToList();
        var reasonIds = lost.Select(d => d.LossReasonId).OfType<long>().Distinct().ToList();
        var reasonNames = await db.CrmReferences.AsNoTracking().Where(x => x.OrganizationId == org && reasonIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var reasons = lost.GroupBy(d => d.LossReasonId).Select(g => new CrmReasonRowDto(g.Key is { } k && reasonNames.TryGetValue(k, out var n) ? n : "—", g.Count(),
            amounts ? g.Sum(d => d.Amount) : null)).OrderByDescending(x => x.Deals).ToList();
        var cycle = CrmDurationDto.Of(wonClosed.Select(d => d.Days));

        // D687: лиды периода по менеджеру и источнику — сколько, сколько со сделкой и с выигранной сделкой; группировка в базе.
        var leads = CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => l.CreatedAtUtc >= fromUtc && l.CreatedAtUtc < toUtc);
        if (f.ResponsibleUserId is { } lr)
        {
            leads = leads.Where(l => l.ResponsibleUserId == lr);
        }

        var leadGroups = await (from l in leads
                                join d in db.CrmDeals.AsNoTracking().Where(x => x.OrganizationId == org) on l.DealId equals (long?)d.Id into dj
                                from d in dj.DefaultIfEmpty()
                                group new
                                {
                                    Qualified = l.Status == CrmLeadStatus.Qualified ? 1 : 0,
                                    HasDeal = l.DealId != null ? 1 : 0,
                                    Won = d != null && d.Outcome == CrmDealOutcome.Won ? 1 : 0,
                                } by new { l.ResponsibleUserId, l.SourceId } into g
                                select new LeadGroup(g.Key.ResponsibleUserId, g.Key.SourceId, g.Count(), g.Sum(x => x.Qualified), g.Sum(x => x.HasDeal), g.Sum(x => x.Won)))
            .ToListAsync(ct);

        // Выручка — оплаты по заказам из выигранных сделок периода (разнесения оплат базы, D85), сумма по сделке — в базе.
        var revenue = amounts
            ? await (from l in db.CrmDealOrders.AsNoTracking()
                     join d in cohort on l.DealId equals d.Id
                     join a in db.CustomerPaymentAllocations.AsNoTracking() on l.SalesOrderId equals a.OrderId
                     join pay in db.CustomerPayments.AsNoTracking() on a.PaymentId equals pay.Id
                     where l.OrganizationId == org && pay.OrganizationId == org && d.Outcome == CrmDealOutcome.Won && a.RemovedAtUtc == null
                           && pay.Status == CustomerPaymentStatus.Posted
                     group a.Amount by d.Id into g
                     select new { DealId = g.Key, Sum = g.Sum() }).ToDictionaryAsync(x => x.DealId, x => x.Sum, ct)
            : [];

        CrmConversionRowDto ConversionOf(long? id, string name, IReadOnlyCollection<LeadGroup> lg, IReadOnlyCollection<DealRow> rows)
        {
            var w = rows.Where(d => d.Outcome == CrmDealOutcome.Won).ToList();
            return new CrmConversionRowDto(id, name, lg.Sum(x => x.Leads), lg.Sum(x => x.Deals), lg.Sum(x => x.Won), rows.Count, w.Count,
                amounts ? w.Sum(d => d.Amount) : null, amounts ? w.Sum(d => revenue.GetValueOrDefault(d.Id)) : null);
        }

        var sourceIds = leadGroups.Select(l => l.SourceId).Concat(created.Select(d => d.SourceId)).OfType<long>().Distinct().ToList();
        var sourceNames = await db.CrmReferences.AsNoTracking().Where(x => x.OrganizationId == org && sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var sources = sourceIds.Select(id => (long?)id).Append(null)
            .Select(sid => ConversionOf(sid, sid is { } x ? sourceNames.GetValueOrDefault(x, "—") : "Не указан", leadGroups.Where(l => l.SourceId == sid).ToList(),
                created.Where(d => d.SourceId == sid).ToList()))
            .Where(s => s.Leads + s.Deals > 0).OrderByDescending(s => s.Deals).ThenByDescending(s => s.Leads).ToList();

        // Активности, выполненные в периоде, по исполнителю.
        var acts = await db.CrmActivities.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.IsDone && a.DoneAtUtc >= fromUtc && a.DoneAtUtc < toUtc)
            .GroupBy(a => a.DoneByUserId).Select(g => new { UserId = g.Key, Count = g.Count() }).ToListAsync(ct);

        // D688: план-факт по месяцам периода целиком, по всей организации (план без воронки); без crm.all — только свой план и факт.
        var monthFrom = new DateOnly(f.From.Year, f.From.Month, 1);
        var monthTo = new DateOnly(f.To.Year, f.To.Month, 1).AddMonths(1).AddDays(-1);
        var factUser = seesAll ? f.ResponsibleUserId : ctx.UserId;
        var settings = await CrmEvents.SettingsAsync(db, org, ct);
        var facts = amounts ? await FactByDayAsync(org, settings.PlanFact, tz, monthFrom, monthTo, factUser, ct) : [];
        var (firstKey, lastKey) = (monthFrom.Year * 12 + monthFrom.Month, monthTo.Year * 12 + monthTo.Month);
        var plans = amounts
            ? await db.CrmPlans.AsNoTracking()
                .Where(p => p.OrganizationId == org && p.PipelineId == null && p.Year * 12 + p.Month >= firstKey && p.Year * 12 + p.Month <= lastKey
                            && (factUser == null || p.UserId == factUser))
                .Select(p => new PlanRow(p.Year, p.Month, p.UserId, p.Amount)).ToListAsync(ct)
            : [];
        var months = new List<DateOnly>();
        for (var m = monthFrom; m <= monthTo; m = m.AddMonths(1))
        {
            months.Add(m);
        }

        decimal TotalPlan(DateOnly m)
        {
            var month = plans.Where(p => p.Year == m.Year && p.Month == m.Month).ToList();
            return factUser is null && month.Any(p => p.UserId == null) ? month.Where(p => p.UserId == null).Sum(p => p.Amount) : month.Sum(p => p.Amount);
        }

        decimal UserPlan(DateOnly m, long userId) => plans.Where(p => p.Year == m.Year && p.Month == m.Month && p.UserId == userId).Sum(p => p.Amount);
        decimal FactIn(DateOnly from, DateOnly to, long? userId) => facts.Where(x => x.Day >= from && x.Day <= to && (userId == null || x.UserId == userId)).Sum(x => x.Amount);
        bool Inside(DateOnly m) => m >= f.From && m.AddMonths(1).AddDays(-1) <= f.To;

        var planFactMonths = amounts
            ? months.Select(m => new CrmPlanFactMonthDto(m.Year, m.Month, TotalPlan(m), FactIn(m, m.AddMonths(1).AddDays(-1), null))).ToList()
            : [];

        // Менеджеры — все, кто встречается в разделах отчёта; имена одним запросом.
        var userIds = created.Select(d => d.UserId).Concat(wonClosed.Select(d => d.ResponsibleUserId)).Concat(acts.Select(a => a.UserId).OfType<long>())
            .Concat(leadGroups.Select(l => l.UserId)).Concat(exits.Select(x => x.ResponsibleUserId)).Concat(facts.Select(x => x.UserId))
            .Concat(plans.Select(p => p.UserId).OfType<long>()).Distinct().Where(Visible).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        userIds = userIds.OrderBy(u => users.GetValueOrDefault(u) ?? "").ThenBy(u => u).ToList();
        string Name(long u) => users.GetValueOrDefault(u) ?? "—";
        var createdBy = created.ToLookup(d => d.UserId);
        var leadsBy = leadGroups.ToLookup(l => l.UserId);
        var exitsBy = exits.ToLookup(x => x.ResponsibleUserId);
        var wonBy = wonClosed.ToLookup(d => d.ResponsibleUserId);

        var managers = userIds.Select(u => new CrmManagerRowDto(u, Name(u), acts.Where(a => a.UserId == u).Sum(a => a.Count), createdBy[u].Count(), wonBy[u].Count(),
                amounts ? wonBy[u].Sum(d => d.Amount) : null, amounts ? months.Where(Inside).Sum(m => UserPlan(m, u)) : null, amounts ? FactIn(f.From, f.To, u) : null))
            .ToList();
        var managerFunnel = userIds.Where(u => createdBy[u].Any()).Select(u => new CrmManagerFunnelDto(u, Name(u), FunnelOf(createdBy[u].ToList()))).ToList();
        var managerConversion = userIds.Where(u => createdBy[u].Any() || leadsBy[u].Any())
            .Select(u => ConversionOf(u, Name(u), leadsBy[u].ToList(), createdBy[u].ToList())).ToList();
        var managerDurations = userIds.Where(u => exitsBy[u].Any() || wonBy[u].Any()).Select(u =>
        {
            var byStage = exitsBy[u].ToLookup(x => x.StageId, x => x.DaysInStage);
            return new CrmManagerDurationDto(u, Name(u), openStages.Select(s => CrmDurationDto.Of(byStage[s.Id])).ToList(), CrmDurationDto.Of(wonBy[u].Select(d => d.Days)));
        }).ToList();
        var managerPlanFact = amounts
            ? userIds.Where(u => plans.Any(p => p.UserId == u) || facts.Any(x => x.UserId == u))
                .Select(u => new CrmManagerPlanFactDto(u, Name(u), months.Select(m => new CrmPlanFactMonthDto(m.Year, m.Month, UserPlan(m, u),
                    FactIn(m, m.AddMonths(1).AddDays(-1), u))).ToList())).ToList()
            : [];

        var conversion = ConversionOf(null, Total, leadGroups, created);
        return new CrmReportDto(f with { PipelineId = pipelineId }, pipeline, funnel, conversion.Leads, leadGroups.Sum(l => l.Qualified),
            conversion.LeadDeals, conversion.LeadWon, created.Count, wonClosed.Count, lost.Count, amounts ? wonClosed.Sum(d => d.Amount) : null, cycle.AvgDays ?? 0,
            reasons, sources, managers, amounts ? months.Where(Inside).Sum(TotalPlan) : null, amounts ? FactIn(f.From, f.To, null) : null, settings.PlanFact, amounts,
            managerFunnel, conversion, managerConversion, durations, cycle, managerDurations, planFactMonths, managerPlanFact);
    }

    /// <summary>
    /// Отчёт в Excel (D630, D689): листы «Воронка», «Воронка по менеджерам», «Конверсия», «Источники», «Длительность этапов», «Причины»,
    /// «Менеджеры», «План-факт». Суммы — пустые без «Цены и суммы». Выгрузка — в журнал.
    /// </summary>
    public async Task<byte[]> ExportAsync(CrmReportFilter f, CancellationToken ct = default)
    {
        var r = await BuildAsync(f, ct);
        var ctx = await guard.CurrentAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmExported, "CrmReport", null, null,
            $"отчёт CRM {f.From:dd.MM.yyyy}–{f.To:dd.MM.yyyy}", r.Pipeline, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        static string N(decimal? v) => v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        static IReadOnlyList<string> Conv(CrmConversionRowDto x) =>
        [
            x.Name, x.Leads.ToString(), x.LeadDeals.ToString(), x.LeadWon.ToString(), N(x.LeadToDeal), N(x.LeadToWon), x.Deals.ToString(), x.Won.ToString(),
            N(x.DealToWon), N(x.WonAmount), N(x.Revenue),
        ];
        string[] convHeader =
        [
            "", "Лидов", "Лидов со сделкой", "Выиграно из лидов", "Лид → сделка, %", "Лид → выигрыш, %", "Новых сделок", "Выиграно", "Сделка → выигрыш, %",
            "Сумма выигрышей", "Выручка (оплаты)",
        ];
        var convNumeric = Enumerable.Range(1, 10).ToHashSet();
        var stageNames = r.Funnel.Select(s => s.Stage).ToList();
        var openNames = r.Durations.Select(s => s.Stage).ToList();
        IEnumerable<IReadOnlyList<string>> DurationRows(string who, IReadOnlyList<CrmDurationDto> cells, CrmDurationDto cycle) =>
            cells.Select((c, i) => (IReadOnlyList<string>)[who, openNames[i], c.Count.ToString(), N(c.AvgDays), N(c.MedianDays)])
                .Append([who, "Цикл выигранной сделки", cycle.Count.ToString(), N(cycle.AvgDays), N(cycle.MedianDays)]);
        IEnumerable<IReadOnlyList<string>> PlanRows(string who, IEnumerable<CrmPlanFactMonthDto> months) =>
            months.Select(m => (IReadOnlyList<string>)[who, $"{m.Month:00}.{m.Year}", N(m.Plan), N(m.Fact), N(m.Done)]);

        return spreadsheet.Write([
            new SheetData("Воронка", ["Этап", "Дошло сделок", "Сумма", "Конверсия в следующий, %", "Среднее дней на этапе"],
                r.Funnel.Select(s => (IReadOnlyList<string>)[s.Stage, s.Reached.ToString(), N(s.Amount), N(s.ConversionToNext), N(s.AvgDays)]).ToList(),
                new HashSet<int> { 1, 2, 3, 4 }),
            new SheetData("Воронка по менеджерам", ["Менеджер", "Этап", "Дошло сделок", "Сумма", "Конверсия в следующий, %"],
                r.ManagerFunnel.SelectMany(m => m.Steps.Select((c, i) => (IReadOnlyList<string>)[m.Manager, stageNames[i], c.Reached.ToString(), N(c.Amount),
                    N(c.ConversionToNext)])).ToList(), new HashSet<int> { 2, 3, 4 }),
            new SheetData("Конверсия", ["Менеджер", .. convHeader[1..]], r.ManagerConversion.Prepend(r.Conversion).Select(Conv).ToList(), convNumeric),
            new SheetData("Источники", ["Источник", .. convHeader[1..]], r.Sources.Select(Conv).ToList(), convNumeric),
            new SheetData("Длительность этапов", ["Менеджер", "Этап", "Переходов / сделок", "Среднее дней", "Медиана дней"],
                DurationRows(Total, r.Durations.Select(d => d.Days).ToList(), r.Cycle)
                    .Concat(r.ManagerDurations.SelectMany(m => DurationRows(m.Manager, m.Stages, m.Cycle))).ToList(), new HashSet<int> { 2, 3, 4 }),
            new SheetData("Причины", ["Причина проигрыша", "Сделок", "Сумма"],
                r.LossReasons.Select(x => (IReadOnlyList<string>)[x.Reason, x.Deals.ToString(), N(x.Amount)]).ToList(), new HashSet<int> { 1, 2 }),
            new SheetData("Менеджеры", ["Менеджер", "Активностей", "Сделок", "Выиграно", "Сумма выигрышей", "План", "Факт"],
                r.Managers.Select(x => (IReadOnlyList<string>)[x.Manager, x.Activities.ToString(), x.Deals.ToString(), x.Won.ToString(), N(x.WonAmount), N(x.Plan),
                    N(x.Fact)]).ToList(), new HashSet<int> { 1, 2, 3, 4, 5, 6 }),
            new SheetData("План-факт", ["Менеджер", "Месяц", "План", "Факт", "Выполнение, %"],
                PlanRows(Total, r.PlanFactMonths).Concat(r.ManagerPlanFact.SelectMany(m => PlanRows(m.Manager, m.Months))).ToList(), new HashSet<int> { 2, 3, 4 }),
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

    /// <summary>
    /// Факт по дням и ответственным (D688) за [fromDate; toDate] по всем воронкам: оплаты по заказам из сделок (по дате оплаты, сумма — в базе)
    /// или выигранные сделки (по местной дате закрытия; из базы — менеджер, время и сумма).
    /// </summary>
    private async Task<List<FactRow>> FactByDayAsync(long org, CrmPlanFact mode, string tz, DateOnly fromDate, DateOnly toDate, long? userId, CancellationToken ct)
    {
        var deals = db.CrmDeals.AsNoTracking().Where(d => d.OrganizationId == org);
        if (userId is { } u)
        {
            deals = deals.Where(d => d.ResponsibleUserId == u);
        }

        if (mode == CrmPlanFact.WonDeals)
        {
            var fromUtc = CrmEvents.ToUtc(fromDate.ToDateTime(TimeOnly.MinValue), tz);
            var toUtc = CrmEvents.ToUtc(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
            var rows = await deals.Where(d => d.Outcome == CrmDealOutcome.Won && d.ClosedAtUtc >= fromUtc && d.ClosedAtUtc < toUtc)
                .Select(d => new { d.ResponsibleUserId, d.ClosedAtUtc, d.Amount }).ToListAsync(ct);
            return rows.GroupBy(x => (x.ResponsibleUserId, Day: CrmEvents.Today(DateTime.SpecifyKind(x.ClosedAtUtc!.Value, DateTimeKind.Utc), tz)))
                .Select(g => new FactRow(g.Key.ResponsibleUserId, g.Key.Day, g.Sum(x => x.Amount))).ToList();
        }

        return await (from l in db.CrmDealOrders.AsNoTracking()
                      join d in deals on l.DealId equals d.Id
                      join a in db.CustomerPaymentAllocations.AsNoTracking() on l.SalesOrderId equals a.OrderId
                      join pay in db.CustomerPayments.AsNoTracking() on a.PaymentId equals pay.Id
                      where l.OrganizationId == org && pay.OrganizationId == org && a.RemovedAtUtc == null && pay.Status == CustomerPaymentStatus.Posted
                            && pay.PaymentDate >= fromDate && pay.PaymentDate <= toDate
                      group a.Amount by new { d.ResponsibleUserId, pay.PaymentDate } into g
                      select new FactRow(g.Key.ResponsibleUserId, g.Key.PaymentDate, g.Sum())).ToListAsync(ct);
    }

    private sealed record DealRow(long Id, long UserId, long? SourceId, decimal Amount, CrmDealOutcome Outcome, long StageId);

    private sealed record LeadGroup(long UserId, long? SourceId, int Leads, int Qualified, int Deals, int Won);

    private sealed record PlanRow(int Year, int Month, long? UserId, decimal Amount);

    private sealed record FactRow(long UserId, DateOnly Day, decimal Amount);
}
