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

    /// <summary>Техкарта вязания по образцу владельца «Артикул 65153»: заполняется через интерфейс ролями и снимается на 1440 и 390 px.</summary>
    [SqlFact]
    public async Task Capture_tech_card_screens()
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
            // Станок и его журнал.
            await Go(page, "dev/machines");
            await page.GetByRole(AriaRole.Button, new() { Name = "Добавить станок", Exact = true }).ClickAsync();
            await page.GetByLabel("Название").FillAsync("STOLL №3");
            await page.GetByLabel("Модель").FillAsync("CMS 530");
            await page.GetByLabel("Нитеводителей").FillAsync("8");
            await page.GetByLabel("Рабочая ширина, см").FillAsync("130");
            await page.GetByLabel("Систем").FillAsync("2");
            await Save(page, "Сохранить", "Сохранено");
            await page.GetByRole(AriaRole.Button, new() { Name = "Журнал и программы" }).First.ClickAsync();
            await page.GetByLabel("Комментарий").FillAsync("перенёс на новое место, нитеводитель 6 заедает");
            await page.GetByLabel("Состояние").SelectOptionAsync(new SelectOptionValue { Label = "Работает с ограничениями" });
            await Save(page, "Записать", "Записано");

            // Справочники технолога: переплетение и операции.
            await Go(page, "dev/settings");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Переплетения" }).ClickAsync();
            await page.Locator("form.inline-form").Last.GetByLabel("Название").FillAsync("Ластик 2×2");
            await Save(page, "Добавить", "Добавлено");
            foreach (var (name, norm, stage) in new[] { ("Вязание", "3", "Вязание"), ("Убрать боковые нитки", "0.3", ""), ("Парить", "0.1", "ВТО"),
                         ("Разделить поштучно", "0.2", ""), ("Сложить по 5 штук, сгиб", "0.2", ""), ("Упаковать в коробки", "0.1", "Упаковка"), ("Маркировать", "0.1", "") })
            {
                await page.GetByLabel("Операция", new() { Exact = true }).FillAsync(name);
                await page.GetByLabel("Норма, мин").Last.FillAsync(norm);
                if (stage.Length > 0)
                {
                    await page.GetByLabel("Этап производства").Last.SelectOptionAsync(new SelectOptionValue { Label = stage });
                }

                await Save(page, "Добавить операцию", "Сохранено");
            }

            // Дизайнер: модель, задание, палитра.
            await Go(page, "dev/models");
            await page.GetByRole(AriaRole.Button, new() { Name = "Новая модель", Exact = true }).ClickAsync();
            await page.GetByLabel("Название").FillAsync("Подвяз с полосками, 2 цвета");
            await page.GetByLabel("Категория").SelectOptionAsync(new SelectOptionValue { Label = "Аксессуар" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Создать модель", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/dev/models/\d+$"));
            await Ready(page);
            var modelUrl = page.Url;
            await page.GetByRole(AriaRole.Button, new() { Name = "Изменить задание", Exact = true }).ClickAsync();
            await page.GetByLabel("Состав").FillAsync("Хлопок 85% / Спандекс 15%");
            await Save(page, "Сохранить задание", "Задание сохранено");
            foreach (var (label, color) in new[] { ("Основной", "#f2efe6"), ("доп.1", "#2ba89a"), ("доп.2", "#d4a04a") })
            {
                await page.GetByLabel("Обозначение").FillAsync(label);
                await page.GetByLabel("Цвет на экране").FillAsync(color);
                await Save(page, "Добавить цвет", "Палитра сохранена");
            }

            // Деталь и техзадание дессинатора.
            await page.GetByRole(AriaRole.Tab, new() { Name = "Детали и заправка" }).ClickAsync();
            await page.GetByLabel("Новая деталь").FillAsync("Подвяз");
            await Save(page, "Добавить деталь", "Деталь добавлена");
            await page.GetByRole(AriaRole.Link, new() { Name = "Подвяз", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/dev/details/\d+$"));
            await Ready(page);
            var detailUrl = page.Url;
            await page.GetByLabel("Станок").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await page.GetByLabel("Переплетение").SelectOptionAsync(new SelectOptionValue { Label = "Ластик 2×2" });
            await page.GetByLabel("Время вязания, мин").FillAsync("3");
            await page.GetByLabel("Вес детали, г").FillAsync("100");
            await page.GetByLabel("Важные комментарии для цеха").FillAsync("Внимательно читаем заправку.\nЕсли хлопок толщиной 20/1 — заправляем в 4 конца.\nЕсли хлопок толщиной 24/1 — заправляем в 5 концов.");
            await Save(page, "Сохранить", "Сохранено");
            var slots = new (int F, string Side, string? Color, string? Label, int Ends, bool Paraffin, bool Elastic)[]
            {
                (8, "слева", "доп.2", null, 4, true, false), (8, "справа", null, "отработка в цвет основы", 2, true, false),
                (7, "справа", null, "Спандекс — в цвет доп.1", 1, false, true),
                (6, "слева", "доп.1", null, 2, true, false), (6, "справа", "доп.1", null, 2, true, false),
                (5, "слева", "Основной", null, 2, true, false), (5, "справа", "Основной", null, 2, true, false),
                (4, "справа", null, "Спандекс — в цвет основы", 1, false, true),
                (3, "слева", "Основной", null, 2, true, false), (3, "справа", "Основной", null, 2, true, false),
                (2, "слева", null, "Спандекс — в цвет доп.2", 1, false, true), (2, "справа", null, "Спандекс — в цвет основы", 1, false, true),
                (1, "справа", null, "Разделитель (нейлон)", 1, false, false),
            };
            foreach (var x in slots)
            {
                var name = $"Нитеводитель {x.F}, {x.Side}";
                await page.GetByRole(AriaRole.Checkbox, new() { Name = name }).CheckAsync();
                var slot = page.Locator(".dev-slot").Filter(new() { HasText = name });
                if (x.Color is not null)
                {
                    await slot.GetByLabel("Цвет / нить").SelectOptionAsync(new SelectOptionValue { Label = x.Color });
                }
                else
                {
                    await slot.GetByLabel("Какая нить").FillAsync(x.Label!);
                }

                await slot.GetByLabel("Нитей").FillAsync(x.Ends.ToString());
                if (!x.Paraffin)
                {
                    await slot.GetByRole(AriaRole.Checkbox, new() { Name = "парафин" }).UncheckAsync();
                }

                if (x.Elastic)
                {
                    await slot.GetByRole(AriaRole.Checkbox, new() { Name = "эластан" }).CheckAsync();
                }
            }

            await Save(page, "Сохранить заправку", "Заправка сохранена");
            await Shot(page, dir, "11-dessinator-zapravka");

            // Конструктор: сетка, размеры детали, упаковка.
            await Go(page, modelUrl);
            await page.GetByRole(AriaRole.Tab, new() { Name = "Размеры" }).ClickAsync();
            await page.GetByLabel("Размеры через запятую").FillAsync("1, 2, 3, 4");
            await page.GetByLabel("Базовый размер").FillAsync("2");
            await Save(page, "Сохранить сетку", "Размерная сетка сохранена");
            var off = new[] { "16", "18", "20", "22.5" };
            var after = new[] { "14", "16", "18", "20" };
            var box = new[] { "70", "60", "50", "50" };
            for (var i = 0; i < 4; i++)
            {
                await page.GetByLabel($"Подвяз, Длина с машины, размер {i + 1}").FillAsync(off[i]);
                await page.GetByLabel($"Подвяз, Длина после ВТО, размер {i + 1}").FillAsync(after[i]);
            }

            await Save(page, "Сохранить размеры деталей", "Размеры деталей сохранены");
            for (var i = 0; i < 4; i++)
            {
                await page.GetByLabel($"Штук в коробке, размер {i + 1}").FillAsync(box[i]);
            }

            await page.GetByLabel("Размер короба").FillAsync("40 × 30 × 30 см");
            await Save(page, "Сохранить упаковку", "Упаковка сохранена");
            await Shot(page, dir, "12-konstruktor-razmery");

            // Технолог: операции с пояснениями для цеха.
            await page.GetByRole(AriaRole.Tab, new() { Name = "Операции" }).ClickAsync();
            foreach (var (name, note) in new[] { ("Вязание", "Основной цикл, ластик 2×2 — см. тех. эскиз машины"), ("Убрать боковые нитки", "и разделить по 5 полосок"),
                         ("Парить", "На второй день парить 2 секунды"), ("Разделить поштучно", ""), ("Сложить по 5 штук, сгиб", ""),
                         ("Упаковать в коробки", "Размер короба — см. блок 5"), ("Маркировать", "Указать цвет доп.1 и цвет доп.2") })
            {
                var select = page.GetByLabel("Операция из справочника");
                var value = await select.Locator("option", new() { HasText = name }).First.GetAttributeAsync("value");
                await select.SelectOptionAsync(value!);
                await page.GetByLabel("Пояснение для цеха", new() { Exact = true }).FillAsync(note);
                await Save(page, "Добавить операцию", "Операция добавлена");
            }

            await Shot(page, dir, "16-tehnolog-operacii");

            await Go(page, $"dev/tech/{detailUrl[(detailUrl.LastIndexOf('/') + 1)..]}");
            await Shot(page, dir, "10-tehkarta-vyazaniya");
            await Go(page, $"print/dev/tech/{detailUrl[(detailUrl.LastIndexOf('/') + 1)..]}");
            await Shot(page, dir, "14-tehkarta-pechat");
            await Go(page, "dev/machines");
            await Shot(page, dir, "15-stanki");
            await page.SetViewportSizeAsync(390, 844);
            await Go(page, $"dev/tech/{detailUrl[(detailUrl.LastIndexOf('/') + 1)..]}");
            await Shot(page, dir, "13-tehkarta-telefon");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task Save(IPage page, string button, string status)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).First.ClickAsync();
        // Прежнее сообщение с тем же текстом могло остаться на экране — даём кнопке отработать.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(400);
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = status }).First).ToBeVisibleAsync();
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
