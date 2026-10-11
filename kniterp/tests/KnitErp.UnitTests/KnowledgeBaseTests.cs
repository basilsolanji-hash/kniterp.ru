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
            Assert.StartsWith("kb-", e.Article.Id);
            Assert.StartsWith("Отрасль", e.Article.Category);
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
    public void Search_finds_industry_and_program_answers(string query, string expected) =>
        Assert.Contains(HelpCenter.Search(query, 5), a => a.Id == expected);
}
