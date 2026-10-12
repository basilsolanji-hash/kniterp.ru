using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Снимки экранов модуля «Разработка» для владельца продукта: включаются переменной KNITERP_SCREENSHOTS (папка для PNG),
/// без неё тест ничего не делает. Данные и картинки — сгенерированные.
/// </summary>
public sealed class DevScreenshotTests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [SqlFact]
    public async Task Capture_development_screens()
    {
        var dir = Environment.GetEnvironmentVariable("KNITERP_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var page = await app.OwnerPageAsync();
        await page.SetViewportSizeAsync(1440, 900);
        try
        {
            // Несколько моделей на разных стадиях — чтобы доска была наполнена.
            var names = new[] { ("Свитер «Северный олень»", "Образец"), ("Кардиган «Косы»", "Рисунок"), ("Шапка «Помпон»", "Задание"),
                ("Джемпер «Школа»", "Пряжа и вязка"), ("Шарф «Ромбы»", "Согласование") };
            long first = 0;
            var ids = new List<long>();
            foreach (var (name, stage) in names)
            {
                await Go(page, "dev/models");
                await page.GetByRole(AriaRole.Button, new() { Name = "Новая модель", Exact = true }).ClickAsync();
                await page.GetByLabel("Название").FillAsync(name);
                await page.GetByRole(AriaRole.Button, new() { Name = "Создать модель", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(new Regex(@"/dev/models/\d+$"));
                await Ready(page);
                ids.Add(long.Parse(page.Url[(page.Url.LastIndexOf('/') + 1)..]));
                if (first == 0)
                {
                    first = long.Parse(page.Url[(page.Url.LastIndexOf('/') + 1)..]);
                    await page.GetByRole(AriaRole.Button, new() { Name = "Изменить задание", Exact = true }).ClickAsync();
                    await page.GetByLabel("Описание, эскиз словами").FillAsync("Свитер оверсайз, жаккард «олени» по груди, горловина — ластик 1×1.");
                    await page.GetByLabel("Размерный ряд").FillAsync("42–52");
                    await page.GetByLabel("Цветовая гамма").FillAsync("молочный, графит, красный");
                    await page.GetByLabel("Целевая себестоимость, ₽").FillAsync("1450");
                    await page.GetByLabel("Целевая цена, ₽").FillAsync("3900");
                    await page.GetByLabel("Тираж, шт").FillAsync("300");
                    await page.GetByLabel("Состав").FillAsync("50% шерсть, 50% акрил");
                    await page.GetByLabel("Уход").FillAsync("ручная стирка 30 °C");
                    await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить задание", Exact = true }).ClickAsync();
                    await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Задание сохранено");
                }

                await page.GetByLabel("Перевести на стадию").SelectOptionAsync(new SelectOptionValue { Label = stage });
                await page.GetByRole(AriaRole.Button, new() { Name = "Перевести", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Стадия изменена");
            }

            await Go(page, $"dev/models/{first}");
            await Shot(page, dir, "02-kartochka-zadanie");
            await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Файлы") }).ClickAsync();
            await page.GetByLabel("Вид файла").SelectOptionAsync(new SelectOptionValue { Label = "Рисунок" });
            await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "jacquard-oleni.png", MimeType = "image/png", Buffer = Png });
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Файл загружен");
            await page.GetByText("История стадий").ClickAsync();
            await Shot(page, dir, "03-kartochka-faily-istoriya");

            await Go(page, "dev/models");
            await Shot(page, dir, "01-doska-po-stadiyam");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Таблица" }).ClickAsync();
            await page.WaitForTimeoutAsync(400);
            await Shot(page, dir, "04-tablica-modelei");
            await Go(page, "dev/settings");
            await Shot(page, dir, "05-nastroiki-avtomatizaciya");
            await Go(page, $"dev/models/{ids[2]}");
            await Shot(page, dir, "08-podskazka-chto-dalshe");

            await page.SetViewportSizeAsync(390, 844);
            await Go(page, "dev/models");
            await Shot(page, dir, "06-telefon-doska");
            await Go(page, $"dev/models/{first}");
            await Shot(page, dir, "07-telefon-kartochka");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task Go(IPage page, string url)
    {
        await page.GotoAsync(url);
        await Ready(page);
    }

    private static async Task Ready(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(400);
    }

    private static Task Shot(IPage page, string dir, string name) =>
        page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
}
