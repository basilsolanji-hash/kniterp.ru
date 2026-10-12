using System.Net;
using System.Net.Sockets;
using KnitErp.Application.Crm;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace KnitErp.Infrastructure.Email;

/// <summary>
/// Входящая почта CRM по IMAP (D676, MailKit): порт 993 — TLS сразу, 143 — STARTTLS (без шифрования не подключаемся). Перед подключением
/// имя сервера разрешается в адреса — внутренние, служебные и локальные адреса запрещены (сервер не ходит по ссылке пользователя во внутреннюю
/// сеть, SSRF). Пароль в лог не пишется. Письма читаются без отметки «прочитано» (BODY.PEEK).
/// </summary>
public sealed class ImapMailboxClient(ILogger<ImapMailboxClient> logger) : ICrmMailboxClient
{
    public async Task TestAsync(CrmMailboxCredentials c, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(c, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task<CrmMailboxFetch> FetchAsync(CrmMailboxCredentials c, long? uidValidity, long? lastUid, int max, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(c, ct);
        try
        {
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            var validity = (long)inbox.UidValidity;
            IList<UniqueId> uids;
            if (uidValidity != validity || lastUid is null)
            {
                // Первое подключение (или папку пересоздали): берём письма за последние сутки, а не весь архив ящика.
                uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-1)), ct);
            }
            else
            {
                uids = await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)lastUid.Value + 1), UniqueId.MaxValue)), ct);
            }

            var list = new List<CrmInboundEmail>();
            foreach (var uid in uids.Where(u => lastUid is null || uidValidity != validity || u.Id > lastUid).OrderBy(u => u.Id).Take(max))
            {
                var message = await inbox.GetMessageAsync(uid, ct);
                if (IsAutomatic(message))
                {
                    list.Add(new CrmInboundEmail(uid.Id, null, null, null, null, null)); // курсор двигается, письмо пропускается (нет адреса)
                    continue;
                }

                var from = message.From.Mailboxes.FirstOrDefault();
                list.Add(new CrmInboundEmail(uid.Id, message.MessageId, from?.Address, string.IsNullOrWhiteSpace(from?.Name) ? null : from.Name, message.Subject,
                    message.TextBody ?? StripHtml(message.HtmlBody)));
            }

            await client.DisconnectAsync(true, ct);
            return new CrmMailboxFetch(validity, list);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or ImapCommandException or ServiceNotConnectedException)
        {
            logger.LogWarning(ex, "IMAP: чтение писем не удалось");
            throw new CrmMailboxException("Почтовый сервер не ответил — проверка повторится через 3 минуты.");
        }
    }

    private async Task<ImapClient> ConnectAsync(CrmMailboxCredentials c, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(c.Host, ct);
        }
        catch (SocketException)
        {
            throw new CrmMailboxException($"Сервер {c.Host} не найден — проверьте адрес.");
        }

        if (addresses.Length == 0 || addresses.Any(IsInternal))
        {
            throw new CrmMailboxException("Адрес сервера ведёт во внутреннюю сеть — укажите внешний сервер IMAP.");
        }

        var client = new ImapClient { Timeout = 20_000 };
        try
        {
            // Сокет открывается к проверенному адресу (между проверкой и подключением DNS не подменит адрес), сертификат сверяется с именем сервера.
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(addresses[0], c.Port, timeout.Token);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            await client.ConnectAsync(socket, c.Host, c.Port, c.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(c.Login, c.Password, ct);
            return client;
        }
        catch (AuthenticationException)
        {
            client.Dispose();
            throw new CrmMailboxException("Логин или пароль не подошли. Для Яндекса, Mail.ru и Gmail нужен пароль приложения.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or SslHandshakeException or ImapProtocolException or ImapCommandException or OperationCanceledException
                                       && !ct.IsCancellationRequested)
        {
            client.Dispose();
            logger.LogWarning(ex, "IMAP: подключение к серверу не удалось");
            throw new CrmMailboxException($"Не удалось подключиться к {c.Host}:{c.Port} по защищённому соединению.");
        }
    }

    /// <summary>Внутренние адреса: loopback, частные сети, link-local, CGNAT, служебные и IPv6 ULA.</summary>
    public static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
              || (b[0] == 100 && b[1] is >= 64 and <= 127)
            : (b[0] & 0xFE) == 0xFC;
    }

    private static bool IsAutomatic(MimeMessage m) =>
        m.Headers[HeaderId.AutoSubmitted] is { } auto && !auto.Equals("no", StringComparison.OrdinalIgnoreCase)
        || m.Headers["Precedence"] is "bulk" or "junk" or "list";

    private static string? StripHtml(string? html) =>
        html is null ? null : WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "<[^>]+>", " ")).Trim();
}
