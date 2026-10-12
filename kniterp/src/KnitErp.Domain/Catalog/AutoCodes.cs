using System.Globalization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Внутренние штрихкоды EAN-13 (D315): префикс 20–29 — диапазон GS1 для внутреннего использования (магазин, склад, свои этикетки);
/// такие коды не пересекаются с кодами производителей. Для маркированных товаров и площадок, требующих GTIN из Национального каталога,
/// нужен настоящий GTIN — его не генерируют, а получают в Честном ЗНАКе (GS1).
/// </summary>
public static class InternalEan13
{
    public const byte MinPrefix = 20;
    public const byte MaxPrefix = 29;
    public const long MaxNumber = 9_999_999_999;

    /// <summary>Префикс (2 цифры) + номер (10 цифр) + контрольная цифра.</summary>
    public static string Build(byte prefix, long number)
    {
        if (prefix is < MinPrefix or > MaxPrefix || number is < 1 or > MaxNumber)
        {
            throw new BusinessRuleException("catalog.barcode.range", "Номера внутренних штрихкодов закончились: смените префикс в настройках каталога.");
        }

        var body = prefix.ToString(CultureInfo.InvariantCulture) + number.ToString("D10", CultureInfo.InvariantCulture);
        return body + CheckDigit(body);
    }

    /// <summary>Контрольная цифра GTIN по первым цифрам: веса 3 и 1 справа налево.</summary>
    public static char CheckDigit(string body)
    {
        var sum = 0;
        for (int i = body.Length - 1, w = 3; i >= 0; i--, w = 4 - w)
        {
            sum += (body[i] - '0') * w;
        }

        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>Код из внутреннего диапазона 20–29 (такой не годится как GTIN для маркировки).</summary>
    public static bool IsInternal(string code) => code.Length == 13 && code[0] == '2';
}

/// <summary>
/// Шаблон артикула (D315): {N} — номер по счётчику, {CODE} — код позиции; остальное — как написано, например «KN-{N}» → «KN-00042».
/// </summary>
public static class ArticleTemplate
{
    public const int MaxLength = 30;
    public const string Number = "{N}";
    public const string Code = "{CODE}";

    public static string Validate(string? template)
    {
        var t = (template ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxLength || (!t.Contains(Number, StringComparison.Ordinal) && !t.Contains(Code, StringComparison.Ordinal))
                                         || t.Any(char.IsControl))
        {
            throw new BusinessRuleException("catalog.settings.article_template",
                "Шаблон артикула — до 30 знаков и содержит {N} (номер) или {CODE} (код позиции), например «KN-{N}».");
        }

        return t;
    }

    public static bool UsesNumber(string template) => template.Contains(Number, StringComparison.Ordinal);

    public static string Apply(string template, string itemCode, int number, byte digits)
    {
        var article = template.Replace(Code, itemCode, StringComparison.Ordinal)
            .Replace(Number, number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'), StringComparison.Ordinal);
        if (article.Length > ItemDetailRules.ArticleMaxLength)
        {
            throw new BusinessRuleException("catalog.article.too_long", "Артикул по шаблону длиннее 64 знаков — сократите шаблон в настройках каталога.");
        }

        return article;
    }
}

/// <summary>
/// Рисунок штрихкода EAN-13 (D315): 95 модулей — краевые 101, шесть цифр левой половины в наборах L/G по первой цифре, разделитель 01010,
/// шесть цифр правой половины в наборе R, краевые 101. Строка из «1» (штрих) и «0» (пробел).
/// </summary>
public static class Ean13Pattern
{
    private static readonly string[] L = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] Parity = ["LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"];

    public static bool CanEncode(string? code) =>
        code is { Length: 13 } c && c.All(char.IsAsciiDigit) && InternalEan13.CheckDigit(c[..12]) == c[12];

    public static string Modules(string code)
    {
        if (!CanEncode(code))
        {
            throw new ArgumentException("Нужен EAN-13: 13 цифр с верной контрольной цифрой.", nameof(code));
        }

        var parity = Parity[code[0] - '0'];
        var sb = new System.Text.StringBuilder(95).Append("101");
        for (var i = 1; i <= 6; i++)
        {
            var l = L[code[i] - '0'];
            sb.Append(parity[i - 1] == 'L' ? l : new string(Complement(l).Reverse().ToArray()));
        }

        sb.Append("01010");
        for (var i = 7; i <= 12; i++)
        {
            sb.Append(Complement(L[code[i] - '0']));
        }

        return sb.Append("101").ToString();
    }

