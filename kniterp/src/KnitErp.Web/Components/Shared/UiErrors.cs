using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using static KnitErp.Web.Localization.Text;

namespace KnitErp.Web.Components.Shared;

/// <summary>Ошибки, которые показываются пользователю как есть. Остальные — сбой, их видит только лог.</summary>
public static class UiErrors
{
    public static bool IsUserFacing(Exception ex) =>
        ex is BusinessRuleException or AccessDeniedException or NotFoundException or ConcurrencyConflictException;

    /// <summary>
    /// Текст ошибки на языке пользователя (D60): отказ в доступе, «не найдено» и конфликт правки переводятся;
    /// сообщения правил сервисов — по шаблонам перевода.
    /// </summary>
    public static string Message(Exception ex) => ex switch
    {
        AccessDeniedException denied => L("Недостаточно прав: {0}. Обратитесь к администратору.", L(Permissions.Describe(denied.PermissionCode))),
        NotFoundException notFound => L("{0}: не найдено.", L(notFound.Entity)),
        // D698: модуль указал объект и кто его изменил — «Сделка: изменил(а) Иванов 13.10 10:42. Обновите страницу и повторите.»
        ConcurrencyConflictException { ObjectName: { } what, ChangedBy: { } who } c =>
            L("{0}: изменил(а) {1}{2}. Обновите страницу и повторите.", L(what), who, c.ChangedAtLocal is { } at ? " " + at.ToString("dd.MM HH:mm") : ""),
        ConcurrencyConflictException => L("Данные изменены другим пользователем. Обновите страницу и повторите."),
        _ => KnitErp.Web.Localization.Text.Message(ex.Message),
    };
}
