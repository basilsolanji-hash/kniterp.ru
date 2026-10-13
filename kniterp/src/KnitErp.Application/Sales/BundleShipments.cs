using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// D352 (сессия 4, Р2): отгрузка и возврат комплектов по заказу покупателя. Комплект в заказе — одна строка, на складе — его
/// компоненты: при отгрузке строка комплекта разворачивается по текущему составу, а в строках документа запоминается комплект и
/// количество наборов. Возврат идёт от отгруженного (зафиксированного) состава, а не от текущего.
/// </summary>
internal static class BundleShipments
{
    /// <summary>Отгружено нетто по комплекту заказа: наборов и компонентов (отгрузки − возвраты, проведённые документы).</summary>
    public sealed record Shipped(decimal Bundles, IReadOnlyDictionary<long, decimal> Components);

    /// <summary>Комплекты из списка позиций.</summary>
    public static async Task<HashSet<long>> BundleIdsAsync(IKnitErpDbContext db, long organizationId, IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.Items.AsNoTracking().Where(i => i.OrganizationId == organizationId && ids.Contains(i.Id) && i.Type == ItemType.Bundle)
                .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Разворот комплекта по текущему составу: компоненты на quantity наборов (округление вверх, D303).</summary>
    public static async Task<IReadOnlyList<(long ItemId, decimal Quantity)>> ExpandAsync(
        IKnitErpDbContext db, long organizationId, long bundleId, decimal quantity, CancellationToken ct)
    {
        var specs = await BundleService.SpecsAsync(db, organizationId, [bundleId], ct);
        if (specs.Count == 0)
        {
            var code = await db.Items.AsNoTracking().Where(i => i.Id == bundleId).Select(i => i.Code).SingleAsync(ct);
            throw new BusinessRuleException("sales.order.bundle_empty", $"У комплекта {code} не задан состав — отгружать нечего. Заполните состав в карточке комплекта.");
        }

        return BundleExpansion.Expand(bundleId, quantity, specs);
    }

    /// <summary>Отгружено нетто по комплектам заказа; exceptDocumentId — без этого документа.</summary>
    public static async Task<Dictionary<long, Shipped>> ShippedAsync(
        IKnitErpDbContext db, long organizationId, long orderId, CancellationToken ct, long? exceptDocumentId = null)
    {
        var rows = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.SalesOrderId == orderId && d.Status == StockDocumentStatus.Posted && d.Id != exceptDocumentId)
            .SelectMany(d => d.Lines.Where(l => l.BundleItemId != null)
                .Select(l => new { d.Id, d.Kind, l.ItemId, l.Quantity, Bundle = l.BundleItemId!.Value, BundleQuantity = l.BundleQuantity!.Value }))
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Bundle).ToDictionary(g => g.Key, g =>
        {
            decimal Sign(StockOperationKind kind) => kind == StockOperationKind.Shipment ? 1 : -1;
            var bundles = g.GroupBy(r => (r.Id, r.Kind)).Sum(d => Sign(d.Key.Kind) * d.Max(r => r.BundleQuantity));
            var components = g.GroupBy(r => r.ItemId).ToDictionary(c => c.Key, c => c.Sum(r => Sign(r.Kind) * r.Quantity));
            return new Shipped(bundles, components);
        });
    }

    /// <summary>Возврат quantity наборов комплекта: компоненты по отгруженному составу (BundleExpansion.Proportional).</summary>
    public static async Task<IReadOnlyList<(long ItemId, decimal Quantity)>> ReturnAsync(
        IKnitErpDbContext db, long organizationId, long bundleId, decimal quantity, Shipped? shipped, CancellationToken ct)
    {
        if (shipped is null || shipped.Bundles <= 0)
        {
            throw new BusinessRuleException("sales.return.bundle_exceeds", "Комплектов возвращается больше, чем отгружено по заказу.");
        }

        var ids = shipped.Components.Keys.ToList();
        var precisions = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, u.Precision })
            .ToDictionaryAsync(x => x.Id, x => x.Precision, ct);
        return BundleExpansion.Proportional(quantity, shipped.Bundles, shipped.Components, precisions);
    }
}
