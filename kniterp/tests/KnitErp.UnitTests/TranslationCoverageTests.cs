using System.Text.Json;
using System.Text.RegularExpressions;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;

namespace KnitErp.UnitTests;

/// <summary>
/// Полнота переводов интерфейса (D60): каждый текст, обёрнутый в L("…") в коде Web, и названия меню и инструментов
/// есть в переводе каждого языка, а в файлах переводов нет забытых ключей.
/// </summary>
public sealed partial class TranslationCoverageTests
{
    private static readonly string WebDir = Path.Combine(FindRoot(), "src", "KnitErp.Web");

    public static TheoryData<string> Languages => new(UiLanguages.All.Where(l => l.Code != UiLanguages.Default).Select(l => l.Code));

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_interface_text_is_translated(string code)
    {
        var translations = Load(code);
        var missing = UsedKeys().Where(k => !translations.TryGetValue(k, out var t) || string.IsNullOrWhiteSpace(t)).Order().ToList();
        Assert.True(missing.Count == 0, $"{code}.json: нет перевода для {missing.Count}:\n" + string.Join("\n", missing));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translation_files_have_no_stale_keys(string code)
    {
        var used = UsedKeys();
        var stale = Load(code).Keys.Where(k => !used.Contains(k)).Order().ToList();
        Assert.True(stale.Count == 0, $"{code}.json: ключи не используются в коде:\n" + string.Join("\n", stale));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Placeholders_survive_translation(string code)
    {
        foreach (var (key, value) in Load(code))
        {
            var expected = Placeholder().Matches(key).Select(m => m.Value).Order();
            Assert.Equal(expected, Placeholder().Matches(value).Select(m => m.Value).Order());
        }
    }

    private static HashSet<string> UsedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(WebDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Call().Matches(text))
            {
                keys.Add(Regex.Unescape(m.Groups[1].Value));
            }
        }

        // Названия разделов меню и групп — строки в MainLayout, переводятся при выводе.
        var layout = File.ReadAllText(Path.Combine(WebDir, "Components", "Layout", "MainLayout.razor"));
        foreach (Match m in MenuItem().Matches(layout))
        {
            keys.Add(m.Groups[1].Value);
        }

        foreach (Match m in MenuSection().Matches(layout))
        {
            keys.Add(m.Groups[1].Value);
        }

        foreach (Match m in MenuPlanned().Matches(layout))
        {
            keys.Add(m.Groups[1].Value);
        }

        foreach (var tool in ToolCatalog.All)
        {
            keys.Add(tool.Title);
            keys.Add(tool.Hint);
        }

        // Названия из домена, которые экраны выводят через L(...): виды и статусы документов, типы номенклатуры.
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Warehousing.StockOperationKind>().Select(KnitErp.Domain.Warehousing.StockDocument.KindName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Warehousing.StockDocumentStatus>().Select(KnitErp.Domain.Warehousing.StockDocument.StatusName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Warehousing.OpeningBalanceStatus>().Select(KnitErp.Domain.Warehousing.OpeningBalance.StatusName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Warehousing.InventoryStatus>().Select(KnitErp.Domain.Warehousing.InventoryCount.StatusName));
        keys.UnionWith(KnitErp.Domain.Catalog.ItemTypes.All.Select(KnitErp.Domain.Catalog.ItemTypes.Name));

        // Прочие названия, которые экраны переводят при выводе.
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Structure.EmploymentStatus>().Select(KnitErp.Domain.Structure.Employee.StatusName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Catalog.VatRateKind>().Select(KnitErp.Domain.Catalog.VatRate.KindName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Workspace.SupportTicketStatus>().Select(KnitErp.Domain.Workspace.SupportTicket.StatusName));
        keys.UnionWith(new[] { (true, true), (true, false), (false, true) }.Select(r => KnitErp.Domain.Catalog.Counterparty.RolesText(r.Item1, r.Item2)));
        keys.UnionWith(SystemRoles.Ordered.Select(SystemRoles.NameOf));

        // Модуль «Сотрудники» (D200–D299): виды и статусы приказов, виды отпусков, основания увольнения.
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Hr.PersonnelOrderKind>().Select(KnitErp.Domain.Hr.PersonnelOrder.KindName));
        keys.UnionWith(Enum.GetValues<KnitErp.Domain.Hr.PersonnelOrderStatus>().Select(KnitErp.Domain.Hr.PersonnelOrder.StatusName));
        keys.UnionWith(KnitErp.Domain.Hr.LeaveKinds.All.Select(KnitErp.Domain.Hr.LeaveKinds.Name));
        keys.UnionWith(KnitErp.Domain.Hr.DismissalGrounds.All.Select(g => g.Title));
        keys.UnionWith(KnitErp.Domain.Hr.TimesheetCodes.All.Select(c => c.Name));
        keys.UnionWith(KnitErp.Domain.Warehousing.OperationReason.Defaults.Select(d => d.Name));
        keys.UnionWith(KnitErp.Domain.Organizations.Countries.All.Select(c => c.Name));
        keys.UnionWith(["без НДС", "ещё не действует", "Без роли", "Вся организация"]);
        keys.UnionWith(KnitErp.Domain.Organizations.Countries.All.SelectMany(c => KnitErp.Domain.Catalog.VatRate.DefaultsFor(1, c.Code)).Select(r => r.Name));
        var labels = File.ReadAllText(Path.Combine(WebDir, "Components", "Shared", "Labels.cs"));
        foreach (Match m in AuditLabel().Matches(labels))
        {
            keys.Add(m.Groups[1].Value);
        }

        // Названия прав — в сообщении «Недостаточно прав: …».
        keys.UnionWith(typeof(Permissions).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => Permissions.Describe((string)f.GetRawConstantValue()!)));

        // Сообщения правил сервисов — шаблонами с {0} (Text.Message сопоставляет их с готовым сообщением),
        // названия сущностей для «… не найдено» и правила налоговых номеров стран.
        foreach (var dir in new[] { "KnitErp.Domain", "KnitErp.Application" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRoot(), "src", dir), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in RuleMessage().Matches(text))
                {
                    var body = Regex.Unescape(m.Groups[2].Value);
                    if (m.Groups[1].Value != "$")
                    {
                        keys.Add(body);
                    }
                    else if (Template(body) is { } template)
                    {
                        keys.Add(template);
                    }
                }

                foreach (Match m in NotFoundEntity().Matches(text))
                {
                    keys.Add(m.Groups[1].Value);
                }
            }
        }

