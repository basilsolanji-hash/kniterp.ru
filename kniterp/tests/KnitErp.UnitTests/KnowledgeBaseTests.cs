using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnitErp.Application.Workspace;
using KnitErp.Application.Workspace.Knowledge;

namespace KnitErp.UnitTests;

/// <summary>
/// D116, D900–D915: отраслевая база знаний и готовые ответы помощника — загружены, оформлены, ссылаются на настоящие экраны,
/// отраслевые и правовые ответы — с источниками; поиск по вопросу своими словами точен и быстр (docs/kb/eval.json).
/// </summary>
public sealed class KnowledgeBaseTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Every_article_is_complete_unique_and_sourced()
    {
        Assert.True(IndustryKnowledge.Entries.Count >= 20);
        Assert.Equal(IndustryKnowledge.Entries.Count, IndustryKnowledge.Entries.Select(e => e.Article.Id).Distinct().Count());
        Assert.All(IndustryKnowledge.Entries, e =>
        {
            // Отраслевые статьи — kb-, статьи модулей — с префиксом модуля (docs/tz/assistant.md §4.5).
            Assert.Matches("^(kb|prod|hr|cat|dev)-[a-z0-9-]+$", e.Article.Id);
            Assert.Matches("^(Отрасль|Модуль) · ", e.Article.Category);
            Assert.NotEmpty(e.Article.Body);
            Assert.NotEmpty(e.Sources);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", e.Reviewed);
        });
        Assert.DoesNotContain(HelpCenter.Articles, a => IndustryKnowledge.Find(a.Id) is not null);
    }

    [Theory]
    [InlineData("какой номер пряжи Nm", "kb-yarn-count")]
    [InlineData("разнооттеночность партия крашения", "kb-dye-lot")]
    [InlineData("класс вязальной машины", "kb-gauge")]
    [InlineData("маркировка честный знак", "kb-marking")]
    [InlineData("как провести сторно", "qa-stock-0006")]
    [InlineData("как исправить отметку на терминале", "prod-correction")]
    [InlineData("баллы отпуск болезнь табель", "prod-points")]
    [InlineData("маршрут изделия этапы", "prod-route")]
    [InlineData("образец у заказчика не вернули", "dev-sample-customer")]
    [InlineData("заправка нитеводителей деталь", "dev-threading")]
    [InlineData("как передать модель в производство", "dev-handoff")]
    public void Search_finds_industry_and_program_answers(string query, string expected) =>
        Assert.Contains(HelpCenter.Search(query, 5), a => a.Id == expected);

    /// <summary>D900: формат ответа — id раздела, вопрос, 1–6 абзацев, роли, источники, дата проверки, уровень.</summary>
    [Fact]
    public void Every_answer_has_valid_format()
    {
        Assert.NotEmpty(KnowledgeAnswers.All);
        var keys = KnowledgeAnswers.Sections.ToDictionary(s => s.Title, s => s.Key);
        Assert.All(KnowledgeAnswers.All, a =>
        {
            Assert.True(keys.TryGetValue(a.Section, out var key), $"{a.Id}: неизвестный раздел «{a.Section}»");
            Assert.Matches($"^qa-{key}-\\d{{4}}$", a.Id);
            Assert.EndsWith("?", a.Question);
            Assert.InRange(a.Question.Length, 8, 200);
            Assert.InRange(a.Answer.Count, 1, 6);
            Assert.All(a.Answer, p => Assert.False(string.IsNullOrWhiteSpace(p), $"{a.Id}: пустой абзац"));
            Assert.All(a.Variants, v => Assert.False(string.IsNullOrWhiteSpace(v), $"{a.Id}: пустой вариант"));
            Assert.NotEmpty(a.Sources);
            Assert.Matches(@"^20\d{2}-\d{2}-\d{2}$", a.Reviewed);
            Assert.Contains(a.Level, new[] { "basic", "advanced" });
        });
    }

    /// <summary>id уникальны во всей базе: ответы, статьи справки и отраслевые статьи.</summary>
    [Fact]
    public void Ids_are_unique_across_knowledge()
    {
        var ids = HelpCenter.Articles.Select(a => a.Id).Concat(IndustryKnowledge.Entries.Select(e => e.Article.Id)).Concat(KnowledgeAnswers.All.Select(a => a.Id)).ToList();
        var duplicates = ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Повторные id: " + string.Join(", ", duplicates));
        Assert.Equal(KnowledgeAnswers.All.Count, KnowledgeSearch.Docs.Count(d => d.Kind == KnowledgeKind.Answer));
    }

    /// <summary>Один вопрос — один ответ: вопросы не повторяются по смыслу слов, вариант не совпадает с чужим вопросом.</summary>
    [Fact]
    public void Questions_are_not_duplicated()
    {
        var byKey = new Dictionary<string, string>();
        var problems = new List<string>();
        foreach (var a in KnowledgeAnswers.All)
        {
            if (!byKey.TryAdd(Key(a.Question), a.Id))
            {
                problems.Add($"{a.Id} повторяет {byKey[Key(a.Question)]}: {a.Question}");
            }
        }

        foreach (var a in KnowledgeAnswers.All)
        {
            foreach (var v in a.Variants)
            {
                if (byKey.TryGetValue(Key(v), out var other) && other != a.Id)
                {
                    problems.Add($"{a.Id}: вариант «{v}» — это вопрос {other}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        static string Key(string text) =>
            string.Join(" ", KnowledgeSearch.Words(text).Select(KnowledgeSearch.StemOf).Where(s => s.Length > 2).Order(StringComparer.Ordinal));
    }

    /// <summary>Ссылки «Открыть» ведут на настоящие экраны: адрес совпадает с @page страницы KnitErp.Web.</summary>
    [Fact]
    public void Links_point_to_existing_screens()
    {
        var templates = Directory.EnumerateFiles(Path.Combine(Root, "src", "KnitErp.Web"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "^@page \"/([^\"]*)\"", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Select(t => t.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.NotEmpty(templates);
        var broken = KnowledgeSearch.Docs
            .Where(d => d.Kind != KnowledgeKind.Help)
            .SelectMany(d => d.Links.Select(l => (d.Id, Link: l)))
            .Where(x => !templates.Any(t => Matches(t, x.Link.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries))))
            .Select(x => $"{x.Id}: {x.Link}")
            .ToList();
        Assert.True(broken.Count == 0, "Нет такого экрана: " + string.Join(", ", broken));

        static bool Matches(string[] template, string[] path) =>
            template.Length == path.Length && template.Zip(path).All(p => p.First.StartsWith('{') || p.First == p.Second);
    }

    /// <summary>
    /// Отраслевые и правовые ответы — не меньше двух источников, у внешнего источника адрес и дата обращения; меняющиеся нормы —
    /// с пометкой «проверьте актуальную редакцию» (assistant.md §4.3).
    /// </summary>
    [Fact]
    public void Industry_and_legal_answers_have_sources()
    {
        var problems = new List<string>();
        foreach (var a in KnowledgeAnswers.All)
        {
            var section = KnowledgeAnswers.SectionOf(a)!;
            if (!section.NeedsSources)
            {
                continue;
            }

            if (a.Sources.Count < 2)
            {
                problems.Add($"{a.Id}: меньше двух источников");
            }

            var web = a.Sources.Where(s => s.Contains("https://", StringComparison.Ordinal)).ToList();
            if (web.Count == 0 || web.Any(s => !Regex.IsMatch(s, @"обращени[ея] 20\d{2}-\d{2}-\d{2}")))
            {
                problems.Add($"{a.Id}: у источника нет адреса или даты обращения");
            }

            if (section.ChangingRules && !a.Answer.Any(p => p.Contains("проверьте актуальную редакцию", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{a.Id}: нет пометки «проверьте актуальную редакцию»");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Встроенные файлы базы знаний — не больше 8 МБ (D914).</summary>
    [Fact]
    public void Embedded_knowledge_fits_size_limit()
    {
        var assembly = typeof(KnowledgeAnswers).Assembly;
        long total = 0;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Knowledge.", StringComparison.Ordinal)))
        {
            using var s = assembly.GetManifestResourceStream(name)!;
            total += s.Length;
        }

        Assert.InRange(total, 1, 8L * 1024 * 1024);
    }

    [Theory]
    [InlineData("остатки", "остатков")]
    [InlineData("провести", "проведение")]
    [InlineData("отгрузка", "отгрузки")]
    [InlineData("покупателю", "покупатели")]
    [InlineData("инвентаризации", "инвентаризация")]
    public void Stemmer_joins_word_forms(string a, string b) =>
        Assert.Equal(RussianStemmer.Stem(a)[..4], RussianStemmer.Stem(b)[..4]);

    /// <summary>D911–D913: регистр и ё, синонимы, опечатки и раскладка клавиатуры.</summary>
    [Theory]
    [InlineData("ЗАБЫЛ ПАРОЛЬ", "qa-start-")]
    [InlineData("забыл пороль", "qa-start-")]
    [InlineData("pf,sk gfhjkm", "qa-start-")]
    [InlineData("двухфакторная аутентификация что это", "qa-start-")]
    public void Search_understands_case_typos_layout_and_synonyms(string query, string idPrefix) =>
        Assert.StartsWith(idPrefix, KnowledgeSearch.Search(query, 1)[0].Doc.Id);

    [Fact]
    public void Unrelated_question_has_no_confident_answer() =>
        Assert.Null(KnowledgeSearch.Confident(KnowledgeSearch.Search("сколько стоит билет на самолёт до сочи", 3)));

    /// <summary>Экран, с которого спрашивают, поднимает свой ответ (D910).</summary>
    [Fact]
    public void Current_screen_lifts_its_answers()
    {
        var plain = KnowledgeSearch.Search("как изменить роль", 10);
        var onUsers = KnowledgeSearch.Search("как изменить роль", 10, "users");
        var top = onUsers[0].Doc;
        Assert.Contains(top.Links, l => l == "users");
        Assert.True(onUsers[0].Score >= plain.First(h => h.Doc.Id == top.Id).Score);
    }

    /// <summary>
    /// Тестовый набор docs/kb/eval.json — вопросы, как их задают люди: верный ответ первым не реже 85 %, в первой тройке — 95 %;
    /// поиск по всей базе — в среднем до 50 мс (D914, D915).
    /// </summary>
    [Fact]
    public void Eval_set_meets_accuracy_and_speed_targets()
    {
        KnowledgeSearch.Warm();
        using var file = File.OpenRead(Path.Combine(Root, "docs", "kb", "eval.json"));
        var eval = JsonSerializer.Deserialize<EvalFile>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var missing = eval.Items.Where(i => KnowledgeSearch.Find(i.Id) is null).Select(i => i.Id).ToList();
        Assert.True(missing.Count == 0, "Нет ответов: " + string.Join(", ", missing));

        int first = 0, top3 = 0;
        var misses = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (var item in eval.Items)
        {
            var hits = KnowledgeSearch.Search(item.Q, 3, item.Route);
            var rank = hits.Select((h, i) => (h, i)).FirstOrDefault(x => x.h.Doc.Id == item.Id, (null!, -1)).Item2;
            first += rank == 0 ? 1 : 0;
            top3 += rank >= 0 ? 1 : 0;
            if (rank != 0)
            {
                misses.Add($"{item.Q} → ждали {item.Id}, нашли {string.Join(", ", hits.Select(h => h.Doc.Id))}");
            }
        }

        watch.Stop();
        var n = eval.Items.Count;
        var report = $"первым {first}/{n} ({100.0 * first / n:F1} %), в тройке {top3}/{n} ({100.0 * top3 / n:F1} %)\n" + string.Join("\n", misses);
        output.WriteLine(report);
        output.WriteLine($"Поиск в среднем {watch.Elapsed.TotalMilliseconds / n:F2} мс на вопрос, записей в базе: {KnowledgeSearch.Docs.Count}");
        Assert.True(first >= 0.85 * n, report);
        Assert.True(top3 >= 0.95 * n, report);
        Assert.True(watch.Elapsed.TotalMilliseconds / n < 50, $"Поиск в среднем {watch.Elapsed.TotalMilliseconds / n:F1} мс");
        Assert.True(n >= MinEvalQuestions, $"В тестовом наборе {n} вопросов, нужно не меньше {MinEvalQuestions}");
    }

    /// <summary>Размер тестового набора по этапам ТЗ: 100 на первом, 400 к четвёртому.</summary>
    private const int MinEvalQuestions = 100;

    private sealed record EvalItem(string Q, string Id, string? Route);

    private sealed record EvalFile(int Version, List<EvalItem> Items);

    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new DirectoryNotFoundException("Не найден корень репозитория (KnitErp.slnx).");
        }
    }
}
