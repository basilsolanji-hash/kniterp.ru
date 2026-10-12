using System.Text;
using System.Text.RegularExpressions;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>
/// Шаблон письма CRM (D674): тема и текст с переменными {{Имя}}, {{Компания}}, {{Менеджер}}, {{Организация}}, {{Сделка}}, {{Сумма}}.
/// Подставляются при выборе в письме клиенту и для каждого получателя рассылки. Справочник — архивируется, не удаляется.
/// </summary>
public sealed partial class CrmEmailTemplate
{
    public const int NameMaxLength = 100;
    public const int SubjectMaxLength = CrmMailing.SubjectMaxLength;
    public const int BodyMaxLength = CrmMailing.TextMaxLength;

    /// <summary>Переменные шаблона и что в них подставляется — для подсказки в интерфейсе.</summary>
    public static readonly IReadOnlyList<(string Name, string Meaning)> Variables =
    [
        ("Имя", "имя контакта"),
        ("Компания", "компания клиента"),
        ("Менеджер", "ответственный менеджер"),
        ("Организация", "ваша организация"),
        ("Сделка", "название сделки"),
        ("Сумма", "сумма сделки"),
    ];

    private CrmEmailTemplate()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public bool IsArchived { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmEmailTemplate Create(long organizationId, string? name, string? subject, string? body, long userId, DateTime nowUtc)
    {
        var t = new CrmEmailTemplate { OrganizationId = organizationId, CreatedByUserId = userId, CreatedAtUtc = nowUtc };
        t.Update(name, subject, body);
        return t;
    }

    public void Update(string? name, string? subject, string? body)
    {
        Name = DomainText.Require(name, NameMaxLength, "Название");
        Subject = DomainText.Require(subject, SubjectMaxLength, "Тема");
        Body = DomainText.Require(body, BodyMaxLength, "Текст");
        if (Unknown(Subject + "\n" + Body) is { } unknown)
        {
            throw new BusinessRuleException("crm.template.variable",
                $"Неизвестная переменная «{unknown}». Можно: {string.Join(", ", Variables.Select(v => v.Name))} — в двойных фигурных скобках.");
        }
    }

    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    /// <summary>Подставляет значения; пустое значение — пустая строка (а не «{{Имя}}» в письме клиенту).</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string?> values) =>
        VariablePattern().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);

    public static bool HasVariables(string text) => VariablePattern().IsMatch(text);

    private static string? Unknown(string text) =>
        VariablePattern().Matches(text).Select(m => m.Groups[1].Value).FirstOrDefault(n => Variables.All(v => v.Name != n));

    [GeneratedRegex(@"\{\{\s*([\p{L}\p{N}_]+)\s*\}\}")]
    private static partial Regex VariablePattern();
}

/// <summary>
/// Простое HTML-письмо из текста (D674): абзацы, переносы строк и ссылки http(s) кликабельны, всё остальное экранировано — HTML из текста
/// пользователя в письмо не попадает. Внизу — серая подпись (отписка для рассылок).
/// </summary>
public static partial class CrmEmailHtml
{
    public static string FromText(string text, string? footer = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><body style=\"margin:0;padding:24px;background:#f5f6f8;font-family:Segoe UI,Arial,sans-serif;color:#1f2933\">");
        sb.Append("<div style=\"max-width:640px;margin:0 auto;background:#fff;border-radius:10px;padding:24px;font-size:15px;line-height:1.5\">");
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append("<p style=\"margin:0 0 12px\">").Append(Linkify(paragraph.Trim('\n'))).Append("</p>");
        }

        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(footer))
        {
            sb.Append("<p style=\"max-width:640px;margin:12px auto 0;font-size:12px;color:#6b7280\">").Append(Linkify(footer)).Append("</p>");
        }

        return sb.Append("</body></html>").ToString();
    }

    private static string Linkify(string text)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in UrlPattern().Matches(text))
        {
            sb.Append(Encode(text[last..m.Index]));
            var url = Encode(m.Value);
            sb.Append("<a href=\"").Append(url).Append("\" style=\"color:#0f766e\">").Append(url).Append("</a>");
            last = m.Index + m.Length;
        }

        return sb.Append(Encode(text[last..])).ToString();
    }

    private static string Encode(string s) => System.Net.WebUtility.HtmlEncode(s).Replace("\n", "<br>");

    [GeneratedRegex(@"https?://[^\s<>""']+[^\s<>""'.,;:!?)]")]
    private static partial Regex UrlPattern();
}

