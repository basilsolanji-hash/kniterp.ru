using System.Text.RegularExpressions;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Приложение для сквозных тестов CRM (D600–D609): своя тестовая база, организация со складом и позицией каталога, Владелец входит
/// с паролем и 2FA в браузере. Ширина рабочего стола — 1440 px, телефона — 390 px (ТЗ CRM §0). Данные вымышленные.
/// </summary>
public sealed class CrmAppFixture : IAsyncLifetime
{
    public const string OwnerEmail = "owner-crm@e2e.local";

    private readonly SqlTestHost _host = new();
    private WebAppProcess? _app;
    private IPlaywright? _playwright;
    private string? _ownerState;

    public string BaseUrl => _app?.BaseUrl ?? string.Empty;
    public string AppLog => _app?.Log ?? string.Empty;
    public string Password { get; } = "E2e-" + Guid.NewGuid().ToString("N")[..16] + "!a";
    public IBrowser Browser { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestHost.EnvVar)))
        {
            return;
        }

        await _host.InitializeAsync();
        await SeedAsync();
        await using (var db = _host.NewDb())
        {
            _app = await WebAppProcess.StartAsync(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetConnectionString(db.Database)!);
        }

        _playwright = await Playwright.CreateAsync();
        Browser = await WebAppProcess.LaunchBrowserAsync(_playwright);
        await SignInOwnerAsync();
    }

    public async Task DisposeAsync()
    {
        if (_playwright is not null)
        {
            await Browser.DisposeAsync();
            _playwright.Dispose();
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _host.DisposeAsync();
    }

    public async Task<IPage> OwnerPageAsync(bool phone, string colorScheme = "light")
    {
        var context = await Browser.NewContextAsync(Options(phone, _ownerState, colorScheme));
        return await context.NewPageAsync();
    }

    private BrowserNewContextOptions Options(bool phone, string? state, string colorScheme = "light") => new()
    {
        BaseURL = BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true, StorageState = state,
        ColorScheme = colorScheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        ViewportSize = phone ? new ViewportSize { Width = 390, Height = 844 } : new ViewportSize { Width = 1440, Height = 900 },
    };

    private async Task SeedAsync()
    {
        CreatedOrganization org;
        await using (var s = _host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                "Общество с ограниченной ответственностью «CRM-Трикотаж»", "ООО «CRM-Трикотаж»", TestInn(), null, false,
                "Europe/Moscow", OwnerEmail, "Владелец", "RU"));
        }

        await using (var s = _host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            await s.Catalog.CreateItemAsync(new ItemCommand("СВ-E2E", "Свитер вязаный", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            await s.Counterparties.CreateAsync(new KnitErp.Application.Catalog.CounterpartyCommand("ООО «Магазин-партнёр»", null, null, false, true, null));
        }

        await using (var s = _host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }
    }

    private static string TestInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = "8392000" + Random.Shared.Next(10, 99);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    private async Task SignInOwnerAsync()
    {
        var context = await Browser.NewContextAsync(Options(false, null));
        var page = await context.NewPageAsync();
        await page.GotoAsync("account/login");
        await page.GetByLabel("Эл. почта").FillAsync(OwnerEmail);
        await page.GetByLabel("Пароль").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("setup-2fa"), new() { Timeout = 30_000 });
        var key = (await page.Locator(".key").InnerTextAsync()).Trim();
        await page.GetByLabel("Код из приложения").FillAsync(KnitErp.Domain.Access.Totp.Compute(key.Replace(" ", ""),
            KnitErp.Domain.Access.Totp.StepAt(DateTime.UtcNow)));
        await page.GetByRole(AriaRole.Button, new() { Name = "Подключить и войти" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });
        _ownerState = await context.StorageStateAsync();
        await context.CloseAsync();
    }
}

