using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>Сквозной сценарий производственного заказа (D90) в браузере: вход, черновик, запуск с фиксацией норм, отмена с причиной.</summary>
public sealed class ProductionOrderE2ETests(AppFixture app) : IClassFixture<AppFixture>
{
    [SqlFact]
    public async Task Workshop_head_creates_releases_and_cancels_production_order()
    {
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        try
        {
            await ScenarioAsync(page);
        }
        catch
        {
            // Снимок и текст страницы — в папку KNITERP_E2E_ARTIFACTS (по умолчанию временная): по ним видно, на чём остановился сценарий.
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "production-order-failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(dir, "production-order-failure.txt"), page.Url + "\n" + await page.InnerTextAsync("body"));
            throw;
        }
    }

    private async Task ScenarioAsync(IPage page)
    {

        await page.GotoAsync("account/login");
        await page.GetByLabel("Эл. почта").FillAsync(AppFixture.UserEmail);
        await page.GetByLabel("Пароль").FillAsync(app.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });

        await page.GotoAsync("production-orders");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Производственные заказы" })).ToBeVisibleAsync();

        // Кнопки работают, когда страница стала интерактивной: повторяем нажатие, пока не откроется форма.
        await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Новый заказ" }),
            page.GetByRole(AriaRole.Heading, new() { Name = "Новый производственный заказ" }));
        await page.GetByLabel("Изделие").FillAsync("СВ-1 — Свитер");
        await page.GetByLabel("Изделие").BlurAsync();
        await Assertions.Expect(page.GetByText("Техкарта версия 1 — зафиксируется при запуске.")).ToBeVisibleAsync();
        await page.GetByLabel("Количество").FillAsync("20");
        await page.GetByLabel("Склад готовой продукции", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Склад готовой продукции" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать черновик" }).ClickAsync();

        await page.WaitForURLAsync(new Regex(@"/production-orders/\d+$"));
        await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Черновик");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Материалы по действующей техкарте" })).ToBeVisibleAsync();
        // 20 свитеров: 3,2 × 2 = 6,4 кг пряжи, с отходом 3% — 6,592 кг.
        await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = "6,592", Exact = true })).ToBeVisibleAsync();

        await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Запустить", Exact = true }),
            page.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Запущен");
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Заказ запущен.");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Материалы по нормам заказа" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Изменить", Exact = true })).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Отменить заказ" }).ClickAsync();
        await page.GetByLabel("Причина отмены").FillAsync("Ошибка планирования (e2e)");
        await page.GetByRole(AriaRole.Button, new() { Name = "Отменить заказ" }).ClickAsync();
        await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Отменён");
        await Assertions.Expect(page.GetByText("Ошибка планирования (e2e)")).ToBeVisibleAsync();

        await page.GotoAsync("production-orders?status=9");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^ПЗ-") })).ToHaveCountAsync(1);
    }

    /// <summary>D92: начальник цеха отмечает этапы в карточке; больше пришедшего с предыдущего этапа отметить нельзя.</summary>
    [SqlFact]
    public async Task Workshop_head_marks_stages_and_sees_limit()
    {
        var order = await app.CreateReleasedOrderAsync(10);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync($"production-orders/{order}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Ход работ" })).ToBeVisibleAsync();

        // Ввод повторяется вместе с нажатием: до подключения интерактивного режима значение поля может сброситься.
        // Повтор — только если отметка не дошла до сервера (нет ответа или «укажите количество»), иначе можно отметить дважды.
        for (var i = 0; i < 20 && !await page.GetByText("Отмечено.").IsVisibleAsync(); i++)
        {
            await page.GetByLabel("Количество", new() { Exact = true }).FillAsync("6");
            await page.GetByRole(AriaRole.Button, new() { Name = "Отметить" }).ClickAsync();
            try
            {
                await page.Locator(".flash").First.WaitForAsync(new() { Timeout = 2000 });
            }
            catch (TimeoutException)
            {
            }
        }

        await Assertions.Expect(page.GetByText("Отмечено.")).ToBeVisibleAsync();
        var knitting = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Вязание" });
        await Assertions.Expect(knitting).ToContainTextAsync("6 шт");

        await page.GetByLabel("Этап", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Сборка" });
        await page.GetByLabel("Количество", new() { Exact = true }).FillAsync("7");
        await page.GetByRole(AriaRole.Button, new() { Name = "Отметить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("можно отметить не больше 6");

        await page.GetByLabel("Количество", new() { Exact = true }).FillAsync("6");
        await page.GetByRole(AriaRole.Button, new() { Name = "Отметить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Сборка" })).ToContainTextAsync("6 шт");
        // После отметок заказ не отменяется — кнопки нет.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Отменить заказ" })).ToHaveCountAsync(0);
        await page.GetByText("История отметок (2)").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = "Начальник цеха" })).ToHaveCountAsync(2);
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync("account/login");
        await page.GetByLabel("Эл. почта").FillAsync(AppFixture.UserEmail);
        await page.GetByLabel("Пароль").FillAsync(app.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });
    }

    private static async Task ClickUntilAsync(ILocator button, ILocator appears)
    {
        for (var i = 0; i < 20; i++)
        {
            if (await button.IsEnabledAsync())
            {
                await button.ClickAsync();
            }

            try
            {
                await appears.WaitForAsync(new() { Timeout = 1000 });
                return;
            }
            catch (TimeoutException)
            {
            }
        }

        await Assertions.Expect(appears).ToBeVisibleAsync();
    }
}
