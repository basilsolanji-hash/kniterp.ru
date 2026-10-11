using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Период отчёта: брак — по дате отметки, отклонения и себестоимость — по дате заказа. Заказы в работе — на сегодня.</summary>
public sealed record ProductionReportFilter(DateOnly From, DateOnly To);

/// <summary>Заказ в работе: сделано по этапам (в порядке маршрута), годное, брак, выпущено, готово к выпуску, просрочка.</summary>
public sealed record OrderInWorkRowDto(
    long Id, string Number, DateOnly OrderDate, DateOnly? DueDate, string ItemCode, string ItemName, string UnitSymbol, decimal Quantity,
    IReadOnlyList<decimal?> StageDone, decimal Defect, decimal Released, decimal ReadyForOutput, ProductionOrderStatus Status, bool Overdue)
{
    public string StatusName => ProductionOrder.StatusName(Status);
}

/// <summary>Брак за период по причине и изделию.</summary>
public sealed record DefectRowDto(string Reason, string ItemCode, string ItemName, string UnitSymbol, decimal Quantity, int Orders);

/// <summary>Отклонение расхода материала заказа от нормы на сделанное (D94).</summary>
public sealed record DeviationRowDto(
    long OrderId, string OrderNumber, string MaterialCode, string MaterialName, string UnitSymbol, decimal Normative, decimal Actual, decimal DeviationPercent,
    bool OverTolerance);

/// <summary>Себестоимость единицы: плановая по снимку норм и фактическая по выданной пряже (D95).</summary>
public sealed record CostRowDto(long OrderId, string OrderNumber, string ItemCode, string ItemName, decimal Released, decimal? PlannedUnitCost, decimal? ActualUnitCost)
{
    /// <summary>Отклонение факта от плана, %.</summary>
    public decimal? DiffPercent => PlannedUnitCost is { } p && p > 0 && ActualUnitCost is { } a ? decimal.Round((a - p) * 100m / p, 2, MidpointRounding.AwayFromZero) : null;
}

/// <summary>Отчёт по производству; Costs — null без права «Цены и суммы».</summary>
public sealed record ProductionReportDto(
    ProductionReportFilter Filter, IReadOnlyList<OrderInWorkRowDto> InWork, IReadOnlyList<DefectRowDto> Defects, IReadOnlyList<DeviationRowDto> Deviations,
    IReadOnlyList<CostRowDto>? Costs, decimal TolerancePercent);

/// <summary>
/// Отчёт по производству (D98): заказы в работе, брак по причинам, отклонения расхода от нормы, плановая и фактическая себестоимость;
/// выгрузка в Excel теми же фильтрами. Право — «Производство: просмотр заказов», себестоимость — ещё «Цены и суммы».
/// Данные — несколькими пакетными запросами, не по запросу на заказ.
/// </summary>
public sealed class ProductionReportService(IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, IClock clock)
{
    /// <summary>Не больше стольких дней в периоде — отчёт строится в памяти.</summary>
    public const int MaxPeriodDays = 366;

