using System.Reflection;
using System.Text.Json;

namespace KnitErp.Application.Workspace;

/// <summary>Статья отраслевой базы с источниками и датой проверки.</summary>
public sealed record KnowledgeArticle(HelpArticle Article, IReadOnlyList<string> Sources, string Reviewed);

/// <summary>
/// Отраслевая база знаний помощника (D116): производство одежды и трикотажа — пряжа, вязание, технология, качество, требования,
/// экономика. Статьи — JSON-файлы Workspace/Knowledge/*.json, встроенные в сборку: их пополняют специалисты и по ТЗ
/// docs/tz/assistant.md без изменения кода. Помощник ищет по ним вместе со справкой; каждая статья хранит источники и дату проверки.
/// Готовые ответы (qa-*.json, D900) читает <see cref="Knowledge.KnowledgeAnswers"/>, синонимы — <see cref="Knowledge.KnowledgeSearch"/>.
/// </summary>
public static class IndustryKnowledge
{
    private static readonly Lazy<IReadOnlyList<KnowledgeArticle>> Loaded = new(Load);

    public static IReadOnlyList<KnowledgeArticle> Entries => Loaded.Value;

    public static IReadOnlyList<HelpArticle> Articles => Loaded.Value.Select(e => e.Article).ToList();

    public static KnowledgeArticle? Find(string id) => Loaded.Value.FirstOrDefault(e => e.Article.Id == id);

    /// <summary>Готовые ответы базы (D900) — тем же загрузчиком знаний.</summary>
    public static IReadOnlyList<Knowledge.KnowledgeAnswer> Answers => Knowledge.KnowledgeAnswers.All;

    private sealed record FileDto(int Version, List<ArticleDto>? Articles);

    private sealed record ArticleDto(string? Id, string? Category, string? Title, List<string>? Body, List<string>? Sources, string? Reviewed);

    private static IReadOnlyList<KnowledgeArticle> Load()
    {
        var assembly = typeof(IndustryKnowledge).Assembly;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var list = new List<KnowledgeArticle>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Knowledge.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)
                                 && !Knowledge.KnowledgeAnswers.IsAnswerFile(n) && !n.EndsWith(".synonyms.json", StringComparison.Ordinal))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<FileDto>(stream, json);
            foreach (var a in file?.Articles ?? [])
            {
                if (string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Title) || a.Body is not { Count: > 0 } || list.Any(x => x.Article.Id == a.Id))
                {
                    continue; // битая или повторная статья не ломает помощника; проверяет тест базы знаний
                }

                list.Add(new KnowledgeArticle(new HelpArticle(a.Id!, a.Category ?? "Отрасль", a.Title!, [], a.Body!), a.Sources ?? [], a.Reviewed ?? ""));
            }
        }

        return list;
    }
}
