using KnitErp.Application.Access;
using KnitErp.Application.Tagging;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Common;

/// <summary>
/// Справочник объектов для тегов (D103) и обсуждений (D107): название, подпись и ссылка на карточку — только для объектов своей
/// организации, которые пользователь вправе видеть (права на вид объекта и область складов). Пустой ответ — «не найдено».
/// </summary>
public static class ObjectDirectory
{
    private static readonly string[] StockViews = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];
    private static readonly string[] OpeningViews = [Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView];

    /// <summary>Объект виден пользователю.</summary>
    public static async Task<TaggedObjectDto?> FindAsync(IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, long id, CancellationToken ct) =>
        (await DescribeAsync(db, ctx, entity, [id], ct)).FirstOrDefault();

    public static async Task<IReadOnlyList<TaggedObjectDto>> DescribeAsync(
        IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var has = ctx.Permissions.Has;
        switch (entity)
        {
            case TaggedEntity.Item when has(Permissions.CatalogView):
                return (await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id))
                        .Select(i => new { i.Id, i.Code, i.Name }).ToListAsync(ct))
                    .Select(i => new TaggedObjectDto(entity, i.Id, $"{i.Code} — {i.Name}", null, $"catalog/items/{i.Id}")).ToList();
            case TaggedEntity.Counterparty when has(Permissions.CatalogView):
                return (await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && ids.Contains(c.Id))
                        .Select(c => new { c.Id, c.Name, c.Inn }).ToListAsync(ct))
                    .Select(c => new TaggedObjectDto(entity, c.Id, c.Name, c.Inn is null ? null : "ИНН " + c.Inn,
                        $"counterparties?search={Uri.EscapeDataString(c.Name)}")).ToList();
            case TaggedEntity.SalesOrder when has(Permissions.SalesView):
                return (await (from o in db.SalesOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.CustomerId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"sales-orders/{o.Id}")).ToList();
            case TaggedEntity.PurchaseOrder when has(Permissions.PurchaseView):
                return (await (from o in db.PurchaseOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.SupplierId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"purchase-orders/{o.Id}")).ToList();
            case TaggedEntity.StockDocument when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.StockDocuments.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id)
                                    && (visible == null || visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value))))
                        .Select(d => new { d.Id, d.Number, d.Kind, d.DocumentDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"{StockDocument.KindName(d.Kind)} {d.Number}", d.DocumentDate.ToString("dd.MM.yyyy"),
                        $"stock-documents/{d.Id}")).ToList();
            }

            case TaggedEntity.InventoryCount when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.InventoryCounts.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id) && (visible == null || visible.Contains(d.WarehouseId)))
                        .Select(d => new { d.Id, d.Number, d.CountDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"Инвентаризация {d.Number}", d.CountDate.ToString("dd.MM.yyyy"), $"inventory/{d.Id}"))
                    .ToList();
            }

            case TaggedEntity.StockBatch when StockViews.Any(has):
                return (await (from b in db.StockBatches.AsNoTracking()
                               join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                               where b.OrganizationId == org && ids.Contains(b.Id)
                               select new { b.Id, b.Number, b.Color, b.DyeLot, i.Code }).ToListAsync(ct))
                    .Select(b => new TaggedObjectDto(entity, b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), b.Code,
                        $"stock-batches?search={Uri.EscapeDataString(b.Number)}")).ToList();
            case TaggedEntity.OpeningBalance when OpeningViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, OpeningViews);
                return (await db.OpeningBalances.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id) && (visible == null || visible.Contains(d.WarehouseId)))
                        .Select(d => new { d.Id, d.Number, d.AsOfDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"Начальные остатки {d.Number}", d.AsOfDate.ToString("dd.MM.yyyy"), $"opening-balances/{d.Id}"))
                    .ToList();
            }

            case TaggedEntity.CustomerInvoice when has(Permissions.SalesView) && has(Permissions.PriceView):
                return (await (from i in db.CustomerInvoices.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on i.CustomerId equals c.Id
                               where i.OrganizationId == org && ids.Contains(i.Id)
                               select new { i.Id, i.Number, i.InvoiceDate, c.Name }).ToListAsync(ct))
                    .Select(i => new TaggedObjectDto(entity, i.Id, $"Счёт {i.Number} от {i.InvoiceDate:dd.MM.yyyy}", i.Name, $"customer-invoices/{i.Id}")).ToList();
            case TaggedEntity.ExpenseReport when has(Permissions.PriceView):
                return (await db.ExpenseReports.AsNoTracking().Where(r => r.OrganizationId == org && ids.Contains(r.Id))
                        .Select(r => new { r.Id, r.Number, r.ReportDate }).ToListAsync(ct))
                    .Select(r => new TaggedObjectDto(entity, r.Id, $"Авансовый отчёт {r.Number}", r.ReportDate.ToString("dd.MM.yyyy"), $"expense-reports/{r.Id}"))
                    .ToList();
            default:
                return [];
        }
    }

}
