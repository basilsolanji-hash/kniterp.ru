using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Сессия № 1, этап 2 (D145–D149): «Настройки автоматизации», «Курсы валют» (ручной курс), «Регулярные операции» (шаблон) и загрузка
/// XML УПД поставщика в черновик поступления — на 1440 и 390 px, без горизонтальной прокрутки, 0 ошибок JS. Данные вымышленные.
/// </summary>
public sealed class AutomationE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public Task Automation_settings_are_saved() => RunAsync("automation-settings", async page =>
    {
        await OpenAsync(page, "settings/automation", "Настройки автоматизации");
        var reminders = page.GetByLabel("Напоминать покупателям об оплате счетов письмом");
        await RetryAsync(page, async () =>
        {
            await reminders.CheckAsync();
            await page.GetByLabel("За сколько дней до срока напомнить").FillAsync("5");
            await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        }, page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Настройки сохранены." }));
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByLabel("За сколько дней до срока напомнить")).ToHaveValueAsync("5");
        await Assertions.Expect(reminders).ToBeCheckedAsync();
    });

    [SqlFact]
    public Task Manual_currency_rate_is_entered() => RunAsync("currency-rates", async page =>
    {
        await OpenAsync(page, "money/currency-rates", "Курсы валют");
        await RetryAsync(page, async () =>
        {
            await page.GetByLabel("Валюта (USD, EUR, CNY)").FillAsync("CNY");
            await page.GetByLabel("Номинал", new() { Exact = true }).FillAsync("10");
            await page.GetByLabel("Курс, руб. за номинал").FillAsync("115,5");
            await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить курс" }).ClickAsync();
        }, page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Курс сохранён." }));
        var row = page.Locator("table.compact tr").Filter(new() { HasText = "CNY" });
        await Assertions.Expect(row).ToContainTextAsync(new Regex(@"115,5000"));
        await Assertions.Expect(row).ToContainTextAsync("вручную");
    });

    [SqlFact]
    public Task Recurring_template_is_created() => RunAsync("recurring", async page =>
    {
        await OpenAsync(page, "money/recurring", "Регулярные операции");
        await RetryAsync(page, () => page.GetByRole(AriaRole.Button, new() { Name = "Новый шаблон" }).ClickAsync(), page.GetByLabel("Название"));
        await page.GetByLabel("Название").FillAsync("Аренда цеха E2E");
        await page.GetByLabel("Сумма", new() { Exact = true }).FillAsync("15000");
        await page.GetByLabel("Основание").FillAsync("Договор аренды");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Шаблон сохранён.");
        await Assertions.Expect(page.Locator("table.compact tr").Filter(new() { HasText = "Аренда цеха E2E" })).ToContainTextAsync("Каждый месяц");
    });

    [SqlFact]
    public Task Supplier_upd_creates_receipt_draft() => RunAsync("upd-import", async page =>
    {
        await OpenAsync(page, $"purchase-orders/upd-import?order={app.UpdOrderId}", "Загрузка УПД поставщика");
        var line = page.Locator("table.compact tr").Filter(new() { HasText = "Доставка" });
        await RetryAsync(page, () => page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "ON_NSCHFDOPPR_e2e.xml", MimeType = "text/xml", Buffer = app.UpdXml,
        }), line);
        await Assertions.Expect(page.Locator("table.compact tr").Filter(new() { HasText = "Пряжа шерсть" })).ToContainTextAsync("ПР-E2E");
        await Assertions.Expect(line).ToContainTextAsync("не найдена");
        await line.GetByRole(AriaRole.Button, new() { Name = "Пропустить" }).ClickAsync();
        await Assertions.Expect(line).ToContainTextAsync("пропущена");
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать поступление" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/stock-documents/\d+$"));
        await Assertions.Expect(page.GetByText("E2E-1").First).ToBeVisibleAsync();
    });

    private static async Task OpenAsync(IPage page, string url, string heading)
    {
        await page.GotoAsync(url);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Exact = true })).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
    }

    /// <summary>До подключения Blazor действие теряется — повторяем, пока не появится результат (не ждём время).</summary>
    private static async Task RetryAsync(IPage page, Func<Task> action, ILocator appears)
    {
        for (var attempt = 0; attempt < 10 && !await appears.IsVisibleAsync(); attempt++)
        {
            await action();
            try
            {
                await appears.WaitForAsync(new() { Timeout = 1500 });
            }
            catch (TimeoutException)
            {
            }
        }

        await Assertions.Expect(appears).ToBeVisibleAsync();
    }

    private async Task RunAsync(string name, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) =>
        {
            if (m.Type == "error")
            {
                errors.Add(m.Text);
            }
        };

        try
        {
            await page.SetViewportSizeAsync(1440, 900);
            await scenario(page);
            Assert.True(await NoScrollAsync(page), "горизонтальная прокрутка на 1440");
            await page.SetViewportSizeAsync(390, 844);
            await page.ReloadAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.True(await NoScrollAsync(page), "горизонтальная прокрутка на 390");
            Assert.Empty(errors);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}-failure.png"), FullPage = true });
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static Task<bool> NoScrollAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");
}
