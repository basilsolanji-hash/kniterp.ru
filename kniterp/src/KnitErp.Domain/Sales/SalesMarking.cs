using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Разобранный код маркировки: GTIN-14, серийный номер и код идентификации (КИ = 01 + GTIN + 21 + серийный).</summary>
public sealed record MarkingCodeParts(string Gtin, string Serial)
{
    public string Cis => "01" + Gtin + "21" + Serial;
}

/// <summary>
/// Разбор кода маркировки «Честный ЗНАК» из DataMatrix GS1 (D1108): «01» + GTIN-14 + «21» + серийный номер, затем через разделитель
/// GS (0x1D) — ключ и код проверки (91/92/93), они не хранятся. Сканер может передать префикс символики «]d2», разделитель — символом
/// GS или текстом «&lt;GS&gt;». Без разделителя серийный номер — 13 символов, если за ним идёт «91» или «93» (лёгкая промышленность,
/// допущение D1108); иначе весь остаток до 20 символов.
/// </summary>
public static class MarkingCodes
{
    public const char Gs = '\u001d';
    public const int SerialMaxLength = 20;
    public const int RawMaxLength = 300;
    private const int LightIndustrySerialLength = 13;

    public static MarkingCodeParts Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new BusinessRuleException("sales.marking.empty", "Отсканируйте код маркировки.");
        }

        if (text.Length > RawMaxLength)
        {
            throw new BusinessRuleException("sales.marking.too_long", "Код маркировки слишком длинный — отсканируйте DataMatrix ещё раз.");
        }

        foreach (var prefix in new[] { "]d2", "]C1", "]Q3" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        text = text.Replace("<GS>", Gs.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{GS}", Gs.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("\\x1d", Gs.ToString(), StringComparison.OrdinalIgnoreCase)
            .TrimStart(Gs);

        if (text.Length < 2 + 14 + 2 + 1 || !text.StartsWith("01", StringComparison.Ordinal) || !text.Substring(2, 14).All(char.IsAsciiDigit)
            || text.Substring(16, 2) != "21")
        {
            throw new BusinessRuleException("sales.marking.format",
                "Это не код маркировки «Честный ЗНАК»: нужен DataMatrix вида 01 + GTIN (14 цифр) + 21 + серийный номер.");
        }

        var gtin = text.Substring(2, 14);
        if (!Marking.CheckDigitValid(gtin))
        {
            throw new BusinessRuleException("sales.marking.gtin_check", $"GTIN {gtin} в коде маркировки неверный (контрольная цифра).");
        }

        var rest = text[18..];
        var gs = rest.IndexOf(Gs);
        string serial;
        if (gs >= 0)
        {
            serial = rest[..gs];
        }
        else if (rest.Length > LightIndustrySerialLength
                 && (rest[LightIndustrySerialLength..].StartsWith("91", StringComparison.Ordinal)
                     || rest[LightIndustrySerialLength..].StartsWith("93", StringComparison.Ordinal)))
        {
            serial = rest[..LightIndustrySerialLength];
        }
        else if (rest.Length <= SerialMaxLength)
        {
            serial = rest;
        }
        else
        {
            throw new BusinessRuleException("sales.marking.separator",
                "Не удалось выделить серийный номер: в коде нет разделителя GS. Проверьте настройку сканера (передача символа GS).");
        }

        if (serial.Length == 0 || serial.Length > SerialMaxLength || serial.Any(c => c < '!' || c > '~'))
        {
            throw new BusinessRuleException("sales.marking.serial", "Серийный номер в коде маркировки — от 1 до 20 латинских букв, цифр и знаков.");
        }

        return new MarkingCodeParts(gtin, serial);
    }
}

/// <summary>
/// Код маркировки в строке складского документа (D1108). В отгрузке код «продан» (IsSold), когда отгрузка проведена: в SQL Server
/// уникален среди проданных кодов организации (фильтрованный UNIQUE) — один код не уходит дважды. Возврат по заказу с этим кодом
/// или сторно отгрузки освобождают его (ReleasedAtUtc); сторно возврата снова делает проданным. В возврате коды только сверяются.
/// </summary>
public sealed class SalesMarkingCode
{
    private SalesMarkingCode()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long StockDocumentId { get; private set; }
    public long ItemId { get; private set; }
    public string Gtin { get; private set; } = string.Empty;
    public string Serial { get; private set; } = string.Empty;
    public bool IsSold { get; private set; }
    public DateTime? SoldAtUtc { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }

    /// <summary>Документ, освободивший код: возврат от покупателя или сама отгрузка при сторно.</summary>
    public long? ReleasedByDocumentId { get; private set; }

    public long ScannedByUserId { get; private set; }
    public DateTime ScannedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public string Cis => "01" + Gtin + "21" + Serial;

    public static SalesMarkingCode Create(long organizationId, long stockDocumentId, long itemId, MarkingCodeParts parts, long userId, DateTime nowUtc) =>
        new()
        {
            OrganizationId = organizationId,
            StockDocumentId = stockDocumentId,
            ItemId = itemId,
            Gtin = parts.Gtin,
            Serial = parts.Serial,
            ScannedByUserId = userId,
            ScannedAtUtc = nowUtc,
        };

    public void MarkSold(DateTime nowUtc)
    {
        IsSold = true;
        SoldAtUtc = nowUtc;
        ReleasedAtUtc = null;
        ReleasedByDocumentId = null;
    }

    public void Release(long documentId, DateTime nowUtc)
    {
        IsSold = false;
        ReleasedAtUtc = nowUtc;
        ReleasedByDocumentId = documentId;
    }
}
