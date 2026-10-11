using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record CatalogPackDto(long UnitId, string Unit, decimal Factor, string? Barcode);

public sealed record CatalogComponentDto(long ItemId, string Code, decimal Quantity);

/// <summary>
/// Позиция для обмена (API v1, D307): все поля списка номенклатуры плюс вид (товар, услуга, комплект), предмет расчёта, маркировка
/// (код вида продукции как в МойСклад), GTIN, минимальная цена (только с правом «Цены и суммы»), упаковки и состав комплекта.
/// </summary>
public sealed record CatalogItemDto(
    long Id, string Code, string Name, ItemType Type, string TypeName, ItemKind Kind, long UnitId, string UnitSymbol, string? Description, bool IsArchived,
    long? VatRateId, string? VatRateName, string? Article, long? GroupId, decimal? SalePrice, long? ParentItemId, int Modifications, string PaymentSubject,
    bool IsMarked, string TrackingType, string? Gtin, decimal? MinPrice, IReadOnlyList<CatalogPackDto> Packs, IReadOnlyList<CatalogComponentDto>? Components);

/// <summary>Доступность позиции: у товара — остаток − резерв заказов, у комплекта — сколько наборов можно собрать; у услуги — Stocked = false.</summary>
public sealed record ItemAvailabilityDto(long ItemId, string Code, ItemKind Kind, bool Stocked, decimal Total, IReadOnlyList<BundleWarehouseAvailabilityDto> Warehouses);

/// <summary>Запросы каталога для обмена и API (D307). Права — как у списка номенклатуры; цены — только с «Цены и суммы».</summary>
public sealed class CatalogQueryService(IKnitErpDbContext db, IAccessGuard guard, BundleService bundles)
{
    /// <summary>Дополняет страницу списка номенклатуры полями обмена — одним запросом на каждую таблицу, без N+1.</summary>
    public async Task<IReadOnlyList<CatalogItemDto>> EnrichAsync(IReadOnlyList<ItemDto> page, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var ids = page.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var trade = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && ids.Contains(i.Id))
            .Select(i => new { i.Id, i.PaymentSubject, i.IsMarked, i.TrackingType, i.Gtin, i.MinPrice }).ToDictionaryAsync(i => i.Id, ct);
        var packs = (await PackService.ListInternalAsync(db, ctx.OrganizationId, ids, ct))
            .ToLookup(p => p.ItemId, p => new CatalogPackDto(p.UnitId, p.UnitSymbol, p.Factor, p.Barcode));
        var bundleIds = page.Where(i => i.Type == ItemType.Bundle).Select(i => i.Id).ToList();
        var components = bundleIds.Count == 0
            ? Enumerable.Empty<(long BundleId, CatalogComponentDto Component)>().ToLookup(x => x.BundleId, x => x.Component)
            : (await (from c in db.BundleComponents.AsNoTracking()
                      join i in db.Items.AsNoTracking() on c.ComponentItemId equals i.Id
                      where c.OrganizationId == ctx.OrganizationId && bundleIds.Contains(c.BundleItemId)
                      orderby c.SortOrder, c.Id
                      select new { c.BundleItemId, c.ComponentItemId, i.Code, c.Quantity }).ToListAsync(ct))
                .ToLookup(x => x.BundleItemId, x => new CatalogComponentDto(x.ComponentItemId, x.Code, x.Quantity));
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        return page.Select(i =>
        {
            var t = trade[i.Id];
            return new CatalogItemDto(i.Id, i.Code, i.Name, i.Type, i.TypeName, ItemTypes.KindOf(i.Type), i.UnitId, i.UnitSymbol, i.Description, i.IsArchived,
                i.VatRateId, i.VatRateName, i.Article, i.GroupId, i.SalePrice, i.ParentItemId, i.Modifications, PaymentSubjectCode(t.PaymentSubject),
                t.IsMarked, Marking.MoySkladCode(t.TrackingType), t.Gtin, prices ? t.MinPrice : null, packs[i.Id].ToList(),
                i.Type == ItemType.Bundle ? components[i.Id].ToList() : null);
        }).ToList();
    }

    public async Task<ItemAvailabilityDto> AvailabilityAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var item = await db.Items.AsNoTracking().Where(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId)
                       .Select(i => new { i.Id, i.Code, i.Type }).SingleOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Номенклатура");
        var kind = ItemTypes.KindOf(item.Type);
        if (item.Type == ItemType.Bundle)
        {
            var bundle = await bundles.GetAsync(itemId, ct);
            return new ItemAvailabilityDto(item.Id, item.Code, kind, false, bundle.TotalAvailable, bundle.Availability);
        }

        if (!ItemTypes.IsStocked(item.Type))
        {
            return new ItemAvailabilityDto(item.Id, item.Code, kind, false, 0, []);
        }

        // Остаток − резерв подтверждённых заказов (D76) по складам, которые пользователь видит.
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseReportView, Permissions.WarehouseDocumentCreate, Permissions.SalesView);
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && (visible == null || visible.Contains(w.Id)))
            .OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync(ct);
        var warehouseIds = warehouses.Select(w => w.Id).ToList();
        var stock = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.ItemId == itemId && warehouseIds.Contains(m.WarehouseId))
            .GroupBy(m => m.WarehouseId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var reserved = await Reservations.ByWarehouseItemAsync(db, ctx.OrganizationId, warehouseIds, [itemId], null, ct);
        var rows = warehouses
            .Select(w => new BundleWarehouseAvailabilityDto(w.Id, w.Name, stock.GetValueOrDefault(w.Id) - reserved.GetValueOrDefault((w.Id, itemId))))
            .Where(r => r.Available != 0 || stock.ContainsKey(r.WarehouseId)).ToList();
        return new ItemAvailabilityDto(item.Id, item.Code, kind, true, rows.Sum(r => r.Available), rows);
    }

    /// <summary>Код признака предмета расчёта как в МойСклад (paymentItemType).</summary>
    public static string PaymentSubjectCode(PaymentSubject subject) => subject switch
    {
        PaymentSubject.Service => "SERVICE",
        PaymentSubject.ExcisableGoods => "EXCISABLE_GOOD",
        _ => "GOOD",
    };
}
