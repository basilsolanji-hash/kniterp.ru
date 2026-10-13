using KnitErp.Application.Common;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// Резерв под заказы покупателей (D76): у подтверждённого заказа с галочкой «Резерв» держится неотгруженный остаток
/// каждой строки (заказано − отгружено + возвращено, не меньше нуля) на складе заказа. Черновик, закрытый и отменённый
/// заказ ничего не держат. Доступно = остаток − резерв. Резерв предупреждает, но отгрузку другого заказа не запрещает.
/// </summary>
internal static class Reservations
{
    /// <summary>Резерв по складу и позиции; excludeOrderId — без резерва этого заказа (что доступно ему самому).</summary>
    public static async Task<Dictionary<(long WarehouseId, long ItemId), decimal>> ByWarehouseItemAsync(
        IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long>? warehouseIds = null, IReadOnlyCollection<long>? itemIds = null,
        long? excludeOrderId = null, CancellationToken ct = default)
    {
        var orders = db.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.Reserve && o.Status == SalesOrderStatus.Confirmed && o.Id != excludeOrderId);
        if (warehouseIds is not null)
        {
            orders = orders.Where(o => warehouseIds.Contains(o.WarehouseId));
        }

        // D300 (сессия 4): услуга не хранится на складе — под неё ничего не резервируется.
        // D352 (сессия 4, Р2): комплект держит компоненты — неотгруженный остаток наборов по текущему составу; отбор по itemIds —
        // после разворота, поэтому строки комплектов читаются всегда.
        var lines = await orders.SelectMany(o => o.Lines.Select(l => new { OrderId = o.Id, o.WarehouseId, l.ItemId, l.Quantity }))
            .Select(l => new { l.OrderId, l.WarehouseId, l.ItemId, l.Quantity, Bundle = db.Items.Any(i => i.Id == l.ItemId && i.Type == ItemType.Bundle) })
            .Where(l => itemIds == null || itemIds.Contains(l.ItemId) || l.Bundle)
            .Where(l => l.Bundle || !db.Items.Any(i => i.Id == l.ItemId && ItemTypes.NotStocked.Contains(i.Type)))
            .ToListAsync(ct);
        if (lines.Count == 0)
        {
            return [];
        }

        var orderIds = lines.Select(l => l.OrderId).Distinct().ToList();
        var moved = BundleLines.InOrderTerms((await db.StockDocuments.AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.SalesOrderId != null && orderIds.Contains(d.SalesOrderId.Value)
                            && d.Status == StockDocumentStatus.Posted)
                .SelectMany(d => d.Lines.Select(l => new { d.Id, OrderId = d.SalesOrderId!.Value, d.Kind, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }))
                .ToListAsync(ct))
            .Select(m => ((m.Id, m.OrderId, Kind: StockOperationKinds.OrderKind(m.Kind)), m.ItemId, m.Quantity, m.BundleItemId, m.BundleQuantity))); // D354
        var net = moved.GroupBy(m => (m.Key.OrderId, m.ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Key.Kind == StockOperationKind.Shipment ? m.Quantity : -m.Quantity));

        var bundleIds = lines.Where(l => l.Bundle).Select(l => l.ItemId).Distinct().ToList();
        var specs = bundleIds.Count == 0 ? [] : await KnitErp.Application.Catalog.BundleService.SpecsAsync(db, organizationId, bundleIds, ct);
        var ownSpecs = specs.ToLookup(s => s.BundleItemId);
        return lines
            .Select(l => (l.WarehouseId, l.ItemId, l.Bundle, Left: Math.Max(0, l.Quantity - net.GetValueOrDefault((l.OrderId, l.ItemId)))))
            .Where(x => x.Left > 0)
            .SelectMany(x => !x.Bundle
                ? [(Key: (x.WarehouseId, x.ItemId), x.Left)]
                : ownSpecs[x.ItemId].Any()
                    ? BundleExpansion.Expand(x.ItemId, x.Left, ownSpecs[x.ItemId]).Select(c => (Key: (x.WarehouseId, c.ItemId), Left: c.Quantity))
                    : [])
            .Where(x => itemIds == null || itemIds.Contains(x.Key.Item2))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Left));
    }
}
