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
    /// D1123 (сессия «Продажи»): письмо клиенту с вложениями (PDF документов), получатели через запятую.
    /// Реализация по умолчанию без вложений отправляет обычное письмо, с вложениями — не умеет (false).
    /// </summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default) =>
        message.Attachments.Count == 0 ? SendAsync(message.To, message.Subject, message.Text, ct) : Task.FromResult(false);
}

/// <summary>D1123 (сессия «Продажи»): вложение письма.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>D1123 (сессия «Продажи»): письмо с вложениями; To — адреса через запятую; SenderName — имя в поле «От».</summary>
public sealed record EmailMessage(string To, string Subject, string Text, IReadOnlyList<EmailAttachment> Attachments, string? SenderName = null);

/// <param name="Url">smtps://host:465 (TLS сразу) или smtp://host:587 (STARTTLS).</param>
/// <param name="PickupDirectory">Разработка и тесты: письма пишутся файлами в папку вместо отправки.</param>
public sealed record EmailOptions(string? Url, string? User, string? Password, string? From, string? PublicUrl, string? PickupDirectory = null);
