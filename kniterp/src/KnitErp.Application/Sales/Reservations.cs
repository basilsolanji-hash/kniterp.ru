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
        var lines = await orders.SelectMany(o => o.Lines.Select(l => new { OrderId = o.Id, o.WarehouseId, l.ItemId, l.Quantity }))
            .Where(l => itemIds == null || itemIds.Contains(l.ItemId))
            .Where(l => !db.Items.Any(i => i.Id == l.ItemId && ItemTypes.NotStocked.Contains(i.Type)))
            .ToListAsync(ct);
        if (lines.Count == 0)
        {
            return [];
        }

        var orderIds = lines.Select(l => l.OrderId).Distinct().ToList();
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.SalesOrderId != null && orderIds.Contains(d.SalesOrderId.Value)
                        && d.Status == StockDocumentStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { OrderId = d.SalesOrderId!.Value, d.Kind, l.ItemId, l.Quantity }))
            .ToListAsync(ct);
        var net = moved.GroupBy(m => (m.OrderId, m.ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Kind == StockOperationKind.Shipment ? m.Quantity : -m.Quantity));

        return lines
            .Select(l => (Key: (l.WarehouseId, l.ItemId), Left: Math.Max(0, l.Quantity - net.GetValueOrDefault((l.OrderId, l.ItemId)))))
            .Where(x => x.Left > 0)
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Left));
    }
}
