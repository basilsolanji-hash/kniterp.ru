using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Строка состава: Available — сколько компонента доступно на всех видимых складах (остаток − резерв заказов).</summary>
public sealed record BundleComponentDto(
    long Id, long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Quantity, int SortOrder, bool IsArchived,
    decimal? Price, decimal? UnitCost, decimal Available, byte[] RowVersion);

public sealed record BundleWarehouseAvailabilityDto(long WarehouseId, string Warehouse, decimal Available);

/// <summary>
/// Комплект (D302): состав, доступность по складам (сколько наборов можно собрать), подсказка цены (сумма цен компонентов и
/// скидка набора — с правом «Цены и суммы»), себестоимость (сумма себестоимостей компонентов — тоже только с этим правом).
/// </summary>
public sealed record BundleDto(
    long BundleId, string Code, string Name, string UnitSymbol, IReadOnlyList<BundleComponentDto> Components,
    IReadOnlyList<BundleWarehouseAvailabilityDto> Availability, decimal TotalAvailable, BundlePriceHint? Price, decimal? Cost,
    bool CanEdit, bool CanSeePrices);

/// <summary>Позиция, которую можно добавить в комплект: действующий товар или модификация (не услуга и не комплект).</summary>
public sealed record BundleComponentOptionDto(long Id, string Code, string Name, string UnitSymbol, byte Precision);

