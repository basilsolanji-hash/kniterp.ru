using System.Globalization;
using static KnitErp.Web.Localization.Text;

namespace KnitErp.Web.Components.Pages;

/// <summary>Вывод величин модуля «Разработка»: время вязания, вес, сантиметры, проценты — коротко, по-русски с переводом единиц.</summary>
public static class DevFormat
{
    /// <summary>Замечание расчёта (D767): шаблон переводится, подстановки (названия) — как есть.</summary>
    public static string Note(KnitErp.Domain.Development.DevNote n) => L(n.Template, n.Args.Cast<object?>().ToArray());

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>«3 мин», «3 мин 30 с», «45 с».</summary>
    public static string Duration(int? seconds)
    {
        if (seconds is not { } s)
        {
            return "—";
        }

        var m = s / 60;
        var r = s % 60;
        return m == 0 ? L("{0} с", r) : r == 0 ? L("{0} мин", m) : L("{0} мин {1} с", m, r);
    }

    /// <summary>Число для поля ввода без хвостовых нулей хранения decimal(18,6): 100.000000 → 100 (пересмотр интерфейса 13.10).</summary>
    public static decimal? Trim(decimal? value) => value is { } v ? v / 1.000000000000000000000000000000000m : null;

    public static string Grams(decimal? grams) => grams is { } g ? L("{0} г", g.ToString("0.##", Ru)) : "—";

    public static string Cm(decimal? cm) => cm is { } c ? c.ToString("0.#", Ru) : "—";

    public static string Percent(decimal p) => p.ToString("0.##", Ru) + " %";

    public static string Number(decimal? v) => v is { } x ? x.ToString("0.##", Ru) : "—";

    /// <summary>Минуты из поля ввода («3,5» → 210 с).</summary>
    public static int? SecondsFromMinutes(decimal? minutes) => minutes is { } m && m > 0 ? (int)Math.Round(m * 60m, MidpointRounding.AwayFromZero) : null;

    public static decimal? MinutesFromSeconds(int? seconds) => seconds is { } s ? Math.Round(s / 60m, 2) : null;
}
