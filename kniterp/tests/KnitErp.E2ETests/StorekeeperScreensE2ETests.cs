using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D892 (сессия «Интерфейс», этап 2–4: роли Владелец и Кладовщик): старший кладовщик открывает все экраны описи на телефоне
/// (390 px, тёмная тема). Где права нет — страница отказа с заголовком, без ошибок; где есть — без прокрутки вбок и нарушений axe.
/// </summary>
public sealed class StorekeeperScreensE2ETests(AppFixture app) : IClassFixture<AppFixture>
{
    [KnitErp.IntegrationTests.SqlFact]
    public async Task Storekeeper_opens_every_screen_on_phone()
    {
        var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true, ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync("account/login");
            await page.GetByLabel("Эл. почта").FillAsync(AppFixture.StorekeeperEmail);
            await page.GetByLabel("Пароль").FillAsync(app.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });

            var problems = await AllScreensE2ETests.WalkAsync(page, "dark", axe: true);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