/// <summary>
/// Почтовый ящик организации для входящих писем в CRM (D676), например sales@: письма клиентов попадают в ленту контакта, сделки или
/// лида по адресу отправителя; незнакомый отправитель — новый лид (если включено). Только IMAP по TLS (порт 993) или STARTTLS (143),
/// адрес сервера — имя хоста (внутренние адреса сети запрещены при подключении). Пароль — шифрованием мастер-ключом, наружу не отдаётся.
/// </summary>
public sealed class CrmMailbox
{
    public const int HostMaxLength = 200;
    public const int LoginMaxLength = 200;
    public const int PasswordMaxLength = 300;
    public const int ErrorMaxLength = 500;

    private CrmMailbox()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public string Login { get; private set; } = string.Empty;
    public string? Password { get; private set; }
    public bool CreateLeads { get; private set; } = true;
    public long ConnectedByUserId { get; private set; }
    public DateTime ConnectedAtUtc { get; private set; }

    /// <summary>Курсор чтения: UIDVALIDITY папки и последний обработанный UID — повторная проверка не задваивает письма.</summary>
    public long? UidValidity { get; private set; }

    public long? LastUid { get; private set; }
    public DateTime? LastCheckAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsConnected => Password is not null;

    public static CrmMailbox Create(long organizationId) => new() { OrganizationId = organizationId };

    public void Connect(string? host, int port, string? login, string? password, bool createLeads, long userId, DateTime nowUtc)
    {
        var h = DomainText.Require(host, HostMaxLength, "Сервер IMAP").ToLowerInvariant();
        if (!ValidHost(h))
        {
            throw new BusinessRuleException("crm.mailbox.host", "Сервер IMAP — имя хоста, например imap.example.org.");
        }

        if (port is not (993 or 143))
        {
            throw new BusinessRuleException("crm.mailbox.port", "Порт IMAP — 993 (TLS) или 143 (STARTTLS).");
        }

        var newLogin = DomainText.Require(login, LoginMaxLength, "Логин");
        if (h != Host || newLogin != Login)
        {
            (UidValidity, LastUid) = (null, null);
        }

        (Host, Port, Login) = (h, port, newLogin);
        Password = DomainText.Require(password, PasswordMaxLength, "Пароль");
        CreateLeads = createLeads;
        (ConnectedByUserId, ConnectedAtUtc, LastError) = (userId, nowUtc, null);
    }

    public void Disconnect() => (Password, LastError) = (null, null);

    public void Checked(long uidValidity, long? lastUid, DateTime nowUtc)
    {
        if (UidValidity != uidValidity)
        {
            (UidValidity, LastUid) = (uidValidity, null);
        }

        LastUid = lastUid ?? LastUid;
        (LastCheckAtUtc, LastError) = (nowUtc, null);
    }

    public void Failed(string error, DateTime nowUtc) =>
        (LastCheckAtUtc, LastError) = (nowUtc, error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error);

    /// <summary>Имя хоста: буквы, цифры, точки и дефисы, хотя бы одна точка, не IP-адрес.</summary>
    public static bool ValidHost(string host) =>
        host.Length is > 3 and <= HostMaxLength && host.Contains('.') && !System.Net.IPAddress.TryParse(host, out _)
        && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && !host.StartsWith('.') && !host.EndsWith('.') && !host.Contains("..");
}
