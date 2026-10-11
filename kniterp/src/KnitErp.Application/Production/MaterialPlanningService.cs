using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>
/// Потребность в материале по открытым производственным заказам: нужно ещё выдать, свободно на складах, ожидается по подтверждённым
/// заказам поставщикам, нехватка; поставщик и цена последней закупки (цена — только с правом «Цены и суммы»).
/// </summary>
public sealed record MaterialNeedDto(
    long ItemId, string Code, string Name, string UnitSymbol, decimal Need, decimal Free, decimal Incoming, int Orders, bool Producible,
    long? SupplierId, string? SupplierName, decimal? LastPrice)
{
    /// <summary>Не хватает с учётом ожидаемых поставок.</summary>
    public decimal Shortage => Math.Max(0, Need - Free - Incoming);
}

public sealed record MaterialPlanDto(IReadOnlyList<MaterialNeedDto> Lines, bool PricesVisible, bool CanOrder, IReadOnlyList<LookupWarehouseDto> Warehouses);

/// <summary>Созданные черновики заказов поставщикам и позиции без известного поставщика.</summary>
public sealed record MaterialOrderResultDto(IReadOnlyList<long> Orders, IReadOnlyList<string> Numbers, IReadOnlyList<string> WithoutSupplier);

/// <summary>
/// Планирование потребности в материалах (D94, дополнение «планирование»): по всем открытым производственным заказам — черновикам
/// (по действующей техкарте) и запущенным (по снимку норм за вычетом выданного) — сколько ещё нужно выдать; свободный остаток —
/// на складах, кроме складов цеха открытых заказов; ожидается — подтверждённые и черновые заказы поставщикам за вычетом принятого.
/// Нехватку закупаемых материалов можно одной кнопкой превратить в черновики заказов поставщикам (по поставщику последней закупки);
/// полуфабрикаты с техкартой — не закупаются, а производятся (D90, дополнение). Смотреть — «Производство: просмотр заказов»,
/// создавать заказы поставщикам — «Закупки: заказы поставщикам и оплаты».
/// </summary>
public sealed class MaterialPlanningService(IKnitErpDbContext db, IAccessGuard guard, IClock clock, PurchaseService purchases)
{
    public async Task<MaterialPlanDto> PlanAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var lines = await LinesAsync(ctx, ct);
        var warehouses = ctx.Permissions.Has(Permissions.PurchaseEdit)
            ? await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived)
                .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct)
            : [];
        return new MaterialPlanDto(lines, ctx.Permissions.Has(Permissions.PriceView), ctx.Permissions.Has(Permissions.PurchaseEdit), warehouses);
    }

    /// <summary>
    /// Черновики заказов поставщикам на нехватку закупаемых материалов: по поставщику последней закупки, цена и НДС — как в ней,
    /// склад приёмки — выбранный. Под блокировкой планирования организации: двойное нажатие не создаст заказы дважды.
    /// </summary>
    public async Task<MaterialOrderResultDto> CreatePurchaseOrdersAsync(long warehouseId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseEdit, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.production.planning.{ctx.OrganizationId}", ct);
            var lines = (await LinesAsync(ctx, ct)).Where(l => l.Shortage > 0 && !l.Producible).ToList();
            if (lines.Count == 0)
            {
                throw new BusinessRuleException("production.planning.no_shortage", "Нехватки закупаемых материалов нет — заказывать нечего.");
            }

            var last = await LastPurchasesAsync(ctx, lines.Select(l => l.ItemId).ToList(), ct);
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(org.TimeZoneId)));
            var created = new List<long>();
            foreach (var group in lines.Where(l => last.ContainsKey(l.ItemId)).GroupBy(l => last[l.ItemId].SupplierId))
            {
                var first = last[group.First().ItemId];
                var order = await purchases.CreateOrderAsync(new PurchaseOrderHeader(today, group.Key, warehouseId, null, null, first.PricesIncludeVat,
                    "Нехватка материалов для производства"), ct);
                foreach (var line in group)
                {
                    var l = last[line.ItemId];
                    var version = await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == order).Select(o => o.RowVersion).SingleAsync(ct);
                    await purchases.SetOrderLineAsync(order, line.ItemId, line.Shortage, l.Price, l.VatPercent, version, ct);
                }

                created.Add(order);
            }

            await tx.CommitAsync(ct);
            var numbers = await db.PurchaseOrders.AsNoTracking().Where(o => created.Contains(o.Id)).OrderBy(o => o.Id).Select(o => o.Number).ToListAsync(ct);
            return new MaterialOrderResultDto(created, numbers, lines.Where(l => !last.ContainsKey(l.ItemId)).Select(l => l.Code).ToList());
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<IReadOnlyList<MaterialNeedDto>> LinesAsync(AccessContext ctx, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var open = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials)
            .Where(o => o.OrganizationId == org && (o.Status == ProductionOrderStatus.Draft || o.Status == ProductionOrderStatus.Released
                                                    || o.Status == ProductionOrderStatus.PartiallyDone))
            .ToListAsync(ct);
        var ids = open.Select(o => o.Id).ToList();
        var issued = (await (from link in db.ProductionOrderDocuments.AsNoTracking()
                             where link.OrganizationId == org && ids.Contains(link.ProductionOrderId)
                                   && (link.Purpose == ProductionDocumentPurpose.Issue || link.Purpose == ProductionDocumentPurpose.Return)
                             join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
                             where d.Status == StockDocumentStatus.Posted
                             from l in d.Lines
                             select new { link.ProductionOrderId, link.Purpose, l.ItemId, l.Quantity }).ToListAsync(ct))
            .GroupBy(x => (x.ProductionOrderId, x.ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Purpose == ProductionDocumentPurpose.Issue ? x.Quantity : -x.Quantity));

        // Черновики — по действующей техкарте (версия ещё не зафиксирована).
        var cards = await db.TechCards.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.OrganizationId == org && c.Status == TechCardStatus.Active).ToListAsync(ct);
        var cardById = cards.ToDictionary(c => c.Id);
        var precision = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, u.Precision })
            .ToDictionaryAsync(x => x.Id, x => (int)x.Precision, ct);
        var need = new Dictionary<long, (decimal Qty, HashSet<long> Orders)>();
        void Add(long itemId, long orderId, decimal qty)
        {
            if (qty <= 0)
            {
                return;
            }

            var current = need.GetValueOrDefault(itemId, (0m, []));
            current.Orders.Add(orderId);
            need[itemId] = (current.Qty + qty, current.Orders);
        }

        foreach (var o in open)
        {
            if (o.Status == ProductionOrderStatus.Draft)
            {
                var card = cardById.GetValueOrDefault(o.TechCardId) ?? cards.FirstOrDefault(c => c.ItemId == o.ItemId);
                if (card is null)
                {
                    continue;
                }

                foreach (var (itemId, _, gross) in card.Requirement(o.Quantity, id => precision.GetValueOrDefault(id, TechCard.NormPrecision)))
                {
                    Add(itemId, o.Id, gross);
                }
            }
            else
            {
                foreach (var m in o.Materials)
                {
                    Add(m.ItemId, o.Id, m.PlannedGross - issued.GetValueOrDefault((o.Id, m.ItemId)));
                }
            }
        }

        var itemIds = need.Keys.ToList();
        if (itemIds.Count == 0)
        {
            return [];
        }

        // Свободно — на складах, кроме складов цеха открытых заказов (там пряжа уже выдана под заказы).
        var wip = open.Select(o => o.WipWarehouseId).Distinct().ToList();
        var free = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == org && itemIds.Contains(m.ItemId) && !wip.Contains(m.WarehouseId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var incoming = await IncomingAsync(org, itemIds, ct);
        var last = await LastPurchasesAsync(ctx, itemIds, ct);
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol }).ToDictionaryAsync(x => x.Id, ct);
        var suppliers = last.Values.Select(l => l.SupplierId).Distinct().ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => suppliers.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var producible = cards.Select(c => c.ItemId).ToHashSet();
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        return need.Select(n =>
        {
            var item = items[n.Key];
            var l = last.GetValueOrDefault(n.Key);
            return new MaterialNeedDto(n.Key, item.Code, item.Name, item.Symbol, n.Value.Qty, Math.Max(0, free.GetValueOrDefault(n.Key)),
                incoming.GetValueOrDefault(n.Key), n.Value.Orders.Count, producible.Contains(n.Key), l?.SupplierId,
                l is null ? null : names.GetValueOrDefault(l.SupplierId), prices ? l?.Price : null);
        }).OrderByDescending(l => l.Shortage > 0).ThenBy(l => l.Code).ToList();
    }

    /// <summary>
    /// Ожидается: подтверждённые заказы поставщикам и черновики (в том числе созданные по нехватке — повторное нажатие не задублирует)
    /// — заказано минус (принято − возвращено) по проведённым документам.
    /// </summary>
    private async Task<Dictionary<long, decimal>> IncomingAsync(long org, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        var ordered = await (from o in db.PurchaseOrders.AsNoTracking()
                             where o.OrganizationId == org && (o.Status == PurchaseOrderStatus.Confirmed || o.Status == PurchaseOrderStatus.Draft)
                             from l in o.Lines
                             where itemIds.Contains(l.ItemId)
                             select new { OrderId = o.Id, l.ItemId, l.Quantity }).ToListAsync(ct);
        var orderIds = ordered.Select(o => o.OrderId).Distinct().ToList();
        var received = await (from d in db.StockDocuments.AsNoTracking()
                              where d.OrganizationId == org && d.PurchaseOrderId != null && orderIds.Contains(d.PurchaseOrderId.Value)
                                    && d.Status == StockDocumentStatus.Posted
                              from l in d.Lines
                              select new { OrderId = d.PurchaseOrderId!.Value, d.Kind, l.ItemId, l.Quantity }).ToListAsync(ct);
        return ordered.GroupBy(o => (o.OrderId, o.ItemId))
            .Select(g => (g.Key.ItemId, Left: Math.Max(0, g.Sum(x => x.Quantity)
                                                           - received.Where(r => r.OrderId == g.Key.OrderId && r.ItemId == g.Key.ItemId)
                                                               .Sum(r => r.Kind == StockOperationKind.ReturnToSupplier ? -r.Quantity : r.Quantity))))
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Left));
    }

    private sealed record LastPurchase(long SupplierId, decimal Price, decimal? VatPercent, bool PricesIncludeVat);

    /// <summary>Последняя закупка позиции: заказ поставщику подтверждён или закрыт, самый поздний по дате.</summary>
    private async Task<Dictionary<long, LastPurchase>> LastPurchasesAsync(AccessContext ctx, IReadOnlyList<long> itemIds, CancellationToken ct) =>
        (await (from o in db.PurchaseOrders.AsNoTracking()
                where o.OrganizationId == ctx.OrganizationId && (o.Status == PurchaseOrderStatus.Confirmed || o.Status == PurchaseOrderStatus.Closed)
                from l in o.Lines
                where itemIds.Contains(l.ItemId)
                select new { l.ItemId, o.OrderDate, o.Id, o.SupplierId, l.Price, l.VatPercent, o.PricesIncludeVat }).ToListAsync(ct))
        .GroupBy(x => x.ItemId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.Id)
            .Select(x => new LastPurchase(x.SupplierId, x.Price, x.VatPercent, x.PricesIncludeVat)).First());
}