        foreach (var country in KnitErp.Domain.Organizations.Countries.All)
        {
            keys.Add(KnitErp.Domain.Organizations.Countries.TaxIdRule(country.Code, organization: true));
            keys.Add(KnitErp.Domain.Organizations.Countries.TaxIdRule(country.Code, organization: false));
        }

        // Справочный центр: категории, заголовки и абзацы статей (их же получает ИИ-помощник).
        foreach (var article in HelpCenter.Articles)
        {
            keys.Add(article.Category);
            keys.Add(article.Title);
            keys.UnionWith(article.Body);
        }

        // Тексты, которые сервисы переводят сами (ui.Translate("…")), служебные ответы помощника
        // и сообщения недоступных внешних сервисов (погода, ИИ-помощник).
        keys.UnionWith([AssistantReplies.Refused, AssistantReplies.Empty]);
        foreach (var dir in new[] { "KnitErp.Application", "KnitErp.Infrastructure" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRoot(), "src", dir), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in ServiceText().Matches(text))
                {
                    keys.Add(Regex.Unescape(m.Groups[1].Value));
                }
            }
        }

        // Показатели главного экрана: название и подпись плитки.
        var dashboard = File.ReadAllText(Path.Combine(FindRoot(), "src", "KnitErp.Application", "Organizations", "DashboardService.cs"));
        foreach (Match m in DashboardTile().Matches(dashboard))
        {
            keys.Add(m.Groups[1].Value);
            keys.Add(m.Groups[2].Value);
        }

        foreach (Match m in DashboardChart().Matches(dashboard))
        {
            keys.Add(m.Groups[1].Value);
        }

        // Пункты «Готовности к запуску».
        var readiness = File.ReadAllText(Path.Combine(FindRoot(), "src", "KnitErp.Application", "Organizations", "LaunchReadinessService.cs"));
        foreach (Match m in ReadinessTitle().Matches(readiness))
        {
            keys.Add(m.Groups[1].Value);
        }

        return keys;
    }

    /// <summary>$"…{expr}…" → «…{0}…» (формат {x:dd.MM.yyyy} → {0:dd.MM.yyyy}); null — если внутри подстановки кавычки.</summary>
    private static string? Template(string body)
    {
        var result = new System.Text.StringBuilder();
        var index = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '{')
            {
                result.Append(body[i]);
                continue;
            }

            var end = body.IndexOf('}', i);
            var hole = body[(i + 1)..end];
            if (hole.Contains('"') || hole.Contains('{'))
            {
                return null;
            }

            var format = Format().Match(hole);
            result.Append('{').Append(index++).Append(format.Success && !hole.Contains('?') ? ":" + format.Groups[1].Value : "").Append('}');
            i = end;
        }

        return result.ToString();
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(WebDir, "Localization", $"{code}.json"))) ?? [];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден KnitErp.slnx");
    }

    [GeneratedRegex("""(?<![\w.])(?:Text\.)?(?:L|CssText)\("((?:[^"\\]|\\.)*)"[,)]""")]
    private static partial Regex Call();

    [GeneratedRegex("""\("[\w/?=&-]*", "([^"]+)", (?:a =>|Docs\))""")]
    private static partial Regex MenuItem();

    [GeneratedRegex("""new\("\w+", "([^"]+)",\s*\[""")]
    private static partial Regex MenuSection();

    [GeneratedRegex("""Soon\("[\w-]+", "([^"]+)"\)""")]
    private static partial Regex MenuPlanned();

    [GeneratedRegex(@"new DashboardTileDto\(""([^""]+)"", \w+, ""([^""]+)""")]
    private static partial Regex DashboardTile();

    [GeneratedRegex(@"charts\.Add\(Chart\(""([^""]+)""")]
    private static partial Regex DashboardChart();

    [GeneratedRegex("""BusinessRuleException\(\s*"[^"]+",\s*(\$?)"((?:[^"\\]|\\.)*)"\s*\)""")]
    private static partial Regex RuleMessage();

    [GeneratedRegex("""NotFoundException\("([^"]+)"\)""")]
    private static partial Regex NotFoundEntity();

    [GeneratedRegex(@":([0-9A-Za-z.#,%\-]+)$")]
    private static partial Regex Format();

    [GeneratedRegex("""\[AuditActions\.\w+\] = "([^"]+)",""")]
    private static partial Regex AuditLabel();

    [GeneratedRegex("""(?:ui\.Translate|ExternalServiceUnavailableException)\("((?:[^"\\]|\\.)*)"\)""")]
    private static partial Regex ServiceText();

    [GeneratedRegex("""items\.Add\(new\("\w+", "([^"]+)",""")]
    private static partial Regex ReadinessTitle();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
