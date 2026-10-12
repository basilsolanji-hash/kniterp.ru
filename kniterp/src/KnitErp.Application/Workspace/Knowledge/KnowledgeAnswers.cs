using System.Text.Json;

namespace KnitErp.Application.Workspace.Knowledge;

/// <summary>
/// Готовый проверенный ответ базы знаний помощника (D900): вопрос, как его задают люди (варианты), ответ по шагам, экраны
/// для кнопки «Открыть», роли, источники и дата проверки. Файлы Workspace/Knowledge/qa-&lt;раздел&gt;.json встроены в сборку.
/// </summary>
public sealed record KnowledgeAnswer(
    string Id,
    string Section,
    string Question,
    IReadOnlyList<string> Variants,
    IReadOnlyList<string> Answer,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Sources,
    string Reviewed,
    string Level)
{
    /// <summary>Тот же ответ в виде статьи справки — для панели «Справка», поиска и помощника.</summary>
    public HelpArticle ToArticle() => new(Id, Section, Question, Links, Answer);
}

/// <summary>Раздел базы ответов (D901): ключ файла и id, название, план по числу ответов, нужны ли внешние источники.</summary>
public sealed record KnowledgeSection(string Key, string Title, int Planned, bool NeedsSources, bool ChangingRules = false);

/// <summary>Загрузчик ответов базы знаний (D900). Битая запись не ломает помощника — её ловит KnowledgeBaseTests.</summary>
public static class KnowledgeAnswers
{
    public const string Program = "Программа · ";

    /// <summary>Состав базы — 4000 ответов (D901, docs/tz/knowledge-base.md §4).</summary>
    public static readonly IReadOnlyList<KnowledgeSection> Sections =
    [
        new("start", "Программа · Начало работы и вход", 150, false),
        new("sales", "Программа · Продажи и счета", 400, false),
        new("purchases", "Программа · Закупки", 250, false),
        new("stock", "Программа · Склад и партии", 350, false),
        new("money", "Программа · Деньги", 250, false),
        new("production", "Программа · Производство", 350, false),
        new("hr", "Программа · Сотрудники и кадры", 250, false),
        new("catalog", "Программа · Каталог, цены, МойСклад", 250, false),
        new("crm", "Программа · Контрагенты, договоры, CRM", 200, false),
        new("reports", "Программа · Отчёты, интеграции, 1С", 150, false),
        new("yarn", "Отрасль · Сырьё и пряжа", 200, true),
        new("knitting", "Отрасль · Оборудование и вязание", 250, true),
        new("sewing", "Отрасль · Швейное производство и отделка", 200, true),
        new("quality", "Отрасль · Качество и дефекты", 150, true),
        new("design", "Отрасль · Размеры и конструирование", 150, true),
        new("economy", "Отрасль · Нормирование и экономика", 150, true),
        new("regulation", "Требования · Регламенты, ГОСТ, маркировка", 150, true, true),
        new("law", "Требования · Труд, персональные данные, налоги", 100, true, true),
        new("errors", "Неполадки и сообщения об ошибках", 150, false),
    ];

    private static readonly Lazy<IReadOnlyList<KnowledgeAnswer>> Loaded = new(Load);

    public static IReadOnlyList<KnowledgeAnswer> All => Loaded.Value;

    public static KnowledgeAnswer? Find(string id) => Loaded.Value.FirstOrDefault(a => a.Id == id);

    public static KnowledgeSection? SectionOf(KnowledgeAnswer answer) => Sections.FirstOrDefault(s => s.Title == answer.Section);

    /// <summary>Имя встроенного файла — ответы (qa-*.json).</summary>
    internal static bool IsAnswerFile(string resourceName) => resourceName.Contains(".Knowledge.qa-", StringComparison.Ordinal);

    private sealed record FileDto(int Version, List<AnswerDto>? Items);

    private sealed record AnswerDto(string? Id, string? Section, string? Question, List<string>? Variants, List<string>? Answer, List<string>? Links,
        List<string>? Roles, List<string>? Sources, string? Reviewed, string? Level);

    private static IReadOnlyList<KnowledgeAnswer> Load()
    {
        var assembly = typeof(KnowledgeAnswers).Assembly;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var list = new List<KnowledgeAnswer>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => IsAnswerFile(n) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<FileDto>(stream, json);
            foreach (var a in file?.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Question) || a.Answer is not { Count: > 0 } || !ids.Add(a.Id))
                {
                    continue;
                }

                list.Add(new KnowledgeAnswer(a.Id!, a.Section ?? "", a.Question!.Trim(), a.Variants ?? [], a.Answer!, a.Links ?? [], a.Roles ?? [],
                    a.Sources ?? [], a.Reviewed ?? "", a.Level ?? "basic"));
            }
        }

        return list;
    }
}