/// <summary>
/// Состав комплектов (D302). Смотреть — «Справочники: просмотр», менять — «Номенклатура: состав комплектов» (catalog.bundle.edit).
/// Изменение состава не меняет проведённые документы: разворот в компоненты фиксируется в строках документа на момент отгрузки.
/// </summary>
public sealed class BundleService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<BundleDto> GetAsync(long bundleId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var bundle = await FindAsync(ctx, bundleId, ct);
        if (bundle.Type != ItemType.Bundle)
        {
            throw new BusinessRuleException("catalog.bundle.not_bundle", $"Позиция {bundle.Code} — не комплект. Состав бывает только у типа «Комплекты».");
        }

        var unit = await db.Units.AsNoTracking().Where(u => u.Id == bundle.UnitId).Select(u => new { u.Symbol, u.Precision }).SingleAsync(ct);
        var rows = await (from c in db.BundleComponents.AsNoTracking()
                          join i in db.Items.AsNoTracking() on c.ComponentItemId equals i.Id
                          join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                          where c.OrganizationId == ctx.OrganizationId && c.BundleItemId == bundleId
                          orderby c.SortOrder, c.Id
                          select new { c, i.Code, i.Name, i.IsArchived, u.Symbol, u.Precision }).ToListAsync(ct);
        var specs = rows.Select(r => new BundleComponentSpec(bundleId, r.c.ComponentItemId, r.c.Quantity, r.Precision)).ToList();
        var componentIds = specs.Select(s => s.ComponentItemId).ToList();

        // Доступность: по каждому складу — остаток − резерв подтверждённых заказов; архивный компонент не доступен.
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseReportView, Permissions.WarehouseDocumentCreate, Permissions.SalesView);
        var available = await AvailableByWarehouseAsync(ctx.OrganizationId, componentIds, visible, ct);
        var archived = rows.Where(r => r.IsArchived).Select(r => r.c.ComponentItemId).ToHashSet();
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && (visible == null || visible.Contains(w.Id)))
            .OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync(ct);
        var byWarehouse = warehouses.Select(w =>
        {
            var stock = componentIds.ToDictionary(id => id, id => archived.Contains(id) ? 0m : available.GetValueOrDefault((w.Id, id)));
            return new BundleWarehouseAvailabilityDto(w.Id, w.Name, BundleExpansion.Available(bundleId, specs, stock, unit.Precision));
        }).ToList();
        var componentAvailable = componentIds.ToDictionary(id => id,
            id => archived.Contains(id) ? 0m : available.Where(a => a.Key.ItemId == id).Sum(a => Math.Max(0, a.Value)));

        var prices = ctx.Permissions.Has(Permissions.PriceView);
        Dictionary<long, decimal> salePrices = [];
        Dictionary<long, decimal> costs = [];
        BundlePriceHint? hint = null;
        decimal? cost = null;
        if (prices)
        {
            // Цена основного вида (D79): у компонентов — для суммы, у комплекта — его собственная.
            var ids = componentIds.Append(bundleId).ToList();
            salePrices = await db.ItemPrices.AsNoTracking()
                .Where(p => p.OrganizationId == ctx.OrganizationId && ids.Contains(p.ItemId))
                .Join(db.PriceTypes.AsNoTracking().Where(t => t.IsDefault), p => p.PriceTypeId, t => t.Id, (p, t) => new { p.ItemId, p.Price })
                .ToDictionaryAsync(x => x.ItemId, x => x.Price, ct);
            hint = BundleExpansion.PriceHint(bundleId, specs, salePrices.Where(p => p.Key != bundleId).ToDictionary(), salePrices.TryGetValue(bundleId, out var own) ? own : null);
            // Себестоимость компонентов — та же оценка, что в отчёте «Прибыльность» (D69): закупки, затем техкарты.
            var unitCosts = await new SalesAnalyticsService(db, guard).CostsAsync(ctx, DateOnly.FromDateTime(clock.UtcNow), componentIds, ct);
            costs = unitCosts.Where(c => c.Value.Source != KnitErp.Domain.Sales.CostSource.None).ToDictionary(c => c.Key, c => c.Value.Cost);
            cost = BundleExpansion.Cost(bundleId, specs, costs);
        }

        var components = rows.Select(r => new BundleComponentDto(r.c.Id, r.c.ComponentItemId, r.Code, r.Name, r.Symbol, r.Precision, r.c.Quantity,
            r.c.SortOrder, r.IsArchived, prices && salePrices.TryGetValue(r.c.ComponentItemId, out var p) ? p : null,
            prices && costs.TryGetValue(r.c.ComponentItemId, out var uc) ? uc : null, componentAvailable[r.c.ComponentItemId], r.c.RowVersion)).ToList();
        return new BundleDto(bundleId, bundle.Code, bundle.Name, unit.Symbol, components, byWarehouse, byWarehouse.Sum(w => w.Available), hint, cost,
            ctx.Permissions.Has(Permissions.BundleEdit) && !bundle.IsArchived, prices);
    }

    /// <summary>Сколько комплектов доступно (сумма по видимым складам) — для API и списков.</summary>
    public async Task<decimal> AvailableAsync(long bundleId, long? warehouseId = null, CancellationToken ct = default)
    {
        var dto = await GetAsync(bundleId, ct);
        return warehouseId is { } w ? dto.Availability.FirstOrDefault(a => a.WarehouseId == w)?.Available ?? 0 : dto.TotalAvailable;
    }

    /// <summary>Позиции для добавления в состав: действующие товары и модификации, без услуг и комплектов.</summary>
    public async Task<IReadOnlyList<BundleComponentOptionDto>> ComponentOptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.BundleEdit, ct);
        return await StockEntry.ActiveItems(db, ctx.OrganizationId, stockedOnly: true)
            .Select(i => new BundleComponentOptionDto(i.Id, i.Code, i.Name, i.UnitSymbol, i.Precision)).ToListAsync(ct);
    }

    /// <summary>Добавить компонент или изменить его количество (на один комплект).</summary>
    public async Task SetComponentAsync(long bundleId, long componentItemId, decimal quantity, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.BundleEdit, ct);
        var bundle = await FindAsync(ctx, bundleId, ct);
        BundleRules.EnsureBundle(bundle);
        var component = await FindAsync(ctx, componentItemId, ct);
        BundleRules.EnsureComponent(bundle, component);
        var precision = await db.Units.AsNoTracking().Where(u => u.Id == component.UnitId).Select(u => u.Precision).SingleAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.bundle.{bundleId}", ct);
        var current = await db.BundleComponents.Where(c => c.OrganizationId == ctx.OrganizationId && c.BundleItemId == bundleId).ToListAsync(ct);
        var existing = current.FirstOrDefault(c => c.ComponentItemId == componentItemId);
        if (existing is null)
        {
            if (current.Count >= BundleComponent.MaxPerBundle)
            {
                throw new BusinessRuleException("catalog.bundle.too_many", $"В комплекте не больше {BundleComponent.MaxPerBundle} компонентов.");
            }

            db.BundleComponents.Add(BundleComponent.Create(ctx.OrganizationId, bundleId, componentItemId, quantity, precision,
                current.Count == 0 ? 0 : current.Max(c => c.SortOrder) + 1));
            Audit(ctx, bundleId, null, $"{component.Code} × {Quantities.Format(quantity)}", $"{bundle.Code}: компонент добавлен");
        }
        else if (existing.Quantity != quantity)
        {
            var before = existing.Quantity;
            existing.SetQuantity(quantity, precision);
            Audit(ctx, bundleId, $"{component.Code} × {Quantities.Format(before)}", $"{component.Code} × {Quantities.Format(quantity)}",
                $"{bundle.Code}: количество компонента");
        }

        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveComponentAsync(long componentRowId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.BundleEdit, ct);
        var row = await db.BundleComponents.SingleOrDefaultAsync(c => c.Id == componentRowId && c.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Компонент комплекта");
        row.EnsureVersion(row.RowVersion, rowVersion);
        var bundle = await FindAsync(ctx, row.BundleItemId, ct);
        BundleRules.EnsureBundle(bundle);
        var code = await db.Items.AsNoTracking().Where(i => i.Id == row.ComponentItemId).Select(i => i.Code).SingleAsync(ct);
        db.BundleComponents.Remove(row);
        Audit(ctx, bundle.Id, $"{code} × {Quantities.Format(row.Quantity)}", "убран", $"{bundle.Code}: компонент");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Порядок компонентов в составе (для печати и карточки): delta −1 — выше, +1 — ниже.</summary>
    public async Task MoveComponentAsync(long componentRowId, int delta, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.BundleEdit, ct);
        var row = await db.BundleComponents.AsNoTracking().SingleOrDefaultAsync(c => c.Id == componentRowId && c.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Компонент комплекта");
        var list = await db.BundleComponents.Where(c => c.OrganizationId == ctx.OrganizationId && c.BundleItemId == row.BundleItemId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        var index = list.FindIndex(c => c.Id == componentRowId);
        var target = Math.Clamp(index + Math.Sign(delta), 0, list.Count - 1);
        if (target == index)
        {
            return;
        }

        var moved = list[index];
        list.RemoveAt(index);
        list.Insert(target, moved);
        for (var i = 0; i < list.Count; i++)
        {
            list[i].SetSortOrder(i);
        }

        Audit(ctx, row.BundleItemId, (index + 1).ToString(), (target + 1).ToString(), "Порядок компонента");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Состав комплектов для разворота (BundleExpansion): строки состава с точностью единиц компонентов.</summary>
    internal static async Task<IReadOnlyList<BundleComponentSpec>> SpecsAsync(
        IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long> bundleIds, CancellationToken ct) =>
        await (from c in db.BundleComponents.AsNoTracking()
               join i in db.Items.AsNoTracking() on c.ComponentItemId equals i.Id
               join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
               where c.OrganizationId == organizationId && bundleIds.Contains(c.BundleItemId)
               select new BundleComponentSpec(c.BundleItemId, c.ComponentItemId, c.Quantity, u.Precision)).ToListAsync(ct);

    /// <summary>Доступно по складу и позиции: остаток − резерв подтверждённых заказов (D76).</summary>
    private async Task<Dictionary<(long WarehouseId, long ItemId), decimal>> AvailableByWarehouseAsync(
        long organizationId, IReadOnlyCollection<long> itemIds, HashSet<long>? warehouseIds, CancellationToken ct)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var stock = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && itemIds.Contains(m.ItemId) && (warehouseIds == null || warehouseIds.Contains(m.WarehouseId)))
            .GroupBy(m => new { m.WarehouseId, m.ItemId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, Sum = g.Sum(m => m.Quantity) })
            .ToListAsync(ct);
        var reserved = await Reservations.ByWarehouseItemAsync(db, organizationId, warehouseIds, itemIds, null, ct);
        return stock.ToDictionary(s => (s.WarehouseId, s.ItemId), s => s.Sum - reserved.GetValueOrDefault((s.WarehouseId, s.ItemId)));
    }

    private async Task<Item> FindAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Номенклатура");

    private void Audit(AccessContext ctx, long bundleId, string? before, string? after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item),
            bundleId.ToString(), before, after, reason, currentUser.CorrelationId));
}
