using KnitErp.Application.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Printing;

/// <summary>
/// D354 (сессия 4, Р1): УПД по отгрузке и акту вместе — один документ со строками товаров и услуг. Правило одно для печати и журнала
/// выданных счетов-фактур: проведённый акт оказания услуг входит в УПД проведённой отгрузки того же заказа той же датой (первой
/// оформленной, если их несколько); иначе акт — отдельный УПД со своим номером. Сторнированная отгрузка акты к себе не берёт.
/// </summary>
internal static class UpdMerge
{
    public sealed record Doc(
        long Id, string Number, StockOperationKind Kind, long OrderId, DateOnly Date, StockDocumentStatus Status,
        IReadOnlyList<(long ItemId, decimal Quantity)> OrderLines);

    /// <summary>Акт → отгрузка, в чей УПД он входит.</summary>
    public static Dictionary<long, long> Hosts(IEnumerable<Doc> docs)
    {
        var posted = docs.Where(d => d.Status == StockDocumentStatus.Posted).ToList();
        var hosts = posted.Where(d => d.Kind == StockOperationKind.Shipment).GroupBy(d => (d.OrderId, d.Date))
            .ToDictionary(g => g.Key, g => g.Min(d => d.Id));
        return posted.Where(d => d.Kind == StockOperationKind.ServiceAct && hosts.ContainsKey((d.OrderId, d.Date)))
            .ToDictionary(d => d.Id, d => hosts[(d.OrderId, d.Date)]);
    }

    /// <summary>Проведённые отгрузки и акты заказов orderIds датами dates — кандидаты для <see cref="Hosts"/>; строки — в терминах заказа.</summary>
    public static async Task<IReadOnlyList<Doc>> CandidatesAsync(
        IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long> orderIds, IReadOnlyCollection<DateOnly> dates, CancellationToken ct)
    {
        if (orderIds.Count == 0)
        {
            return [];
        }

        var rows = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.SalesOrderId != null && orderIds.Contains(d.SalesOrderId.Value)
                        && dates.Contains(d.DocumentDate) && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.ServiceAct))
            .Select(d => new { d.Id, d.Number, d.Kind, OrderId = d.SalesOrderId!.Value, d.DocumentDate, d.Status,
                Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }).ToList() })
            .ToListAsync(ct);
        return rows.Select(r => new Doc(r.Id, r.Number, r.Kind, r.OrderId, r.DocumentDate, r.Status,
                BundleLines.InOrderTerms(r.Lines.Select(l => (0, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity)))
                    .Select(l => (l.ItemId, l.Quantity)).ToList()))
            .ToList();
    }
}