    public async Task<ProductionReportDto> BuildAsync(ProductionReportFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        if (filter.To < filter.From || filter.To.DayNumber - filter.From.DayNumber > MaxPeriodDays)
        {
            throw new BusinessRuleException("production.report.period", $"Период — от одного дня до {MaxPeriodDays} дней, начало не позже конца.");
        }

        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(org.TimeZoneId)));
        var inWorkStatuses = new[] { ProductionOrderStatus.Released, ProductionOrderStatus.PartiallyDone, ProductionOrderStatus.Done };
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials)
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.Status != ProductionOrderStatus.Draft && o.Status != ProductionOrderStatus.Cancelled
                        && (inWorkStatuses.Contains(o.Status) || (o.OrderDate >= filter.From && o.OrderDate <= filter.To)))
            .OrderBy(o => o.OrderDate).ThenBy(o => o.Id).Take(5000).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var ops = await ProductionSteps.SummedAsync(db, ctx.OrganizationId, ids, ct);
        var moves = await (from link in db.ProductionOrderDocuments.AsNoTracking()
                           where link.OrganizationId == ctx.OrganizationId && ids.Contains(link.ProductionOrderId)
                           join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
                           where d.Status == StockDocumentStatus.Posted
                           from l in d.Lines
                           select new { link.ProductionOrderId, link.Purpose, l.ItemId, l.Quantity }).ToListAsync(ct);
        var itemIds = orders.Select(o => o.ItemId).Concat(orders.SelectMany(o => o.Materials.Select(m => m.ItemId))).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol })
            .ToDictionaryAsync(x => x.Id, ct);

        decimal Released(ProductionOrder o) =>
            moves.Where(m => m.ProductionOrderId == o.Id && m.Purpose == ProductionDocumentPurpose.Output && m.ItemId == o.ItemId).Sum(m => m.Quantity);
        ProductionFlow Flow(ProductionOrder o) =>
            new(o.Quantity, ops[o.Id], Released(o), o.RouteValue);

        var inWork = orders.Where(o => inWorkStatuses.Contains(o.Status)).Select(o =>
        {
            var flow = Flow(o);
            var item = items[o.ItemId];
            return new OrderInWorkRowDto(o.Id, o.Number, o.OrderDate, o.DueDate, item.Code, item.Name, item.Symbol, o.Quantity,
                ProductionFlow.Stages.Select(s => flow.Route.Contains(s) ? flow.Done(s) : (decimal?)null).ToList(), flow.Defect, flow.Released, flow.ReadyForOutput, o.Status,
                o.DueDate is { } due && due < today && o.Status != ProductionOrderStatus.Done);
        }).OrderByDescending(r => r.Overdue).ThenBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Number).ToList();

        // Брак — по всем заказам организации за период отметки (заказ мог быть открыт раньше периода).
        var defectOps = await (from op in db.ProductionOperations.AsNoTracking()
                               where op.OrganizationId == ctx.OrganizationId && op.Kind == ProductionOperationKind.Defect
                                     && op.OccurredOn >= filter.From && op.OccurredOn <= filter.To
                               join o in db.ProductionOrders.AsNoTracking() on op.ProductionOrderId equals o.Id
                               join i in db.Items.AsNoTracking() on o.ItemId equals i.Id
                               join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                               select new { op.Reason, op.Quantity, op.ProductionOrderId, i.Code, i.Name, u.Symbol }).ToListAsync(ct);
        var defects = defectOps
            .GroupBy(d => (Reason: (d.Reason ?? "—").Trim(), d.Code, d.Name, d.Symbol), new ReasonComparer())
            .Select(g => new DefectRowDto(g.Key.Reason, g.Key.Code, g.Key.Name, g.Key.Symbol, g.Sum(x => x.Quantity), g.Select(x => x.ProductionOrderId).Distinct().Count()))
            .OrderByDescending(d => d.Quantity).ThenBy(d => d.Reason).ToList();

        var period = orders.Where(o => o.OrderDate >= filter.From && o.OrderDate <= filter.To).ToList();
        var deviations = new List<DeviationRowDto>();
        var tolerance = (await ProductionSettingsService.LoadAsync(db, ctx.OrganizationId, ct)).TolerancePercent;
        foreach (var o in period)
        {
            var flow = Flow(o);
            var balance = MaterialBalance.Calculate(o.Materials, o.Quantity,
                moves.Where(m => m.ProductionOrderId == o.Id && m.Purpose != ProductionDocumentPurpose.Output)
                    .Select(m => new MaterialMovement(m.ItemId, m.Purpose, m.Quantity)), flow.Released, flow.Defect);
            deviations.AddRange(balance.Where(b => b.DeviationPercent is not null).Select(b => new DeviationRowDto(o.Id, o.Number,
                items[b.ItemId].Code, items[b.ItemId].Name, items[b.ItemId].Symbol, decimal.Round(b.Normative!.Value, 6), b.Consumed + b.Waste,
                b.DeviationPercent!.Value, b.OverTolerance(tolerance))));
        }

        List<CostRowDto>? costs = null;
        if (ctx.Permissions.Has(Permissions.PriceView))
        {
            var prices = await CurrentAsync(db, ctx.OrganizationId, period.SelectMany(o => o.Materials.Select(m => m.ItemId)).Distinct().ToList(), ct);
            costs = period.Select(o =>
            {
                var released = Released(o);
                decimal? actual = 0m;
                foreach (var m in o.Materials)
                {
                    var net = moves.Where(x => x.ProductionOrderId == o.Id && x.ItemId == m.ItemId && x.Purpose == ProductionDocumentPurpose.Issue).Sum(x => x.Quantity)
                              - moves.Where(x => x.ProductionOrderId == o.Id && x.ItemId == m.ItemId && x.Purpose == ProductionDocumentPurpose.Return).Sum(x => x.Quantity);
                    var price = prices.TryGetValue(m.ItemId, out var p) ? p : m.PlannedPrice;
                    actual = price is null || actual is null ? null : actual + net * price.Value;
                }

                var planned = o.PlannedCost() is { } c ? decimal.Round(c / o.Quantity, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
                return new CostRowDto(o.Id, o.Number, items[o.ItemId].Code, items[o.ItemId].Name, released, planned,
                    released > 0 && actual is { } a ? decimal.Round(a / released, 2, MidpointRounding.AwayFromZero) : null);
            }).ToList();
        }

        return new ProductionReportDto(filter, inWork, defects, deviations.OrderByDescending(d => Math.Abs(d.DeviationPercent)).ToList(), costs,
            tolerance);
    }

    /// <summary>Тот же отчёт в Excel: лист на каждый раздел, количества и деньги — числами.</summary>
    public async Task<byte[]> ExportAsync(ProductionReportFilter filter, CancellationToken ct = default)
    {
        var r = await BuildAsync(filter, ct);
        var stages = ProductionFlow.Stages.Select(ProductionFlow.StageName).ToList();
        var sheets = new List<SheetData>
        {
            new("Заказы в работе", ["Номер", "Дата", "Срок", "Код", "Изделие", "Количество", .. stages, "Брак", "Выпущено", "Готово к выпуску", "Статус", "Просрочен"],
                r.InWork.Select(o => (IReadOnlyList<string>)[o.Number, o.OrderDate.ToString("dd.MM.yyyy"), o.DueDate?.ToString("dd.MM.yyyy") ?? "", o.ItemCode, o.ItemName,
                    N(o.Quantity), .. o.StageDone.Select(d => d is { } v ? N(v) : ""), N(o.Defect), N(o.Released), N(o.ReadyForOutput), o.StatusName, o.Overdue ? "да" : ""]).ToList(),
                Numbers(5, 6 + stages.Count + 3)),
            new("Брак по причинам", ["Причина", "Код", "Изделие", "Количество", "Ед.", "Заказов"],
                r.Defects.Select(d => (IReadOnlyList<string>)[d.Reason, d.ItemCode, d.ItemName, N(d.Quantity), d.UnitSymbol, d.Orders.ToString(CultureInfo.InvariantCulture)]).ToList(),
                new HashSet<int> { 3, 5 }),
            new("Отклонения расхода", ["Заказ", "Код", "Материал", "Норма на сделанное", "Расход с отходами", "Ед.", "Отклонение, %", "Больше допуска"],
                r.Deviations.Select(d => (IReadOnlyList<string>)[d.OrderNumber, d.MaterialCode, d.MaterialName, N(d.Normative), N(d.Actual), d.UnitSymbol,
                    N(d.DeviationPercent), d.OverTolerance ? "да" : ""]).ToList(),
                new HashSet<int> { 3, 4, 6 }),
        };
        if (r.Costs is { } costs)
        {
            sheets.Add(new("Себестоимость", ["Заказ", "Код", "Изделие", "Выпущено", "Плановая на единицу", "Фактическая на единицу", "Отклонение, %"],
                costs.Select(c => (IReadOnlyList<string>)[c.OrderNumber, c.ItemCode, c.ItemName, N(c.Released), c.PlannedUnitCost is { } p ? N(p) : "",
                    c.ActualUnitCost is { } a ? N(a) : "", c.DiffPercent is { } d ? N(d) : ""]).ToList(),
                new HashSet<int> { 3, 4, 5, 6 }));
        }

        return spreadsheet.Write(sheets);
    }

    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static HashSet<int> Numbers(int from, int toExclusive) => Enumerable.Range(from, toExclusive - from).ToHashSet();

    /// <summary>Причины брака сравниваются без учёта регистра: «дыры» и «Дыры» — одна строка.</summary>
    private sealed class ReasonComparer : IEqualityComparer<(string Reason, string Code, string Name, string Symbol)>
    {
        public bool Equals((string Reason, string Code, string Name, string Symbol) x, (string Reason, string Code, string Name, string Symbol) y) =>
            string.Equals(x.Reason, y.Reason, StringComparison.OrdinalIgnoreCase) && x.Code == y.Code;

        public int GetHashCode((string Reason, string Code, string Name, string Symbol) obj) =>
            HashCode.Combine(obj.Reason.ToUpperInvariant(), obj.Code);
    }

    private static Task<IReadOnlyDictionary<long, decimal>> CurrentAsync(IKnitErpDbContext db, long organizationId, IReadOnlyList<long> itemIds, CancellationToken ct) =>
        ProductionPrices.CurrentAsync(db, organizationId, itemIds, ct);
}

