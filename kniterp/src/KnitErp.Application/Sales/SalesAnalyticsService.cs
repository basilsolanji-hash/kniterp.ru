using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Период — по дате документа; Customer и Search — подстрока наименования (или ИНН) покупателя и кода/наименования позиции.</summary>
public sealed record SalesAnalyticsFilter(DateOnly From, DateOnly To, string? Customer = null, string? Search = null);

/// <summary>Реализация позиции за период: отгружено минус возвращено, выручка без НДС и НДС — по ценам заказов.</summary>
public sealed record SalesByItemRowDto(
    long ItemId, string Code, string Name, string UnitSymbol, decimal Quantity, decimal Revenue, decimal Vat, int Customers, int Documents)
{
    public decimal RevenueWithVat => Revenue + Vat;
    public decimal? AveragePrice => Quantity == 0 ? null : Money.Round(Revenue / Quantity);
}

public enum ProfitGrouping : byte
{
    Item = 1,
    Customer = 2,
}

/// <summary>
/// Строка прибыльности. Cost — себестоимость проданного по известным ценам; CostComplete = false — у части позиций цена
/// неизвестна, прибыль не считается. Source — только для строки позиции.
/// </summary>
public sealed record ProfitRowDto(
    long Id, string? Code, string Name, string? UnitSymbol, decimal? Quantity, decimal Revenue, decimal Cost, bool CostComplete, CostSource Source)
{
    public decimal? Profit => CostComplete ? Revenue - Cost : null;
    public decimal? MarginPercent => Profit is { } p && Revenue != 0 ? Math.Round(p / Revenue * 100, 1) : null;
}

public sealed record ProfitabilityDto(IReadOnlyList<ProfitRowDto> Rows, decimal Revenue, decimal CostedRevenue, decimal Cost)
{
    /// <summary>Прибыль по строкам, где себестоимость известна полностью.</summary>
    public decimal Profit => CostedRevenue - Cost;
    public decimal? MarginPercent => CostedRevenue == 0 ? null : Math.Round(Profit / CostedRevenue * 100, 1);
    public decimal UncostedRevenue => Revenue - CostedRevenue;
}

/// <summary>
/// «Товары и реализации» и «Прибыльность» (D69). Источник — проведённые отгрузки и возвраты по заказам покупателей
/// (отгрузки без заказа не имеют цены и не входят). Выручка — без НДС по ценам заказа. Себестоимость — <see cref="UnitCosts"/>
/// на конец периода. Права: «Продажи: просмотр» и «Цены и суммы».
/// </summary>
public sealed class SalesAnalyticsService(IKnitErpDbContext db, IAccessGuard guard)
{
    public const int MaxPeriodDays = 731;

