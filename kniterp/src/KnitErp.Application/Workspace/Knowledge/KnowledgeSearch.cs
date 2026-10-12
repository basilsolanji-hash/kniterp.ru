using System.Text;
using System.Text.Json;

namespace KnitErp.Application.Workspace.Knowledge;

/// <summary>Вид записи базы: статья справки, отраслевая статья, готовый ответ.</summary>
public enum KnowledgeKind
{
    Help,
    Industry,
    Answer,
}

/// <summary>Запись поискового индекса: статья справки, отраслевая статья или готовый ответ (D910).</summary>
public sealed record KnowledgeDoc(KnowledgeKind Kind, HelpArticle Article, IReadOnlyList<string> Variants, IReadOnlyList<string> Sources, string Reviewed)
{
    public string Id => Article.Id;

    /// <summary>Экраны для кнопки «Открыть» (адреса без ведущей «/»). Пустой адрес главной сюда не попадает.</summary>
    public IReadOnlyList<string> Links => Article.Routes.Where(r => r.Length > 0).ToList();
}

/// <summary>Найденная запись: оценка и доля слов вопроса, найденных в записи.</summary>
public sealed record KnowledgeHit(KnowledgeDoc Doc, double Score, double Coverage);

/// <summary>
/// Поиск помощника по вопросу своими словами (D910–D915): нормализация (регистр, ё/е, раскладка клавиатуры), русская морфология
/// (Snowball), синонимы программы и отрасли (Knowledge/synonyms.json), исправление опечаток, вес вопроса и вариантов выше текста
/// ответа (BM25F), экран, с которого спрашивают, поднимает свои ответы. Индекс строится один раз при первом обращении.
/// </summary>
public static class KnowledgeSearch
{
    private const double K1 = 1.2;
    private const double QuestionWeight = 3.0;
    private const double VariantWeight = 2.5;
    private const double BodyWeight = 1.0;
    private const double SectionWeight = 0.5;
    private const double PrefixWeight = 0.35;
    private const int PrefixLength = 5;

    private static readonly Lazy<Index> Built = new(Build);

    /// <summary>Все записи индекса: справка, отраслевые статьи, готовые ответы.</summary>
    public static IReadOnlyList<KnowledgeDoc> Docs => Built.Value.Docs;

    /// <summary>Группы синонимов из Knowledge/synonyms.json.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Synonyms => Built.Value.SynonymGroups;

    /// <summary>Строит индекс заранее (при запуске приложения), чтобы первый вопрос не ждал.</summary>
    public static void Warm() => _ = Built.Value;

    public static KnowledgeDoc? Find(string id) => Built.Value.ById.GetValueOrDefault(id);

