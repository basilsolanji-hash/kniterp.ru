namespace KnitErp.Application.Workspace;

/// <summary>
/// Нерабочие праздничные дни России по ст. 112 Трудового кодекса РФ (D83). Переносы выходных дней устанавливает Правительство
/// на каждый год отдельным постановлением — они здесь не рассчитываются и в календаре не показываются.
/// </summary>
public static class ProductionCalendar
{
    private static readonly (int Month, int Day, string Name)[] Russia =
    [
        (1, 1, "Новогодние каникулы"), (1, 2, "Новогодние каникулы"), (1, 3, "Новогодние каникулы"), (1, 4, "Новогодние каникулы"),
        (1, 5, "Новогодние каникулы"), (1, 6, "Новогодние каникулы"), (1, 7, "Рождество Христово"), (1, 8, "Новогодние каникулы"),
        (2, 23, "День защитника Отечества"), (3, 8, "Международный женский день"), (5, 1, "Праздник Весны и Труда"),
        (5, 9, "День Победы"), (6, 12, "День России"), (11, 4, "День народного единства"),
    ];

    /// <summary>Праздники страны в диапазоне дат; для стран, кроме России, — пусто.</summary>
    public static IEnumerable<(DateOnly Date, string Name)> Holidays(string countryCode, DateOnly from, DateOnly to)
    {
        if (countryCode != "RU")
        {
            yield break;
        }

        for (var year = from.Year; year <= to.Year; year++)
        {
            foreach (var (month, day, name) in Russia)
            {
                var date = new DateOnly(year, month, day);
                if (date >= from && date <= to)
                {
                    yield return (date, name);
                }
            }
        }
    }

    /// <summary>Дата через n рабочих дней после date: без суббот, воскресений и праздников страны (переносы не учитываются).</summary>
    public static DateOnly AddWorkingDays(string countryCode, DateOnly date, int days)
    {
        var holidays = Holidays(countryCode, date, date.AddDays(days * 3 + 30)).Select(h => h.Date).ToHashSet();
        var d = date;
        while (days > 0)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d))
            {
                days--;
            }
        }

        return d;
    }

    /// <summary>Годовщины даты в диапазоне; 29 февраля в невисокосный год — 28 февраля.</summary>
    public static IEnumerable<DateOnly> Anniversaries(DateOnly date, DateOnly from, DateOnly to)
    {
        for (var year = from.Year; year <= to.Year; year++)
        {
            var day = Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month));
            var d = new DateOnly(year, date.Month, day);
            if (d >= from && d <= to)
            {
                yield return d;
            }
        }
    }
}
