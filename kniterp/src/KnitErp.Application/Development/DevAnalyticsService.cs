using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

public sealed record DevStageStatDto(DevStage Stage, string Name, int Open, int Overdue, int Late, decimal? AvgDays);

public sealed record DevLoadDto(string User, string Role, int Open, int Overdue);

public sealed record DevReasonDto(string Reason, int Count);

public sealed record DevPlanFactDto(long ModelId, string Number, string Name, decimal? TargetCost, decimal? CalculatedCost, decimal? Difference);

/// <summary>
/// Аналитика разработки за период (D760–D766): в работе и просрочки по стадиям, среднее время разработки и рисунка, образцы на модель,
/// решения по образцам и причины доработок, загрузка участников, план-факт себестоимости (только с правом на суммы).
/// </summary>
public sealed record DevAnalyticsDto(
    DateOnly From, DateOnly To, int InWork, int Overdue, int Late, int Handed, int Cancelled, decimal? AvgDevelopmentDays, decimal? AvgPatternDays,
    decimal? SamplesPerModel, int SamplesAccepted, int SamplesRework, int SamplesRejected, int SamplesWaiting, IReadOnlyList<DevStageStatDto> Stages,
    IReadOnlyList<DevReasonDto> ReworkReasons, IReadOnlyList<DevLoadDto> Load, IReadOnlyList<DevPlanFactDto>? PlanFact,
    IReadOnlyList<DevSampleOutDto>? SamplesOut = null, decimal? PatternVersionsPerModel = null, IReadOnlyList<DevPatternReuseDto>? PatternReuse = null,
    int SamplesToCustomers = 0, int SamplesKeptBySoldTo = 0, int SamplesReturned = 0);

/// <summary>Рисунок, использованный в нескольких моделях (ТЗ §12 «повторное использование рисунков из библиотеки»).</summary>
public sealed record DevPatternReuseDto(long PatternId, string Number, string Name, int Models);

/// <summary>Данные модуля разработки для главного экрана (ТЗ §12): в работе, просрочено, образцы ждут решения, у заказчиков и не возвращены к сроку (D723).</summary>
public sealed record DevDashboardData(int InWork, int Overdue, int SamplesWaiting, string Href = "dev/models", int SamplesAtCustomer = 0,
    int SamplesOverdue = 0);

/// <summary>
/// Источник блока «Разработка» главного экрана (ТЗ §12, по образцу IProductionDashboardSource D111): база подключает его к своему
/// блоку по запросу в docs/sessions/development.md. Без `dev.view` — null. Вызывается с контекстом базы главного экрана, без параллельных запросов.
/// </summary>
public interface IDevelopmentDashboardSource
{
    Task<DevDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct);
}

/// <summary>
/// Аналитика разработки (D760–D766, этап 7 ТЗ). Смотреть — `dev.view`; суммы плана-факта — только с `dev.cost.view`. Версии и повторное
/// использование рисунков — по библиотеке рисунков (D726). Фактическая себестоимость производственных заказов не считается: факта затрат
/// по заказам в системе пока нет.
/// </summary>
public sealed class DevAnalyticsService(IKnitErpDbContext db, IAccessGuard guard, IClock clock, ISpreadsheetFormat spreadsheet) : IDevelopmentDashboardSource
{
    public const int MaxPeriodDays = 731;

    public async Task<DevAnalyticsDto> ReportAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        if (to < from || to.DayNumber - from.DayNumber > MaxPeriodDays)
        {
            throw new BusinessRuleException("dev.analytics.period", "Период — не длиннее двух лет, начало не позже конца.");
        }