    /// <summary>Лучшие записи по вопросу. route — экран, с которого спрашивают (его ответы выше).</summary>
    public static IReadOnlyList<KnowledgeHit> Search(string? query, int take = 5, string? route = null, KnowledgeKind? kind = null)
    {
        var index = Built.Value;
        var terms = index.QueryTerms(query ?? string.Empty);
        if (terms.Count == 0)
        {
            return [];
        }

        var scores = new Dictionary<int, (double Score, double Matched, double TitleMatched)>();
        var termIdf = new Dictionary<string, double>(StringComparer.Ordinal);
        var totalIdf = 0.0;
        foreach (var (term, weight) in terms)
        {
            if (!index.Postings.TryGetValue(term, out var postings))
            {
                totalIdf += weight * index.MaxIdf;
                continue;
            }

            var idf = index.Idf(postings.Count) * weight;
            totalIdf += idf;
            termIdf[term] = idf;
            foreach (var p in postings)
            {
                var d = index.Stats[p.Doc];
                var tf = QuestionWeight * p.Question / (0.5 + 0.5 * d.QuestionLength / index.AvgQuestion)
                         + VariantWeight * Math.Min(p.Variant, 3) / (0.5 + 0.5 * d.VariantLength / index.AvgVariant)
                         + BodyWeight * p.Body / (0.25 + 0.75 * d.BodyLength / index.AvgBody)
                         + SectionWeight * p.Section;
                var s = idf * tf / (K1 + tf);
                scores.TryGetValue(p.Doc, out var cur);
                scores[p.Doc] = (cur.Score + s, cur.Matched + idf, cur.TitleMatched + (p.Question + p.Variant > 0 ? idf : 0));
            }
        }

        var path = RoutePath(route);
        return scores
            .Where(x => kind is null || index.Docs[x.Key].Kind == kind)
            .Select(x =>
            {
                var doc = index.Docs[x.Key];
                var coverage = totalIdf > 0 ? x.Value.Matched / totalIdf : 0;
                var phrase = totalIdf > 0 ? BestPhrase(index.Phrasings[x.Key], termIdf) / totalIdf : 0;
                // Доля смысла вопроса, найденная в записи, и особенно в одной её формулировке (вопрос или вариант), важнее частоты слов.
                var score = x.Value.Score * (0.4 + coverage) * (0.4 + phrase);
                if (path.Length > 0 && doc.Links.Any(l => SameScreen(path, l)))
                {
                    score *= 1.2;
                }

                if (doc.Kind == KnowledgeKind.Answer)
                {
                    score *= 1.05; // готовый ответ точнее общей статьи при равном совпадении
                }

                return new KnowledgeHit(doc, score, coverage);
            })
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Doc.Id, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Лучшая формулировка записи: сумма весов слов вопроса, найденных в ней, с поправкой на то, какую часть формулировки
    /// эти слова покрывают, — короткий вариант, совпавший целиком, сильнее длинного вопроса, где совпало одно слово.
    /// </summary>
    private static double BestPhrase(IReadOnlyList<HashSet<string>> phrasings, Dictionary<string, double> termIdf)
    {
        var best = 0.0;
        foreach (var phrase in phrasings)
        {
            double matched = 0;
            var hits = 0;
            foreach (var (term, idf) in termIdf)
            {
                if (phrase.Contains(term))
                {
                    matched += idf;
                    hits += term[0] is '^' or '~' ? 0 : 1;
                }
            }

            var words = phrase.Count(t => t[0] is not ('^' or '~'));
            var value = matched * (0.75 + 0.25 * (words == 0 ? 0 : Math.Min(1.0, (double)hits / words)));
            best = Math.Max(best, value);
        }

        return best;
    }

    /// <summary>Доля смысла вопроса, которую должен покрыть лучший ответ, чтобы помощник показал его как готовый.</summary>
    public const double ConfidentCoverage = 0.6;

    /// <summary>Лучшая запись, если она уверенно отвечает на вопрос (D910); иначе null — помощник предложит варианты или «Поддержку».</summary>
    public static KnowledgeDoc? Confident(IReadOnlyList<KnowledgeHit> hits) =>
        hits.Count > 0 && hits[0].Coverage >= ConfidentCoverage ? hits[0].Doc : null;

    /// <summary>Нормализованные слова текста: нижний регистр, ё → е, только буквы и цифры.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = char.ToLowerInvariant(ch);
            sb.Append(c == 'ё' ? 'е' : char.IsLetterOrDigit(c) ? c : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Основа слова: русская морфология, у латиницы — без окончания множественного числа.</summary>
    public static string StemOf(string word) =>
        word[0] is >= 'а' and <= 'я' ? RussianStemmer.Stem(word)
        : word.Length > 4 && word.EndsWith('s') && word.All(char.IsAsciiLetter) ? word[..^1] : word;

    private static string RoutePath(string? route) => (route ?? string.Empty).Trim().TrimStart('/').Split('?', '#')[0].TrimEnd('/');

    /// <summary>Экран вопроса совпадает с экраном ответа или вложен в него (sales-orders/15 → sales-orders).</summary>
    public static bool SameScreen(string path, string link)
    {
        var l = RoutePath(link);
        return l.Length > 0 && (path == l || path.StartsWith(l + "/", StringComparison.Ordinal));
    }

    /// <summary>Служебные слова вопроса не говорят о теме.</summary>
    private static readonly HashSet<string> StopWords =
    [
        "как", "что", "где", "для", "это", "или", "при", "мне", "меня", "можно", "нужно", "надо", "почему", "зачем", "когда", "какой", "какая",
        "какие", "каком", "какое", "каких", "если", "чтобы", "все", "его", "она", "они", "оно", "этот", "эта", "эти", "тот", "так", "уже", "еще",
        "там", "тут", "вот", "мой", "моя", "мои", "свой", "свою", "свои", "наш", "наша", "наши", "вы", "ты", "мы", "он", "я", "и", "в", "во", "на",
        "с", "со", "к", "ко", "по", "о", "об", "от", "до", "из", "за", "у", "а", "но", "же", "ли", "бы", "не", "ни", "да", "то", "же", "чем",
        "кто", "сам", "ваш", "вас", "нам", "нас", "им", "их", "ему", "ей", "быть", "есть", "был", "была", "было", "были", "будет", "очень", "просто",
        "пожалуйста", "подскажите", "скажите", "подскажи", "скажи", "хочу", "хотим", "хотел", "хотела", "могу", "можем", "ли", "либо", "тоже", "также",
        "куда", "откуда", "сколько", "который", "которые", "которая", "чего", "нибудь", "вообще", "теперь", "сейчас", "сюда", "этом", "этой", "этого",
        "the", "how", "what", "and", "for", "to", "in", "of", "a", "is",
        // Узбекский, казахский, белорусский — служебные слова вопроса на языке интерфейса (D60).
        "qanday", "nima", "qayerda", "nega", "uchun", "kerak", "bilan", "mumkin", "qilish", "qachon",
        "қалай", "қайда", "неге", "үшін", "керек", "болады", "қашан", "жасау", "қандай",
        "што", "дзе", "чаму", "трэба", "можна", "калі", "каб", "зрабіць", "які", "якая",
    ];

    /// <summary>Латинская раскладка → русская: «ghjdtcnb» → «провести».</summary>
    private const string Latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Cyrillic = "йцукенгшщзхъфывапролджэячсмитьбюё";

    private sealed record Posting(int Doc, int Question, int Variant, int Body, int Section);

    private sealed record DocStats(double QuestionLength, double VariantLength, double BodyLength);

    private sealed class Index
    {
        public required List<KnowledgeDoc> Docs { get; init; }
        public required Dictionary<string, KnowledgeDoc> ById { get; init; }
        public required List<DocStats> Stats { get; init; }

        /// <summary>Формулировки записи — вопрос и каждый вариант отдельно — как наборы основ.</summary>
        public required List<IReadOnlyList<HashSet<string>>> Phrasings { get; init; }
        public required Dictionary<string, List<Posting>> Postings { get; init; }
        public required List<IReadOnlyList<string>> SynonymGroups { get; init; }

        /// <summary>Фразы синонимов по первой основе: последовательность основ → номер группы.</summary>
        public required Dictionary<string, List<(string[] Stems, int Group)>> Phrases { get; init; }

        /// <summary>Основы словаря по длине — для исправления опечаток.</summary>
        public required Dictionary<int, List<string>> VocabularyByLength { get; init; }

        public double AvgQuestion { get; init; }
        public double AvgVariant { get; init; }
        public double AvgBody { get; init; }
        public double MaxIdf => Idf(1);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _corrections = new();

        public double Idf(int df) => Math.Log(1 + (Docs.Count - df + 0.5) / (df + 0.5));

        /// <summary>Основы вопроса с весами: исправленные опечатки и угаданная раскладка весят меньше, синонимы — как слово.</summary>
        public List<(string Term, double Weight)> QueryTerms(string query)
        {
            var stems = new List<(string Stem, double Weight)>();
            foreach (var word in Words(query))
            {
                if (StopWords.Contains(word))
                {
                    continue;
                }

                var stem = StemOf(word);
                if (Postings.ContainsKey(stem) || IsDigits(word))
                {
                    stems.Add((stem, 1));
                    continue;
                }

                if (word.All(c => Latin.Contains(c)) && word.Length >= 3)
                {
                    var swapped = new string(word.Select(c => Cyrillic[Latin.IndexOf(c)]).ToArray());
                    var swappedStem = StemOf(swapped);
                    if (!StopWords.Contains(swapped) && Postings.ContainsKey(swappedStem))
                    {
                        stems.Add((swappedStem, 0.9));
                        continue;
                    }
                }

                if (Correct(stem) is { } fixedStem)
                {
                    stems.Add((fixedStem, 0.8));
                }
                else if (word.Length >= 2)
                {
                    stems.Add((stem, 1)); // неизвестное слово снижает уверенность (покрытие), но не мешает поиску
                }
            }

            var result = new List<(string, double)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (term, weight) in WithSynonyms(stems.Select(s => s.Stem).ToList()).Select((t, i) => (t, i)).Select(x =>
                         (x.t, x.t.StartsWith('~') ? 0.8 : x.t.StartsWith('^') ? PrefixWeight : stems.FirstOrDefault(s => s.Stem == x.t).Weight)))
            {
                if (seen.Add(term))
                {
                    result.Add((term, weight <= 0 ? 1 : weight));
                }
            }

            return result;
        }

        /// <summary>Основы плюс метки групп синонимов («~12»), если в тексте встретилась фраза группы.</summary>
        public List<string> WithSynonyms(IReadOnlyList<string> stems)
        {
            var result = new List<string>(stems);
            // Беглые гласные и чередования («остаток» — «остатки», «провести» — «проведение») стеммер не сводит к одной основе:
            // общие первые буквы длинной основы — слабый дополнительный признак.
            result.AddRange(stems.Where(t => t.Length >= PrefixLength + 1 && t[0] is >= 'а' and <= 'я').Select(t => "^" + t[..PrefixLength]));
            for (var i = 0; i < stems.Count; i++)
            {
                if (!Phrases.TryGetValue(stems[i], out var candidates))
                {
                    continue;
                }

                foreach (var (phrase, group) in candidates)
                {
                    if (i + phrase.Length <= stems.Count && phrase.Select((s, k) => stems[i + k] == s).All(x => x))
                    {
                        result.Add("~" + group);
                    }
                }
            }

            return result;
        }

        /// <summary>Ближайшая основа словаря: 1 правка для коротких слов, 2 — для длинных; при равенстве — более частая.</summary>
        private string? Correct(string stem)
        {
            if (stem.Length < 4 || IsDigits(stem))
            {
                return null;
            }

            return _corrections.GetOrAdd(stem, s =>
            {
                var max = s.Length >= 8 ? 2 : 1;
                string? best = null;
                var bestDistance = int.MaxValue;
                var bestDf = 0;
                for (var len = s.Length - max; len <= s.Length + max; len++)
                {
                    if (!VocabularyByLength.TryGetValue(len, out var words))
                    {
                        continue;
                    }

                    foreach (var w in words)
                    {
                        var d = Distance(s, w, max);
                        var df = Postings[w].Count;
                        if (d <= max && (d < bestDistance || (d == bestDistance && df > bestDf)))
                        {
                            (best, bestDistance, bestDf) = (w, d, df);
                        }
                    }
                }

                return best;
            });
        }
    }

    private static bool IsDigits(string s) => s.All(char.IsDigit);

    /// <summary>Расстояние Дамерау — Левенштейна (с перестановкой соседних букв), с отсечением по max.</summary>
    private static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }

