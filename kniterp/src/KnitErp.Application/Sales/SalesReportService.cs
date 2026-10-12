using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Разрез аналитики продаж (D1160).</summary>
public enum SalesDimension : byte
{
    Customer = 1,
    Manager = 2,
    Group = 3,
    Channel = 4,
    Period = 5,
    Item = 6,
}

public enum SalesPeriodStep : byte
{
    Day = 1,
    Week = 2,
    Month = 3,
}

/// <summary>
/// Отбор аналитики продаж (D1160): период — по дате отгрузки и возврата; ManagerId, ChannelId, GroupId — 0 означает «не указан»
/// (без ответственного, без канала, без группы).
/// </summary>
public sealed record SalesReportFilter(
    DateOnly From, DateOnly To, SalesDimension By = SalesDimension.Customer, SalesPeriodStep Step = SalesPeriodStep.Month, long? ManagerId = null,
    long? ChannelId = null, long? CustomerId = null, long? GroupId = null);

/// <summary>
/// Строка разреза (D1160): Id null — «без ответственного / канала / группы». Revenue — выручка без НДС (отгрузки − возвраты по ценам
/// заказа), Cost — себестоимость проданного (как «Прибыльность», D69); без права «Цены и суммы» обе — null. CostComplete = false —
/// у части позиций цены нет, маржа не считается.
/// </summary>
public sealed record SalesReportRowDto(
    long? Id, string? Name, string? Note, DateOnly? PeriodStart, decimal Quantity, decimal? Revenue, decimal? Cost, bool CostComplete, int Orders, int Customers)
{
    public decimal? Margin => Revenue is { } r && Cost is { } c && CostComplete ? r - c : null;
    public decimal? MarginPercent => Margin is { } m && Revenue is { } r && r != 0 ? Math.Round(m / r * 100, 1) : null;

    /// <summary>Средний чек — выручка без НДС на заказ, по которому была отгрузка в периоде.</summary>
    public decimal? AverageCheck => Revenue is { } r && Orders > 0 ? Money.Round(r / Orders) : null;
}

/// <summary>Отчёт по разрезу: строки, итог, повторные покупатели (два и больше заказов с отгрузкой в периоде).</summary>
public sealed record SalesReportDto(
    SalesReportFilter Filter, IReadOnlyList<SalesReportRowDto> Rows, SalesReportRowDto Total, int RepeatCustomers, bool AmountsVisible, bool OwnOnly)
{
    public decimal? RepeatSharePercent => Total.Customers == 0 ? null : Math.Round(RepeatCustomers * 100m / Total.Customers, 1);
}

public enum AbcSubject : byte
{
    Customer = 1,
    Item = 2,
}

public enum AbcBasis : byte
{
    Revenue = 1,
    Quantity = 2,
}

/// <summary>Строка ABC (D1161): доля и накопленная доля, класс A/B/C.</summary>
public sealed record AbcRowDto(long Id, string Name, string? Code, decimal Value, decimal SharePercent, decimal CumulativePercent, char Class);

/// <summary>ABC-анализ: Basis — фактическая основа (без права «Цены и суммы» — количество, а не выручка).</summary>
public sealed record AbcDto(AbcSubject Subject, AbcBasis Basis, decimal APercent, decimal BPercent, IReadOnlyList<AbcRowDto> Rows, decimal Total)
{
    public int Count(char cls) => Rows.Count(r => r.Class == cls);
    public decimal Share(char cls) => Rows.Where(r => r.Class == cls).Sum(r => r.SharePercent);
}

/// <summary>План-факт менеджера (D1162): без права «Цены и суммы» суммы — null.</summary>
public sealed record PlanFactRowDto(long? ManagerId, string? Manager, decimal? Plan, decimal? Fact, byte[]? PlanVersion)
{
    public decimal? Percent => Plan is > 0 && Fact is { } f ? Math.Round(f / Plan.Value * 100, 1) : null;
    public decimal? Deviation => Plan is { } p && Fact is { } f ? f - p : null;
}

/// <summary>План-факт за месяц; ElapsedPercent — какая часть месяца прошла (для темпа), CanEdit — можно вводить план.</summary>
public sealed record PlanFactDto(DateOnly Month, IReadOnlyList<PlanFactRowDto> Rows, bool AmountsVisible, bool OwnOnly, bool CanEdit, decimal ElapsedPercent)
{
    public decimal? Plan => AmountsVisible ? Rows.Sum(r => r.Plan ?? 0) : null;
    public decimal? Fact => AmountsVisible ? Rows.Sum(r => r.Fact ?? 0) : null;
    public decimal? Percent => Plan is > 0 && Fact is { } f ? Math.Round(f / Plan.Value * 100, 1) : null;
}

public enum FunnelStage : byte
{
    Created = 1,
    Confirmed = 2,
    Shipped = 3,
    Paid = 4,
}

/// <summary>Этап воронки (D1163): заказы и сумма с НДС; ConversionPercent — доля от предыдущего этапа.</summary>
public sealed record FunnelStageDto(FunnelStage Stage, int Orders, decimal? Amount, decimal? ConversionPercent);

public sealed record SalesFunnelDto(IReadOnlyList<FunnelStageDto> Stages, int Cancelled, bool AmountsVisible, bool OwnOnly);

