using KnitErp.Application.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace KnitErp.Infrastructure.Email;

/// <summary>
/// Письма через SMTP с входом (D81): тот же почтовый ящик, что у оповещений монитора (SMTP_URL, SMTP_USER, SMTP_PASSWORD,
/// SMTP_FROM в .env). Порт 25 у облачных серверов обычно закрыт, поэтому — 465 (TLS) или 587 (STARTTLS).
/// В разработке письма можно складывать в папку (Email:PickupDirectory).
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    /// <summary>Отправитель — SMTP_FROM, а если не задан — сам ящик (SMTP_USER), как у монитора.</summary>
    private string? From => string.IsNullOrWhiteSpace(options.From) ? options.User : options.From;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.PublicUrl) && !string.IsNullOrWhiteSpace(From)
        && (!string.IsNullOrWhiteSpace(options.PickupDirectory) || Uri.TryCreate(options.Url, UriKind.Absolute, out _));

    public string? PublicUrl => options.PublicUrl?.TrimEnd('/');

    public Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct = default) =>
        SendAsync(new EmailMessage(to, subject, text), ct);

    public async Task<bool> SendAsync(EmailMessage m, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var message = new MimeMessage();
            // D1123 (сессия «Продажи»): имя отправителя — организации, адрес — ящика системы; получатели через запятую.
            message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(m.SenderName) ? "knitERP" : m.SenderName, From!));
            foreach (var address in m.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(MailboxAddress.Parse(address));
            }

            if (m.ReplyTo is { } replyTo && MailboxAddress.TryParse(replyTo, out var reply))
            {
                reply.Name = m.ReplyToName;
                message.ReplyTo.Add(reply);
            }

            if (m.UnsubscribeUrl is { } unsubscribe)
            {
                message.Headers.Add("List-Unsubscribe", $"<{unsubscribe}>");
            }

            message.Subject = m.Subject;
            var builder = new BodyBuilder { TextBody = m.Text, HtmlBody = m.Html };
            foreach (var a in m.Attachments ?? [])
            {
                builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
            }
            message.Body = builder.ToMessageBody();

            if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
            {
                Directory.CreateDirectory(options.PickupDirectory);
                var path = Path.Combine(options.PickupDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
                await message.WriteToAsync(path, ct);
                return true;
            }

            var uri = new Uri(options.Url!);
            var security = uri.Scheme.Equals("smtps", StringComparison.OrdinalIgnoreCase)
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            using var client = new SmtpClient { Timeout = 15_000 };
            await client.ConnectAsync(uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? (security == SecureSocketOptions.SslOnConnect ? 465 : 587) : uri.Port,
                security, ct);
            if (!string.IsNullOrWhiteSpace(options.User))
            {
                await client.AuthenticateAsync(options.User, options.Password ?? string.Empty, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Адрес получателя в лог не пишем: это персональные данные.
            logger.LogWarning(ex, "Письмо «{Subject}» не отправлено", m.Subject);
            return false;
        }
    }
}
