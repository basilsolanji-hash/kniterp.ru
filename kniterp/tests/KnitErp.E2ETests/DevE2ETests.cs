using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D700–D706: модуль «Разработка», этап 1 — модель через форму, задание, стадии, файл рисунка, доска; телефон 390 px без
/// горизонтальной прокрутки; ноль ошибок JavaScript. Владелец — из <see cref="HrAppFixture"/>. Данные и картинки сгенерированы.
/// </summary>
public sealed class DevE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    /// <summary>PNG 1×1, сгенерированный в тесте (не реальное изображение).</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [SqlFact]
    public Task Model_goes_through_brief_stages_and_files() => RunAsync("dev-model", false, async page =>
    {
        var name = $"Свитер «Олени {Stamp}»";
        await CreateModelAsync(page, name);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex(Regex.Escape(name)) })).ToContainTextAsync("Идея");

        // Задание на разработку.
        await page.GetByRole(AriaRole.Button, new() { Name = "Изменить задание", Exact = true }).ClickAsync();
        await page.GetByLabel("Размерный ряд").FillAsync("42–52");
        await page.GetByLabel("Целевая себестоимость, ₽").FillAsync("1500");
        await page.GetByLabel("Тираж, шт").FillAsync("300");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить задание", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Задание сохранено");
        await Assertions.Expect(page.Locator("dl.facts").Last).ToContainTextAsync("42–52");

        // Стадии: «Дальше» дважды, затем перевод через список.
        await page.GetByRole(AriaRole.Button, new() { Name = "Дальше: Задание", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Стадия изменена");
        await page.GetByRole(AriaRole.Button, new() { Name = "Дальше: Рисунок", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex(Regex.Escape(name)) })).ToContainTextAsync("Рисунок");
        await page.GetByLabel("Перевести на стадию").SelectOptionAsync(new SelectOptionValue { Label = "Образец" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Перевести", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex(Regex.Escape(name)) })).ToContainTextAsync("Образец");

        // Файл рисунка: две загрузки одного имени — две версии.
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Файлы") }).ClickAsync();
        await page.GetByLabel("Вид файла").SelectOptionAsync(new SelectOptionValue { Label = "Рисунок" });
        for (var i = 0; i < 2; i++)
        {
            await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "jacquard.png", MimeType = "image/png", Buffer = Png });
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Файл загружен");
        }

        await Assertions.Expect(page.Locator(".dev-files li")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".dev-files")).ToContainTextAsync("версия 2");

        // История стадий и доска.
        await page.GetByText("История стадий").ClickAsync();
        await Assertions.Expect(page.Locator("details.panel tbody tr")).ToHaveCountAsync(4);
        await page.GotoAsync("dev/models");
        await Ready(page);
        var column = page.Locator("section.dev-col", new() { HasText = "Образец" }).First;
        await Assertions.Expect(column).ToContainTextAsync(name);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Таблица" }).ClickAsync();
        await Assertions.Expect(page.Locator("table")).ToContainTextAsync(name);
        await page.GotoAsync("dev/settings");
        await Ready(page);
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Себестоимость");
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Шаблоны задания по категориям");
    });

    [SqlFact]
    public Task Phone_board_card_and_tasks_fit_the_screen() => RunAsync("dev-phone", true, async page =>
    {
        var name = $"Шапка «Телефон {Stamp}»";
        await CreateModelAsync(page, name);
        await AssertNoHorizontalScrollAsync(page, "карточка");
        foreach (var tab in new[] { "Задание", "Рисунок", "Образцы", "Файлы", "Обсуждение" })
        {
            await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex($"^{tab}") }).ClickAsync();
            await AssertNoHorizontalScrollAsync(page, tab);
        }

        await page.GotoAsync("dev/models");
        await Ready(page);
        await Assertions.Expect(page.Locator("section.dev-col").First).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "доска");
        await page.GotoAsync("dev/my");
        await Ready(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Мои задачи разработки" })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "мои задачи");
        await page.GotoAsync("dev/settings");
        await Ready(page);
        await AssertNoHorizontalScrollAsync(page, "настройки");
    });

    [SqlFact]
    public Task Automation_fills_name_brief_and_moves_stage_by_file() => RunAsync("dev-automation", false, async page =>
    {
        // Шаблон задания для категории «Жилет».
        await page.GotoAsync("dev/settings");
        await Ready(page);
        var row = page.Locator("tr", new() { HasText = "Жилет" }).First;
        await row.GetByRole(AriaRole.Button, new() { Name = "Изменить", Exact = true }).ClickAsync();
        await page.GetByLabel("Размерный ряд").FillAsync("44–54");
        await page.GetByLabel("Состав").FillAsync("100% хлопок");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить шаблон", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Шаблон сохранён");

        // Модель без названия и даты: название, запуск и задание подставляются сами.
        await page.GotoAsync("dev/models");
        await Ready(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Новая модель", Exact = true }).ClickAsync();
        await page.GetByLabel("Категория").SelectOptionAsync(new SelectOptionValue { Label = "Жилет" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать модель", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/models/\d+$"));
        await Ready(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Жилет");
        await Assertions.Expect(page.Locator("dl.facts").Last).ToContainTextAsync("44–54");
        await Assertions.Expect(page.Locator(".dev-hints")).ToContainTextAsync("эскиз");

        // Рисунок на «Идее» — модель сама переходит на «Рисунок».
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Файлы") }).ClickAsync();
        await page.GetByLabel("Вид файла").SelectOptionAsync(new SelectOptionValue { Label = "Рисунок" });
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "zhilet.png", MimeType = "image/png", Buffer = Png });
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Файл загружен");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Рисунок");

        // «Создать похожую» открывает новую модель на «Идее» с тем же заданием.
        var url = page.Url;
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать похожую", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => u != url && Regex.IsMatch(u, @"/dev/models/\d+$"));
        await Ready(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("(копия)");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Идея");
        await Assertions.Expect(page.Locator("dl.facts").Last).ToContainTextAsync("100% хлопок");
    });

    private static async Task CreateModelAsync(IPage page, string name)
    {
        await page.GotoAsync("dev/models");
        await Ready(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Новая модель", Exact = true }).ClickAsync();
        await page.GetByLabel("Название").FillAsync(name);
        await page.GetByLabel("Категория").SelectOptionAsync(new SelectOptionValue { Label = "Свитер" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать модель", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/models/\d+$"));
        await Ready(page);
    }

    private static async Task Ready(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
    }

    private static async Task AssertNoHorizontalScrollAsync(IPage page, string where) =>
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"),
            $"Горизонтальная прокрутка на телефоне: {where}");

    private async Task RunAsync(string name, bool phone, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync(phone);
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
            await scenario(page);
            Assert.True(errors.Count == 0, "Ошибки JavaScript: " + string.Join(" | ", errors));
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
}