    public async Task<IReadOnlyList<SalesByItemRowDto>> SalesByItemAsync(SalesAnalyticsFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var lines = await SoldAsync(ctx, filter, ct);
        var items = await ItemsAsync(lines.Select(l => l.ItemId), ct);
        return lines.GroupBy(l => l.ItemId)
            .Select(g => new SalesByItemRowDto(g.Key, items[g.Key].Code, items[g.Key].Name, items[g.Key].Symbol, g.Sum(l => l.Quantity),
                g.Sum(l => l.Revenue), g.Sum(l => l.Vat), g.Select(l => l.CustomerId).Distinct().Count(), g.Select(l => l.DocumentId).Distinct().Count()))
            .Where(r => r.Quantity != 0 || r.Revenue != 0)
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Code)
            .ToList();
    }

    public async Task<ProfitabilityDto> ProfitabilityAsync(SalesAnalyticsFilter filter, ProfitGrouping grouping, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(filter, ct);
        var lines = await SoldAsync(ctx, filter, ct);
        var costs = await CostsAsync(ctx, filter.To, lines.Select(l => l.ItemId).Distinct().ToList(), ct);
        decimal? CostOf(SoldLine l) => costs.TryGetValue(l.ItemId, out var c) ? Money.Round(c.Cost * l.Quantity) : null;

        List<ProfitRowDto> rows;
        if (grouping == ProfitGrouping.Item)
        {
            var items = await ItemsAsync(lines.Select(l => l.ItemId), ct);
            rows = lines.GroupBy(l => l.ItemId).Select(g =>
            {
                var known = costs.TryGetValue(g.Key, out var c);
                return new ProfitRowDto(g.Key, items[g.Key].Code, items[g.Key].Name, items[g.Key].Symbol, g.Sum(l => l.Quantity), g.Sum(l => l.Revenue),
                    known ? g.Sum(l => CostOf(l)!.Value) : 0, known, known ? c.Source : CostSource.None);
            }).ToList();
        }
        else
        {
            var ids = lines.Select(l => l.CustomerId).Distinct().ToList();
            var names = await db.Counterparties.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            rows = lines.GroupBy(l => l.CustomerId).Select(g => new ProfitRowDto(g.Key, null, names[g.Key], null, null, g.Sum(l => l.Revenue),
                    g.Sum(l => CostOf(l) ?? 0), g.All(l => costs.ContainsKey(l.ItemId)), CostSource.None))
                .ToList();
        }

        rows = rows.Where(r => r.Revenue != 0 || r.Quantity is not (null or 0))
            .OrderByDescending(r => r.Profit ?? decimal.MinValue).ThenByDescending(r => r.Revenue).ToList();
        var complete = rows.Where(r => r.CostComplete).ToList();
        return new ProfitabilityDto(rows, rows.Sum(r => r.Revenue), complete.Sum(r => r.Revenue), complete.Sum(r => r.Cost));
    }

    private async Task<AccessContext> DemandAsync(SalesAnalyticsFilter filter, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        if (filter.To < filter.From || filter.To.DayNumber - filter.From.DayNumber > MaxPeriodDays)
        {
            throw new BusinessRuleException("report.period", $"Период — от даты «с» до даты «по», не длиннее {MaxPeriodDays} дней.");
        }

        return ctx;
    }

    private sealed record SoldLine(long DocumentId, long CustomerId, long ItemId, decimal Quantity, decimal Revenue, decimal Vat);

    /// <summary>Строки отгрузок (+) и возвратов (−) по заказам за период с выручкой и НДС по ценам заказа.</summary>
    private async Task<List<SoldLine>> SoldAsync(AccessContext ctx, SalesAnalyticsFilter filter, CancellationToken ct)
    {
        var docs = from d in db.StockDocuments.AsNoTracking()
                   where d.OrganizationId == ctx.OrganizationId && d.SalesOrderId != null && d.Status == StockDocumentStatus.Posted
                         && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn)
                         && d.DocumentDate >= filter.From && d.DocumentDate <= filter.To
                   join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                   select new { d, c.Name, c.Inn };
        if (!string.IsNullOrWhiteSpace(filter.Customer))
        {
            var text = filter.Customer.Trim();
            docs = docs.Where(x => x.Name.Contains(text) || x.Inn == text);
        }

        var raw = from x in docs
                  from l in x.d.Lines
                  join i in db.Items.AsNoTracking() on l.ItemId equals i.Id
                  select new { x.d.Id, CustomerId = x.d.CounterpartyId!.Value, OrderId = x.d.SalesOrderId!.Value, x.d.Kind, l.ItemId, l.Quantity, i.Code, i.Name };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            raw = raw.Where(r => r.Code.Contains(text) || r.Name.Contains(text));
        }

        var list = await raw.ToListAsync(ct);
        var orderIds = list.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return list.Select(r =>
        {
            var line = orders[r.OrderId].Lines.Single(l => l.ItemId == r.ItemId);
            var sign = r.Kind == StockOperationKind.CustomerReturn ? -1 : 1;
            var amount = Money.Round(r.Quantity * line.Amount / line.Quantity);
            var vat = Money.Round(r.Quantity * line.VatAmount / line.Quantity);
            return new SoldLine(r.Id, r.CustomerId, r.ItemId, sign * r.Quantity, sign * (amount - vat), sign * vat);
        }).ToList();
    }

    /// <summary>
    /// Себестоимость единицы на дату: закупки по заказам до даты включительно и действующие техкарты.
    /// internal — D302 (сессия 4): та же оценка для себестоимости комплекта по компонентам (BundleService).
    /// </summary>
    internal async Task<IReadOnlyDictionary<long, (decimal Cost, CostSource Source)>> CostsAsync(
        AccessContext ctx, DateOnly asOf, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        var cards = await db.TechCards.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status == TechCardStatus.Active).ToDictionaryAsync(c => c.ItemId, ct);
        var received = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.PurchaseOrderId != null && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Receipt || d.Kind == StockOperationKind.ReturnToSupplier) && d.DocumentDate <= asOf)
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
        return UnitCosts.Calculate(purchases, cards, itemIds);
    }

    private sealed record ItemInfo(string Code, string Name, string Symbol);

    private async Task<Dictionary<long, ItemInfo>> ItemsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => list.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, Info = new ItemInfo(i.Code, i.Name, u.Symbol) })
            .ToDictionaryAsync(x => x.Id, x => x.Info, ct);
    }
}
