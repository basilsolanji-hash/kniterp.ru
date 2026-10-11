namespace KnitErp.Domain.Access;

/// <summary>Язык интерфейса (D60): код, культура .NET и самоназвание языка для переключателя.</summary>
public sealed record UiLanguage(string Code, string Culture, string NativeName);

/// <summary>
/// Языки интерфейса: русский (основной), узбекский (латиница — официальная письменность), казахский (кириллица),
/// белорусский. Меняется только язык текстов; формат чисел и дат остаётся одинаковым для всех, чтобы ввод
/// количеств не зависел от языка пользователя.
/// </summary>
public static class UiLanguages
{
    public const string Default = "ru";

    public static readonly IReadOnlyList<UiLanguage> All =
    [
        new("ru", "ru-RU", "Русский"),
        new("uz", "uz-Latn-UZ", "Oʻzbekcha"),
        new("kk", "kk-KZ", "Қазақша"),
        new("be", "be-BY", "Беларуская"),
    ];

    /// <summary>
    /// Порядок кнопок выбора языка — по алфавиту самоназваний: кириллица (Қ — как К), затем латиница.
    /// <see cref="All"/> не трогаем: первый элемент — язык по умолчанию.
    /// </summary>
    public static readonly IReadOnlyList<UiLanguage> Menu =
        All.OrderBy(l => IsLatin(l.NativeName) ? 1 : 0)
            .ThenBy(l => l.NativeName.Replace('Қ', 'К').Replace('қ', 'к'), StringComparer.Ordinal)
            .ToList();

    private static bool IsLatin(string name) => name.Length > 0 && name[0] < '\u0400';

    public static UiLanguage? Find(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}
