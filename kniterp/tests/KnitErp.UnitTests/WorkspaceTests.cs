using KnitErp.Application.Workspace;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Workspace;

namespace KnitErp.UnitTests;

public sealed class WorkspaceTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2 + 2 × 2", 6)]
    [InlineData("(2 + 2) * 2", 8)]
    [InlineData("1250 + 20%", 1500)]
    [InlineData("1250 − 20%", 1000)]
    [InlineData("400 × 15%", 60)]
    [InlineData("15% от 400", 60)]
    [InlineData("50%", 0.5)]
    [InlineData("12,5 * 4", 50)]
    [InlineData("1 250,5 + 0,5", 1251)]
    [InlineData("-3 + 5", 2)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("0.1 + 0.2", 0.3)]
    public void Calculator_evaluates_like_office_calculator(string expression, double expected) =>
        Assert.Equal((decimal)expected, Calculator.Evaluate(expression));

    [Theory]
    [InlineData("")]
    [InlineData("2 +")]
    [InlineData("5 / 0")]
    [InlineData("(1 + 2")]
    [InlineData("2 + abc")]
    [InlineData("alert(1)")]
    public void Calculator_rejects_bad_input_with_message(string expression) =>
        Assert.Equal("calculator.invalid", Assert.Throws<BusinessRuleException>(() => Calculator.Evaluate(expression)).Code);

    [Fact]
    public void Calculator_formats_in_russian_style() => Assert.Equal("1 250,5", Calculator.Format(1250.5m));

    [Fact]
    public void Panel_settings_drop_unknown_and_duplicate_tools_and_clamp_visible()
    {
        var clean = PersonalToolsService.Clean(new PanelSettings(["weather", "unknown", "weather", "calculator"], 99));
        Assert.Equal(["weather", "calculator"], clean.Pinned);
        // Поиск, обучение и поддержка на своих местах всегда — в закреплённые не попадают.
        Assert.Equal(["notes"], PersonalToolsService.Clean(new PanelSettings(["search", "notes", "support", "help"], 5)).Pinned);
        Assert.Equal(5, ToolCatalog.DefaultPinned.Count);
        Assert.DoesNotContain(ToolCatalog.DefaultPinned, ToolCatalog.Fixed.Contains);
        Assert.Equal(ToolCatalog.MaxVisible, clean.Visible);
        Assert.Equal(0, PersonalToolsService.Clean(new PanelSettings([], -5)).Visible);
    }

    [Fact]
    public void Help_finds_article_for_current_screen_and_by_words()
    {
        Assert.Equal("documents", HelpCenter.ForRoute("stock-documents/15")!.Id);
        Assert.Equal("reports", HelpCenter.ForRoute("reports/turnover")!.Id);
        Assert.Equal("start", HelpCenter.ForRoute("")!.Id);
        Assert.Null(HelpCenter.ForRoute("unknown-page"));
        // D910: готовый ответ базы знаний о сторно точнее общей статьи — оба в первой тройке.
        Assert.Contains(HelpCenter.Search("как сделать сторно", 3), a => a.Id is "documents" or "qa-stock-0006");
        Assert.Contains(HelpCenter.Search("инвентаризация недостача"), a => a.Id == "inventory");
        Assert.Empty(HelpCenter.Search("а"));
    }

    [Theory]
    [InlineData("uz", "inventarizatsiya kamomad", "inventory")]
    [InlineData("kk", "түгендеу кемшілік", "inventory")]
    [InlineData("be", "інвентарызацыя недастача", "inventory")]
    [InlineData("uz", "zaxira kodlar telefon yoʻqolsa", "security")]
    [InlineData("kk", "ҚҚС мөлшерлемесі", "vat")]
    public void Help_search_understands_interface_language(string code, string query, string expected)
    {
        var language = new FileUiText(code);
        Assert.Equal(expected, HelpCenter.Search(query, 5, language)[0].Id);
        // Без перевода — только русский текст: статью на языке пользователя не находит (D913 исправляет опечатки, поэтому близкие
        // русские слова, например «недастача» → «недостача», находят русские ответы).
        Assert.DoesNotContain(HelpCenter.Search(query, 5), a => a.Id == expected);
    }

    /// <summary>Переводы из Localization/{code}.json — как в интерфейсе.</summary>
    private sealed class FileUiText(string code) : IUiText
    {
        private readonly Dictionary<string, string> _dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Root(), "src", "KnitErp.Web", "Localization", $"{code}.json")))!;

        public string LanguageCode => code;

        public string Translate(string russian) => _dict.GetValueOrDefault(russian, russian);

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(dir!.FullName, "KnitErp.slnx")))
            {
                dir = dir.Parent;
            }

            return dir.FullName;
        }
    }

    [Theory]
    [InlineData("fabrika.ru", "https://fabrika.ru/")]
    [InlineData("  https://www.ks-knit.ru/catalog ", "https://www.ks-knit.ru/catalog")]
    [InlineData("", null)]
    public void Website_is_normalized(string input, string? expected) => Assert.Equal(expected, Organization.NormalizeWebsite(input));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://files.ru")]
    [InlineData("https://")]
    public void Website_rejects_unsafe_addresses(string input) =>
        Assert.Equal("org.website.invalid", Assert.Throws<BusinessRuleException>(() => Organization.NormalizeWebsite(input)).Code);

    [Fact]
    public void Support_ticket_answer_marks_unread_for_author_and_closed_is_final()
    {
        var t = SupportTicket.Create(1, "ОБ-000001", 7, "Не проводится списание", "Пишет «не хватает остатка»", "Складские документы", Now);
        Assert.Equal((SupportTicketStatus.Open, false), (t.Status, t.UnreadByAuthor));
        t.TakeInWork(Now);
        Assert.Equal("field.required", Assert.Throws<BusinessRuleException>(() => t.Reply(" ", 8, Now)).Code);
        t.Reply("Сначала проведите поступление", 8, Now);
        Assert.Equal((SupportTicketStatus.Answered, true), (t.Status, t.UnreadByAuthor));
        t.MarkReadByAuthor();
        t.Close(Now);
        Assert.Equal("support.closed", Assert.Throws<BusinessRuleException>(() => t.Reply("ещё", 8, Now)).Code);
    }

    [Fact]
    public void Tool_data_size_is_limited() =>
        Assert.Equal("workspace.too_large", Assert.Throws<BusinessRuleException>(() =>
            UserToolData.Create(1, 1, "notes", new string('x', UserToolData.MaxJsonLength + 1), Now)).Code);
}
