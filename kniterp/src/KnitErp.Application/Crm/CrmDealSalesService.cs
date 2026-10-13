using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Продажи из сделки (D684): счёт по заказу сделки и документы клиенту (заказ, счёт, УПД) — вызовами модуля продаж базы, без копии его
/// логики. CRM проверяет только своё: сделка видна пользователю и заказ создан из неё; права на счёт и отправку — у продаж
/// («Продажи: заказы и оплаты», «Цены и суммы»). Отправка документов записывается в ленту сделки.
/// </summary>
public sealed class CrmDealSalesService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock,
    CustomerInvoiceService invoices, SalesOrderDocumentsService documents)
{
    /// <summary>Счёт по заказу сделки: действующий — его Id, нет — выставляется на сегодня.</summary>
    public async Task<long> InvoiceAsync(long dealId, long orderId, DateOnly? dueDate = null, CancellationToken ct = default)
    {
        await EnsureOrderOfDealAsync(dealId, orderId, ct);
        return await invoices.FindForOrderAsync(orderId, ct) ?? await invoices.CreateAsync(orderId, dueDate, null, ct);
    }

    /// <summary>Черновик письма клиенту с документом заказа сделки (адрес, тема и текст — по шаблонам продаж).</summary>
    public async Task<SalesMailDraftDto> DraftAsync(long dealId, long orderId, SalesMailDocument document, CancellationToken ct = default)
    {
        await EnsureOrderOfDealAsync(dealId, orderId, ct);
        return await documents.DraftAsync(orderId, document, ct: ct);
    }

    /// <summary>Отправить документы клиенту (очередь писем продаж, PDF при отправке); в ленту сделки — запись «Документы клиенту».</summary>
    public async Task SendAsync(long dealId, SalesMailRequest request, CancellationToken ct = default)
    {
        var ctx = await EnsureOrderOfDealAsync(dealId, request.OrderId, ct);
        await documents.SendAsync(request, ct);
        var names = string.Join(", ", request.Documents.Select(d => SalesMailMessage.DocumentName(d.Document)));
        var order = await db.SalesOrders.AsNoTracking().Where(o => o.Id == request.OrderId).Select(o => o.Number).SingleAsync(ct);
        db.CrmActivities.Add(CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Email, CrmTarget.Deal, dealId, $"Документы клиенту: {names} по заказу {order}",
            request.Subject, null, ctx.UserId, ctx.UserId, clock.UtcNow, done: true,
            result: request.To is { } to ? $"в очереди на {CrmText.MaskEmail(CrmText.NormalizeEmail(to) ?? to)}" : "в очереди", source: CrmActivitySource.Automation));
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmActivityCreated, nameof(CrmDeal), dealId.ToString(),
            null, $"документы клиенту: {names}, заказ {order}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    private async Task<AccessContext> EnsureOrderOfDealAsync(long dealId, long orderId, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        if (!await CrmAccess.Deals(db, ctx).AnyAsync(d => d.Id == dealId, ct)
            || !await db.CrmDealOrders.AnyAsync(l => l.OrganizationId == ctx.OrganizationId && l.DealId == dealId && l.SalesOrderId == orderId, ct))
        {
            throw new NotFoundException("Заказ покупателя");
        }

        return ctx;
    }
}
