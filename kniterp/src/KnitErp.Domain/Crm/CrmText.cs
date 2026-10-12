using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>
/// Нормализация и маскирование контактных данных CRM (D605). Телефон приводится к E.164 («+79000000001»): по нему, по почте
/// в нижнем регистре и по ИНН ищутся дубликаты. В журнал аудита телефоны и почты пишутся только маской (D612).
/// </summary>
public static class CrmText
{
    public const int PhoneMaxLength = 40;
    public const int EmailMaxLength = 254;
    public const int MessengerMaxLength = 100;

    /// <summary>
    /// Телефон в E.164 или null, если поле пустое. Российский номер можно писать с 8 или без кода страны: «8 900 000-00-01»,
    /// «900 000 00 01» → «+79000000001». Другие страны — с «+» и кодом страны.
    /// </summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var text = phone.Trim();
        var digits = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                digits.Append(ch);
            }
            else if (!(ch is ' ' or '-' or '(' or ')' or '.' or '+' || char.IsWhiteSpace(ch)))
            {
                throw Invalid();
            }
        }

        var d = digits.ToString();
        if (!text.StartsWith('+'))
        {
            if (d.Length == 11 && d[0] == '8')
            {
                d = "7" + d[1..];
            }
            else if (d.Length == 10)
            {
                d = "7" + d;
            }
        }

        return d.Length is >= 11 and <= 15 && d[0] != '0' ? "+" + d : throw Invalid();

        static BusinessRuleException Invalid() =>
            new("crm.phone", "Телефон указан неверно: нужен номер с кодом страны, например +7 900 000-00-01.");
    }

    /// <summary>Почта в нижнем регистре или null; простая проверка «имя@домен.зона».</summary>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var e = email.Trim().ToLowerInvariant();
        var at = e.IndexOf('@');
        if (e.Length > EmailMaxLength || at <= 0 || at != e.LastIndexOf('@') || e.IndexOf('.', at) < at + 2 || e.EndsWith('.') || e.Any(char.IsWhiteSpace))
        {
            throw new BusinessRuleException("crm.email", "Почта указана неверно: нужен адрес вида name@example.org.");
        }

        return e;
    }

    /// <summary>ИНН: только цифры, 10 или 12 знаков; пусто — null. Контрольную сумму проверяет карточка контрагента при квалификации.</summary>
    public static string? NormalizeInn(string? inn)
    {
        if (string.IsNullOrWhiteSpace(inn))
        {
            return null;
        }

        var i = inn.Trim();
        return i.All(char.IsAsciiDigit) && i.Length is 10 or 12 ? i : throw new BusinessRuleException("crm.inn", "ИНН — 10 или 12 цифр.");
    }

    /// <summary>Телефон для показа: «+79000000001» → «+7 900 000-00-01»; другие страны — как хранится.</summary>
    public static string? FormatPhone(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var d = normalized.TrimStart('+');
        return d.Length == 11 && d[0] == '7' ? $"+7 {d[1..4]} {d[4..7]}-{d[7..9]}-{d[9..]}" : normalized;
    }

    /// <summary>Маска телефона для журнала: «+7 900 ***-**-01».</summary>
    public static string? MaskPhone(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var d = normalized.TrimStart('+');
        if (d.Length == 11 && d[0] == '7')
        {
            return $"+7 {d[1..4]} ***-**-{d[^2..]}";
        }

        return d.Length > 4 ? $"+{d[..2]} *** {d[^2..]}" : "***";
    }

    /// <summary>Маска почты для журнала: «t***@example.org».</summary>
    public static string? MaskEmail(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var at = normalized.IndexOf('@');
        return at <= 0 ? "***" : $"{normalized[0]}***{normalized[at..]}";
    }

    /// <summary>Обязательный текст для сценариев приложения (DomainText — внутренний в домене).</summary>
    public static string Require(string? value, int maxLength, string field) => DomainText.Require(value, maxLength, field);

    public static string? Optional(string? value, int maxLength, string field) => DomainText.Optional(value, maxLength, field);

    /// <summary>Мессенджер: Telegram, WhatsApp, VK — ссылка или имя пользователя, как ввёл менеджер.</summary>
    public static string? Messenger(string? value) => DomainText.Optional(value, MessengerMaxLength, "Мессенджер");
}
