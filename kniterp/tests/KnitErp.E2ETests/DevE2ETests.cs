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
        foreach (var tab in new[] { "Задание", "Детали и заправка", "Размеры", "Образцы", "Операции", "Файлы", "Обсуждение" })
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

    [SqlFact]
    public Task Dessinator_threading_and_shop_tech_card_on_phone() => RunAsync("dev-tech", false, async page =>
    {
        // Станок в справочнике.
        await page.GotoAsync("dev/machines");
        await Ready(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить станок", Exact = true }).ClickAsync();
        await page.GetByLabel("Название").FillAsync($"STOLL №{Stamp}");
        await page.GetByLabel("Нитеводителей").FillAsync("8");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Сохранено");

        // Модель, палитра дизайнера и деталь.
        await CreateModelAsync(page, $"Подвяз «Полоски {Stamp}»");
        var modelUrl = page.Url;
        await page.GetByLabel("Обозначение").FillAsync("Основной");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить цвет", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Палитра сохранена");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Детали и заправка" }).ClickAsync();
        await page.GetByLabel("Новая деталь").FillAsync("Подвяз");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить деталь", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Деталь добавлена");

        // Техзадание дессинатора: станок, время, заправка нитеводителя 5 с обеих сторон.
        await page.GetByRole(AriaRole.Link, new() { Name = "Подвяз", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/details/\d+$"));
        await Ready(page);
        await page.GetByLabel("Станок").SelectOptionAsync(new SelectOptionValue { Label = $"STOLL №{Stamp} · E7 · 8 нитевод." });
        await page.GetByLabel("Время вязания, мин").FillAsync("3");
        await page.GetByLabel("Важные комментарии для цеха").FillAsync("Если хлопок 20/1 — 4 конца");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Сохранено");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Нитеводитель 5, слева" }).CheckAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Нитеводитель 5, справа" }).CheckAsync();
        await page.Locator(".dev-slot").Filter(new() { HasText = "Нитеводитель 5, слева" }).GetByLabel("Цвет / нить").SelectOptionAsync(new SelectOptionValue { Label = "Основной" });
        await page.Locator(".dev-slot").Filter(new() { HasText = "Нитеводитель 5, справа" }).GetByLabel("Цвет / нить").SelectOptionAsync(new SelectOptionValue { Label = "Основной" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить заправку", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Заправка сохранена");
        var techUrl = page.Url.Replace("/dev/details/", "/dev/tech/");

        // Образец для цеха — на вкладке «Образцы».
        await page.GotoAsync(modelUrl);
        await Ready(page);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Образцы" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Новый образец", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Образец создан");

        // Техкарта на телефоне: без горизонтальной прокрутки, заправка видна, факт записывается.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(techUrl);
        await Ready(page);
        await Assertions.Expect(page.Locator(".ks-tech .kt-row")).ToHaveCountAsync(8);
        await Assertions.Expect(page.Locator(".ks-tech")).ToContainTextAsync("Основной");
        await Assertions.Expect(page.Locator(".ks-tech")).ToContainTextAsync("3 мин");
        await AssertNoHorizontalScrollAsync(page, "техкарта");
        await page.GetByLabel("Время вязания, мин").Last.FillAsync("3.2");
        await page.GetByLabel("Вес детали, г").Last.FillAsync("101");
        await page.GetByLabel("Доля цвета Основной").FillAsync("100");
        await page.GetByRole(AriaRole.Button, new() { Name = "Записать факт", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Факт записан");
        await Assertions.Expect(page.Locator("table").Last).ToContainTextAsync("3 мин 12 с");
        await AssertNoHorizontalScrollAsync(page, "техкарта с фактом");
        await page.GotoAsync("dev/machines");
        await Ready(page);
        await AssertNoHorizontalScrollAsync(page, "станки");
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