        var org = ctx.OrganizationId;
        var zone = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById(zone));
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById(zone));
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, zone));
        var plan = await DevStagePlan.LoadAsync(db, org, ct);

        // Открытые модели — сейчас (не за период): стадия, срок, исполнители.
        var open = await db.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled)
            .Select(m => new
            {
                m.Stage, m.StageSinceUtc, m.PlannedLaunch, m.DesignerUserId, m.DessinatorUserId, m.ConstructorUserId, m.TechnologistUserId, m.ProgrammerUserId,
            })
            .ToListAsync(ct);
        bool Overdue(DevStage s, DateTime since) => DevModel.IsStageOverdue(s, since, plan.PlanDays(s), now);
        bool Late(DevStage s, DateOnly? launch) => DevModel.IsLate(s, launch, today);

        // История стадий за период: среднее время на стадии (дней на прежней), время рисунка, передачи и отмены.
        var entries = await db.DevModelStages.AsNoTracking().Where(e => e.OrganizationId == org && e.AtUtc >= fromUtc && e.AtUtc < toUtc)
            .Select(e => new { e.ModelId, e.FromStage, e.ToStage, e.AtUtc, e.DaysInPrevious }).ToListAsync(ct);
        var avgDays = entries.Where(e => e.FromStage is not null && e.DaysInPrevious is not null).GroupBy(e => e.FromStage!.Value)
            .ToDictionary(g => g.Key, g => Avg(g.Select(e => (decimal)e.DaysInPrevious!.Value)));
        var handed = entries.Where(e => e.ToStage == DevStage.Handed).ToList();
        var handedIds = handed.Select(h => h.ModelId).Distinct().ToList();
        var created = await db.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && handedIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Number, m.Name, m.CreatedAtUtc, m.TargetCost }).ToListAsync(ct);
        var devDays = handed.Join(created, h => h.ModelId, c => c.Id, (h, c) => (decimal)(h.AtUtc - c.CreatedAtUtc).TotalDays).ToList();

        var stages = DevStages.Path.Where(s => s != DevStage.Handed && plan.IsEnabled(s)).Select(s => new DevStageStatDto(s, DevStages.Name(s),
            open.Count(m => m.Stage == s), open.Count(m => m.Stage == s && Overdue(m.Stage, m.StageSinceUtc)), open.Count(m => m.Stage == s && Late(m.Stage, m.PlannedLaunch)),
            avgDays.GetValueOrDefault(s))).ToList();

        // Образцы, созданные за период: решения и причины доработок (комментарий решения).
        var samples = await db.DevSamples.AsNoTracking().Where(s => s.OrganizationId == org && s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc)
            .Select(s => new { s.ModelId, s.Status, s.Comment, s.ReasonId }).ToListAsync(ct);
        var reasonIds = samples.Where(s => s.ReasonId is not null).Select(s => s.ReasonId!.Value).Distinct().ToList();
        var reasonNames = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && reasonIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var reasons = samples.Where(s => s.Status is DevSampleStatus.Rework or DevSampleStatus.Rejected)
            // D768: причина из справочника, иначе — текст решения (как написали).
            .GroupBy(s => s.ReasonId is { } rid && reasonNames.TryGetValue(rid, out var name) ? name
                : string.IsNullOrWhiteSpace(s.Comment) ? "без комментария" : s.Comment.Trim().ToLowerInvariant())
            .Select(g => new DevReasonDto(g.Key, g.Count())).OrderByDescending(r => r.Count).ThenBy(r => r.Reason).Take(15).ToList();

        // Загрузка: открытые модели по исполнителю и роли.
        var assignments = open.SelectMany(m => new (DevRole Role, long? User)[]
            {
                (DevRole.Designer, m.DesignerUserId), (DevRole.Dessinator, m.DessinatorUserId), (DevRole.Constructor, m.ConstructorUserId),
                (DevRole.Technologist, m.TechnologistUserId), (DevRole.Programmer, m.ProgrammerUserId),
            }.Where(a => a.User is not null).Select(a => (a.Role, User: a.User!.Value, Overdue: Overdue(m.Stage, m.StageSinceUtc)))).ToList();
        var userIds = assignments.Select(a => a.User).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var load = assignments.GroupBy(a => (a.User, a.Role))
            .Select(g => new DevLoadDto(names.GetValueOrDefault(g.Key.User, "—"), DevModel.RoleName(g.Key.Role), g.Count(), g.Count(a => a.Overdue)))
            .OrderByDescending(l => l.Open).ThenBy(l => l.User).ToList();

        // План-факт себестоимости переданных за период: целевая из задания против последней версии расчёта (только с правом на суммы).
        List<DevPlanFactDto>? planFact = null;
        if (ctx.Permissions.Has(Permissions.DevCostView))
        {
            var runs = await db.DevCostRuns.AsNoTracking().Where(r => r.OrganizationId == org && handedIds.Contains(r.ModelId))
                .Select(r => new { r.ModelId, r.Version, r.Cost }).ToListAsync(ct);
            planFact = created.OrderBy(c => c.Number).Select(c =>
            {
                var cost = runs.Where(r => r.ModelId == c.Id).OrderByDescending(r => r.Version).Select(r => (decimal?)r.Cost).FirstOrDefault();
                return new DevPlanFactDto(c.Id, c.Number, c.Name, c.TargetCost, cost, cost is { } v && c.TargetCost is { } t ? v - t : null);
            }).ToList();
        }

        var withSamples = samples.Select(s => s.ModelId).Distinct().Count();

        // D726: версий рисунка на модель — у моделей с образцами или переходами за период, где выбран рисунок; повторное использование —
        // рисунки, выбранные в двух и более моделях (за всё время).
        var activeIds = samples.Select(s => s.ModelId).Concat(entries.Select(e => e.ModelId)).Distinct().ToList();
        var versionsPerModel = await (from m in db.DevModels.AsNoTracking()
                                      join p in db.DevPatterns.AsNoTracking() on m.PatternId equals p.Id
                                      where m.OrganizationId == org && activeIds.Contains(m.Id)
                                      select p.CurrentVersion).ToListAsync(ct);
        var patternVersions = Avg(versionsPerModel.Select(v => (decimal)v));
        var reuse = await (from m in db.DevModels.AsNoTracking()
                           join p in db.DevPatterns.AsNoTracking() on m.PatternId equals p.Id
                           where m.OrganizationId == org
                           group m by new { p.Id, p.Number, p.Name } into g
                           where g.Count() > 1
                           orderby g.Count() descending
                           select new DevPatternReuseDto(g.Key.Id, g.Key.Number, g.Key.Name, g.Count())).Take(50).ToListAsync(ct);

        // D723: образцы, переданные заказчикам за период, и чем закончилось.
        var periodFrom = from;
        var periodTo = to;
        var custody = await db.DevSamples.AsNoTracking()
            .Where(s => s.OrganizationId == org && s.HandedOn != null && s.HandedOn >= periodFrom && s.HandedOn <= periodTo)
            .Select(s => new { s.Custody }).ToListAsync(ct);
        return new DevAnalyticsDto(from, to, open.Count, open.Count(m => Overdue(m.Stage, m.StageSinceUtc)), open.Count(m => Late(m.Stage, m.PlannedLaunch)),
            handedIds.Count, entries.Where(e => e.ToStage == DevStage.Cancelled).Select(e => e.ModelId).Distinct().Count(), Avg(devDays),
            avgDays.GetValueOrDefault(DevStage.Pattern), withSamples == 0 ? null : Math.Round((decimal)samples.Count / withSamples, 1),
            samples.Count(s => s.Status == DevSampleStatus.Accepted), samples.Count(s => s.Status == DevSampleStatus.Rework),
            samples.Count(s => s.Status == DevSampleStatus.Rejected), samples.Count(s => s.Status is DevSampleStatus.Knitting or DevSampleStatus.Ready),
            stages, reasons, load, planFact, await DevShopService.SamplesOut(db, org, today, ct), patternVersions, reuse,
            custody.Count, custody.Count(c => c.Custody is DevSampleCustody.Kept or DevSampleCustody.Sold), custody.Count(c => c.Custody == DevSampleCustody.Returned));
    }

    /// <summary>Выгрузка отчёта в Excel (ТЗ §12): лист на каждый раздел; суммы — только с правом на себестоимость.</summary>
    public async Task<byte[]> ExportAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var r = await ReportAsync(from, to, ct);
        static string N(decimal? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        var sheets = new List<SheetData>
        {
            new("Итоги", ["Показатель", "Значение"],
            [
                ["Период", $"{r.From:dd.MM.yyyy} – {r.To:dd.MM.yyyy}"], ["В работе", $"{r.InWork}"], ["Стадия дольше нормы", $"{r.Overdue}"],
                ["Просрочен запуск", $"{r.Late}"], ["Передано в производство", $"{r.Handed}"], ["Отменено", $"{r.Cancelled}"],
                ["Среднее время разработки, дн.", N(r.AvgDevelopmentDays)], ["Среднее время рисунка, дн.", N(r.AvgPatternDays)],
                ["Образцов на модель", N(r.SamplesPerModel)], ["Образцы: приняты", $"{r.SamplesAccepted}"], ["Образцы: на доработку", $"{r.SamplesRework}"],
                ["Образцы: отклонены", $"{r.SamplesRejected}"], ["Образцы: ждут решения", $"{r.SamplesWaiting}"],
                ["Версий рисунка на модель", N(r.PatternVersionsPerModel)], ["Образцов передано заказчикам", $"{r.SamplesToCustomers}"],
                ["Оставлены у заказчиков или проданы", $"{r.SamplesKeptBySoldTo}"], ["Возвращены", $"{r.SamplesReturned}"],
            ]),
            new("Повторное использование рисунков", ["Рисунок", "Название", "Моделей"],
                (r.PatternReuse ?? []).Select(x => (IReadOnlyList<string>)[x.Number, x.Name, $"{x.Models}"]).ToList(), new HashSet<int> { 2 }),
            new("Стадии", ["Стадия", "В работе", "Дольше нормы", "Просрочен запуск", "Среднее, дн."],
                r.Stages.Select(s => (IReadOnlyList<string>)[s.Name, $"{s.Open}", $"{s.Overdue}", $"{s.Late}", N(s.AvgDays)]).ToList(), new HashSet<int> { 1, 2, 3, 4 }),
            new("Причины доработок", ["Причина", "Образцов"], r.ReworkReasons.Select(x => (IReadOnlyList<string>)[x.Reason, $"{x.Count}"]).ToList(), new HashSet<int> { 1 }),
            new("Загрузка", ["Участник", "Роль", "Моделей в работе", "Из них дольше нормы"],
                r.Load.Select(l => (IReadOnlyList<string>)[l.User, l.Role, $"{l.Open}", $"{l.Overdue}"]).ToList(), new HashSet<int> { 2, 3 }),
        };
        sheets.Add(new SheetData("Образцы у заказчиков", ["Модель", "Название", "Образец", "Кому", "Передан", "Вернуть до", "Просрочен"],
            (r.SamplesOut ?? []).Select(x => (IReadOnlyList<string>)[x.Number, x.ModelName, $"{x.No}", x.Recipient, x.HandedOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                x.ReturnBy?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "", x.Overdue ? "да" : ""]).ToList(), new HashSet<int> { 2 }));
        if (r.PlanFact is { } pf)
        {
            sheets.Add(new SheetData("План-факт себестоимости", ["Модель", "Название", "Целевая", "Расчёт", "Разница"],
                pf.Select(p => (IReadOnlyList<string>)[p.Number, p.Name, N(p.TargetCost), N(p.CalculatedCost), N(p.Difference)]).ToList(), new HashSet<int> { 2, 3, 4 }));
        }

        return spreadsheet.Write(sheets);
    }

    public async Task<DevDashboardData?> GetAsync(IKnitErpDbContext dashboardDb, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.DevView))
        {
            return null;
        }

        var org = ctx.OrganizationId;
        var plan = await DevStagePlan.LoadAsync(dashboardDb, org, ct);
        var now = clock.UtcNow;
        var open = await dashboardDb.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled)
            .Select(m => new { m.Stage, m.StageSinceUtc }).ToListAsync(ct);
        var waiting = await dashboardDb.DevSamples.AsNoTracking().CountAsync(s => s.OrganizationId == org && s.Status == DevSampleStatus.Ready, ct);
        var out_ = await dashboardDb.DevSamples.AsNoTracking().Where(s => s.OrganizationId == org && s.Custody == DevSampleCustody.AtCustomer)
            .Select(s => s.ReturnBy).ToListAsync(ct);
        return new DevDashboardData(open.Count, open.Count(m => DevModel.IsStageOverdue(m.Stage, m.StageSinceUtc, plan.PlanDays(m.Stage), now)), waiting,
            SamplesAtCustomer: out_.Count, SamplesOverdue: out_.Count(r => r is { } d && d < today));
    }

    private static decimal? Avg(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }
}
