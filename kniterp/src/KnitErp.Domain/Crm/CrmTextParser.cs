using System.Text.RegularExpressions;

namespace KnitErp.Domain.Crm;

/// <summary>Что удалось найти в тексте сообщения клиента. Все поля — подсказки: пользователь проверяет перед сохранением.</summary>
public sealed record CrmParsedText(
    string? Title, string? ContactName, string? Phone, string? Email, string? Messenger, string? CompanyName, string? Inn, string? Need);

/// <summary>
/// «Умная вставка» (D661, предложение А1): из текста сообщения в мессенджере или письма извлекаются телефон (E.164), почта, ИНН
/// (10 или 12 цифр с верной контрольной суммой), название компании с формой (ООО «…», ИП …), имя «@пользователя» Telegram,
/// количество с изделием («300 свитеров») для названия лида и подпись («С уважением, Иванова Анна»). Без внешних сервисов.
/// </summary>
public static partial class CrmTextParser
{
    public static CrmParsedText Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return new(null, null, null, null, null, null, null, null);
        }

        if (t.Length > CrmLead.NeedMaxLength)
        {
            t = t[..CrmLead.NeedMaxLength];
        }

        var email = EmailRe().Match(t) is { Success: true } em ? Safe(() => CrmText.NormalizeEmail(em.Value)) : null;
        string? inn = null;
        foreach (Match m in InnRe().Matches(t))
        {
            var v = m.Groups[1].Value;
            if (Organizations.RussianRequisites.IsValidLegalEntityInn(v) || Organizations.RussianRequisites.IsValidPersonInn(v))
            {
                inn = v;
                break;
            }
        }

        string? phone = null;
        foreach (Match m in PhoneRe().Matches(t))
        {
            var digits = new string(m.Value.Where(char.IsAsciiDigit).ToArray());
            if (inn is not null && digits == inn)
            {
                continue;
            }

            phone = Safe(() => CrmText.NormalizePhone(m.Value));
            if (phone is not null)
            {
                break;
            }
        }

        var company = CompanyRe().Match(t) is { Success: true } cm ? Clean(cm.Value) : null;
        var messenger = TelegramRe().Match(t) is { Success: true } tg && !(email?.Contains(tg.Value[1..], StringComparison.OrdinalIgnoreCase) ?? false)
            ? tg.Value
            : null;
        var name = SignatureRe().Match(t) is { Success: true } sm ? Clean(sm.Groups[1].Value) : null;
        var qty = QuantityRe().Match(t) is { Success: true } qm ? Clean(qm.Value) : null;
        var title = qty ?? FirstLine(t);
        return new(title, name, phone, email, messenger, company, inn, t);
    }

    private static string? FirstLine(string t)
    {
        var line = t.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? t;
        line = Clean(line);
        return line.Length <= 80 ? line : line[..80].TrimEnd() + "…";
    }

    private static string Clean(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',', '.', ';', ':');

    private static string? Safe(Func<string?> f)
    {
        try
        {
            return f();
        }
        catch (Common.BusinessRuleException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRe();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\s\-()]*){10,15}(?!\d)")]
    private static partial Regex PhoneRe();

    [GeneratedRegex(@"ИНН[:\s№]*(\d{10}|\d{12})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex InnRe();

    [GeneratedRegex(@"(?:ООО|ОАО|АО|ЗАО|ПАО|ИП|ТОО|ОДО|ЧП|МЧЖ)\s*(?:«[^»\n]{1,100}»|""[^""\n]{1,100}""|[А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]+(?:\s[А-ЯЁ][а-яё]+){0,2})")]
    private static partial Regex CompanyRe();

    [GeneratedRegex(@"(?<![\w@.])@[A-Za-z][A-Za-z0-9_]{4,31}\b")]
    private static partial Regex TelegramRe();

    [GeneratedRegex(@"(?:С уважением|Спасибо|Меня зовут|Контактное лицо)[,:\s]+([А-ЯЁ][а-яё]+(?:\s[А-ЯЁ][а-яё]+){0,2})")]
    private static partial Regex SignatureRe();

    [GeneratedRegex(@"\b\d{1,6}\s?(?:шт\.?\s)?(?:свитер|джемпер|кардиган|шап|шарф|плед|носк|носоч|вареж|перчат|худи|толстов|костюм|издел|единиц|пар)[а-яё]*", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRe();
}
