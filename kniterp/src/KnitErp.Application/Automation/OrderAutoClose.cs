using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Automation;

/// <summary>
/// D150 (А6): автозакрытие заказов. Подтверждённый заказ покупателя, по каждой строке отгруженный полностью (отгружено минус
/// возвращено не меньше заказанного; комплект — комплектами, услуга — актом, как в списке заказов) и оплаченный (разнесено не меньше
/// суммы заказа), закрывается сам; так же заказ поставщику — поступлением и оплатой. Не закрывается, если по заказу есть черновики
/// складских документов (открытый возврат, недопроведённая отгрузка) — человек ещё работает. Закрытие — статусом, как кнопкой
/// «Закрыть», с записью в журнал «автоматически» без пользователя; вернуть в работу можно кнопкой «Вернуть в работу».
/// Каждый заказ проверяется заново под блокировкой контрагента (как разноска) — гонки с разноской и отгрузкой нет.
/// </summary>
public static class OrderAutoClose
{
    public const int Batch = 200;

    /// <summary>Пройти по организациям; возвращает число закрытых заказов.</summary>
    public static async Task<int> RunAsync(IKnitErpDbContext db, IClock clock, CancellationToken ct = default)
    {
        var closed = 0;
        var orgs = await db.Organizations.AsNoTracking().Select(o => o.Id).ToListAsync(ct);
        foreach (var org in orgs)
        {
            var settings = await AutomationSettingsService.ForAsync(db, org, ct);
            if (settings.AutoCloseSalesOrders)
            {
                closed += await SalesAsync(db, clock, org, ct);
            }

            if (settings.AutoClosePurchaseOrders)
            {
                closed += await PurchasesAsync(db, clock, org, ct);
            }
        }

        return closed;
    }

    public static async Task<int> SalesAsync(IKnitErpDbContext db, IClock clock, long org, CancellationToken ct)
    {
        // Кандидаты: подтверждённые, без черновиков складских документов, оплаченные полностью.
        var candidates = await db.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == SalesOrderStatus.Confirmed
                        && !db.StockDocuments.Any(d => d.OrganizationId == org && d.SalesOrderId == o.Id && d.Status == StockDocumentStatus.Draft)
                        && PaidByOrder.Sales(db, org).Where(a => a.OrderId == o.Id).Sum(a => (decimal?)a.Amount) >= o.Lines.Sum(l => l.Amount))
            .OrderBy(o => o.Id).Select(o => new { o.Id, o.CustomerId }).Take(Batch).ToListAsync(ct);
        var closed = 0;
        foreach (var c in candidates)
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.allocation.customer.{org}.{c.CustomerId}", ct);
            var order = await db.SalesOrders.Include(o => o.Lines).SingleAsync(o => o.Id == c.Id && o.OrganizationId == org, ct);
            await db.ReloadAsync(order, ct);
            var moved = Moved(await db.StockDocuments.AsNoTracking()
                .Where(d => d.OrganizationId == org && d.SalesOrderId == c.Id && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft))
                .SelectMany(d => d.Lines.Select(l => new MovedLine(d.Id, d.Kind, d.Status, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity)))
                .ToListAsync(ct), StockOperationKind.Shipment);
            var paid = (await PaidByOrder.SalesAsync(db, org, [c.Id], ct)).GetValueOrDefault(c.Id);
            if (order.Status != SalesOrderStatus.Confirmed || moved is null || paid < order.Total
                || !order.Lines.All(l => moved.GetValueOrDefault(l.ItemId) >= l.Quantity))
            {
                db.DiscardChanges();
                continue;
            }

            order.Close();
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, null, AuditActions.OrderAutoClosed, nameof(SalesOrder), order.Id.ToString(),
                "Подтверждён", "Закрыт", $"{order.Number}: автоматически — отгружен и оплачен полностью", null));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            closed++;
        }

        return closed;
    }

    public static async Task<int> PurchasesAsync(IKnitErpDbContext db, IClock clock, long org, CancellationToken ct)
    {
        var candidates = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == PurchaseOrderStatus.Confirmed
                        && !db.StockDocuments.Any(d => d.OrganizationId == org && d.PurchaseOrderId == o.Id && d.Status == StockDocumentStatus.Draft)
                        && PaidByOrder.Purchases(db, org).Where(a => a.OrderId == o.Id).Sum(a => (decimal?)a.Amount) >= o.Lines.Sum(l => l.Amount))
            .OrderBy(o => o.Id).Select(o => new { o.Id, o.SupplierId }).Take(Batch).ToListAsync(ct);
        var closed = 0;
        foreach (var c in candidates)
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.allocation.supplier.{org}.{c.SupplierId}", ct);
            var order = await db.PurchaseOrders.Include(o => o.Lines).SingleAsync(o => o.Id == c.Id && o.OrganizationId == org, ct);
            await db.ReloadAsync(order, ct);
            var moved = Moved(await db.StockDocuments.AsNoTracking()
                .Where(d => d.OrganizationId == org && d.PurchaseOrderId == c.Id && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft))
                .SelectMany(d => d.Lines.Select(l => new MovedLine(d.Id, d.Kind, d.Status, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity)))
                .ToListAsync(ct), StockOperationKind.Receipt);
            var paid = (await PaidByOrder.PurchasesAsync(db, org, [c.Id], ct)).GetValueOrDefault(c.Id);
            if (order.Status != PurchaseOrderStatus.Confirmed || moved is null || paid < order.Total
                || !order.Lines.All(l => moved.GetValueOrDefault(l.ItemId) >= l.Quantity))
            {
                db.DiscardChanges();
                continue;
            }

            order.Close();
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, null, AuditActions.OrderAutoClosed, nameof(PurchaseOrder), order.Id.ToString(),
                "Подтверждён", "Закрыт", $"{order.Number}: автоматически — поступил и оплачен полностью", null));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            closed++;
        }

        return closed;
    }

    private sealed record MovedLine(long DocumentId, StockOperationKind Kind, StockDocumentStatus Status, long ItemId, decimal Quantity, long? BundleItemId,
        decimal? BundleQuantity);

    /// <summary>Чистое движение по позиции заказа (в терминах заказа); null — есть черновик документа (заказ не закрывается).</summary>
    private static Dictionary<long, decimal>? Moved(IReadOnlyList<MovedLine> lines, StockOperationKind outgoing)
    {
        if (lines.Any(l => l.Status == StockDocumentStatus.Draft))
        {
            return null;
        }

        return BundleLines.InOrderTerms(lines.Select(l => ((l.DocumentId, Kind: StockOperationKinds.OrderKind(l.Kind)), l.ItemId, l.Quantity, l.BundleItemId,
                l.BundleQuantity)))
            .GroupBy(x => x.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Key.Kind == outgoing ? x.Quantity : -x.Quantity));
    }
}
