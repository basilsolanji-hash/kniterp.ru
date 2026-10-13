using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// Подтверждённый заказ, по которому есть что отгружать (D1150, D1151): для заданий на сборку и отгрузок по нескольким заказам.
/// PickList — номер открытого задания на сборку, Group — номер черновика отгрузки, в которые заказ уже входит.
/// </summary>
public sealed record OrderToShipDto(
    long OrderId, string Number, DateOnly OrderDate, DateOnly? ShipDate, long CustomerId, string Customer, long WarehouseId, string Warehouse,
    string? PickList, bool Picked, string? Group);

public sealed record PickListFilter(PickListStatus? Status = null, DateOnly? From = null, DateOnly? To = null, long? WarehouseId = null, long? OrderId = null);

public sealed record PickListRowDto(
    long Id, string Number, DateOnly Date, string Warehouse, PickListStatus Status, bool PartiallyPicked, string Orders, int Lines, decimal Quantity,
    decimal Picked)
{
    public string StatusName => SalesPickList.StatusName(Status);
}

public sealed record PickLineDto(
    long Id, long OrderId, string OrderNumber, string Customer, long ItemId, string Code, string Name, string UnitSymbol, byte Precision,
    decimal Quantity, decimal Picked, string? Location, decimal? Stock)
{
    public bool Done => Picked >= Quantity;
}

/// <summary>Задание на сборку. CanPick — кладовщик склада задания (складские права в области склада) или Владелец.</summary>
public sealed record PickListDto(
    long Id, string Number, DateOnly Date, long WarehouseId, string Warehouse, PickListStatus Status, bool PartiallyPicked, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? PickedBy, DateTime? PickedAtUtc, IReadOnlyList<PickLineDto> Lines, bool CanPick,
    byte[] RowVersion)
{
    public string StatusName => SalesPickList.StatusName(Status);
}

