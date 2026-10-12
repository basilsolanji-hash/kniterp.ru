namespace KnitErp.Application.Workspace.Knowledge;

/// <summary>
/// Стеммер русского языка по алгоритму Snowball (Porter для русского) — D911: «остатки», «остатков», «остаток» сводятся к одной
/// основе, «провести» и «проведённый» — к близким. Слово ожидается в нижнем регистре с «ё» → «е».
/// </summary>
public static class RussianStemmer
{
    private static readonly string[] PerfectiveGerund1 = ["вшись", "вши", "в"];
    private static readonly string[] PerfectiveGerund2 = ["ившись", "ывшись", "ивши", "ывши", "ив", "ыв"];
    private static readonly string[] Adjective =
        ["ими", "ыми", "его", "ого", "ему", "ому", "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой", "ем", "им", "ым", "ом", "их", "ых", "ую", "юю", "ая", "яя", "ою", "ею"];
    private static readonly string[] Participle1 = ["ем", "нн", "вш", "ющ", "щ"];
    private static readonly string[] Participle2 = ["ивш", "ывш", "ующ"];
    private static readonly string[] Reflexive = ["ся", "сь"];
    private static readonly string[] Verb1 = ["ете", "йте", "ешь", "нно", "ла", "на", "ли", "ем", "ло", "но", "ет", "ют", "ны", "ть", "й", "л", "н"];
    private static readonly string[] Verb2 =
        ["ейте", "уйте", "ила", "ыла", "ена", "ите", "или", "ыли", "ило", "ыло", "ено", "ует", "уют", "ены", "ить", "ыть", "ишь", "ей", "уй", "ил", "ыл", "им", "ым",
         "ен", "ят", "ит", "ыт", "ую", "ю"];
    private static readonly string[] Noun =
        ["иями", "ями", "ами", "ией", "иям", "ием", "иях", "ев", "ов", "ие", "ье", "еи", "ии", "ей", "ой", "ий", "ям", "ем", "ам", "ом", "ах", "ях", "ию", "ью", "ия",
         "ья", "а", "е", "и", "й", "о", "у", "ы", "ь", "ю", "я"];
    private static readonly string[] Superlative = ["ейше", "ейш"];
    private static readonly string[] Derivational = ["ость", "ост"];

    public static string Stem(string word)
    {
        if (word.Length < 3 || !word.All(c => c is >= 'а' and <= 'я'))
        {
            return word;
        }

        var rv = 0;
        while (rv < word.Length && !IsVowel(word[rv]))
        {
            rv++;
        }

        rv++;
        if (rv >= word.Length)
        {
            return word;
        }

        var r1 = Region(word, 0);
        var r2 = Region(word, r1);
        var w = word;

        // Шаг 1.
        if (!TryRemove(ref w, rv, PerfectiveGerund2, false) && !TryRemove(ref w, rv, PerfectiveGerund1, true))
        {
            TryRemove(ref w, rv, Reflexive, false);
            if (TryRemove(ref w, rv, Adjective, false))
            {
                if (!TryRemove(ref w, rv, Participle2, false))
                {
                    TryRemove(ref w, rv, Participle1, true);
                }
            }
            else if (!TryRemove(ref w, rv, Verb2, false) && !TryRemove(ref w, rv, Verb1, true))
            {
                TryRemove(ref w, rv, Noun, false);
            }
        }

        // Шаг 2.
        if (w.Length > rv && w.EndsWith('и'))
        {
            w = w[..^1];
        }

        // Шаг 3.
        TryRemove(ref w, r2, Derivational, false);

        // Шаг 4.
        if (w.Length - 2 >= rv && w.EndsWith("нн", StringComparison.Ordinal))
        {
            w = w[..^1];
        }
        else if (TryRemove(ref w, rv, Superlative, false))
        {
            if (w.Length - 2 >= rv && w.EndsWith("нн", StringComparison.Ordinal))
            {
                w = w[..^1];
            }
        }
        else if (w.Length > rv && w.EndsWith('ь'))
        {
            w = w[..^1];
        }

        return w;
    }

    private static bool IsVowel(char c) => c is 'а' or 'е' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я';

    /// <summary>Начало области: после первой согласной, стоящей за гласной, начиная с from.</summary>
    private static int Region(string w, int from)
    {
        for (var i = from + 1; i < w.Length; i++)
        {
            if (!IsVowel(w[i]) && IsVowel(w[i - 1]))
            {
                return i + 1;
            }
        }

        return w.Length;
    }

    /// <summary>Убирает самое длинное окончание из списка, целиком лежащее в области; afterAOrYa — окончание после «а»/«я» (они остаются).</summary>
    private static bool TryRemove(ref string w, int region, string[] suffixes, bool afterAOrYa)
    {
        string? best = null;
        foreach (var s in suffixes)
        {
            if (w.Length - s.Length < region || !w.EndsWith(s, StringComparison.Ordinal) || (best is not null && best.Length >= s.Length))
            {
                continue;
            }

            if (afterAOrYa)
            {
                var p = w.Length - s.Length - 1;
                if (p < region || (w[p] != 'а' && w[p] != 'я'))
                {
                    continue;
                }
            }

            best = s;
        }

        if (best is null)
        {
            return false;
        }

        w = w[..^best.Length];
        return true;
    }
}