/// <summary>Списки для отборов аналитики: менеджеры, каналы (с площадкой), группы.</summary>
public sealed record SalesReportOptionsDto(IReadOnlyList<LookupDto> Managers, IReadOnlyList<LookupDto> Channels, IReadOnlyList<LookupDto> Groups,
    bool AmountsVisible, bool CanEditPlan, bool OwnOnly);

/// <summary>Заказ в блоке «Отгрузить сегодня» (D1167): что осталось отгрузить; сумма — только с правом «Цены и суммы».</summary>
public sealed record ShipTodayRowDto(long OrderId, string Number, string Customer, DateOnly ShipDate, decimal LeftQuantity, decimal? LeftAmount);

public sealed record ShipTodayDto(DateOnly Today, int DueToday, int Overdue, decimal? Amount, IReadOnlyList<ShipTodayRowDto> Top);

/// <summary>Блок «Продажи за месяц к плану» (D1167).</summary>
public sealed record SalesPlanBlockDto(DateOnly Month, decimal Plan, decimal Fact, decimal ElapsedPercent, bool OwnOnly)
{
    public decimal? Percent => Plan > 0 ? Math.Round(Fact / Plan * 100, 1) : null;
}

/// <summary>
/// Аналитика продаж (D1160–D1167): выручка и маржа по разрезам, ABC, план-факт, средний чек и повторные покупки, воронка
/// «заказ → отгрузка → оплата», блоки главного экрана, выгрузка в Excel. Источник — проведённые отгрузки и возвраты по заказам
/// (как «Товары и реализации», D69); суммируется в SQL Server, в память приходят только итоги по (разрез, позиция).
/// Права: «Продажи: отчёты»; суммы, себестоимость и маржа — только с «Цены и суммы» (иначе сервер отдаёт null);
/// роль с видимостью «Свои» видит только заказы, где пользователь ответственный.
/// </summary>
public sealed class SalesReportService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxPeriodDays = SalesAnalyticsService.MaxPeriodDays;
    public const int MaxDayRows = 92;
    public const int MaxShipTodayOrders = 500;

    // ---------- Разрезы ----------

    public async Task<SalesReportDto> ReportAsync(SalesReportFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        return await BuildAsync(ctx, filter, own, ct);
    }

    private sealed class Fact
    {
        public DateOnly Date { get; init; }
        public long OrderId { get; init; }
        public long CustomerId { get; init; }
        public long? ManagerId { get; init; }
        public long? ChannelId { get; init; }
        public long? GroupId { get; init; }
        public long ItemId { get; init; }
        public decimal Quantity { get; init; }
        public decimal Revenue { get; init; }
    }

    private sealed class KeyedFact
    {
        public long? Key { get; init; }
        public long OrderId { get; init; }
        public long CustomerId { get; init; }
        public long ItemId { get; init; }
        public decimal Quantity { get; init; }
        public decimal Revenue { get; init; }
    }

    /// <summary>
    /// Строки отгрузок (+) и возвратов (−) по заказам за период: выручка без НДС = количество × (сумма − НДС) / количество строки заказа.
    /// Запрос, не список: его группирует SQL Server.
    /// </summary>
    private IQueryable<Fact> Facts(AccessContext ctx, DateOnly from, DateOnly to, SalesReportFilter filter, bool own)
    {
        var org = ctx.OrganizationId;
        var docs = db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId != null && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn)
                        && d.DocumentDate >= from && d.DocumentDate <= to);
        var orders = db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org);
        if (own)
        {
            var me = ctx.UserId;
            orders = orders.Where(o => o.ResponsibleUserId == me);
        }

        if (filter.ManagerId is { } m)
        {
            orders = m == 0 ? orders.Where(o => o.ResponsibleUserId == null) : orders.Where(o => o.ResponsibleUserId == m);
        }

        if (filter.ChannelId is { } c)
        {
            orders = c == 0 ? orders.Where(o => o.ChannelId == null) : orders.Where(o => o.ChannelId == c);
        }

        if (filter.CustomerId is { } customer)
        {
            orders = orders.Where(o => o.CustomerId == customer);
        }

        var items = db.Items.AsNoTracking().Where(i => i.OrganizationId == org);
        if (filter.GroupId is { } g)
        {
            items = g == 0 ? items.Where(i => i.GroupId == null) : items.Where(i => i.GroupId == g);
        }

        return from d in docs
               join o in orders on d.SalesOrderId equals o.Id
               from l in d.Lines
               join i in items on l.ItemId equals i.Id
               from ol in o.Lines.Where(x => x.ItemId == l.ItemId)
               select new Fact
               {
                   Date = d.DocumentDate,
                   OrderId = o.Id,
                   CustomerId = o.CustomerId,
                   ManagerId = o.ResponsibleUserId,
                   ChannelId = o.ChannelId,
                   GroupId = i.GroupId,
                   ItemId = l.ItemId,
                   Quantity = d.Kind == StockOperationKind.CustomerReturn ? -l.Quantity : l.Quantity,
                   Revenue = (d.Kind == StockOperationKind.CustomerReturn ? -l.Quantity : l.Quantity) * (ol.Amount - ol.VatAmount) / ol.Quantity,
               };
    }

    private static IQueryable<KeyedFact> Keyed(IQueryable<Fact> facts, SalesReportFilter filter)
    {
        var from = filter.From;
        // Номер дня (как DateOnly.DayNumber) из года и дня года — так его считает SQL Server без функций провайдера.
        var fromDay = from.DayNumber;
        var mondayDay = from.AddDays(-(((int)from.DayOfWeek + 6) % 7)).DayNumber;
        return filter.By switch
        {
            SalesDimension.Manager => facts.Select(f => new KeyedFact
                { Key = f.ManagerId, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity, Revenue = f.Revenue }),
            SalesDimension.Group => facts.Select(f => new KeyedFact
                { Key = f.GroupId, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity, Revenue = f.Revenue }),
            SalesDimension.Channel => facts.Select(f => new KeyedFact
                { Key = f.ChannelId, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity, Revenue = f.Revenue }),
            SalesDimension.Item => facts.Select(f => new KeyedFact
                { Key = f.ItemId, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity, Revenue = f.Revenue }),
            SalesDimension.Period when filter.Step == SalesPeriodStep.Day => facts.Select(f => new KeyedFact
            {
                Key = (f.Date.Year - 1) * 365 + (f.Date.Year - 1) / 4 - (f.Date.Year - 1) / 100 + (f.Date.Year - 1) / 400 + f.Date.DayOfYear - 1 - fromDay, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity,
                Revenue = f.Revenue,
            }),
            SalesDimension.Period when filter.Step == SalesPeriodStep.Week => facts.Select(f => new KeyedFact
            {
                Key = ((f.Date.Year - 1) * 365 + (f.Date.Year - 1) / 4 - (f.Date.Year - 1) / 100 + (f.Date.Year - 1) / 400 + f.Date.DayOfYear - 1 - mondayDay) / 7,
                OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity,
                Revenue = f.Revenue,
            }),
            SalesDimension.Period => facts.Select(f => new KeyedFact
            {
                Key = (f.Date.Year - from.Year) * 12 + f.Date.Month - from.Month, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId,
                Quantity = f.Quantity, Revenue = f.Revenue,
            }),
            _ => facts.Select(f => new KeyedFact
                { Key = f.CustomerId, OrderId = f.OrderId, CustomerId = f.CustomerId, ItemId = f.ItemId, Quantity = f.Quantity, Revenue = f.Revenue }),
        };
    }

    private async Task<SalesReportDto> BuildAsync(AccessContext ctx, SalesReportFilter filter, bool own, CancellationToken ct)
    {
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var keyed = Keyed(Facts(ctx, filter.From, filter.To, filter, own), filter);
        var sums = await keyed.GroupBy(k => new { k.Key, k.ItemId })
            .Select(g => new { g.Key.Key, g.Key.ItemId, Quantity = g.Sum(x => x.Quantity), Revenue = g.Sum(x => x.Revenue) })
            .ToListAsync(ct);
        var counts = await keyed.GroupBy(k => k.Key)
            .Select(g => new { g.Key, Orders = g.Select(x => x.OrderId).Distinct().Count(), Customers = g.Select(x => x.CustomerId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key ?? long.MinValue, ct);
        var perCustomer = await keyed.GroupBy(k => k.CustomerId).Select(g => g.Select(x => x.OrderId).Distinct().Count()).ToListAsync(ct);
        var totalOrders = await keyed.Select(k => k.OrderId).Distinct().CountAsync(ct);

        IReadOnlyDictionary<long, (decimal Cost, CostSource Source)> costs = new Dictionary<long, (decimal, CostSource)>();
        if (amounts && sums.Count > 0)
        {
            costs = await new SalesAnalyticsService(db, guard).CostsAsync(ctx, filter.To, sums.Select(s => s.ItemId).Distinct().ToList(), ct);
        }

        var names = await NamesAsync(ctx, filter, sums.Where(s => s.Key is not null).Select(s => s.Key!.Value).Distinct().ToList(), ct);
        var rows = sums.GroupBy(s => s.Key).Select(g =>
        {
            var c = counts[g.Key ?? long.MinValue];
            var revenue = Money.Round(g.Sum(x => x.Revenue));
            var complete = g.All(x => costs.ContainsKey(x.ItemId));
            var cost = Money.Round(g.Where(x => costs.ContainsKey(x.ItemId)).Sum(x => x.Quantity * costs[x.ItemId].Cost));
            var (name, note, start) = g.Key is { } id ? names(id) : (null, null, null);
            return new SalesReportRowDto(g.Key, name, note, start, g.Sum(x => x.Quantity), amounts ? revenue : null, amounts ? cost : null,
                amounts && complete, c.Orders, c.Customers);
        }).Where(r => r.Quantity != 0 || r.Revenue is not (null or 0)).ToList();

        rows = filter.By == SalesDimension.Period
            ? rows.OrderBy(r => r.PeriodStart).ToList()
            : rows.OrderByDescending(r => amounts ? r.Revenue : r.Quantity).ThenBy(r => r.Id is null).ThenBy(r => r.Name, StringComparer.CurrentCulture).ToList();
        var total = new SalesReportRowDto(null, null, null, null, rows.Sum(r => r.Quantity), amounts ? rows.Sum(r => r.Revenue ?? 0) : null,
            amounts ? rows.Sum(r => r.Cost ?? 0) : null, amounts && rows.All(r => r.CostComplete), totalOrders, perCustomer.Count);
        return new SalesReportDto(filter, rows, total, perCustomer.Count(n => n >= 2), amounts, own);
    }

    /// <summary>Названия ключей разреза; для периода — подпись и начало периода.</summary>
    private async Task<Func<long, (string? Name, string? Note, DateOnly? Start)>> NamesAsync(
        AccessContext ctx, SalesReportFilter filter, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        switch (filter.By)
        {
            case SalesDimension.Customer:
            {
                var map = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && ids.Contains(c.Id))
                    .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                return id => (map.GetValueOrDefault(id), null, null);
            }
            case SalesDimension.Manager:
            {
                var map = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
                return id => (map.GetValueOrDefault(id), null, null);
            }
            case SalesDimension.Group:
            {
                var map = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org && ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
                return id => (map.GetValueOrDefault(id), null, null);
            }
            case SalesDimension.Channel:
            {
                var map = await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == org && ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
                var settings = await db.SalesChannelSettings.AsNoTracking().Where(s => s.OrganizationId == org && ids.Contains(s.ChannelId))
                    .ToDictionaryAsync(s => s.ChannelId, ct);
                return id => (map.GetValueOrDefault(id), settings.TryGetValue(id, out var s) ? ChannelNote(s) : null, null);
            }
            case SalesDimension.Item:
            {
                var map = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id))
                    .ToDictionaryAsync(i => i.Id, i => new { i.Code, i.Name }, ct);
                return id => (map.TryGetValue(id, out var i) ? i.Name : null, map.TryGetValue(id, out var j) ? j.Code : null, null);
            }
            default:
                return id => PeriodOf(filter, (int)id);
        }
    }

    /// <summary>«Wildberries, FBO» — площадка и схема канала.</summary>
    public static string? ChannelNote(SalesChannelSettings s) => s.Marketplace is { } m
        ? SalesChannelSettings.MarketplaceName(m) + (s.Scheme is { } sc ? ", " + SalesChannelSettings.SchemeName(sc) : "")
        : null;

    private static (string? Name, string? Note, DateOnly? Start) PeriodOf(SalesReportFilter filter, int key)
    {
        var from = filter.From;
        switch (filter.Step)
        {
            case SalesPeriodStep.Day:
            {
                var day = from.AddDays(key);
                return (day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), null, day);
            }
            case SalesPeriodStep.Week:
            {
                var monday = from.AddDays(-(((int)from.DayOfWeek + 6) % 7)).AddDays(7 * key);
                var end = monday.AddDays(6);
                return ($"{monday:dd.MM}–{end:dd.MM.yyyy}", null, monday);
            }
            default:
            {
                var month = new DateOnly(from.Year, from.Month, 1).AddMonths(key);
                return (month.ToString("MM.yyyy", CultureInfo.InvariantCulture), null, month);
            }
        }
    }

    // ---------- ABC ----------

    /// <summary>
    /// ABC-анализ (D1161): по убыванию выручки (или количества), класс A — пока накопленная доля до строки меньше aPercent, B — меньше
    /// aPercent + bPercent, остальное — C. Без права «Цены и суммы» — по количеству.
    /// </summary>
    public async Task<AbcDto> AbcAsync(SalesReportFilter filter, AbcSubject subject, AbcBasis basis = AbcBasis.Revenue, decimal aPercent = 80,
        decimal bPercent = 15, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        return await AbcCoreAsync(ctx, filter, own, subject, basis, aPercent, bPercent, ct);
    }

    private async Task<AbcDto> AbcCoreAsync(AccessContext ctx, SalesReportFilter filter, bool own, AbcSubject subject, AbcBasis basis, decimal aPercent,
        decimal bPercent, CancellationToken ct)
    {
        if (aPercent <= 0 || bPercent < 0 || aPercent + bPercent >= 100)
        {
            throw new BusinessRuleException("sales.abc.thresholds", "Границы ABC: доля A больше нуля, A + B меньше 100%.");
        }

        if (!ctx.Permissions.Has(Permissions.PriceView))
        {
            basis = AbcBasis.Quantity;
        }

        var keyed = Keyed(Facts(ctx, filter.From, filter.To, filter, own),
            filter with { By = subject == AbcSubject.Customer ? SalesDimension.Customer : SalesDimension.Item });
        var values = await keyed.GroupBy(k => k.Key!.Value)
            .Select(g => new { Id = g.Key, Value = basis == AbcBasis.Revenue ? g.Sum(x => x.Revenue) : g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var ids = values.Select(v => v.Id).ToList();
        Dictionary<long, (string Name, string? Code)> names = subject == AbcSubject.Customer
            ? await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => (c.Name, (string?)null), ct)
            : await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => (i.Name, (string?)i.Code), ct);
        var list = values.Select(v => (v.Id, Value: basis == AbcBasis.Revenue ? Money.Round(v.Value) : v.Value))
            .OrderByDescending(v => v.Value).ThenBy(v => names[v.Id].Name, StringComparer.CurrentCulture).ToList();
        var total = list.Where(v => v.Value > 0).Sum(v => v.Value);
        var rows = new List<AbcRowDto>(list.Count);
        decimal before = 0;
        foreach (var (id, value) in list)
        {
            var share = total == 0 || value <= 0 ? 0 : value / total * 100;
            var cls = value <= 0 ? 'C' : before < aPercent ? 'A' : before < aPercent + bPercent ? 'B' : 'C';
            before += share;
            rows.Add(new AbcRowDto(id, names[id].Name, names[id].Code, value, Math.Round(share, 2), Math.Round(Math.Min(before, 100), 2), cls));
        }

        return new AbcDto(subject, basis, aPercent, bPercent, rows, total);
    }

    // ---------- План-факт ----------

    /// <summary>
    /// План-факт за месяц (D1162): факт — выручка без НДС по заказам, где менеджер ответственный. allManagers — для ввода плана:
    /// все активные сотрудники организации, даже без плана и продаж.
    /// </summary>
    public async Task<PlanFactDto> PlanFactAsync(DateOnly month, bool allManagers = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        return await PlanFactCoreAsync(ctx, SalesPlan.MonthOf(month), own, allManagers, today, ct);
    }

    private async Task<PlanFactDto> PlanFactCoreAsync(AccessContext ctx, DateOnly month, bool own, bool allManagers, DateOnly today, CancellationToken ct)
    {
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var end = month.AddMonths(1).AddDays(-1);
        var facts = await Facts(ctx, month, end, new SalesReportFilter(month, end), own)
            .GroupBy(f => f.ManagerId).Select(g => new { g.Key, Revenue = g.Sum(x => x.Revenue) })
            .ToDictionaryAsync(x => x.Key ?? 0, x => Money.Round(x.Revenue), ct);
        var plans = await db.SalesPlans.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.Month == month)
            .Where(p => !own || p.ManagerUserId == ctx.UserId).ToDictionaryAsync(p => p.ManagerUserId, ct);
        var ids = facts.Keys.Where(k => k != 0).Concat(plans.Keys).ToHashSet();
        if (allManagers && !own)
        {
            ids.UnionWith(await ActiveMembersAsync(ctx.OrganizationId, ct));
        }
        else if (own)
        {
            ids.Add(ctx.UserId);
        }

        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var rows = ids.Select(id => new PlanFactRowDto(id, names.GetValueOrDefault(id),
                amounts ? plans.TryGetValue(id, out var p) ? p.Amount : 0 : null, amounts ? facts.GetValueOrDefault(id) : null,
                plans.TryGetValue(id, out var v) ? v.RowVersion : null))
            .OrderBy(r => r.Manager, StringComparer.CurrentCulture).ToList();
        if (facts.ContainsKey(0))
        {
            rows.Add(new PlanFactRowDto(null, null, amounts ? 0 : null, amounts ? facts[0] : null, null));
        }

        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var elapsed = today < month ? 0 : today > end ? 100 : Math.Round(today.Day * 100m / days, 1);
        var canEdit = amounts && ctx.Permissions.Has(Permissions.SalesPlanEdit);
        return new PlanFactDto(month, rows, amounts, own, canEdit, elapsed);
    }

    private async Task<List<long>> ActiveMembersAsync(long org, CancellationToken ct) =>
        await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == org && m.Status == MembershipStatus.Active)
            .Select(m => m.UserId).ToListAsync(ct);

    /// <summary>
    /// План менеджера на месяц (D1162): право «Продажи: план продаж» и «Цены и суммы»; менеджер — активный сотрудник организации.
    /// Сумма 0 — плана нет. Запись в журнал аудита.
    /// </summary>
    public async Task SetPlanAsync(DateOnly month, long managerUserId, decimal amount, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesPlanEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        if (!(await ActiveMembersAsync(ctx.OrganizationId, ct)).Contains(managerUserId))
        {
            throw new NotFoundException("Сотрудник");
        }

        var first = SalesPlan.MonthOf(month);
        var plan = await db.SalesPlans.SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.Month == first && p.ManagerUserId == managerUserId, ct);
        var before = plan is null ? null : plan.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            if (plan is null)
            {
                plan = SalesPlan.Create(ctx.OrganizationId, first, managerUserId);
                plan.Set(amount, ctx.UserId, clock.UtcNow);
                db.SalesPlans.Add(plan);
            }
            else
            {
                plan.EnsureVersion(plan.RowVersion, rowVersion ?? []);
                plan.Set(amount, ctx.UserId, clock.UtcNow);
            }
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        var manager = await db.Users.AsNoTracking().Where(u => u.Id == managerUserId).Select(u => u.DisplayName).SingleAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.SalesPlanChanged, nameof(SalesPlan),
            $"{first:yyyy-MM}:{managerUserId}", before, amount.ToString("0.00", CultureInfo.InvariantCulture), $"{manager}, {first:MM.yyyy}",
            currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    // ---------- Воронка ----------

    /// <summary>
    /// Воронка «заказ → отгрузка → оплата» (D1163) по заказам с датой в периоде: оформлено (не отменено), подтверждено, отгружено
    /// (хотя бы одна проведённая отгрузка; сумма — отгружено минус возвращено), оплачено полностью (разноски оплат не меньше суммы заказа).
    /// Суммы — с НДС, как в заказе; без права «Цены и суммы» — null.
    /// </summary>
    public async Task<SalesFunnelDto> FunnelAsync(SalesReportFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        return await FunnelCoreAsync(ctx, filter, own, ct);
    }

    private async Task<SalesFunnelDto> FunnelCoreAsync(AccessContext ctx, SalesReportFilter filter, bool own, CancellationToken ct)
    {
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var org = ctx.OrganizationId;
        var orders = db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.OrderDate >= filter.From && o.OrderDate <= filter.To);
        if (own)
        {
            orders = orders.Where(o => o.ResponsibleUserId == ctx.UserId);
        }

        if (filter.ManagerId is { } m)
        {
            orders = m == 0 ? orders.Where(o => o.ResponsibleUserId == null) : orders.Where(o => o.ResponsibleUserId == m);
        }

        if (filter.ChannelId is { } c)
        {
            orders = c == 0 ? orders.Where(o => o.ChannelId == null) : orders.Where(o => o.ChannelId == c);
        }

        if (filter.CustomerId is { } customer)
        {
            orders = orders.Where(o => o.CustomerId == customer);
        }

        var list = await orders.Select(o => new { o.Id, o.Status, Total = o.Lines.Sum(l => (decimal?)l.Amount) ?? 0 }).ToListAsync(ct);
        var moved = await (from d in db.StockDocuments.AsNoTracking()
                           join o in orders on d.SalesOrderId equals o.Id
                           where d.OrganizationId == org && d.Status == StockDocumentStatus.Posted
                                 && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn)
                           from l in d.Lines
                           from ol in o.Lines.Where(x => x.ItemId == l.ItemId)
                           select new
                           {
                               OrderId = o.Id,
                               Shipment = d.Kind == StockOperationKind.Shipment ? 1 : 0,
                               Value = (d.Kind == StockOperationKind.CustomerReturn ? -l.Quantity : l.Quantity) * ol.Amount / ol.Quantity,
                           })
            .GroupBy(x => x.OrderId).Select(g => new { g.Key, Shipments = g.Sum(x => x.Shipment), Value = g.Sum(x => x.Value) })
            .ToDictionaryAsync(x => x.Key, ct);
        var paid = await (from a in db.CustomerPaymentAllocations.AsNoTracking()
                          join p in db.CustomerPayments.AsNoTracking() on a.PaymentId equals p.Id
                          join o in orders on a.OrderId equals o.Id
                          where a.OrganizationId == org && a.RemovedAtUtc == null && p.Status == CustomerPaymentStatus.Posted
                          group a.Amount by a.OrderId into g
                          select new { g.Key, Amount = g.Sum() })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, ct);

        var created = list.Where(o => o.Status != SalesOrderStatus.Cancelled).ToList();
        var confirmed = created.Where(o => o.Status is SalesOrderStatus.Confirmed or SalesOrderStatus.Closed).ToList();
        var shipped = confirmed.Where(o => moved.TryGetValue(o.Id, out var x) && x.Shipments > 0).ToList();
        var fullyPaid = shipped.Where(o => o.Total > 0 && paid.GetValueOrDefault(o.Id) >= o.Total).ToList();
        decimal? Amount(decimal value) => amounts ? Money.Round(value) : null;
        decimal? Conversion(int count, int previous) => previous == 0 ? null : Math.Round(count * 100m / previous, 1);
        var stages = new List<FunnelStageDto>
        {
            new(FunnelStage.Created, created.Count, Amount(created.Sum(o => o.Total)), null),
            new(FunnelStage.Confirmed, confirmed.Count, Amount(confirmed.Sum(o => o.Total)), Conversion(confirmed.Count, created.Count)),
            new(FunnelStage.Shipped, shipped.Count, Amount(shipped.Sum(o => moved[o.Id].Value)), Conversion(shipped.Count, confirmed.Count)),
            new(FunnelStage.Paid, fullyPaid.Count, Amount(fullyPaid.Sum(o => paid[o.Id])), Conversion(fullyPaid.Count, shipped.Count)),
        };
        return new SalesFunnelDto(stages, list.Count - created.Count, amounts, own);
    }

    // ---------- Отборы ----------

    public async Task<SalesReportOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        var org = ctx.OrganizationId;
        var managerIds = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.ResponsibleUserId != null)
            .Select(o => o.ResponsibleUserId!.Value).Distinct().ToListAsync(ct);
        managerIds.AddRange(await db.SalesPlans.AsNoTracking().Where(p => p.OrganizationId == org).Select(p => p.ManagerUserId).Distinct().ToListAsync(ct));
        if (own)
        {
            managerIds = [ctx.UserId];
        }

        var managers = await db.Users.AsNoTracking().Where(u => managerIds.Contains(u.Id)).OrderBy(u => u.DisplayName)
            .Select(u => new LookupDto(u.Id, u.DisplayName)).ToListAsync(ct);
        var settings = await db.SalesChannelSettings.AsNoTracking().Where(s => s.OrganizationId == org).ToDictionaryAsync(s => s.ChannelId, ct);
        var channels = (await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == LookupKind.SalesChannel && !l.IsArchived)
                .OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
            .Select(l => new LookupDto(l.Id, settings.TryGetValue(l.Id, out var s) && ChannelNote(s) is { } n ? $"{l.Name} ({n})" : l.Name)).ToList();
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org && !g.IsArchived).OrderBy(g => g.Name)
            .Select(g => new LookupDto(g.Id, g.Name)).ToListAsync(ct);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        return new SalesReportOptionsDto(managers, channels, groups, amounts, amounts && ctx.Permissions.Has(Permissions.SalesPlanEdit), own);
    }

    // ---------- Excel ----------

    /// <summary>
    /// D1165: все отчёты страницы одной книгой — разрез, ABC покупателей и позиций, план-факт месяца даты «по», воронка.
    /// Числа — числовыми столбцами; без права «Цены и суммы» столбцы сумм пустые.
    /// </summary>
    public async Task<byte[]> ExportAsync(SalesReportFilter filter, ISpreadsheetFormat spreadsheet, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        var report = await BuildAsync(ctx, filter, own, ct);
        var abcCustomers = await AbcCoreAsync(ctx, filter, own, AbcSubject.Customer, AbcBasis.Revenue, 80, 15, ct);
        var abcItems = await AbcCoreAsync(ctx, filter, own, AbcSubject.Item, AbcBasis.Revenue, 80, 15, ct);
        var plan = await PlanFactCoreAsync(ctx, SalesPlan.MonthOf(filter.To), own, false, today, ct);
        var funnel = await FunnelCoreAsync(ctx, filter, own, ct);
        static string N(decimal? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : "";
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        var period = $"{filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";

        var dimension = DimensionName(filter.By);
        var rows = report.Rows.Select(r => (IReadOnlyList<string>)[r.Name ?? NoneName(filter.By), r.Note ?? "", N(r.Quantity), N(r.Revenue), N(r.Cost),
            N(r.Margin), N(r.MarginPercent), I(r.Orders), N(r.AverageCheck), I(r.Customers)]).ToList();
        var t = report.Total;
        rows.Add(["Итого", "", N(t.Quantity), N(t.Revenue), N(t.Cost), N(t.Margin), N(t.MarginPercent), I(t.Orders), N(t.AverageCheck), I(t.Customers)]);
        rows.Add(["Повторные покупатели", "", "", "", "", "", N(report.RepeatSharePercent), "", "", I(report.RepeatCustomers)]);

        IReadOnlyList<IReadOnlyList<string>> Abc(AbcDto a) => a.Rows.Select(r => (IReadOnlyList<string>)[r.Class.ToString(), r.Code ?? "", r.Name, N(r.Value),
            N(r.SharePercent), N(r.CumulativePercent)]).ToList();
        var abcHeader = new[] { "Класс", "Код", "Наименование", "Выручка без НДС", "Доля, %", "Накопленная доля, %" };
        var planRows = plan.Rows.Select(r => (IReadOnlyList<string>)[r.Manager ?? "Без ответственного", N(r.Plan), N(r.Fact), N(r.Percent), N(r.Deviation)]).ToList();
        planRows.Add(["Итого", N(plan.Plan), N(plan.Fact), N(plan.Percent), N(plan.Fact - plan.Plan)]);
        var funnelRows = funnel.Stages.Select(s => (IReadOnlyList<string>)[FunnelName(s.Stage), I(s.Orders), N(s.Amount), N(s.ConversionPercent)]).ToList();
        funnelRows.Add(["Отменено", I(funnel.Cancelled), "", ""]);
        return spreadsheet.Write(
        [
            new SheetData(dimension, [dimension, "Пояснение", "Количество", "Выручка без НДС", "Себестоимость", "Маржа", "Маржа, %", "Заказов", "Средний чек",
                "Покупателей", period], rows.Select(r => (IReadOnlyList<string>)[.. r, ""]).ToList(), new HashSet<int> { 2, 3, 4, 5, 6, 7, 8, 9 }),
            new SheetData("ABC покупателей", [.. abcHeader.Select(h => h == "Выручка без НДС" && abcCustomers.Basis == AbcBasis.Quantity ? "Количество" : h)],
                Abc(abcCustomers), new HashSet<int> { 3, 4, 5 }),
            new SheetData("ABC позиций", [.. abcHeader.Select(h => h == "Выручка без НДС" && abcItems.Basis == AbcBasis.Quantity ? "Количество" : h)],
                Abc(abcItems), new HashSet<int> { 3, 4, 5 }),
            new SheetData("План-факт", ["Менеджер", "План", "Факт", "Выполнение, %", "Отклонение", plan.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture)],
                planRows.Select(r => (IReadOnlyList<string>)[.. r, ""]).ToList(), new HashSet<int> { 1, 2, 3, 4 }),
            new SheetData("Воронка", ["Этап", "Заказов", "Сумма с НДС", "Конверсия, %", period], funnelRows.Select(r => (IReadOnlyList<string>)[.. r, ""]).ToList(),
                new HashSet<int> { 1, 2, 3 }),
        ]);
    }

    public static string DimensionName(SalesDimension d) => d switch
    {
        SalesDimension.Manager => "Менеджер",
        SalesDimension.Group => "Группа",
        SalesDimension.Channel => "Канал",
        SalesDimension.Period => "Период",
        SalesDimension.Item => "Позиция",
        _ => "Покупатель",
    };

    public static string NoneName(SalesDimension d) => d switch
    {
        SalesDimension.Manager => "Без ответственного",
        SalesDimension.Group => "Без группы",
        SalesDimension.Channel => "Без канала",
        _ => "—",
    };

    public static string FunnelName(FunnelStage s) => s switch
    {
        FunnelStage.Confirmed => "Подтверждено",
        FunnelStage.Shipped => "Отгружено",
        FunnelStage.Paid => "Оплачено полностью",
        _ => "Оформлено заказов",
    };

    // ---------- Главный экран ----------

    /// <summary>
    /// D1167: блок «Отгрузить сегодня» — подтверждённые заказы с датой отгрузки сегодня или раньше, по которым ещё есть что отгрузить.
    /// Своим коротким контекстом. Без права «Продажи: просмотр» или без таких заказов — null (блока нет); сумма — только с правом цен.
    /// </summary>
    public static async Task<ShipTodayDto?> ShipTodayAsync(IKnitErpDbContextFactory factory, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, user, clock).CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesView))
        {
            return null;
        }

        var org = ctx.OrganizationId;
        var today = await SalesCredit.TodayAsync(db, org, clock.UtcNow, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        var query = db.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == SalesOrderStatus.Confirmed && o.ShipDate != null && o.ShipDate <= today);
        if (own)
        {
            query = query.Where(o => o.ResponsibleUserId == ctx.UserId);
        }

        var orders = await query.OrderBy(o => o.ShipDate).ThenBy(o => o.Id).Take(MaxShipTodayOrders)
            .Select(o => new { o.Id, o.Number, o.CustomerId, ShipDate = o.ShipDate!.Value, Lines = o.Lines.Select(l => new { l.ItemId, l.Quantity, l.Amount }).ToList() })
            .ToListAsync(ct);
        if (orders.Count == 0)
        {
            return null;
        }

        var ids = orders.Select(o => o.Id).ToList();
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId != null && ids.Contains(d.SalesOrderId.Value) && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn))
            .SelectMany(d => d.Lines.Select(l => new
            {
                OrderId = d.SalesOrderId!.Value, l.ItemId, Quantity = d.Kind == StockOperationKind.CustomerReturn ? -l.Quantity : l.Quantity,
            }))
            .GroupBy(x => new { x.OrderId, x.ItemId }).Select(g => new { g.Key.OrderId, g.Key.ItemId, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => (x.OrderId, x.ItemId), x => x.Quantity, ct);
        var left = orders.Select(o =>
        {
            var lines = o.Lines.Select(l => (Left: Math.Max(0, l.Quantity - moved.GetValueOrDefault((o.Id, l.ItemId))), l.Quantity, l.Amount)).ToList();
            return new
            {
                o.Id, o.Number, o.CustomerId, o.ShipDate, Quantity = lines.Sum(l => l.Left), Amount = Money.Round(lines.Sum(l => l.Left * l.Amount / l.Quantity)),
            };
        }).Where(o => o.Quantity > 0).ToList();
        if (left.Count == 0)
        {
            return null;
        }

        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var top = left.Take(5).ToList();
        var customerIds = top.Select(o => o.CustomerId).Distinct().ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return new ShipTodayDto(today, left.Count(o => o.ShipDate == today), left.Count(o => o.ShipDate < today), amounts ? left.Sum(o => o.Amount) : null,
            top.Select(o => new ShipTodayRowDto(o.Id, o.Number, names[o.CustomerId], o.ShipDate, o.Quantity, amounts ? o.Amount : null)).ToList());
    }

    /// <summary>
    /// D1167: блок «Продажи за месяц к плану» — план и факт текущего месяца (с видимостью «Свои» — свои). Своим коротким контекстом;
    /// без прав «Продажи: отчёты» и «Цены и суммы», а также без плана и продаж — null.
    /// </summary>
    public static async Task<SalesPlanBlockDto?> PlanBlockAsync(IKnitErpDbContextFactory factory, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        await using var db = factory.Create();
        var guard = new AccessGuard(db, user, clock);
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesReportView) || !ctx.Permissions.Has(Permissions.PriceView))
        {
            return null;
        }

        var service = new SalesReportService(db, guard, user, clock);
        var today = await SalesCredit.TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct);
        var own = await SalesReceivables.OwnOnlyAsync(db, ctx, clock.UtcNow, ct);
        var data = await service.PlanFactCoreAsync(ctx, SalesPlan.MonthOf(today), own, false, today, ct);
        var plan = data.Plan ?? 0;
        var fact = data.Fact ?? 0;
        return plan == 0 && fact == 0 ? null : new SalesPlanBlockDto(data.Month, plan, fact, data.ElapsedPercent, own);
    }

    private async Task<AccessContext> DemandAsync(SalesReportFilter filter, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        if (filter.To < filter.From || filter.To.DayNumber - filter.From.DayNumber > MaxPeriodDays)
        {
            throw new BusinessRuleException("report.period", $"Период — от даты «с» до даты «по», не длиннее {MaxPeriodDays} дней.");
        }

        if (filter.By == SalesDimension.Period && filter.Step == SalesPeriodStep.Day && filter.To.DayNumber - filter.From.DayNumber >= MaxDayRows)
        {
            throw new BusinessRuleException("sales.report.days", $"По дням — период не длиннее {MaxDayRows} дней; для большего выберите недели или месяцы.");
        }

        return ctx;
    }
}
