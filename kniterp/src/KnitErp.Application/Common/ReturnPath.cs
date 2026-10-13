namespace KnitErp.Application.Common;

/// <summary>
/// D152: адрес возврата для кнопки «Закрыть» (параметр back=…) — только относительный путь внутри приложения: начинается с буквы
/// или цифры, без «//», схемы, кавычек и скобок (буквы, цифры и «/-_.?=&amp;%»), до 300 символов. Иначе — null: кнопка ведёт в
/// родительский раздел, а не на чужой сайт (открытое перенаправление).
/// </summary>
public static class ReturnPath
{
    public static string? Safe(string? back)
    {
        if (string.IsNullOrWhiteSpace(back))
        {
            return null;
        }

        var raw = back.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal) || raw.StartsWith('\\'))
        {
            return null; // адрес другого сайта без схемы
        }

        var b = raw.TrimStart('/');
        return b.Length > 0 && b.Length <= 300 && !b.Contains("//", StringComparison.Ordinal) && char.IsAsciiLetterOrDigit(b[0])
               && b.All(c => char.IsAsciiLetterOrDigit(c) || "/-_.?=&%".Contains(c))
            ? b
            : null;
    }
}
