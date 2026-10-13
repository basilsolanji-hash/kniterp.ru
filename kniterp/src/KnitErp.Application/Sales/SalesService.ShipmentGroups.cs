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

public sealed record DeliveryMethodDto(long Id, string Name, DeliveryKind Kind, bool IsArchived, byte[] RowVersion)
{
    public string KindName => SalesDeliveryMethod.KindName(Kind);
}

public sealed record ShipmentGroupFilter(ShipmentGroupStatus? Status = null, DateOnly? From = null, DateOnly? To = null, string? Search = null, long? OrderId = null);

public sealed record ShipmentGroupRowDto(
    long Id, string Number, DateOnly Date, string Customer, string Warehouse, ShipmentGroupStatus Status, string? Delivery, string? TrackingNumber,
    string Orders, int? Packages, decimal? GrossWeightKg)
{
    public string StatusName => SalesShipmentGroup.StatusName(Status);
}

public sealed record ShipmentGroupDocumentDto(
    long DocumentId, string DocumentNumber, StockDocumentStatus Status, long OrderId, string OrderNumber, int Lines, decimal Quantity)
{
    public string StatusName => StockDocument.StatusName(Status);
}

/// <summary>Строка упаковочного листа: заказ, позиция, количество (без цен).</summary>
public sealed record PackingLineDto(int No, string OrderNumber, string Code, string Name, string UnitSymbol, decimal Quantity);

/// <summary>
/// Отгрузка по нескольким заказам (D1151). CanEdit — логистику правят продажи или кладовщик склада; CanPost — проведение складских
/// документов в области склада.
/// </summary>
public sealed record ShipmentGroupDto(
    long Id, string Number, DateOnly Date, long CustomerId, string Customer, string? CustomerAddress, long WarehouseId, string Warehouse,
    ShipmentGroupStatus Status, long? DeliveryMethodId, string? Delivery, string? TrackingNumber, int? Packages, decimal? GrossWeightKg, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? PostedBy, DateTime? PostedAtUtc, IReadOnlyList<ShipmentGroupDocumentDto> Documents,
    IReadOnlyList<PackingLineDto> Lines, string Seller, bool CanEdit, bool CanPost, byte[] RowVersion)
{
    public string StatusName => SalesShipmentGroup.StatusName(Status);
}

