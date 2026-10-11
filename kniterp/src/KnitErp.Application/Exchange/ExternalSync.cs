using KnitErp.Domain.Catalog;

namespace KnitErp.Application.Exchange;

// D310 (сессия 4): места под следующие адаптеры обмена. Только контракты — реализаций пока нет; см. docs/moysklad-mapping.md, «Следующие шаги».

/// <summary>Изменение объекта во внешней системе (вебхук или опрос): что и где поменялось. Тело вебхука не доверяется — объект перечитывается по API.</summary>
public sealed record ExternalChange(ExternalSystem System, ExternalObjectType ObjectType, string ExternalId, string Action, DateTime ReceivedAtUtc);

/// <summary>Результат выгрузки одного объекта во внешнюю систему.</summary>
public sealed record ExternalPushResult(long LocalId, string? ExternalId, bool Success, string? Error);

/// <summary>
/// Источник каталога во внешней системе (МойСклад, 1С, маркетплейсы, сайт): постраничное чтение карточек. Реализация на МойСклад —
/// <c>IMoySkladClient</c> + движок <c>MoySkladImportService</c>; для других площадок — по этому контракту с той же таблицей external_links.
/// </summary>
public interface IExternalCatalogSource
{
    ExternalSystem System { get; }

    /// <summary>Проверить доступ и вернуть название аккаунта во внешней системе.</summary>
    Task<string?> CheckAsync(string token, CancellationToken ct);
}

/// <summary>
/// Двусторонний обмен (следующее ТЗ): выгрузка изменённых в knitERP позиций и приём изменений из внешней системы.
/// Сопоставление — по external_links (внешний id, версия, отпечаток значений), конфликт решается по правилу «кто изменил позже»
/// с замечанием в отчёте.
/// </summary>
public interface IExternalSync
{
    ExternalSystem System { get; }

    /// <summary>Выгрузить позиции организации, изменённые после прошлой синхронизации.</summary>
    Task<IReadOnlyList<ExternalPushResult>> PushChangedAsync(long organizationId, CancellationToken ct);

    /// <summary>Принять изменение из внешней системы (вебхук): перечитать объект и обновить связанную позицию.</summary>
    Task ApplyChangeAsync(long organizationId, ExternalChange change, CancellationToken ct);
}
