using KnitErp.Application.Access;
using KnitErp.Application.Common;

namespace KnitErp.Application.Crm;

/// <summary>
/// D607, D683: событие продаж «заказ подтверждён» через точку подключения базы (<see cref="ISalesOrderEvents"/>, D117) — база не вызывает
/// CRM напрямую. Заказ из открытой сделки переводит её в «Выиграна» в той же транзакции подтверждения.
/// </summary>
public sealed class CrmSalesOrderEvents(IClock clock, ICurrentUser currentUser) : ISalesOrderEvents
{
    public Task OnConfirmedAsync(IKnitErpDbContext db, AccessContext ctx, long salesOrderId, CancellationToken ct) =>
        CrmDealService.OnOrderConfirmedAsync(db, ctx, salesOrderId, clock.UtcNow, currentUser.CorrelationId, ct);
}
