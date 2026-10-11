namespace KnitErp.Domain.Common;

/// <summary>
/// Штрихкод Code 128 (ГОСТ 30743-2001 / ISO/IEC 15417) для ярлыков (D101): набор B для букв, переход на набор C для хвоста из
/// чётного числа цифр (короче и надёжнее читается). Результат — ширины модулей «штрих, пробел, …» вместе с контрольным символом
/// и стоп-символом; рисует их интерфейс. Внешние библиотеки не нужны.
/// </summary>
public static class Code128
{
    /// <summary>Ширины элементов символов 0–106 (106 — стоп, 7 элементов); сумма каждого символа — 11 модулей, стопа — 13.</summary>
    public static readonly IReadOnlyList<string> Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104;
    private const int StartC = 105;
    private const int CodeC = 99;
    private const int Stop = 106;

    /// <summary>Значения символов: старт, данные, контрольный, стоп.</summary>
    public static IReadOnlyList<int> Symbols(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 80 || text.Any(c => c < 32 || c > 126))
        {
            throw new BusinessRuleException("barcode.code128", "Code 128: печатные латинские символы и цифры, до 80 знаков.");
        }

        // Хвост из цифр чётной длины (от 4) — набором C, по две цифры в символе.
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[^(digits + 1)]))
        {
            digits++;
        }

        if (digits % 2 == 1)
        {
            digits--;
        }

        var head = digits >= 4 ? text[..^digits] : text;
        var tail = digits >= 4 ? text[^digits..] : "";
        var values = new List<int>();
        if (head.Length == 0)
        {
            values.Add(StartC);
        }
        else
        {
            values.Add(StartB);
            values.AddRange(head.Select(c => c - 32));
            if (tail.Length > 0)
            {
                values.Add(CodeC);
            }
        }

        for (var i = 0; i < tail.Length; i += 2)
        {
            values.Add((tail[i] - '0') * 10 + (tail[i + 1] - '0'));
        }

        var checksum = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            checksum += values[i] * i;
        }

        values.Add(checksum % 103);
        values.Add(Stop);
        return values;
    }

    /// <summary>Ширины элементов (в модулях) подряд: штрих, пробел, штрих… Без полей тишины — их добавляет отрисовка.</summary>
    public static IReadOnlyList<int> Widths(string text) =>
        Symbols(text).SelectMany(v => Patterns[v].Select(c => c - '0')).ToList();
}
