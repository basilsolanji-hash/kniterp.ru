using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// База знаний помощника (docs/tz/knowledge-base.md, D900–D929): помощник находит готовый ответ по вопросу своими словами
/// и показывает кнопку «Открыть» экрана — на компьютере и на телефоне 390 px без горизонтальной прокрутки.
/// Снимки экрана — в KNITERP_SCREENSHOTS, если переменная задана.
/// </summary>
public sealed class AssistantKbE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public Task Desktop_assistant_finds_answer() => AskAsync(false, "как выставить счет покупателю по заказу", "Открыть");

    [SqlFact]
    public Task Phone_assistant_finds_answer() => AskAsync(true, "как провести поступление товара на склад", "Открыть");

    private async Task AskAsync(bool phone, string question, string openLabel)
    {
        var page = await app.OwnerPageAsync(phone);
        var name = phone ? "assistant-phone" : "assistant-desktop";
        try
        {
            await page.GotoAsync("");
            await page.WaitForSelectorAsync("nav", new() { Timeout = 30_000 });
            await page.EvaluateAsync("() => document.dispatchEvent(new Event('knit:action'))");
            var input = page.GetByLabel("Вопрос");
            await input.FillAsync(question);
            await page.GetByRole(AriaRole.Button, new() { Name = "Спросить" }).ClickAsync();
            var answer = page.Locator(".chat-msg.bot .kb-answer").First;
            await answer.WaitForAsync(new() { Timeout = 15_000 });
            Assert.True((await answer.InnerTextAsync()).Length > 40);
            Assert.True(await page.Locator(".chat-msg.bot").GetByRole(AriaRole.Link, new() { Name = openLabel }).First.IsVisibleAsync());
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"),
                "Горизонтальная прокрутка страницы");
            await Shot(page, name);
        }
        catch
        {
            await Shot(page, name + "-failure");
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [SqlFact]
    public async Task Help_search_shows_qa_answer_with_sources()
    {
        var page = await app.OwnerPageAsync(phone: true);
        try
        {
            await page.GotoAsync("stock-documents");
            await page.WaitForSelectorAsync("nav", new() { Timeout = 30_000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Справка и обучение" }).First.ClickAsync();
            await page.GetByLabel("Поиск по справке").FillAsync("отменить проведённое списание");
            var first = page.Locator(".help-results li button").First;
            await first.WaitForAsync(new() { Timeout = 15_000 });
            await Shot(page, "help-search-phone");
            await first.ClickAsync();
            await page.Locator(".help-article").WaitForAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
            await Shot(page, "help-answer-phone");
        }
        catch
        {
            await Shot(page, "help-failure");
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task Shot(IPage page, string name)
    {
        var dir = Environment.GetEnvironmentVariable("KNITERP_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}.png"), FullPage = false });
        }
    }
}
