using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>
/// Поставщик позиции (D309). LastPrice — цена без НДС за единицу в последнем подтверждённом заказе этому поставщику (null — заказов нет
/// или нет права «Цены и суммы»), LastOrderDate — дата этого заказа.
/// </summary>
public sealed record ItemSupplierDto(
    long Id, long SupplierId, string Supplier, string? SupplierArticle, string? SupplierItemName, bool IsMain, decimal? LastPrice, DateOnly? LastOrderDate,
    byte[] RowVersion);

public sealed record SupplierOptionDto(long Id, string Name);

/// <summary>
/// Поставщики позиции (D309): смотреть — «Справочники: просмотр», менять — «Справочники: изменение». Поставщик — контрагент своей
/// организации с ролью «поставщик». Последняя цена — из заказов поставщику, без отдельного хранения.
/// </summary>
public sealed class ItemSupplierService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<ItemSupplierDto>> ListAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        await FindItemAsync(ctx, itemId, ct);
        var rows = await (from s in db.ItemSuppliers.AsNoTracking()
                          join c in db.Counterparties.AsNoTracking() on s.SupplierId equals c.Id
                          where s.OrganizationId == ctx.OrganizationId && s.ItemId == itemId
                          orderby s.IsMain descending, c.Name
                          select new { s, c.Name }).ToListAsync(ct);
        var last = ctx.Permissions.Has(Permissions.PriceView)
            ? await LastPricesAsync(ctx.OrganizationId, itemId, rows.Select(r => r.s.SupplierId).ToList(), ct)
            : [];
        return rows.Select(r => new ItemSupplierDto(r.s.Id, r.s.SupplierId, r.Name, r.s.SupplierArticle, r.s.SupplierItemName, r.s.IsMain,
            last.TryGetValue(r.s.SupplierId, out var p) ? p.Price : null, last.TryGetValue(r.s.SupplierId, out var d) ? d.Date : null, r.s.RowVersion)).ToList();
    }

    public async Task<IReadOnlyList<SupplierOptionDto>> SupplierOptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        return await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.IsSupplier && !c.IsArchived)
            .OrderBy(c => c.Name).Select(c => new SupplierOptionDto(c.Id, c.Name)).ToListAsync(ct);
    }

    /// <summary>Добавить поставщика или изменить его артикул и наименование; isMain — сделать основным (прежний основной снимается).</summary>
    public async Task<long> SetAsync(long itemId, long supplierId, string? article, string? name, bool isMain, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await FindItemAsync(ctx, itemId, ct);
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        var supplier = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == supplierId && c.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Контрагент");
        if (!supplier.IsSupplier || supplier.IsArchived)
        {
            throw new BusinessRuleException("catalog.supplier.not_supplier", $"«{supplier.Name}» не отмечен как действующий поставщик.");
        }

        if (item.Type == ItemType.Bundle)
        {
            throw new BusinessRuleException("catalog.supplier.bundle", "У комплекта нет поставщиков — их задают у компонентов.");
        }

        ItemSupplier.Validate(article, name);
        var all = await db.ItemSuppliers.Where(s => s.OrganizationId == ctx.OrganizationId && s.ItemId == itemId).ToListAsync(ct);
        var row = all.FirstOrDefault(s => s.SupplierId == supplierId);
        if (row is null && all.Count >= ItemSupplier.MaxPerItem)
        {
            throw new BusinessRuleException("catalog.supplier.too_many", $"У позиции не больше {ItemSupplier.MaxPerItem} поставщиков.");
        }

        // Проверки выше — до изменений: у вкладки Blazor один DbContext.
        var created = row is null;
        var makeMain = isMain || all.Count == 0 || row?.IsMain == true;
        if (makeMain && all.Where(s => s != row && s.IsMain).ToList() is { Count: > 0 } previous)
        {
            // Сначала снять прежнего основного — уникальный индекс «один основной у позиции».
            previous.ForEach(p => p.SetMain(false));
            await db.SaveChangesAsync(ct);
        }

        if (row is null)
        {
            row = ItemSupplier.Create(ctx.OrganizationId, item, supplierId, article, name, makeMain);
            db.ItemSuppliers.Add(row);
        }
        else
        {
            row.Set(article, name, makeMain);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), itemId.ToString(),
            created ? null : supplier.Name, $"{supplier.Name}{(row.SupplierArticle is { } a ? ", арт. " + a : "")}{(row.IsMain ? ", основной" : "")}",
            "Поставщик позиции", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        return row.Id;
    }

    public async Task RemoveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var row = await db.ItemSuppliers.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Поставщик позиции");
        row.EnsureVersion(row.RowVersion, rowVersion);
        var name = await db.Counterparties.AsNoTracking().Where(c => c.Id == row.SupplierId).Select(c => c.Name).SingleAsync(ct);
        db.ItemSuppliers.Remove(row);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), row.ItemId.ToString(),
            name, "убран", "Поставщик позиции", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Основной поставщик позиции и его последняя цена — для подстановки в заказ поставщику (запрос Р5 сессии № 1); null — не задан.
    /// </summary>
    public async Task<ItemSupplierDto?> MainAsync(long itemId, CancellationToken ct = default) =>
        (await ListAsync(itemId, ct)).FirstOrDefault(s => s.IsMain);

    /// <summary>Цена без НДС за единицу в последнем подтверждённом или закрытом заказе каждому поставщику.</summary>
    private async Task<Dictionary<long, (decimal Price, DateOnly Date)>> LastPricesAsync(long org, long itemId, IReadOnlyList<long> suppliers, CancellationToken ct)
    {
        if (suppliers.Count == 0)
        {
            return [];
        }

        var lines = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && suppliers.Contains(o.SupplierId)
                        && (o.Status == PurchaseOrderStatus.Confirmed || o.Status == PurchaseOrderStatus.Closed))
            .SelectMany(o => o.Lines.Where(l => l.ItemId == itemId && l.Quantity > 0)
                .Select(l => new { o.SupplierId, o.OrderDate, o.Id, l.Amount, l.VatAmount, l.Quantity }))
            .ToListAsync(ct);
        return lines.GroupBy(l => l.SupplierId).ToDictionary(g => g.Key, g =>
        {
            var last = g.OrderByDescending(l => l.OrderDate).ThenByDescending(l => l.Id).First();
            return (Money.Round((last.Amount - last.VatAmount) / last.Quantity), last.OrderDate);
        });
    }

    private async Task<Item> FindItemAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Номенклатура");
}
