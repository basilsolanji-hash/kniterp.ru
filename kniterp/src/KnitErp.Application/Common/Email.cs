namespace KnitErp.Application.Common;

/// <summary>
/// Отправка служебных писем (D81: ссылка «Забыли пароль»). Не настроена — функции, которым нужна почта, недоступны,
/// а интерфейс предлагает обратиться к администратору.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>
    /// Адрес системы для ссылок в письмах, из настроек сервера (а не из заголовка Host запроса — иначе подменённый
    /// заголовок отправил бы ссылку с токеном на чужой сайт).
    /// </summary>
    string? PublicUrl { get; }

    /// <summary>Отправляет письмо. Ошибку отправки не бросает наружу — пишет в лог и возвращает false.</summary>
    Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct = default);

    /// <summary>
    /// Письмо CRM (D674): текст и HTML-вариант, ответ — на почту менеджера (Reply-To), для рассылок — заголовок List-Unsubscribe.
    /// Отправитель без поддержки HTML отправляет текст.
    /// </summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default) => SendAsync(message.To, message.Subject, message.Text, ct);
}

/// <param name="ReplyTo">Куда пойдёт ответ клиента — почта менеджера (отправитель остаётся адресом системы, SPF/DKIM не ломаются).</param>
/// <param name="ReplyToName">Имя менеджера для Reply-To.</param>
/// <param name="UnsubscribeUrl">Ссылка отписки для заголовка List-Unsubscribe (RFC 2369) — почтовые сервисы показывают кнопку «Отписаться».</param>
public sealed record EmailMessage(string To, string Subject, string Text, string? Html = null, string? ReplyTo = null, string? ReplyToName = null,
    string? UnsubscribeUrl = null);

/// <param name="Url">smtps://host:465 (TLS сразу) или smtp://host:587 (STARTTLS).</param>
/// <param name="PickupDirectory">Разработка и тесты: письма пишутся файлами в папку вместо отправки.</param>
public sealed record EmailOptions(string? Url, string? User, string? Password, string? From, string? PublicUrl, string? PickupDirectory = null);