            if (rowMin > max)
            {
                return max + 1;
            }
        }

        return d[a.Length, b.Length];
    }

    private sealed record SynonymsFile(int Version, List<List<string>>? Groups);

    private static List<IReadOnlyList<string>> LoadSynonyms()
    {
        var assembly = typeof(KnowledgeSearch).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Knowledge.synonyms.json", StringComparison.Ordinal));
        if (name is null)
        {
            return [];
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        var file = JsonSerializer.Deserialize<SynonymsFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (file?.Groups ?? []).Where(g => g.Count >= 2).Select(g => (IReadOnlyList<string>)g).ToList();
    }

    private static List<string> StemsOf(string text) => Words(text).Where(w => !StopWords.Contains(w)).Select(StemOf).ToList();

    private static Index Build()
    {
        var docs = new List<KnowledgeDoc>();
        docs.AddRange(HelpCenter.Articles.Select(a => new KnowledgeDoc(KnowledgeKind.Help, a, [], ["справка knitERP"], "")));
        docs.AddRange(IndustryKnowledge.Entries.Select(e => new KnowledgeDoc(KnowledgeKind.Industry, e.Article, [], e.Sources, e.Reviewed)));
        docs.AddRange(KnowledgeAnswers.All.Select(a => new KnowledgeDoc(KnowledgeKind.Answer, a.ToArticle(), a.Variants, a.Sources, a.Reviewed)));
        var byId = new Dictionary<string, KnowledgeDoc>(StringComparer.Ordinal);
        docs = docs.Where(d => byId.TryAdd(d.Id, d)).ToList();

        var groups = LoadSynonyms();
        var phrases = new Dictionary<string, List<(string[] Stems, int Group)>>(StringComparer.Ordinal);
        for (var g = 0; g < groups.Count; g++)
        {
            foreach (var phrase in groups[g])
            {
                var stems = StemsOf(phrase).ToArray();
                if (stems.Length == 0)
                {
                    continue;
                }

                if (!phrases.TryGetValue(stems[0], out var list))
                {
                    phrases[stems[0]] = list = [];
                }

                list.Add((stems, g));
            }
        }

        var index = new Index
        {
            Docs = docs, ById = byId, Stats = [], Phrasings = [], Postings = new(StringComparer.Ordinal), SynonymGroups = groups, Phrases = phrases,
            VocabularyByLength = [],
        };

        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        double sumQ = 0, sumV = 0, sumB = 0;
        var stats = new List<DocStats>();
        for (var i = 0; i < docs.Count; i++)
        {
            var doc = docs[i];
            counts.Clear();
            var q = index.WithSynonyms(StemsOf(doc.Article.Title));
            var variants = doc.Variants.Select(x => index.WithSynonyms(StemsOf(x))).ToList();
            var v = variants.SelectMany(x => x).ToList();
            index.Phrasings.Add([new HashSet<string>(q, StringComparer.Ordinal), .. variants.Select(x => new HashSet<string>(x, StringComparer.Ordinal))]);
            var b = index.WithSynonyms(StemsOf(doc.Article.Text));
            var s = StemsOf(doc.Article.Category);
            Count(counts, q, 0);
            Count(counts, v, 1);
            Count(counts, b, 2);
            Count(counts, s, 3);
            foreach (var (term, c) in counts)
            {
                if (!index.Postings.TryGetValue(term, out var list))
                {
                    index.Postings[term] = list = [];
                }

                list.Add(new Posting(i, c[0], c[1], c[2], c[3]));
            }

            stats.Add(new DocStats(q.Count, v.Count, b.Count));
            sumQ += q.Count;
            sumV += v.Count;
            sumB += b.Count;
        }

        foreach (var term in index.Postings.Keys.Where(t => !t.StartsWith('~') && !t.StartsWith('^') && t.Length >= 3))
        {
            if (!index.VocabularyByLength.TryGetValue(term.Length, out var list))
            {
                index.VocabularyByLength[term.Length] = list = [];
            }

            list.Add(term);
        }

        var n = Math.Max(1, docs.Count);
        return new Index
        {
            Docs = docs, ById = byId, Stats = stats, Phrasings = index.Phrasings, Postings = index.Postings, SynonymGroups = groups, Phrases = phrases,
            VocabularyByLength = index.VocabularyByLength,
            AvgQuestion = Math.Max(1, sumQ / n), AvgVariant = Math.Max(1, sumV / n), AvgBody = Math.Max(1, sumB / n),
        };

        static void Count(Dictionary<string, int[]> counts, IEnumerable<string> terms, int field)
        {
            foreach (var t in terms)
            {
                if (!counts.TryGetValue(t, out var c))
                {
                    counts[t] = c = new int[4];
                }

                c[field]++;
            }
        }
    }
}
