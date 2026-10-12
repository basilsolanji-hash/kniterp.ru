using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>
/// «Создать по образцу» (D319): новая позиция с теми же типом, единицей, НДС, группой, описанием, сведениями (кроме артикула),
/// предметом расчёта и маркировкой (без GTIN), ценами, упаковками (без штрихкодов), поставщиками и составом комплекта.
/// Не копируются уникальные значения — штрихкоды, GTIN, артикул (по шаблону настроек, если он включён) — и модификации.
/// Создание идёт через обычное создание позиции (те же правила, автокод, автоштрихкод) и одной транзакцией.
/// </summary>
public sealed class ItemCopyService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, CatalogService catalog, NomenclatureService nomenclature)
{
    public async Task<long> CopyAsync(long sourceId, string? code, string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var org = ctx.OrganizationId;
        var src = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == sourceId && i.OrganizationId == org, ct)
                  ?? throw new NotFoundException("Номенклатура");
        if (src.ParentItemId is not null)
        {
            throw new BusinessRuleException("catalog.copy.variant", "Модификацию не копируют — создайте её в основной позиции или скопируйте основную.");
        }

        var newName = string.IsNullOrWhiteSpace(name) ? $"{src.Name} (копия)" : name;
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var id = await catalog.CreateItemAsync(new ItemCommand(code ?? string.Empty, newName, src.Type, src.UnitId, src.Description, src.VatRateId, src.GroupId), ct);
            var item = await db.Items.SingleAsync(i => i.Id == id, ct);
            item.SetDetails(src.Details with { Article = item.Article });
            if (ItemTypes.IsStocked(item.Type) || item.Type == ItemType.Service)
            {
                item.SetTrade(src.Trade with { Gtin = null });
            }

            // Ревью: цены копируются только тому, кто их видит, и только действующих видов (архивный вид раньше отменял всю копию).
            var prices = ctx.Permissions.Has(Permissions.PriceView)
                ? await (from p in db.ItemPrices.AsNoTracking()
                         join t in db.PriceTypes.AsNoTracking() on p.PriceTypeId equals t.Id
                         where p.OrganizationId == org && p.ItemId == sourceId && !t.IsArchived
                         select new { p.PriceTypeId, p.Price }).ToDictionaryAsync(p => p.PriceTypeId, p => (decimal?)p.Price, ct)
                : [];
            await db.SaveChangesAsync(ct);
            if (prices.Count > 0)
            {
                await nomenclature.SetPricesAsync(id, prices, ct); // с историей цен и журналом
            }

            if (ItemTypes.IsStocked(item.Type))
            {
                var precision = await db.Units.AsNoTracking().Where(u => u.Id == item.UnitId).Select(u => u.Precision).SingleAsync(ct);
                foreach (var pack in await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org && p.ItemId == sourceId).ToListAsync(ct))
                {
                    db.ItemPacks.Add(ItemPack.Create(org, item, pack.UnitId, pack.Factor, precision, null));
                }
            }

            if (item.Type != ItemType.Bundle)
            {
                foreach (var s in await db.ItemSuppliers.AsNoTracking().Where(x => x.OrganizationId == org && x.ItemId == sourceId).ToListAsync(ct))
                {
                    db.ItemSuppliers.Add(ItemSupplier.Create(org, item, s.SupplierId, s.SupplierArticle, s.SupplierItemName, s.IsMain));
                }
            }
            else
            {
                // Количества компонентов уже проверены у образца по точности их единиц — копируются как есть.
                foreach (var c in await db.BundleComponents.AsNoTracking().Where(x => x.OrganizationId == org && x.BundleItemId == sourceId).ToListAsync(ct))
                {
                    db.BundleComponents.Add(BundleComponent.Create(org, id, c.ComponentItemId, c.Quantity, UnitOfMeasure.MaxPrecision, c.SortOrder));
                }
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogCreated, nameof(Item), id.ToString(CultureInfo.InvariantCulture),
                null, $"{item.Code} {item.Name}", $"Создана по образцу {src.Code}", currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }
}
