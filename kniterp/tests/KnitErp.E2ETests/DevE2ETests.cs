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
    private static readonly byte[] Program = [0x53, 0x49, 0x4E, 0x00, 0x01, 0x02];
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
        await DevUi.MoveToStageAsync(page, "Образец");
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
        await page.GotoAsync("dev/settings?section=templates");
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
        await page.GotoAsync("dev/settings?section=templates");
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
        await DevUi.OpenMoreAsync(page);
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
        // D719: в техкарте вязания — только блоки дессинатора (операции и размеры — во вкладках модели); обсуждение детали — здесь же.
        await Assertions.Expect(page.Locator(".ks-tech")).Not.ToContainTextAsync("Технологические операции");
        await page.GetByPlaceholder("Написать комментарий…").FillAsync("нитеводитель 6 заедает");
        await page.GetByRole(AriaRole.Button, new() { Name = "Отправить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#discussion")).ToContainTextAsync("нитеводитель 6 заедает");

        // Этикетка детали: открывается из техкарты, штрихкод Code 128 нарисован.
        var detailId = techUrl[(techUrl.LastIndexOf('/') + 1)..];
        await page.GotoAsync($"print/dev/label?kind=detail&id={detailId}&copies=2");
        await Assertions.Expect(page.Locator(".dev-label")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".dev-label svg").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".dev-label").First).ToContainTextAsync("-D" + detailId);

        // Широкий экран: заправка и общая информация — рядом, без пустого поля справа.
        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(techUrl);
        await Ready(page);
        var feed = await page.Locator(".ks-tech .kt-main").BoundingBoxAsync();
        var info = await page.Locator(".ks-tech .kt-info").BoundingBoxAsync();
        Assert.True(info!.X > feed!.X + feed.Width - 1, "общая информация должна стоять справа от заправки");
        await page.SetViewportSizeAsync(390, 844);

        // D721: списание пряжи у образца — без пряжи в палитре объясняет, чего не хватает.
        await page.GotoAsync(modelUrl);
        await Ready(page);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Образцы" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Списать пряжу", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.Locator(".dev-yarn")).ToContainTextAsync("Нечего списывать");

        // D723: принятый образец передаём заказчику со сроком возврата — метка «У заказчика», без горизонтальной прокрутки.
        await page.GetByRole(AriaRole.Button, new() { Name = "Записать решение", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Решение записано" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Передать заказчику", Exact = true }).First.ClickAsync();
        await page.GetByLabel("Кому").FillAsync("ООО «Пример», Иванова");
        await page.GetByRole(AriaRole.Button, new() { Name = "Передать", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Образец передан заказчику" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".tag", new() { HasText = "У заказчика" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Вернули на фабрику", Exact = true })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "образец у заказчика");

        // D725: технический паспорт открывается из карточки модели как PDF.
        await DevUi.OpenMoreAsync(page);
        var passportHref = await page.GetByRole(AriaRole.Link, new() { Name = "Техпаспорт" }).GetAttributeAsync("href") ?? throw new InvalidOperationException("нет ссылки");
        await DevUi.CloseMoreAsync(page);
        var passport = await page.APIRequest.GetAsync(new Uri(new Uri(page.Url), "/" + passportHref).ToString());
        Assert.Equal(200, passport.Status);
        Assert.Equal("application/pdf", passport.Headers["content-type"]);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await passport.BodyAsync())[..4]));

        // D750: утверждённая модель — панель передачи в производство с нормами и причиной, почему пока нельзя.
        // D746: без сохранённого расчёта себестоимости утвердить нельзя — сначала версия расчёта.
        await DevUi.MoveToStageAsync(page, "Утверждено");
        await page.GetByRole(AriaRole.Button, new() { Name = "Перевести", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("сохраните расчёт себестоимости");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Себестоимость" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить версию расчёта", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Сохранена версия расчёта" })).ToBeVisibleAsync();
        await DevUi.MoveToStageAsync(page, "Утверждено");
        await page.GetByRole(AriaRole.Button, new() { Name = "Перевести", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Утверждено");
        await Assertions.Expect(page.Locator(".dev-handoff")).ToContainTextAsync("Нет ни одной нормы пряжи");
        await AssertNoHorizontalScrollAsync(page, "передача в производство");

        // D740: себестоимость — итоги плитками, строки с источником цены, без горизонтальной прокрутки на телефоне.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Себестоимость" }).ClickAsync();
        await Assertions.Expect(page.Locator(".dev-cost-tiles")).ToContainTextAsync("Маржа");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Сохранить версию расчёта", Exact = true })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "себестоимость");
        await page.GotoAsync("dev/settings?section=cost");
        await Ready(page);
        await Assertions.Expect(page.GetByLabel("Ставка работы за минуту")).ToBeVisibleAsync();

        // D760: аналитика разработки — плитки, стадии, выгрузка в Excel; на телефоне без горизонтальной прокрутки.
        await page.GotoAsync("dev/analytics");
        await Ready(page);
        await Assertions.Expect(page.Locator(".dev-cost-tiles").First).ToContainTextAsync("В работе");
        await Assertions.Expect(page.Locator("#samples-out")).ToContainTextAsync("ООО «Пример», Иванова");
        await AssertNoHorizontalScrollAsync(page, "аналитика");
        var xlsx = await page.APIRequest.GetAsync(await page.GetByRole(AriaRole.Link, new() { Name = "Выгрузить в Excel" }).GetAttributeAsync("href") is { } href
            ? new Uri(new Uri(page.Url), "/" + href).ToString() : throw new InvalidOperationException("нет ссылки")); // <base href="/">
        Assert.Equal(200, xlsx.Status);
        await page.GotoAsync("dev/machines");
        await Ready(page);
        await AssertNoHorizontalScrollAsync(page, "станки");
    });

    /// <summary>
    /// Приёмка модуля (ТЗ разработки §14): модель → рисунок → станок, деталь, заправка, программа → образец № 1 на доработку →
    /// новая версия рисунка → образец № 2 с замерами принят → расчёт и утверждение → передача → позиция каталога и черновик техкарты.
    /// Ошибок JavaScript — 0 (проверяет RunAsync); карточка на телефоне без горизонтальной прокрутки.
    /// </summary>
    [SqlFact]
    public Task Acceptance_model_from_idea_to_production() => RunAsync("dev-acceptance", false, async page =>
    {
        var yarn = await app.CreateYarnAsync("Хлопок 20/1 приёмка");
        var machineName = "STOLL №" + Random.Shared.Next(100, 999);
        await page.GotoAsync("dev/machines");
        await Ready(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить станок", Exact = true }).ClickAsync();
        await page.GetByLabel("Название").FillAsync(machineName);
        await page.GetByLabel("Нитеводителей").FillAsync("8");
        await Click(page, "Сохранить", "Сохранено");

        var name = "Свитер «Приёмка» " + Guid.NewGuid().ToString("N")[..4];
        await CreateModelAsync(page, name);
        var modelUrl = page.Url;

        // Дизайнер: одноцветная палитра с пряжей; дессинатор: рисунок (автопереход на «Рисунок»).
        await page.GetByLabel("Обозначение").FillAsync("Основной");
        await SelectContainingAsync(page.GetByLabel("Пряжа из номенклатуры"), yarn); // «код — название»
        await Click(page, "Добавить цвет", "Палитра сохранена");
        await UploadPatternAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Рисунок");

        // Пряжа и вязка модели (ТЗ §8): плотность на 10 см.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Детали и заправка" }).ClickAsync();
        await Click(page, "Изменить пряжу и вязку", null);
        await page.GetByLabel("Петель на 10 см").FillAsync("28");
        await Click(page, "Сохранить пряжу и вязку", "Пряжа и вязка сохранены");

        // Деталь: станок, вес, заправка, программа машины.
        await page.GetByLabel("Новая деталь").FillAsync("Перед");
        await Click(page, "Добавить деталь", "Деталь добавлена");
        await page.GetByRole(AriaRole.Link, new() { Name = "Перед", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/details/\d+$"));
        await Ready(page);
        await SelectContainingAsync(page.GetByLabel("Станок"), machineName);
        await page.GetByLabel("Время вязания, мин").FillAsync("12");
        await page.GetByLabel("Вес детали, г").FillAsync("250");
        await Click(page, "Сохранить", "Сохранено");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Нитеводитель 5, слева" }).CheckAsync();
        await SelectContainingAsync(page.Locator(".dev-slot").Filter(new() { HasText = "Нитеводитель 5, слева" }).GetByLabel("Цвет / нить"), "Основной");
        await Click(page, "Сохранить заправку", "Заправка сохранена");
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "pered.sin", MimeType = "application/octet-stream", Buffer = Program });
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Программа загружена" })).ToBeVisibleAsync();

        // Конструктор: сетка и табель мер.
        await page.GotoAsync(modelUrl);
        await Ready(page);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Размеры" }).ClickAsync();
        await page.GetByLabel("Размеры через запятую").FillAsync("46, 48");
        await page.GetByLabel("Базовый размер").FillAsync("46");
        await Click(page, "Сохранить сетку", "Размерная сетка сохранена");
        await page.GetByLabel("Точка измерения").FillAsync("Длина изделия");
        await page.GetByLabel("Допуск ±, см").FillAsync("1");
        await Click(page, "Добавить точку", null);
        await page.GetByLabel("Длина изделия, размер 46").FillAsync("62");
        await Click(page, "Сохранить табель мер", null);
        await page.GetByLabel("Вес изделия, размер 46").FillAsync("410");
        await Click(page, "Сохранить вес", "Вес изделия сохранён");

        // Образец № 1 — на доработку; доработка рисунка — его новая версия.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Образцы" }).ClickAsync();
        await page.GetByLabel("Размер образца").SelectOptionAsync(new SelectOptionValue { Label = "46" });
        await Click(page, "Новый образец", "Образец создан");
        await page.GetByLabel("Решение").SelectOptionAsync(new SelectOptionValue { Label = "На доработку" });
        await page.GetByLabel("Что не так / комментарий").FillAsync("рисунок съехал на 2 петли");
        await Click(page, "Записать решение", "Решение записано");
        // ТЗ §8: план доработки образца «На доработку» — что меняем.
        var sample1 = page.Locator("[data-test=dev-sample-1]");
        await sample1.GetByLabel("Что меняем").FillAsync("сдвинуть рисунок на 2 петли");
        await sample1.GetByRole(AriaRole.Button, new() { Name = "Сохранить план доработки", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "План доработки сохранён" })).ToBeVisibleAsync();
        await Assertions.Expect(sample1.Locator("[data-test=dev-sample-rework]")).ToContainTextAsync("сдвинуть рисунок на 2 петли");
        await UploadPatternAsync(page);
        await Assertions.Expect(page.Locator(".dev-files")).ToContainTextAsync("версия 2");

        // Образец № 2 с замером — принят.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Образцы" }).ClickAsync();
        await page.GetByLabel("Размер образца").SelectOptionAsync(new SelectOptionValue { Label = "46" });
        await Click(page, "Новый образец", "Образец создан");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Образец № 2" })).ToBeVisibleAsync();
        // ТЗ §8: паспорт образца — партия пряжи.
        var sample2 = page.Locator("[data-test=dev-sample-2]");
        await sample2.GetByLabel("Партия пряжи").FillAsync("П-2026-17");
        await sample2.GetByRole(AriaRole.Button, new() { Name = "Сохранить паспорт", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Паспорт образца сохранён" })).ToBeVisibleAsync();
        await Assertions.Expect(sample2.Locator("[data-test=dev-sample-making]")).ToContainTextAsync("П-2026-17");
        await page.GetByLabel("Замер Длина изделия").First.FillAsync("62.5");
        await Click(page, "Сохранить замеры", "Замеры сохранены");
        await page.GetByLabel("Решение").First.SelectOptionAsync(new SelectOptionValue { Label = "Принят" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Записать решение", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Решение записано" })).ToBeVisibleAsync();

        // ТЗ §8: сравнение образцов рядом — замеры, вес, фото; на телефоне без горизонтальной прокрутки страницы.
        var compareBoxes = page.GetByRole(AriaRole.Checkbox, new() { Name = "Сравнить", Exact = true });
        await Assertions.Expect(compareBoxes).ToHaveCountAsync(2);
        var compareButton = page.GetByRole(AriaRole.Button, new() { Name = "Сравнить выбранные" });
        await Assertions.Expect(compareButton).ToBeDisabledAsync();
        await compareBoxes.Nth(0).CheckAsync();
        await compareBoxes.Nth(1).CheckAsync();
        await Assertions.Expect(compareButton).ToBeEnabledAsync();
        await compareButton.ClickAsync();
        var compare = page.Locator("[data-test=dev-sample-compare]");
        await Assertions.Expect(compare).ToContainTextAsync("Длина изделия");
        await Assertions.Expect(compare).ToContainTextAsync("Образец № 1");
        await page.SetViewportSizeAsync(390, 844);
        await AssertNoHorizontalScrollAsync(page, "сравнение образцов");
        await page.SetViewportSizeAsync(1440, 900);

        // Расчёт себестоимости и утверждение.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Себестоимость" }).ClickAsync();
        await Click(page, "Сохранить версию расчёта", "Сохранена версия расчёта");
        await DevUi.MoveToStageAsync(page, "Утверждено");
        await page.GetByRole(AriaRole.Button, new() { Name = "Перевести", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Утверждено");

        // Передача: позиция «Готовая продукция» и черновик техкарты с нормой пряжи.
        await Assertions.Expect(page.Locator(".dev-handoff")).ToContainTextAsync(yarn);
        await page.GetByRole(AriaRole.Button, new() { Name = "Передать в производство", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".dev-handoff")).ToContainTextAsync("Передано в производство");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Передано в производство");
        var item = await page.GetByRole(AriaRole.Link, new() { Name = "Позиция в номенклатуре" }).GetAttributeAsync("href");
        await page.GetByRole(AriaRole.Link, new() { Name = "Черновик техкарты производства" }).ClickAsync();
        await Ready(page);
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync(yarn);
        await page.GotoAsync(item!);
        await Ready(page);
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync(name);

        // Карточка переданной модели на телефоне: без горизонтальной прокрутки, есть «Новая версия» и техпаспорт.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(modelUrl);
        await Ready(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Новая версия", Exact = true })).ToBeVisibleAsync();
        await DevUi.OpenMoreAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Техпаспорт" })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(page, "переданная модель");
    });

    /// <summary>
    /// D726 (ТЗ §7): библиотека рисунков → новый рисунок → сетка CSV и доли по файлу → пряжа колорита → утверждение → рисунок в модели;
    /// карточка рисунка на телефоне без горизонтальной прокрутки.
    /// </summary>
    /// <summary>ui-review §0 п. 6–7: карточка доски открывается нажатием на любое место, «назад» к списку — с тем же поиском.</summary>
    [SqlFact]
    public Task Row_opens_model_and_list_keeps_filters() => RunAsync("dev-rows", false, async page =>
    {
        var name = "Строка " + Guid.NewGuid().ToString("N")[..6];
        await CreateModelAsync(page, "Другая " + Guid.NewGuid().ToString("N")[..6]); // чтобы поиск действительно отбирал
        await CreateModelAsync(page, name);
        var modelUrl = page.Url;
        await page.GotoAsync("dev/models");
        await Ready(page);

        // Ввод в первые секунды после открытия может потеряться — повторяем и проверяем результат.
        var card = page.Locator("li.dev-cardlet", new() { HasText = name });
        var other = page.Locator("li.dev-cardlet").Filter(new() { HasNotText = name });
        for (var i = 0; i < 5 && (i == 0 || await other.CountAsync() > 0 || await card.CountAsync() == 0); i++)
        {
            await page.GetByLabel("Поиск", new() { Exact = true }).FillAsync(name);
            await page.GetByRole(AriaRole.Button, new() { Name = "Показать", Exact = true }).ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(300);
        }

        await Assertions.Expect(card).ToHaveCountAsync(1);
        await Assertions.Expect(other).ToHaveCountAsync(0);

        // Нажатие в середину карточки (не на номер) открывает модель.
        await card.ClickAsync();
        await page.WaitForURLAsync(modelUrl);
        await Ready(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "← Модели в разработке" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/models$"));
        await Ready(page);
        await Assertions.Expect(page.GetByLabel("Поиск", new() { Exact = true })).ToHaveValueAsync(name);
        await Assertions.Expect(page.Locator("li.dev-cardlet")).ToHaveCountAsync(1);
    });

    [SqlFact]
    public Task Pattern_library_card_and_model_link() => RunAsync("dev-pattern", false, async page =>
    {
        var cls = await app.CreateMachineClassAsync("E7 приёмка " + Random.Shared.Next(100, 999), 8);
        var yarn = await app.CreateYarnAsync("Шерсть рисунок " + Random.Shared.Next(100, 999));
        var name = "Олени " + Guid.NewGuid().ToString("N")[..4];
        await page.GotoAsync("dev/patterns");
        await Ready(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Новый рисунок", Exact = true }).ClickAsync();
        await page.GetByLabel("Название").FillAsync(name);
        await page.GetByLabel("Раппорт: петель").FillAsync("4");
        await SelectContainingAsync(page.GetByLabel("Класс машины").First, cls);
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать рисунок", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/dev/patterns/\d+$"));
        await Ready(page);
        var patternUrl = page.Url;
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Черновик");

        // Сетка рисунка и доли цветов по файлу.
        await Assertions.Expect(page.Locator("input[type=file]").First).ToBeEnabledAsync(); // страница стала интерактивной
        await page.Locator("input[type=file]").First.SetInputFilesAsync(new FilePayload { Name = "oleni.csv", MimeType = "text/csv", Buffer = "1,1,2,2\n1,2,2,2\n"u8.ToArray() });
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Файл загружен" })).ToBeVisibleAsync();
        await Click(page, "Посчитать доли по файлу", "Доли посчитаны");
        await Assertions.Expect(page.GetByText("Сумма долей: 100 %")).ToBeVisibleAsync();

        // Ширина изделия кратна раппорту; пряжа обоих цветов в колорите «Основной».
        await page.GetByLabel("Ширина изделия, петель").FillAsync("24");
        await Click(page, "Сохранить свойства", "Свойства сохранены");
        foreach (var color in new[] { "Цвет 1", "Цвет 2" })
        {
            await SelectContainingAsync(page.GetByLabel($"Основной, {color}: пряжа"), yarn);
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Раскладка сохранена" })).ToBeVisibleAsync();
        }

        await Click(page, "Утвердить рисунок", "Статус рисунка");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Утверждён");

        // Рисунок в модели: версия 1, колорит «Основной».
        await CreateModelAsync(page, "Свитер с рисунком " + Guid.NewGuid().ToString("N")[..4]);
        var block = page.Locator(".panel").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Рисунок модели" }) });
        await SelectContainingAsync(block.Locator("select").First, name);
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Рисунок модели сохранён" })).ToBeVisibleAsync();
        await block.GetByLabel("Колорит").SelectOptionAsync(new SelectOptionValue { Label = "Основной" });
        await Click(page, "Сохранить рисунок модели", "Рисунок модели сохранён");
        await Assertions.Expect(block).ToContainTextAsync("колорит «Основной»");

        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(patternUrl);
        await Ready(page);
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync("Использован в моделях");
        await AssertNoHorizontalScrollAsync(page, "карточка рисунка");
        await page.GotoAsync("dev/patterns");
        await Ready(page);
        await Assertions.Expect(page.Locator("table")).ToContainTextAsync(name);
        await AssertNoHorizontalScrollAsync(page, "библиотека рисунков");
    });

    private static async Task UploadPatternAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Файлы") }).ClickAsync();
        await page.GetByLabel("Вид файла").SelectOptionAsync(new SelectOptionValue { Label = "Рисунок" });
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "risunok.png", MimeType = "image/png", Buffer = Png });
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Файл загружен" })).ToBeVisibleAsync();
    }

    private static async Task SelectContainingAsync(ILocator select, string text) =>
        await select.SelectOptionAsync((await select.Locator("option", new() { HasText = text }).First.GetAttributeAsync("value"))!);

    /// <summary>Нажать кнопку и дождаться сообщения сервера (или просто ответа, если сообщение не задано).</summary>
    private static async Task Click(IPage page, string button, string? status)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).First.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (status is not null)
        {
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = status }).First).ToBeVisibleAsync();
        }
    }

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