public sealed partial class SalesService
{
    /// <summary>
    /// Способы доставки (D1152). Пока справочник пуст, при первом открытии пользователем, который ведёт заказы, он заполняется типовыми.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryMethodDto>> DeliveryMethodsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesView) && !WarehouseWork.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        if (ctx.Permissions.Has(Permissions.SalesEdit) && !await db.SalesDeliveryMethods.AnyAsync(m => m.OrganizationId == ctx.OrganizationId, ct))
        {
            foreach (var (name, kind) in SalesDeliveryMethod.Defaults)
            {
                db.SalesDeliveryMethods.Add(SalesDeliveryMethod.Create(ctx.OrganizationId, name, kind));
            }

            Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesDeliveryMethod), ctx.OrganizationId, null,
                string.Join(", ", SalesDeliveryMethod.Defaults.Select(d => d.Name)), "типовые способы доставки");
            await db.SaveChangesAsync(ct);
        }

        return await db.SalesDeliveryMethods.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId && (includeArchived || !m.IsArchived))
            .OrderBy(m => m.IsArchived).ThenBy(m => m.Kind).ThenBy(m => m.Name)
            .Select(m => new DeliveryMethodDto(m.Id, m.Name, m.Kind, m.IsArchived, m.RowVersion)).ToListAsync(ct);
    }

    public async Task<long> CreateDeliveryMethodAsync(string? name, DeliveryKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        if (await db.SalesDeliveryMethods.CountAsync(m => m.OrganizationId == ctx.OrganizationId, ct) >= SalesDeliveryMethod.MaxMethods)
        {
            throw new BusinessRuleException("sales.delivery.too_many", $"Способов доставки не больше {SalesDeliveryMethod.MaxMethods}.");
        }

        var method = SalesDeliveryMethod.Create(ctx.OrganizationId, name, kind);
        if (await db.SalesDeliveryMethods.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && !m.IsArchived && m.Name == method.Name, ct))
        {
            throw new BusinessRuleException("sales.delivery.duplicate", $"Способ доставки «{method.Name}» уже есть.");
        }

        db.SalesDeliveryMethods.Add(method);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesDeliveryMethod), method.Id, null, method.Name, "способ доставки");
        await db.SaveChangesAsync(ct);
        return method.Id;
    }

    public async Task SetDeliveryMethodArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var method = await db.SalesDeliveryMethods.SingleOrDefaultAsync(m => m.Id == id && m.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Способ доставки");
        method.EnsureVersion(method.RowVersion, rowVersion);
        method.SetArchived(archived);
        Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesDeliveryMethod), id, archived ? "действует" : "в архиве",
            archived ? "в архиве" : "действует", method.Name);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<IReadOnlyList<ShipmentGroupRowDto>> ListShipmentGroupsAsync(ShipmentGroupFilter filter, CancellationToken ct = default)
    {
        var (ctx, visible) = await PickScopeAsync(ct);
        var q = db.SalesShipmentGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId && (visible == null || visible.Contains(g.WarehouseId)));
        if (filter.Status is { } status)
        {
            q = q.Where(g => g.Status == status);
        }

        if (filter.From is { } from)
        {
            q = q.Where(g => g.GroupDate >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(g => g.GroupDate <= to);
        }

        if (filter.OrderId is { } orderId)
        {
            q = q.Where(g => g.Documents.Any(d => d.SalesOrderId == orderId));
        }

        var rows = from g in q
                   join c in db.Counterparties.AsNoTracking() on g.CustomerId equals c.Id
                   join w in db.Warehouses.AsNoTracking() on g.WarehouseId equals w.Id
                   join m in db.SalesDeliveryMethods.AsNoTracking() on g.DeliveryMethodId equals m.Id into mj
                   from m in mj.DefaultIfEmpty()
                   select new { g, Customer = c.Name, Warehouse = w.Name, Delivery = m == null ? null : m.Name };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            rows = rows.Where(r => r.g.Number.Contains(text) || r.Customer.Contains(text) || (r.g.TrackingNumber != null && r.g.TrackingNumber.Contains(text)));
        }

        var list = await rows.OrderByDescending(r => r.g.GroupDate).ThenByDescending(r => r.g.Id).Take(MaxRows)
            .Select(r => new { r.g.Id, r.g.Number, r.g.GroupDate, r.Customer, r.Warehouse, r.g.Status, r.Delivery, r.g.TrackingNumber, r.g.Packages,
                r.g.GrossWeightKg, Orders = r.g.Documents.Select(d => d.SalesOrderId).ToList() })
            .ToListAsync(ct);
        var orderIds = list.SelectMany(r => r.Orders).Distinct().ToList();
        var numbers = await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        return list.Select(r => new ShipmentGroupRowDto(r.Id, r.Number, r.GroupDate, r.Customer, r.Warehouse, r.Status, r.Delivery, r.TrackingNumber,
            string.Join(", ", r.Orders.Select(o => numbers[o])), r.Packages, r.GrossWeightKg)).ToList();
    }

    public async Task<ShipmentGroupDto> GetShipmentGroupAsync(long id, CancellationToken ct = default)
    {
        var (ctx, visible) = await PickScopeAsync(ct);
        var g = await db.SalesShipmentGroups.AsNoTracking().Include(x => x.Documents)
                    .SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Отгрузка");
        if (visible is not null && !visible.Contains(g.WarehouseId))
        {
            throw new NotFoundException("Отгрузка");
        }

        var docIds = g.Documents.Select(d => d.StockDocumentId).ToList();
        var docs = await db.StockDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Number, d.Status, d.SalesOrderId, Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList() })
            .ToListAsync(ct);
        var orderIds = g.Documents.Select(d => d.SalesOrderId).ToList();
        var orders = await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Number, o.LegalEntityId }).ToDictionaryAsync(o => o.Id, ct);
        var itemIds = docs.SelectMany(d => d.Lines.Select(l => l.ItemId)).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol }).ToDictionaryAsync(x => x.Id, ct);
        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == g.CustomerId).Select(c => new { c.Name, c.Address }).SingleAsync(ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == g.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        var delivery = g.DeliveryMethodId is { } dm
            ? await db.SalesDeliveryMethods.AsNoTracking().Where(m => m.Id == dm).Select(m => m.Name).SingleAsync(ct)
            : null;
        var entityId = orders.Values.Select(o => o.LegalEntityId).FirstOrDefault();
        var seller = await db.LegalEntities.AsNoTracking().Where(e => e.Id == entityId).Select(e => e.Name).SingleOrDefaultAsync(ct) ?? string.Empty;
        long?[] userIds = [g.CreatedByUserId, g.PostedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var documents = g.Documents.Select(link => docs.Single(d => d.Id == link.StockDocumentId))
            .Select(d => new ShipmentGroupDocumentDto(d.Id, d.Number, d.Status, d.SalesOrderId!.Value, orders[d.SalesOrderId.Value].Number, d.Lines.Count,
                d.Lines.Sum(l => l.Quantity)))
            .OrderBy(d => d.OrderNumber).ToList();
        var no = 0;
        var lines = docs.Where(d => d.Status != StockDocumentStatus.Cancelled)
            .SelectMany(d => d.Lines.Select(l => (Order: orders[d.SalesOrderId!.Value].Number, Item: items[l.ItemId], l.Quantity)))
            .OrderBy(l => l.Order).ThenBy(l => l.Item.Code)
            .Select(l => new PackingLineDto(++no, l.Order, l.Item.Code, l.Item.Name, l.Item.Symbol, l.Quantity)).ToList();
        var canEdit = g.Status != ShipmentGroupStatus.Cancelled && (ctx.Permissions.Has(Permissions.SalesEdit) || CanWorkAt(ctx, g.WarehouseId));
        var canPost = g.Status == ShipmentGroupStatus.Draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, g.WarehouseId);
        return new ShipmentGroupDto(g.Id, g.Number, g.GroupDate, g.CustomerId, customer.Name, customer.Address, g.WarehouseId, warehouse, g.Status,
            g.DeliveryMethodId, delivery, g.TrackingNumber, g.Packages, g.GrossWeightKg, g.Comment, users[g.CreatedByUserId], g.CreatedAtUtc,
            g.PostedByUserId is { } p ? users[p] : null, g.PostedAtUtc, documents, lines, seller, canEdit, canPost, g.RowVersion);
    }

    /// <summary>
    /// Отгрузка по нескольким заказам одного покупателя и склада (D1151): по черновику отгрузки на каждый заказ (как «Оформить
    /// отгрузку») и консолидация с логистикой — в одной транзакции. Заказ не может быть в двух черновиках отгрузки.
    /// </summary>
    public async Task<long> CreateShipmentGroupAsync(IReadOnlyCollection<long> orderIds, SalesShipmentLogistics logistics, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new BusinessRuleException("sales.shipment_group.no_orders", "Выберите заказы для отгрузки.");
        }

        var orders = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.Number, o.CustomerId, o.WarehouseId, o.Status }).ToListAsync(ct);
        if (orders.Count != ids.Count)
        {
            throw new NotFoundException("Заказ покупателя");
        }

        var first = orders[0];
        if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, first.WarehouseId))
        {
            if (!ctx.Permissions.Has(Permissions.WarehouseDocumentCreate))
            {
                await guard.DenyAsync(ctx, Permissions.WarehouseDocumentCreate, ct);
            }

            throw new NotFoundException("Заказ покупателя");
        }

        if (orders.Any(o => o.CustomerId != first.CustomerId || o.WarehouseId != first.WarehouseId))
        {
            throw new BusinessRuleException("sales.shipment_group.mixed", "В одну отгрузку — заказы одного покупателя с одного склада.");
        }

        if (orders.FirstOrDefault(o => o.Status != SalesOrderStatus.Confirmed) is { } notConfirmed)
        {
            throw new BusinessRuleException("sales.order.not_confirmed", $"Заказ {notConfirmed.Number} не подтверждён или закрыт.");
        }

        var busy = await (from d in db.SalesShipmentGroupDocuments.AsNoTracking()
                          join g in db.SalesShipmentGroups.AsNoTracking() on d.GroupId equals g.Id
                          where d.OrganizationId == ctx.OrganizationId && ids.Contains(d.SalesOrderId) && g.Status == ShipmentGroupStatus.Draft
                          select new { d.SalesOrderId, g.Number }).FirstOrDefaultAsync(ct);
        if (busy is not null)
        {
            throw new BusinessRuleException("sales.shipment_group.busy",
                $"Заказ {orders.Single(o => o.Id == busy.SalesOrderId).Number} уже в черновике отгрузки {busy.Number}.");
        }

        await EnsureDeliveryMethodAsync(ctx, logistics.DeliveryMethodId, null, ct);
        var today = await TodayAsync(ctx, ct);
        SalesShipmentGroup.Create(ctx.OrganizationId, "—", today, first.CustomerId, first.WarehouseId, logistics, ctx.UserId, clock.UtcNow);

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SalesShipmentGroup.NumberPrefix, ct);
            var group = SalesShipmentGroup.Create(ctx.OrganizationId, number, today, first.CustomerId, first.WarehouseId, logistics, ctx.UserId, clock.UtcNow);
            foreach (var order in orders.OrderBy(o => o.Number))
            {
                var docId = await CreateShipmentAsync(order.Id, ct);
                group.AddDocument(docId, order.Id);
            }

            db.SalesShipmentGroups.Add(group);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.SalesShipmentGroupCreated, nameof(SalesShipmentGroup), group.Id, null, $"заказов: {orders.Count}",
                $"{number}: {string.Join(", ", orders.Select(o => o.Number))}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return group.Id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Логистика: способ доставки, трек-номер, мест, вес брутто — и после проведения (трек-номер приходит позже).</summary>
    public async Task SetShipmentLogisticsAsync(long id, SalesShipmentLogistics logistics, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var group = await db.SalesShipmentGroups.SingleOrDefaultAsync(g => g.Id == id && g.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Отгрузка");
        if (!ctx.Permissions.Has(Permissions.SalesEdit) && !CanWorkAt(ctx, group.WarehouseId))
        {
            if (!WarehouseWork.Any(ctx.Permissions.Has))
            {
                await guard.DenyAsync(ctx, Permissions.SalesEdit, ct);
            }

            throw new NotFoundException("Отгрузка");
        }

        group.EnsureVersion(group.RowVersion, rowVersion);
        await EnsureDeliveryMethodAsync(ctx, logistics.DeliveryMethodId, group.DeliveryMethodId, ct);
        var before = $"{group.DeliveryMethodId}/{group.TrackingNumber}/{group.Packages}/{group.GrossWeightKg}";
        group.SetLogistics(logistics);
        Audit(ctx, AuditActions.SalesShipmentGroupChanged, nameof(SalesShipmentGroup), id, before,
            $"{group.DeliveryMethodId}/{group.TrackingNumber}/{group.Packages}/{group.GrossWeightKg}", $"{group.Number}: логистика");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Провести все документы отгрузки одной транзакцией: не хватает одного — не проводится ни один.</summary>
    public async Task PostShipmentGroupAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentPost, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var group = await db.SalesShipmentGroups.Include(g => g.Documents)
                            .SingleOrDefaultAsync(g => g.Id == id && g.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Отгрузка");
            if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentPost, group.WarehouseId))
            {
                throw new NotFoundException("Отгрузка");
            }

            group.EnsureVersion(group.RowVersion, rowVersion);
            if (group.Status != ShipmentGroupStatus.Draft)
            {
                throw new BusinessRuleException("sales.shipment_group.status", "Отгрузка уже проведена или отменена.");
            }

            var docIds = group.Documents.Select(d => d.StockDocumentId).ToList();
            var docs = await db.StockDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id)).Select(d => new { d.Id, d.Number, d.Status }).ToListAsync(ct);
            if (docs.FirstOrDefault(d => d.Status is StockDocumentStatus.Cancelled or StockDocumentStatus.Reversed) is { } bad)
            {
                throw new BusinessRuleException("sales.shipment_group.document_cancelled", $"Документ {bad.Number} отменён или сторнирован — отгрузку не провести.");
            }

            foreach (var doc in docs.Where(d => d.Status == StockDocumentStatus.Draft).OrderBy(d => d.Id))
            {
                var version = await db.StockDocuments.AsNoTracking().Where(d => d.Id == doc.Id).Select(d => d.RowVersion).SingleAsync(ct);
                await documents.PostAsync(doc.Id, version, ct);
            }

            if (group.Status == ShipmentGroupStatus.Draft)
            {
                group.MarkPosted(ctx.UserId, clock.UtcNow);
                Audit(ctx, AuditActions.SalesShipmentGroupChanged, nameof(SalesShipmentGroup), id, "Черновик", "Отгружено", group.Number);
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

    /// <summary>Отмена черновика отгрузки — вместе с черновиками её документов (проведённые отменить нельзя — только сторно).</summary>
    public async Task CancelShipmentGroupAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var group = await db.SalesShipmentGroups.Include(g => g.Documents)
                            .SingleOrDefaultAsync(g => g.Id == id && g.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Отгрузка");
            if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, group.WarehouseId))
            {
                throw new NotFoundException("Отгрузка");
            }

            group.EnsureVersion(group.RowVersion, rowVersion);
            var docIds = group.Documents.Select(d => d.StockDocumentId).ToList();
            var docs = await db.StockDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id)).Select(d => new { d.Id, d.Status, d.RowVersion }).ToListAsync(ct);
            if (docs.Any(d => d.Status is StockDocumentStatus.Posted))
            {
                throw new BusinessRuleException("sales.shipment_group.has_posted", "Часть документов уже проведена — отменить отгрузку нельзя, только сторнировать документы.");
            }

            group.Cancel();
            foreach (var doc in docs.Where(d => d.Status == StockDocumentStatus.Draft))
            {
                await documents.CancelAsync(doc.Id, doc.RowVersion, ct);
            }

            Audit(ctx, AuditActions.SalesShipmentGroupChanged, nameof(SalesShipmentGroup), id, "Черновик", "Отменено", group.Number);
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task EnsureDeliveryMethodAsync(AccessContext ctx, long? id, long? current, CancellationToken ct)
    {
        if (id is not { } methodId || methodId == current)
        {
            return;
        }

        var method = await db.SalesDeliveryMethods.AsNoTracking().SingleOrDefaultAsync(m => m.Id == methodId && m.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Способ доставки");
        if (method.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Способ доставки «{method.Name}» в архиве.");
        }
    }
}
