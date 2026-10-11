using KnitErp.Application.Access;

namespace KnitErp.Application.Common;

/// <summary>
/// D117: события заказа покупателя для модулей (CRM — «сделка выиграна», D600). Модуль регистрирует реализацию в DI
/// (<c>services.AddScoped&lt;ISalesOrderEvents, …&gt;()</c>); база вызывает её в той же транзакции, что и само действие, после
/// сохранения заказа. Ошибка модуля откатывает действие целиком — поэтому обработчик делает только своё и быстро.
/// Работает через переданный контекст базы, без параллельных запросов; права на свои данные проверяет сам.
/// </summary>
public interface ISalesOrderEvents
{
    /// <summary>Заказ подтверждён (статус «Подтверждён» уже сохранён).</summary>
    Task OnConfirmedAsync(IKnitErpDbContext db, AccessContext ctx, long salesOrderId, CancellationToken ct);
}
