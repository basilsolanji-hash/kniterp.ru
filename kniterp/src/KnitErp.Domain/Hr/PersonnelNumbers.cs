namespace KnitErp.Domain.Hr;

/// <summary>
/// Следующий табельный номер для приёма (D225): наибольший числовой номер организации + 1 с той же шириной («0007» → «0008», «99» → «100»).
/// Нечисловые номера («А-15») не учитываются; номеров нет — «0001». Пользователь может заменить предложенный номер.
/// </summary>
public static class PersonnelNumbers
{
    public static string Next(IEnumerable<string> existing)
    {
        var numeric = existing.Select(n => n.Trim()).Where(n => n.Length is > 0 and <= 18 && n.All(char.IsAsciiDigit)).ToList();
        if (numeric.Count == 0)
        {
            return "0001";
        }

        var max = numeric.MaxBy(long.Parse)!;
        var next = (long.Parse(max) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return next.PadLeft(max.Length, '0');
    }
}