/// <summary>Текущая цена материалов для плана и факта производства.</summary>
internal static class ProductionPrices
{
    /// <summary>
    /// Плановая цена единицы материала без НДС — как себестоимость в «Прибыльности» (D69): средняя цена приёмок по заказам
    /// поставщикам за вычетом возвратов, для полуфабриката — по его действующей техкарте.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, decimal>> CurrentAsync(IKnitErpDbContext db, long organizationId, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        var cards = await db.TechCards.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.OrganizationId == organizationId && c.Status == TechCardStatus.Active).ToDictionaryAsync(c => c.ItemId, ct);
        var received = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.PurchaseOrderId != null && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Receipt || d.Kind == StockOperationKind.ReturnToSupplier))
            .SelectMany(d => d.Lines.Select(l => new { OrderId = d.PurchaseOrderId!.Value, d.Kind, l.ItemId, l.Quantity }))
            .ToListAsync(ct);
        var orderIds = received.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var purchases = received.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g =>
        {
            decimal quantity = 0, value = 0;
            foreach (var r in g)
            {
                var line = orders[r.OrderId].Lines.Single(l => l.ItemId == r.ItemId);
                var sign = r.Kind == StockOperationKind.ReturnToSupplier ? -1 : 1;
                quantity += sign * r.Quantity;
                value += sign * r.Quantity * (line.Amount - line.VatAmount) / line.Quantity;
            }

            return (quantity, value);
        });
        var costs = UnitCosts.Calculate(purchases, cards, itemIds).ToDictionary(x => x.Key, x => x.Value);

        // Полуфабрикат собственного производства (D90, дополнение): если его уже выпускали — фактическая себестоимость выпуска
        // (выдано − возвращено по его заказам × цена материалов) / выпущено; закупка по-прежнему важнее, оценка по техкарте — запасной путь.
        var produced = itemIds.Where(id => !costs.TryGetValue(id, out var c) || c.Source != CostSource.Purchases).ToList();
        if (produced.Count > 0)
        {
            var moves = await (from o in db.ProductionOrders.AsNoTracking()
                               where o.OrganizationId == organizationId && produced.Contains(o.ItemId)
                               join link in db.ProductionOrderDocuments.AsNoTracking() on o.Id equals link.ProductionOrderId
                               join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
                               where d.Status == StockDocumentStatus.Posted
                               from l in d.Lines
                               select new { o.ItemId, link.Purpose, Line = l.ItemId, l.Quantity }).ToListAsync(ct);
            var materialIds = moves.Where(m => m.Purpose != ProductionDocumentPurpose.Output).Select(m => m.Line).Distinct().ToList();
            var materialCosts = materialIds.Count == 0 ? new Dictionary<long, (decimal Cost, CostSource Source)>()
                : UnitCosts.Calculate(purchases, cards, materialIds).ToDictionary(x => x.Key, x => x.Value);
            foreach (var group in moves.GroupBy(m => m.ItemId))
            {
                var released = group.Where(m => m.Purpose == ProductionDocumentPurpose.Output && m.Line == group.Key).Sum(m => m.Quantity);
                var net = group.Where(m => m.Purpose is ProductionDocumentPurpose.Issue or ProductionDocumentPurpose.Return)
                    .GroupBy(m => m.Line)
                    .Select(g => (Item: g.Key, Qty: g.Sum(m => m.Purpose == ProductionDocumentPurpose.Issue ? m.Quantity : -m.Quantity))).ToList();
                if (released > 0 && net.All(n => materialCosts.ContainsKey(n.Item)))
                {
                    costs[group.Key] = (net.Sum(n => n.Qty * materialCosts[n.Item].Cost) / released, CostSource.TechCard);
                }
            }
        }

        return costs.ToDictionary(x => x.Key, x => x.Value.Cost);
    }
}
