using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Printing;

/// <summary>
/// Товарная накладная ТОРГ-12 и транспортная накладная (D1141) по проведённой отгрузке: строки, суммы и стороны — как у УПД (D68),
/// плюс масса нетто (вес позиции из карточки × количество) и адрес доставки из заказа. Места и масса брутто, перевозчик и транспорт
/// заполняются от руки, пока их не хранит документ отгрузки. Warnings — чего не хватает; на бумагу не выводятся.
/// </summary>
public sealed record ShipmentFormDto(
    UpdPrintDto Upd, IReadOnlyList<decimal?> LineNetKg, decimal? TotalNetKg, string? DeliveryAddress, string OrderNumber, DateOnly OrderDate,
    IReadOnlyList<string> Warnings);

public sealed partial class PrintService
{
    public async Task<ShipmentFormDto> ShipmentFormAsync(long stockDocumentId, CancellationToken ct = default)
    {
        var upd = await UpdAsync(stockDocumentId, ct);
        var ctx = await DemandAsync(ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
            .SingleAsync(d => d.Id == stockDocumentId && d.OrganizationId == ctx.OrganizationId, ct);
        var order = await db.SalesOrders.AsNoTracking().Where(o => o.Id == doc.SalesOrderId)
            .Select(o => new { o.Number, o.OrderDate, o.DeliveryAddress }).SingleAsync(ct);
        var ids = doc.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).Select(i => new { i.Code, i.WeightKg }).ToListAsync(ct);
        var weight = items.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.First().WeightKg);

        // Строки УПД отсортированы по коду позиции — масса считается по той же строке.
        var net = upd.Lines.Select(l => weight.GetValueOrDefault(l.Code) is { } kg ? (decimal?)Math.Round(kg * l.Quantity, 3) : null).ToList();
        var warnings = upd.Warnings.Where(w => !w.Contains("строке 5", StringComparison.Ordinal) && !w.Contains("строка 5", StringComparison.Ordinal)).ToList();
        if (net.Any(n => n is null))
        {
            warnings.Add("Не у всех позиций указан вес — масса нетто посчитана не полностью. Заполните «Вес, кг» в карточке номенклатуры.");
        }

        return new ShipmentFormDto(upd, net, net.All(n => n is null) ? null : net.Sum(n => n ?? 0), order.DeliveryAddress, order.Number,
            order.OrderDate, warnings);
    }
}
