using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnitErp.Domain.Crm;

namespace KnitErp.Application.Crm;

/// <summary>
/// Разбор и проверка вебхуков мессенджеров (D670): MAX — update_type message_created; Telegram — Update.message (текст лички);
/// WhatsApp — entry[].changes[].value.messages[] (type text). Всё прочее (группы, стикеры, статусы доставки) пропускается.
/// </summary>
public static class CrmMessengerHooks
{
    public const int MaxBody = 65_536;

    public static CrmMessengerKind? Kind(string? key) => key?.ToLowerInvariant() switch
    {
        "max" => CrmMessengerKind.Max,
        "telegram" => CrmMessengerKind.Telegram,
        "whatsapp" => CrmMessengerKind.WhatsApp,
        _ => null,
    };

    /// <summary>Тело запроса не больше 64 КБ (заголовок Content-Length может отсутствовать); больше — null.</summary>
    public static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static bool SameSecret(string? a, string? b) =>
        a is not null && b is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// Подлинность запроса сверх секрета в адресе: Telegram — заголовок X-Telegram-Bot-Api-Secret-Token обязателен; MAX — X-Max-Bot-Api-Secret
    /// обязателен (D121); WhatsApp — X-Hub-Signature-256 = HMAC-SHA256(тело, секрет приложения). Сравнение — за постоянное время.
    /// </summary>
    public static bool Authentic(CrmMessengerKind kind, Func<string, string?> header, string secret, string? appSecret, byte[] body)
    {
        switch (kind)
        {
            case CrmMessengerKind.Telegram:
                return SameSecret(header("X-Telegram-Bot-Api-Secret-Token"), secret);
            case CrmMessengerKind.Max:
                // D121 (база): заголовок обязателен — бот подписывается с секретом, MAX присылает его в каждом запросе.
                return SameSecret(header("X-Max-Bot-Api-Secret"), secret);
            default:
                var signature = header("X-Hub-Signature-256") ?? "";
                if (appSecret is null || !signature.StartsWith("sha256=", StringComparison.Ordinal))
                {
                    return false;
                }

                var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();
                return SameSecret(signature.ToLowerInvariant(), expected);
        }
    }

    public static IReadOnlyList<CrmMessengerUpdate> Parse(CrmMessengerKind kind, JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return kind switch
        {
            CrmMessengerKind.Max => ParseMax(doc) is { } m ? [m] : [],
            CrmMessengerKind.Telegram => ParseTelegram(doc) is { } t ? [t] : [],
            _ => ParseWhatsApp(doc),
        };
    }

    /// <summary>MAX message_created: message.sender.user_id, name, username; message.body.text, mid.</summary>
    public static CrmMessengerUpdate? ParseMax(JsonElement doc)
    {
        if (!doc.TryGetProperty("update_type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "message_created"
            || !doc.TryGetProperty("message", out var m) || !m.TryGetProperty("sender", out var sender) || !m.TryGetProperty("body", out var body))
        {
            return null;
        }

        var userId = sender.TryGetProperty("user_id", out var u) ? u.ToString() : null;
        var text = Str(body, "text");
        return string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text) ? null
            : new CrmMessengerUpdate(userId, Str(sender, "name"), Str(sender, "username"), text, Str(body, "mid"));
    }

    /// <summary>
    /// Telegram Update: message.chat.id (только личные чаты — type private), message.from.first_name/last_name/username, message.text,
    /// message_id. Номер сообщения уникален только в чате — внешний номер «chat_id:message_id».
    /// </summary>
    public static CrmMessengerUpdate? ParseTelegram(JsonElement doc)
    {
        if (!doc.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("chat", out var chat)
            || Str(chat, "type") != "private" || !chat.TryGetProperty("id", out var chatId) || Str(m, "text") is not { Length: > 0 } text)
        {
            return null;
        }

        string? name = null, username = null;
        if (m.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object)
        {
            name = string.Join(' ', new[] { Str(from, "first_name"), Str(from, "last_name") }.Where(x => !string.IsNullOrWhiteSpace(x)));
            username = Str(from, "username") is { } un ? "@" + un : null;
        }

        var id = chatId.ToString();
        return new CrmMessengerUpdate(id, string.IsNullOrWhiteSpace(name) ? null : name, username, text,
            m.TryGetProperty("message_id", out var mid) ? $"{id}:{mid}" : null);
    }

    /// <summary>WhatsApp Cloud API: entry[].changes[].value.messages[] — from (номер), id, text.body; имя — value.contacts[].profile.name.</summary>
    public static IReadOnlyList<CrmMessengerUpdate> ParseWhatsApp(JsonElement doc)
    {
        var list = new List<CrmMessengerUpdate>();
        if (!doc.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in entries.EnumerateArray().Take(10))
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray().Take(10))
            {
                if (!change.TryGetProperty("value", out var value) || !value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var names = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in contacts.EnumerateArray())
                    {
                        if (Str(c, "wa_id") is { } wa && c.TryGetProperty("profile", out var profile) && Str(profile, "name") is { } n)
                        {
                            names[wa] = n;
                        }
                    }
                }

                foreach (var msg in messages.EnumerateArray().Take(20))
                {
                    if (Str(msg, "type") != "text" || Str(msg, "from") is not { Length: > 0 } from || !from.All(char.IsAsciiDigit)
                        || !msg.TryGetProperty("text", out var t) || Str(t, "body") is not { Length: > 0 } text)
                    {
                        continue;
                    }

                    list.Add(new CrmMessengerUpdate(from, names.GetValueOrDefault(from), null, text, Str(msg, "id"), "+" + from));
                }
            }
        }

        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
