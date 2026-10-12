using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed partial class SalesService
{
    /// <summary>
    /// D1106: пересчитать скидки подтверждённого заказа по действующим правилам — явным действием с причиной (право «Продажи: скидки
    /// и акции»). Только пока по заказу нет отгрузок (черновиков и проведённых) и действующих счетов: их суммы уже посчитаны по старым ценам.
    /// Возвращает число строк, у которых изменилась скидка.
    /// </summary>
    public async Task<int> ReapplyDiscountsAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesDiscountEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var order = await LoadOrderAsync(ctx, id, ct);
        order.EnsureVersion(order.RowVersion, rowVersion);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > SalesOrder.OverrideReasonMaxLength)
        {
            throw new BusinessRuleException("sales.discount.reason", "Укажите причину пересчёта скидок (до 500 знаков).");
        }

        var why = reason.Trim();
        if (await db.StockDocuments.AnyAsync(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == id
                && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft), ct)
            || await db.CustomerInvoices.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.SalesOrderId == id && i.Status == CustomerInvoiceStatus.Issued, ct))
        {
            throw new BusinessRuleException("sales.discount.reapply_documents",
                "По заказу уже есть отгрузки или выставленный счёт — скидки не пересчитываются. Отмените их или оформите новый заказ.");
        }

        var discounts = await SalesDiscounts.ComputeAsync(db, ctx.OrganizationId, order, ct);
        var before = order.Total;
        var changed = order.Lines.Count(l => discounts.TryGetValue(l.ItemId, out var d) && d.Total != l.DiscountPercent);
        order.ReapplyDiscountsConfirmed(discounts);
        Audit(ctx, AuditActions.SalesDiscountsReapplied, nameof(SalesOrder), id, $"{before:0.00}", $"{order.Total:0.00}", $"{order.Number}: {why}");
        await db.SaveOrConflictAsync(ct);
        return changed;
    }
}