    private static string Complement(string bits) => new(bits.Select(b => b == '1' ? '0' : '1').ToArray());
}

/// <summary>
/// Проверка, что штрихкод прочитается сканером (D316): ширина модуля (самого тонкого штриха) на этикетке. Для термопринтера 203 dpi модуль
/// должен быть не тоньше 2 точек ≈ 0,25 мм; EAN-13 со своим рисунком надёжно читается и при ≈ 0,2 мм (проверено декодером на 203 и 300 dpi).
/// Тоньше — штрихи сливаются: длинный Code 128 на маленькой этикетке не читается.
/// </summary>
public static class BarcodeFit
{
    public const decimal MinModuleCode128Mm = 0.25m;
    public const decimal MinModuleEan13Mm = 0.19m;

    /// <summary>Модулей вместе с полями тишины, как их рисует этикетка: EAN-13 — 95 + 11 + 7; Code 128 — штрихи + 2 × 10.</summary>
    public static int Modules(string code) =>
        Ean13Pattern.CanEncode(code) ? 113 : KnitErp.Domain.Common.Code128.Widths(code).Sum() + 20;

    /// <summary>Ширина модуля в мм на доступной ширине; EAN-13 ограничен ещё и высотой (рисуется с сохранением пропорций 113 × 46).</summary>
    public static decimal ModuleMm(string code, decimal widthMm, decimal barcodeHeightMm)
    {
        var modules = Modules(code);
        var byWidth = widthMm / modules;
        return Ean13Pattern.CanEncode(code) ? Math.Min(byWidth, barcodeHeightMm / 46m) : byWidth;
    }

    public static bool Readable(string code, decimal widthMm, decimal barcodeHeightMm) =>
        ModuleMm(code, widthMm, barcodeHeightMm) >= (Ean13Pattern.CanEncode(code) ? MinModuleEan13Mm : MinModuleCode128Mm);

    /// <summary>Сколько мм ширины нужно, чтобы код прочитался.</summary>
    public static decimal RequiredWidthMm(string code) =>
        decimal.Round(Modules(code) * (Ean13Pattern.CanEncode(code) ? MinModuleEan13Mm : MinModuleCode128Mm), 1, MidpointRounding.ToPositiveInfinity);
}

/// <summary>
/// D325: ссылка печати этикеток со строками документа — «12x5,13x3»: позиция и сколько этикеток. Штучное количество — столько этикеток,
/// дробное (кг, м) или нулевое — одна; повторы позиции складываются. Без «x» — одна этикетка (старые ссылки «ids=1,2»).
/// </summary>
public static class LabelLink
{
    public const int MaxPerItem = 1000;

    public static string Build(IEnumerable<(long ItemId, decimal Quantity)> lines, int maxItems)
    {
        var counts = new Dictionary<long, int>();
        foreach (var (id, qty) in lines)
        {
            var n = qty > 0 && qty == decimal.Truncate(qty) && qty <= MaxPerItem ? (int)qty : qty > MaxPerItem ? MaxPerItem : 1;
            counts[id] = Math.Min(MaxPerItem, (counts.TryGetValue(id, out var was) ? was : 0) + n);
        }

        return string.Join(",", counts.Take(maxItems).Select(c => c.Value == 1
            ? c.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c.Key}x{c.Value}")));
    }

    public static IReadOnlyList<(long ItemId, int Count)> Parse(string? ids)
    {
        var counts = new Dictionary<long, int>();
        foreach (var part in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('x', 'X', '*');
            if (pieces.Length is < 1 or > 2 || !long.TryParse(pieces[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                continue;
            }

            var n = 1;
            if (pieces.Length == 2 && (!int.TryParse(pieces[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) || n <= 0))
            {
                continue;
            }

            counts[id] = Math.Min(MaxPerItem, (counts.TryGetValue(id, out var was) ? was : 0) + n);
        }

        return counts.Select(c => (c.Key, c.Value)).ToList();
    }
}
