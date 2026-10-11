using System.Text.RegularExpressions;

namespace KnitErp.Domain.Hr;

/// <summary>
/// Маска персональных данных в свободном тексте (D210): последовательность из 9 и более цифр (с пробелами, дефисами) —
/// СНИЛС, ИНН, паспорт, телефон, номер счёта — заменяется на «[скрыто]». Применяется к выдержкам, которые уходят
/// в журнал аудита и уведомления: журнал читают шире, чем карточку сотрудника.
/// </summary>
public static partial class PersonalDataMask
{
    public const string Hidden = "[скрыто]";

    public static string Mask(string text) => LongNumber().Replace(text, Hidden);

    public static bool HasLongNumber(string text) => LongNumber().IsMatch(text);

    [GeneratedRegex(@"(?<!\d)\+?(?:\d[\s\-()]{0,2}){8,}\d(?!\d)")]
    private static partial Regex LongNumber();
}
