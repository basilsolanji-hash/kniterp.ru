using KnitErp.Application.Workspace;

namespace KnitErp.UnitTests;

/// <summary>D116: отраслевая база знаний — статьи загружены, оформлены и находятся поиском помощника.</summary>
public sealed class KnowledgeBaseTests
{
    [Fact]
    public void Every_article_is_complete_unique_and_sourced()
    {
        Assert.True(IndustryKnowledge.Entries.Count >= 20);
        Assert.Equal(IndustryKnowledge.Entries.Count, IndustryKnowledge.Entries.Select(e => e.Article.Id).Distinct().Count());
        Assert.All(IndustryKnowledge.Entries, e =>
        {
            // Отраслевые статьи — kb-, статьи модулей — с префиксом модуля (docs/tz/assistant.md §4.5).
            Assert.Matches("^(kb|prod|hr|cat)-[a-z0-9-]+$", e.Article.Id);
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
    [InlineData("как провести сторно", "documents")]
    [InlineData("как исправить отметку на терминале", "prod-correction")]
    [InlineData("баллы отпуск болезнь табель", "prod-points")]
    [InlineData("маршрут изделия этапы", "prod-route")]
    public void Search_finds_industry_and_program_answers(string query, string expected) =>
        Assert.Contains(HelpCenter.Search(query, 5), a => a.Id == expected);
}