public sealed partial class SalesService
{
    private static readonly string[] WarehouseWork = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost];

    /// <summary>
    /// Подтверждённые заказы с неотгруженными товарами — на складах, где пользователь работает (или все, если он ведёт продажи).
    /// </summary>
    public async Task<IReadOnlyList<OrderToShipDto>> OrdersToShipAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var all = ctx.Permissions.Has(Permissions.SalesView);
        var visible = all ? null : WarehouseScope.Visible(ctx, WarehouseWork);
        if (!all && !WarehouseWork.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        var orders = await (from o in db.SalesOrders.AsNoTracking()
                            where o.OrganizationId == ctx.OrganizationId && o.Status == SalesOrderStatus.Confirmed
                                  && (visible == null || visible.Contains(o.WarehouseId))
                            join c in db.Counterparties.AsNoTracking() on o.CustomerId equals c.Id
                            join w in db.Warehouses.AsNoTracking() on o.WarehouseId equals w.Id
                            orderby o.ShipDate ?? o.OrderDate, o.Id
                            select new { o.Id, o.Number, o.OrderDate, o.ShipDate, o.CustomerId, Customer = c.Name, o.WarehouseId, Warehouse = w.Name,
                                Lines = o.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList() })
            .Take(MaxRows).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var moved = await MovedAsync(ctx, ids, ct);
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, orders.SelectMany(o => o.Lines.Select(l => l.ItemId)).Distinct(), ct);
        var picks = await (from l in db.SalesPickListLines.AsNoTracking()
                           join p in db.SalesPickLists.AsNoTracking() on l.PickListId equals p.Id
                           where l.OrganizationId == ctx.OrganizationId && ids.Contains(l.SalesOrderId) && p.Status != PickListStatus.Cancelled
                           select new { l.SalesOrderId, p.Number, p.Status, p.Id })
            .ToListAsync(ct);
        var groups = await (from d in db.SalesShipmentGroupDocuments.AsNoTracking()
                            join g in db.SalesShipmentGroups.AsNoTracking() on d.GroupId equals g.Id
                            where d.OrganizationId == ctx.OrganizationId && ids.Contains(d.SalesOrderId) && g.Status == ShipmentGroupStatus.Draft
                            select new { d.SalesOrderId, g.Number }).ToListAsync(ct);
        return orders
            .Where(o => o.Lines.Any(l => !services.Contains(l.ItemId) && l.Quantity - Net(moved, o.Id, l.ItemId) > 0))
            .Select(o =>
            {
                var pick = picks.Where(p => p.SalesOrderId == o.Id).OrderByDescending(p => p.Id).FirstOrDefault();
                return new OrderToShipDto(o.Id, o.Number, o.OrderDate, o.ShipDate, o.CustomerId, o.Customer, o.WarehouseId, o.Warehouse,
                    pick?.Number, pick?.Status == PickListStatus.Picked, groups.FirstOrDefault(g => g.SalesOrderId == o.Id)?.Number);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<PickListRowDto>> ListPickListsAsync(PickListFilter filter, CancellationToken ct = default)
    {
        var (ctx, visible) = await PickScopeAsync(ct);
        var q = db.SalesPickLists.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && (visible == null || visible.Contains(p.WarehouseId)));
        if (filter.Status is { } status)
        {
            q = q.Where(p => p.Status == status);
        }

        if (filter.From is { } from)
        {
            q = q.Where(p => p.ListDate >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(p => p.ListDate <= to);
        }

        if (filter.WarehouseId is { } wh)
        {
            q = q.Where(p => p.WarehouseId == wh);
        }

        if (filter.OrderId is { } orderId)
        {
            q = q.Where(p => p.Lines.Any(l => l.SalesOrderId == orderId));
        }

        var rows = await (from p in q.OrderByDescending(p => p.ListDate).ThenByDescending(p => p.Id).Take(MaxRows)
                          join w in db.Warehouses.AsNoTracking() on p.WarehouseId equals w.Id
                          select new
                          {
                              p.Id, p.Number, p.ListDate, Warehouse = w.Name, p.Status,
                              Lines = p.Lines.Select(l => new { l.SalesOrderId, l.Quantity, l.Picked }).ToList(),
                          }).ToListAsync(ct);
        var orderIds = rows.SelectMany(r => r.Lines.Select(l => l.SalesOrderId)).Distinct().ToList();
        var numbers = await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        return rows.Select(r => new PickListRowDto(r.Id, r.Number, r.ListDate, r.Warehouse, r.Status,
                r.Status == PickListStatus.Open && r.Lines.Any(l => l.Picked > 0),
                string.Join(", ", r.Lines.Select(l => l.SalesOrderId).Distinct().Select(id => numbers[id])), r.Lines.Count,
                r.Lines.Sum(l => l.Quantity), r.Lines.Sum(l => l.Picked)))
            .ToList();
    }

    public async Task<PickListDto> GetPickListAsync(long id, CancellationToken ct = default)
    {
        var (ctx, visible) = await PickScopeAsync(ct);
        var list = await db.SalesPickLists.AsNoTracking().Include(p => p.Lines)
                       .SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Задание на сборку");
        if (visible is not null && !visible.Contains(list.WarehouseId))
        {
            throw new NotFoundException("Задание на сборку");
        }

        var itemIds = list.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        var orderIds = list.Lines.Select(l => l.SalesOrderId).Distinct().ToList();
        var orders = await (from o in db.SalesOrders.AsNoTracking()
                            where orderIds.Contains(o.Id)
                            join c in db.Counterparties.AsNoTracking() on o.CustomerId equals c.Id
                            select new { o.Id, o.Number, Customer = c.Name }).ToDictionaryAsync(o => o.Id, ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == list.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        long?[] userIds = [list.CreatedByUserId, list.PickedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // ТЗ §11, D118: остаток — только если склад в области складских прав пользователя.
        Dictionary<long, decimal>? stock = null;
        if (WarehouseScope.VisibleMovements(db, ctx) is { } movements && WarehouseScope.StockViews.Any(p => WarehouseScope.Covers(ctx, p, list.WarehouseId)))
        {
            stock = await movements.Where(m => m.WarehouseId == list.WarehouseId && itemIds.Contains(m.ItemId))
                .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        }

        var lines = list.Lines
            .Select(l => new PickLineDto(l.Id, l.SalesOrderId, orders[l.SalesOrderId].Number, orders[l.SalesOrderId].Customer, l.ItemId,
                items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Symbol, items[l.ItemId].Precision, l.Quantity, l.Picked, l.Location,
                stock is null ? null : stock.GetValueOrDefault(l.ItemId)))
            .OrderBy(l => l.Location is null).ThenBy(l => l.Location, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.Code).ThenBy(l => l.OrderNumber)
            .ToList();
        return new PickListDto(list.Id, list.Number, list.ListDate, list.WarehouseId, warehouse, list.Status, list.PartiallyPicked, list.Comment,
            users[list.CreatedByUserId], list.CreatedAtUtc, list.PickedByUserId is { } p ? users[p] : null, list.PickedAtUtc, lines,
            list.Status != PickListStatus.Cancelled && CanWorkAt(ctx, list.WarehouseId), list.RowVersion);
    }

    /// <summary>
    /// Задание на сборку по подтверждённым заказам одного склада (D1150): неотгруженные товары (без услуг), место хранения — из
    /// прошлого задания по этой позиции на этом складе. Заказ не может быть в двух открытых заданиях. Создают — кто ведёт заказы
    /// или кладовщик склада.
    /// </summary>
    public async Task<long> CreatePickListAsync(IReadOnlyCollection<long> orderIds, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new BusinessRuleException("sales.pick.no_orders", "Выберите заказы для сборки.");
        }

        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OrganizationId == ctx.OrganizationId && ids.Contains(o.Id)).ToListAsync(ct);
        if (orders.Count != ids.Count)
        {
            throw new NotFoundException("Заказ покупателя");
        }

        var warehouseId = orders[0].WarehouseId;
        if (orders.Any(o => o.WarehouseId != warehouseId))
        {
            throw new BusinessRuleException("sales.pick.warehouses", "В одно задание на сборку — заказы одного склада.");
        }

        if (!ctx.Permissions.Has(Permissions.SalesEdit) && !CanWorkAt(ctx, warehouseId))
        {
            if (!WarehouseWork.Any(ctx.Permissions.Has))
            {
                await guard.DenyAsync(ctx, Permissions.SalesEdit, ct);
            }

            throw new NotFoundException("Заказ покупателя");
        }

        if (orders.FirstOrDefault(o => o.Status != SalesOrderStatus.Confirmed) is { } notConfirmed)
        {
            throw new BusinessRuleException("sales.order.not_confirmed", $"Заказ {notConfirmed.Number} не подтверждён или закрыт.");
        }

        var open = await (from l in db.SalesPickListLines.AsNoTracking()
                          join p in db.SalesPickLists.AsNoTracking() on l.PickListId equals p.Id
                          where l.OrganizationId == ctx.OrganizationId && ids.Contains(l.SalesOrderId) && p.Status == PickListStatus.Open
                          select new { l.SalesOrderId, p.Number }).FirstOrDefaultAsync(ct);
        if (open is not null)
        {
            throw new BusinessRuleException("sales.pick.already",
                $"Заказ {orders.Single(o => o.Id == open.SalesOrderId).Number} уже в открытом задании на сборку {open.Number}.");
        }

        var moved = await MovedAsync(ctx, ids, ct);
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, orders.SelectMany(o => o.Lines.Select(l => l.ItemId)).Distinct(), ct);
        var raw = orders.OrderBy(o => o.Id)
            .SelectMany(o => o.Lines.Where(l => !services.Contains(l.ItemId)).Select(l => (OrderId: o.Id, l.ItemId, Quantity: l.Quantity - Net(moved, o.Id, l.ItemId))))
            .Where(l => l.Quantity > 0).ToList();
        var itemIds = raw.Select(l => l.ItemId).Distinct().ToList();
        var locations = (await (from l in db.SalesPickListLines.AsNoTracking()
                                join p in db.SalesPickLists.AsNoTracking() on l.PickListId equals p.Id
                                where l.OrganizationId == ctx.OrganizationId && p.WarehouseId == warehouseId && itemIds.Contains(l.ItemId) && l.Location != null
                                select new { l.ItemId, l.Location, l.Id }).ToListAsync(ct))
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First().Location);
        var lines = raw.Select(l => (l.OrderId, l.ItemId, l.Quantity, locations.GetValueOrDefault(l.ItemId))).ToList();
        var today = await TodayAsync(ctx, ct);
        SalesPickList.Create(ctx.OrganizationId, "—", today, warehouseId, comment, lines, ctx.UserId, clock.UtcNow); // проверки до выдачи номера

        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SalesPickList.NumberPrefix, ct);
        var list = SalesPickList.Create(ctx.OrganizationId, number, today, warehouseId, comment, lines, ctx.UserId, clock.UtcNow);
        db.SalesPickLists.Add(list);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesPickListCreated, nameof(SalesPickList), list.Id, null, $"строк: {lines.Count}",
            $"{number}: {string.Join(", ", orders.Select(o => o.Number))}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return list.Id;
    }

    /// <summary>Собрано по строке (частично или полностью). Заказ, собранный по заданию целиком, двигает этап (D110: «Заказ собран»).</summary>
    public Task SetPickedAsync(long id, long lineId, decimal picked, byte[] rowVersion, CancellationToken ct = default) =>
        PickActionAsync(id, rowVersion, (list, ctx) =>
        {
            var line = list.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new BusinessRuleException("sales.pick.line", "Такой строки в задании нет.");
            var before = line.Picked;
            list.SetPicked(lineId, picked, ctx.UserId, clock.UtcNow);
            return $"строка {lineId}: собрано {Quantities.Format(before)} → {Quantities.Format(picked)}";
        }, ct);

    /// <summary>Всё задание собрано полностью.</summary>
    public Task PickAllAsync(long id, byte[] rowVersion, CancellationToken ct = default) =>
        PickActionAsync(id, rowVersion, (list, ctx) =>
        {
            list.PickAll(ctx.UserId, clock.UtcNow);
            return "собрано полностью";
        }, ct);

    public Task SetPickLocationAsync(long id, long lineId, string? location, byte[] rowVersion, CancellationToken ct = default) =>
        PickActionAsync(id, rowVersion, (list, _) =>
        {
            list.SetLocation(lineId, location);
            return $"строка {lineId}: место «{location?.Trim()}»";
        }, ct);

    public Task CancelPickListAsync(long id, byte[] rowVersion, CancellationToken ct = default) =>
        PickActionAsync(id, rowVersion, (list, _) =>
        {
            list.Cancel();
            return "отменено";
        }, ct);

    private async Task PickActionAsync(long id, byte[] rowVersion, Func<SalesPickList, AccessContext, string> action, CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!WarehouseWork.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseDocumentCreate, ct);
        }

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var list = await db.SalesPickLists.Include(p => p.Lines).SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Задание на сборку");
            if (!CanWorkAt(ctx, list.WarehouseId))
            {
                throw new NotFoundException("Задание на сборку");
            }

            list.EnsureVersion(list.RowVersion, rowVersion);
            var pickedBefore = FullyPickedOrders(list);
            var change = action(list, ctx);
            Audit(ctx, AuditActions.SalesPickListChanged, nameof(SalesPickList), id, null, change, list.Number);
            await db.SaveOrConflictAsync(ct);
            var newlyPicked = FullyPickedOrders(list).Except(pickedBefore).ToList();
            if (newlyPicked.Count > 0)
            {
                await StageAutomation.ApplyAsync(db, ctx, newlyPicked, clock.UtcNow, currentUser.CorrelationId, ct, StageTrigger.Picked);
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Заказы, все строки которых в задании собраны полностью (отменённое задание ничего не собирает).</summary>
    private static HashSet<long> FullyPickedOrders(SalesPickList list) =>
        list.Status == PickListStatus.Cancelled
            ? []
            : list.Lines.GroupBy(l => l.SalesOrderId).Where(g => g.All(l => l.Picked >= l.Quantity)).Select(g => g.Key).ToHashSet();

    /// <summary>Кладовщик склада: создание или проведение складских документов в области этого склада.</summary>
    private static bool CanWorkAt(AccessContext ctx, long warehouseId) => WarehouseWork.Any(p => WarehouseScope.Covers(ctx, p, warehouseId));

    /// <summary>Задания видят продажи (все склады) и кладовщики (склады своей области).</summary>
    private async Task<(AccessContext, HashSet<long>?)> PickScopeAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (ctx.Permissions.Has(Permissions.SalesView))
        {
            return (ctx, null);
        }

        if (!WarehouseScope.StockViews.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        return (ctx, WarehouseScope.Visible(ctx, WarehouseScope.StockViews) ?? null);
    }
}