/// <summary>
/// CRM, этап 1: лид → квалификация → сделка → канбан → заказ покупателя → подтверждение → сделка выиграна (1440 px);
/// списки и карточки на телефоне 390 px без горизонтальной прокрутки, в светлой и тёмной теме. Ошибок JavaScript — ноль.
/// </summary>
public sealed class CrmE2ETests(CrmAppFixture app) : IClassFixture<CrmAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    [SqlFact]
    public Task Lead_to_won_deal_through_sales_order() => RunAsync("crm-flow", false, "light", async page =>
    {
        await OpenAsync(page, "crm/leads");
        await page.GetByLabel("Что нужно клиенту").FillAsync($"300 свитеров {Stamp}");
        await page.GetByLabel("Контактное лицо").FillAsync("Иванова Анна");
        await page.GetByLabel("Телефон").FillAsync("+7 900 000-00-01");
        await page.GetByLabel("Компания").FillAsync($"ООО «Магазин {Stamp}»");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить лид" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/leads/\d+$"));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex($"Л-\\d+ 300 свитеров {Stamp}") })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Взять в работу" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Лид в работе");
        await page.GetByRole(AriaRole.Button, new() { Name = "Квалифицировать" }).ClickAsync();
        await page.GetByLabel("Сумма сделки").FillAsync("15000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать сделку" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/deals/\d+$"));
        var dealUrl = page.Url;
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Новая" })).ToHaveAttributeAsync("aria-current", "step");

        // Лента этапов: «Выявление потребности».
        await page.GetByRole(AriaRole.Button, new() { Name = "Выявление потребности" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Сделка на этапе «Выявление потребности»");

        // Канбан: карточка в колонке этапа, кнопка «→» переносит её дальше.
        await OpenAsync(page, "crm/deals");
        var column = page.GetByRole(AriaRole.Region, new() { Name = "Выявление потребности" });
        await Assertions.Expect(column.GetByRole(AriaRole.Link, new() { Name = $"300 свитеров {Stamp}" })).ToBeVisibleAsync();
        await column.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("на этап правее") }).First.ClickAsync();
        var samples = page.GetByRole(AriaRole.Region, new() { Name = "Образцы / расчёт" });
        await Assertions.Expect(samples.GetByRole(AriaRole.Link, new() { Name = $"300 свитеров {Stamp}" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Таблица" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex($"300 свитеров {Stamp}") })).ToContainTextAsync("Образцы / расчёт");

        // Позиция каталога и заказ покупателя из сделки.
        await page.GotoAsync(dealUrl);
        await WaitBlazorAsync(page);
        await page.GetByLabel("Поиск позиции").FillAsync("СВ-E2E");
        await page.GetByLabel("Поиск позиции").PressAsync("Tab");
        await Assertions.Expect(page.GetByLabel("Позиция каталога")).ToHaveValueAsync(new Regex(@"\d+"));
        await page.GetByLabel("Количество").FillAsync("10");
        await page.GetByLabel("Цена").FillAsync("1500");
        await page.GetByRole(AriaRole.Form, new() { Name = "Добавить позицию" }).GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Позиция добавлена");
        await Assertions.Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("СВ-E2E") })).ToContainTextAsync("15 000,00");

        await page.GetByRole(AriaRole.Button, new() { Name = "Создать заказ" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать черновик заказа" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Заказ покупателя создан");

        // Заказ подтверждается прямо из сделки → сделка «Выиграна», дальше — «В производство».
        await page.GetByRole(AriaRole.Button, new() { Name = "Подтвердить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "В производство" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Выиграна" })).ToHaveAttributeAsync("aria-current", "step");
        await Assertions.Expect(page.Locator(".page-head .tag")).ToHaveTextAsync("Выиграна");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^ЗК-\d+") })).ToBeVisibleAsync();
        await page.GetByText("История этапов").ClickAsync();
        await Assertions.Expect(page.Locator("details.panel")).ToContainTextAsync("Согласование → Выиграна");
    });

    [SqlFact]
    public Task Settings_add_pipeline_stage_and_source() => RunAsync("crm-settings", false, "dark", async page =>
    {
        await OpenAsync(page, "crm/settings");
        await page.GetByLabel("Новая воронка").FillAsync($"Маркетплейсы {Stamp}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Воронка добавлена");
        var pipeline = page.GetByRole(AriaRole.Region, new() { Name = $"Маркетплейсы {Stamp}" });
        await pipeline.GetByLabel("Новый этап").FillAsync("Пробная партия");
        await pipeline.GetByRole(AriaRole.Button, new() { Name = "Добавить этап" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Этап добавлен");
        await Assertions.Expect(pipeline.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("Пробная партия") })).ToBeVisibleAsync();
    });

    [SqlFact]
    public Task Phone_lists_and_cards_fit_390() => RunAsync("crm-phone", true, "dark", async page =>
    {
        foreach (var path in new[] { "crm/leads", "crm/deals", "crm/deals?view=table", "crm/contacts", "crm/settings", "crm/tasks", "crm/summary", "crm/mailings", "crm/chats" })
        {
            await OpenAsync(page, path);
            Assert.True(await NoHorizontalScrollAsync(page), path);
            await Shot(page, "crm-phone-" + path.Replace("crm/", "").Replace("?view=table", "-table"));
        }

        // Новая сделка с телефона и её карточка.
        await OpenAsync(page, "crm/deals");
        await page.GetByRole(AriaRole.Button, new() { Name = "Новая сделка" }).ClickAsync();
        await page.GetByLabel("Название").FillAsync($"Пледы {Stamp}");
        await page.GetByLabel("Поиск покупателя").FillAsync("Магазин");
        await page.GetByLabel("Поиск покупателя").PressAsync("Tab");
        await Assertions.Expect(page.GetByLabel("Найденные")).ToHaveValueAsync(new Regex(@"\d+"));
        Assert.True(await NoHorizontalScrollAsync(page));
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/deals/\d+$"));
        await WaitBlazorAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex($"Пледы {Stamp}") })).ToBeVisibleAsync();
        Assert.True(await NoHorizontalScrollAsync(page));
        await OpenAsync(page, "crm/deals");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = $"Пледы {Stamp}" })).ToBeVisibleAsync();
        Assert.True(await NoHorizontalScrollAsync(page));
        await page.ScreenshotAsync(new() { Path = Path.Combine(Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath(), "crm-phone-board.png"), FullPage = true });
    });

    [SqlFact]
    public Task Smart_paste_offer_print_and_new_screens() => RunAsync("crm-offer", false, "light", async page =>
    {
        // А1: умная вставка заполняет поля лида из сообщения клиента.
        await OpenAsync(page, "crm/leads");
        await page.GetByText("Умная вставка: вставьте сообщение клиента").ClickAsync();
        await page.GetByLabel("Сообщение клиента").FillAsync($"Здравствуйте! Нужно 200 шапок {Stamp}. ООО «Вязаный мир». С уважением, Мария, +7 900 000-00-02, buyer{Stamp}@example.org");
        await page.GetByRole(AriaRole.Button, new() { Name = "Разобрать" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Телефон")).ToHaveValueAsync(new Regex("900"));
        await Assertions.Expect(page.GetByLabel("Контактное лицо")).ToHaveValueAsync("Мария");
        await page.GetByLabel("Что нужно клиенту").FillAsync($"200 шапок {Stamp}");
        await Shot(page, "crm-01-lead-smart-paste");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить лид" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/leads/\d+$"));
        await WaitBlazorAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Квалифицировать" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать сделку" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/deals/\d+$"));
        await WaitBlazorAsync(page);

        // Б5: КП одной кнопкой из позиций сделки, печать / PDF.
        await page.GetByLabel("Поиск позиции").FillAsync("СВ-E2E");
        await page.GetByLabel("Поиск позиции").PressAsync("Tab");
        await Assertions.Expect(page.GetByLabel("Позиция каталога")).ToHaveValueAsync(new Regex(@"\d+"));
        await page.GetByLabel("Количество").FillAsync("200");
        await page.GetByLabel("Цена").FillAsync("900");
        await page.GetByRole(AriaRole.Form, new() { Name = "Добавить позицию" }).GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Позиция добавлена");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сформировать КП" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("КП сформировано");
        await Assertions.Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(@"КП-\d+") })).ToContainTextAsync("180 000,00");
        await Shot(page, "crm-02-deal-offer");
        var href = await page.GetByRole(AriaRole.Link, new() { Name = "Печать / PDF" }).First.GetAttributeAsync("href");
        await OpenAsync(page, href!);
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Коммерческое предложение");
        await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Свитер вязаный");
        await Shot(page, "crm-03-offer-print");

        // Новые экраны открываются без ошибок.
        foreach (var (path, name) in new[] { ("crm/tasks", "crm-04-tasks"), ("crm/summary", "crm-05-summary"), ("crm/mailings", "crm-06-mailings"),
                     ("crm/chats", "crm-07-chats"), ("crm/settings", "crm-08-settings") })
        {
            await OpenAsync(page, path);
            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".error")).ToHaveCountAsync(0);
            await Shot(page, name);
        }
    });

    [SqlFact]
    public Task Templates_messengers_mailbox_and_mailing_from_template() => RunAsync("crm-mail", false, "light", async page =>
    {
        // D670: три мессенджера в настройках; форма подключения Telegram открывается и закрывается.
        await OpenAsync(page, "crm/settings");
        var messengers = page.GetByRole(AriaRole.Region, new() { Name = "Мессенджеры" });
        foreach (var name in new[] { "Telegram", "WhatsApp", "MAX" })
        {
            await Assertions.Expect(messengers.GetByRole(AriaRole.Article, new() { Name = name })).ToBeVisibleAsync();
        }

        var whatsapp = messengers.GetByRole(AriaRole.Article, new() { Name = "WhatsApp" });
        await whatsapp.GetByRole(AriaRole.Button, new() { Name = "Подключить" }).ClickAsync();
        await Assertions.Expect(whatsapp.GetByLabel("Phone number ID")).ToBeVisibleAsync();
        await Assertions.Expect(whatsapp.GetByLabel("Секрет приложения (App secret)")).ToBeVisibleAsync();
        await whatsapp.GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Входящая почта" }).GetByRole(AriaRole.Button, new() { Name = "Подключить ящик" })).ToBeVisibleAsync();

        // D674: шаблон письма с переменными.
        var templates = page.GetByRole(AriaRole.Region, new() { Name = "Шаблоны писем" });
        await templates.GetByLabel("Название").FillAsync($"Спасибо за заявку {Stamp}");
        await templates.GetByLabel("Тема").FillAsync("{{Организация}}: заявка принята");
        await templates.GetByLabel("Текст").FillAsync("Здравствуйте, {{Имя}}! Ваш менеджер — {{Менеджер}}.");
        await templates.GetByRole(AriaRole.Button, new() { Name = "Добавить шаблон" }).ClickAsync();
        await Assertions.Expect(templates.GetByRole(AriaRole.Status)).ToContainTextAsync("Шаблон сохранён");
        await Assertions.Expect(templates.GetByRole(AriaRole.Row, new() { NameRegex = new Regex($"Спасибо за заявку {Stamp}") })).ToBeVisibleAsync();
        await templates.ScrollIntoViewIfNeededAsync();
        await Shot(page, "crm-09-settings-messengers-mail");

        // Рассылка из шаблона: тема и текст подставились с переменными.
        await OpenAsync(page, "crm/mailings");
        await page.GetByLabel("Шаблон").SelectOptionAsync(new SelectOptionValue { Label = $"Спасибо за заявку {Stamp}" });
        await Assertions.Expect(page.GetByLabel("Тема")).ToHaveValueAsync("{{Организация}}: заявка принята");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить черновик" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Черновик сохранён" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByLabel("Время отправки")).ToBeVisibleAsync();
        await Shot(page, "crm-10-mailing-template");
    });

    /// <summary>Приёмка (13.10.2026): лид → сделка → КП → заказ → счёт → оплата; оплата видна в сделке.</summary>
    [SqlFact]
    public Task Lead_deal_offer_order_invoice_payment() => RunAsync("crm-pay", false, "light", async page =>
    {
        await OpenAsync(page, "crm/leads");
        await page.GetByLabel("Что нужно клиенту").FillAsync($"150 шарфов {Stamp}");
        await page.GetByLabel("Контактное лицо").FillAsync("Ольга Оплатова");
        await page.GetByLabel("Телефон").FillAsync("+7 900 000-00-09");
        await page.GetByLabel("Компания").FillAsync($"ООО «Оплата {Stamp}»");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить лид" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/leads/\d+$"));
        await WaitBlazorAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Квалифицировать" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Создать сделку" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/crm/deals/\d+$"));
        await WaitBlazorAsync(page);
        var dealUrl = page.Url;

        await page.GetByLabel("Поиск позиции").FillAsync("СВ-E2E");
        await page.GetByLabel("Поиск позиции").PressAsync("Tab");
        await Assertions.Expect(page.GetByLabel("Позиция каталога")).ToHaveValueAsync(new Regex(@"\d+"));
        await page.GetByLabel("Количество").FillAsync("150");
        await page.GetByLabel("Цена").FillAsync("1000");
        await page.GetByRole(AriaRole.Form, new() { Name = "Добавить позицию" }).GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Позиция добавлена" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Сформировать КП" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(@"КП-\d+") })).ToContainTextAsync("150 000,00");

        // Заказ по КП → подтверждение из сделки → счёт.
        await page.GetByRole(AriaRole.Button, new() { Name = "Заказ по КП" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Создать заказ по КП-") }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Заказ покупателя создан" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Подтвердить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Выставить счёт" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Выставить счёт" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^Счёт СЧ-\d+$") })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".crm-paid")).ToContainTextAsync("Оплачено по сделке: 0,00 из 150 000,00");
        await Shot(page, "crm-11-deal-invoice");

        // Оплата — в карточке заказа (модуль продаж).
        await page.GetByRole(AriaRole.Link, new() { Name = "Открыть", Exact = true }).First.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/sales-orders/\d+$"));
        await WaitBlazorAsync(page);
        var pay = page.Locator("form", new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Записать оплату" }) });
        await pay.GetByLabel("Сумма").FillAsync("150000");
        await pay.GetByRole(AriaRole.Button, new() { Name = "Записать оплату" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Оплата записана")).ToBeVisibleAsync();

        // Сделка: выиграна, оплачена.
        await OpenAsync(page, dealUrl.Replace(app.BaseUrl, ""));
        await Assertions.Expect(page.Locator(".page-head .tag")).ToHaveTextAsync("Выиграна");
        await Assertions.Expect(page.Locator(".crm-paid")).ToContainTextAsync("150 000,00 из 150 000,00");
        await Assertions.Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(@"ЗК-\d+") })).ToContainTextAsync("оплачен");
        await Shot(page, "crm-12-deal-paid");
    });

    private static Task Shot(IPage page, string name)
    {
        var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        return page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
    }

    private static Task<bool> NoHorizontalScrollAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");

    private static async Task OpenAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        await WaitBlazorAsync(page);
    }

    private static async Task WaitBlazorAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
    }

    /// <summary>Сценарий в отдельной вкладке; ошибки JavaScript и консоли собираются — их должно быть ноль.</summary>
    private async Task RunAsync(string name, bool phone, string scheme, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync(phone, scheme);
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
            Assert.True(errors.Count == 0, string.Join("\n", errors) + "\n" + string.Concat(app.AppLog.Split('\n').Where(l => l.Contains("fail") || l.Contains("Exception") || l.Contains("   at ")).Take(40).Select(l => l + "\n")));
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
