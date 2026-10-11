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

    /// <summary>D93: кладовщик выдаёт пряжу в цех из карточки заказа и возвращает часть обратно.</summary>
    [SqlFact]
    public async Task Storekeeper_issues_yarn_to_workshop()
    {
        var order = await app.CreateReleasedOrderAsync(20);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page, AppFixture.StorekeeperEmail);
        await page.GotoAsync($"production-orders/{order}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Материалы и баланс" })).ToBeVisibleAsync();
        // Количество подставлено по плану: 6,592 кг на 20 свитеров.
        await Assertions.Expect(page.GetByLabel("Количество: ПР-1")).ToHaveValueAsync("6,592");

        for (var i = 0; i < 20 && !await page.GetByText("Документ проведён.").IsVisibleAsync(); i++)
        {
            await page.GetByLabel("Склад материалов").SelectOptionAsync(new SelectOptionValue { Label = "Склад пряжи" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Провести" }).ClickAsync();
            try
            {
                await page.Locator(".flash").First.WaitForAsync(new() { Timeout = 2000 });
            }
            catch (TimeoutException)
            {
            }
        }

        await Assertions.Expect(page.GetByText("Документ проведён.")).ToBeVisibleAsync();
        // Ячейки таблицы баланса — по подписи колонки (data-label), в карточке две таблицы с этой пряжей.
        await Assertions.Expect(page.Locator("td[data-label='Выдано']")).ToHaveTextAsync("6,592");
        await Assertions.Expect(page.Locator("td[data-label='В цехе (НЗП)']")).ToHaveTextAsync("6,592");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^ПМ-") })).ToHaveCountAsync(1);

        // Возврат части пряжи из цеха на склад (кладовщику выдано право на склад цеха).
        await page.GetByLabel("Операция").SelectOptionAsync(new SelectOptionValue { Label = "Возврат из цеха" });
        await page.GetByLabel("Количество: ПР-1").FillAsync("1");
        await page.GetByLabel("Склад материалов").SelectOptionAsync(new SelectOptionValue { Label = "Склад пряжи" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Провести" }).ClickAsync();
        await Assertions.Expect(page.Locator("td[data-label='Возвращено']")).ToHaveTextAsync("1");
        await Assertions.Expect(page.Locator("td[data-label='В цехе (НЗП)']")).ToHaveTextAsync("5,592");

        // D97: возврат оформили по ошибке — сторно из карточки заказа (последний документ — первым в списке).
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Сторно ПМ-") }).First.ClickAsync();
        await page.GetByLabel("Причина сторно", new() { Exact = true }).FillAsync("Вернули по ошибке");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сторнировать" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Документ сторнирован.")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("td[data-label='Возвращено']")).ToHaveTextAsync("0");
        await Assertions.Expect(page.Locator("td[data-label='В цехе (НЗП)']")).ToHaveTextAsync("6,592");
    }

    /// <summary>D95: кладовщик выпускает продукцию с расходом по норме и возвращает остаток пряжи; начальник цеха закрывает заказ.</summary>
    [SqlFact]
    public async Task Output_return_rest_and_close_order()
    {
        var order = await app.CreatePackedOrderAsync(10);
        await using (var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true }))
        {
            var page = await context.NewPageAsync();
            await LoginAsync(page, AppFixture.StorekeeperEmail);
            await page.GotoAsync($"production-orders/{order}");
            await Assertions.Expect(page.GetByText("Выпущено 0 из 10 шт, готово к выпуску (упаковано): 10.")).ToBeVisibleAsync();
            await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Рассчитать расход" }), page.GetByLabel("Расход: ПР-1"));
            // Норма без отхода: 3,2 кг на 10 свитеров; выдано 3,296 (с отходом 3%).
            await Assertions.Expect(page.GetByLabel("Расход: ПР-1")).ToHaveValueAsync("3,2");
            await page.GetByRole(AriaRole.Button, new() { Name = "Выпустить", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Продукция выпущена на склад.")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Выполнен");
            await Assertions.Expect(page.Locator("td[data-label='В цехе (НЗП)']")).ToHaveTextAsync("0,096");

            // После полного выпуска выдачи нет — по умолчанию возврат остатка.
            await Assertions.Expect(page.GetByLabel("Операция")).ToHaveValueAsync("Return");
            await page.GetByLabel("Количество: ПР-1").FillAsync("0,096");
            await page.GetByLabel("Склад материалов").SelectOptionAsync(new SelectOptionValue { Label = "Склад пряжи" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Провести" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Документ проведён.")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("td[data-label='В цехе (НЗП)']")).ToHaveTextAsync("0");

            // D96: накладная на передачу готовой продукции по образцу МХ-18.
            var print = await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Печать ПТ-") }).GetAttributeAsync("href");
            var act = await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Печать СП-") }).GetAttributeAsync("href");
            await page.GotoAsync(print!);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^НАКЛАДНАЯ НА ПЕРЕДАЧУ ГОТОВОЙ ПРОДУКЦИИ") })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("0335018")).ToBeVisibleAsync();

            // Акт расхода материалов при выпуске: норма на выпущенное и факт.
            await page.GotoAsync(act!);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^АКТ РАСХОДА МАТЕРИАЛОВ В ПРОИЗВОДСТВО") })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Председатель комиссии")).ToBeVisibleAsync();
        }

        await using (var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true }))
        {
            var page = await context.NewPageAsync();
            await LoginAsync(page);
            await page.GotoAsync($"production-orders/{order}");
            await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Закрыть заказ" }), page.GetByLabel("Причина закрытия"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Закрыть заказ" }).ClickAsync();
            await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Закрыт");
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Заказ закрыт.");
        }
    }

    /// <summary>D98: начальник цеха видит заказ в отчёте по производству и скачивает отчёт в Excel.</summary>
    [SqlFact]
    public async Task Workshop_head_opens_production_report_and_downloads_excel()
    {
        var order = await app.CreatePackedOrderAsync(7);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync("reports/production");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Отчёт по производству" })).ToBeVisibleAsync();
        var row = page.GetByRole(AriaRole.Row).Filter(new() { Has = page.Locator($"a[href='production-orders/{order}']") });
        await Assertions.Expect(row).ToContainTextAsync("7 шт");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Брак по причинам" })).ToBeVisibleAsync();
        // Себестоимость начальнику цеха без права «Цены и суммы» не показывается.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Себестоимость: план и факт" })).ToHaveCountAsync(0);

        var href = await page.GetByRole(AriaRole.Link, new() { Name = "Excel" }).GetAttributeAsync("href");
        var response = await page.APIRequest.GetAsync(href!);
        Assert.Equal(200, response.Status);
        Assert.StartsWith("application/vnd.openxmlformats", response.Headers["content-type"]);
    }

    /// <summary>D99: на карточке производственного заказа есть теги и обсуждение; комментарий виден после отправки.</summary>
    [SqlFact]
    public async Task Workshop_head_comments_on_production_order()
    {
        var order = await app.CreateReleasedOrderAsync(3);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync($"production-orders/{order}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обсуждение" })).ToBeVisibleAsync();
        var text = page.GetByLabel("Новый комментарий");
        // Ввод с событием oninput: до подключения интерактивного режима кнопка «Отправить» не включится — повторяем ввод.
        for (var i = 0; i < 20 && !await page.GetByRole(AriaRole.Button, new() { Name = "Отправить" }).IsEnabledAsync(); i++)
        {
            await text.FillAsync("Нитки для манжет — с партии 12");
            await page.WaitForTimeoutAsync(500);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Отправить" }).ClickAsync();
        await Assertions.Expect(page.Locator("section.discussion")).ToContainTextAsync("Нитки для манжет — с партии 12");
    }

    /// <summary>D90 (дополнение): из карточки заказа покупателя — черновики производственных заказов одной кнопкой.</summary>
    [SqlFact]
    public async Task Production_orders_created_from_sales_order_card()
    {
        var sales = await app.CreateConfirmedSalesOrderAsync(12);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync($"sales-orders/{sales}");
        var panel = page.GetByRole(AriaRole.Region, new() { Name = "Производство" });
        await Assertions.Expect(panel).ToContainTextAsync("12 шт");
        await ClickUntilAsync(panel.GetByRole(AriaRole.Button, new() { Name = "Создать производственные заказы" }), panel.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }));
        await panel.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }).ClickAsync();
        await Assertions.Expect(panel.GetByRole(AriaRole.Status)).ToContainTextAsync("Созданы черновики: ПЗ-");
        await Assertions.Expect(panel).ToContainTextAsync("Всё, что можно произвести, уже запланировано.");
        await panel.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^ПЗ-") }).Last.ClickAsync();
        await Assertions.Expect(page.Locator("h1 .tag")).ToHaveTextAsync("Черновик");
        await Assertions.Expect(page.GetByText("Заказ покупателя ЗК-")).ToBeVisibleAsync();
    }

    /// <summary>D90 (дополнение «сборка из полуфабрикатов»): из заказа на кофты — заказ на рукава кнопкой у материала.</summary>
    [SqlFact]
    public async Task Workshop_head_creates_semi_finished_order_from_material()
    {
        var order = await app.CreateOrderWithSemiFinishedAsync(5);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync($"production-orders/{order}");
        await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Заказ на полуфабрикат: РУ-1" }),
            page.GetByText("Полуфабрикат для заказа", new() { Exact = true }));
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("РУ-1 Рукав");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^ПЗ-") }).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("10 шт")).ToBeVisibleAsync(); // 2 рукава × 5 кофт
    }

    /// <summary>D94 (дополнение «планирование»): начальник цеха видит потребность в пряже по открытым заказам; заказывать поставщикам — не его право.</summary>
    [SqlFact]
    public async Task Workshop_head_sees_material_planning()
    {
        await app.CreateReleasedOrderAsync(500); // 160 кг пряжи с отходом при 50 кг на складе — нехватка
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync("production/planning");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Потребность в материалах" })).ToBeVisibleAsync();
        var yarn = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "ПР-1" });
        await Assertions.Expect(yarn).ToHaveClassAsync(new Regex("short"));
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Создать заказы поставщикам на нехватку" })).ToHaveCountAsync(0);
    }

    /// <summary>D92 (дополнение «терминал цеха»): вязальщица на телефоне вводит код операции и отмечает часть работы с причиной.</summary>
    [SqlFact]
    public async Task Worker_marks_partial_work_from_phone_terminal()
    {
        var order = await app.CreateReleasedOrderAsync(10);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }, IsMobile = true, HasTouch = true,
        });
        var page = await context.NewPageAsync();
        await LoginAsync(page, AppFixture.WorkerEmail);
        await page.GotoAsync("shop");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Терминал цеха" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Исполнитель: Петрова А. С.")).ToBeVisibleAsync();

        await page.GetByLabel("Код операции").FillAsync($"оп-{order}-1");
        await ClickUntilAsync(page.GetByRole(AriaRole.Button, new() { Name = "Открыть" }), page.GetByRole(AriaRole.Heading, new() { Name = "Вязание" }));
        await page.WaitForURLAsync(new Regex($@"/shop/op/{order}/1$"));
        await Assertions.Expect(page.Locator(".shop-big div").First).ToContainTextAsync("10");

        await page.GetByLabel("Сделано", new() { Exact = true }).FillAsync("6");
        await page.GetByLabel("Почему не всё").SelectOptionAsync(new SelectOptionValue { Label = "Не хватило времени" });
        await page.GetByLabel("Комментарий").FillAsync("смена закончилась (e2e)");
        await page.GetByRole(AriaRole.Button, new() { Name = "Отметить частично" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Отмечено: 6 шт.");
        await Assertions.Expect(page.Locator(".shop-big div").Nth(2)).ToContainTextAsync("6");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"),
            "На телефоне не должно быть горизонтальной прокрутки.");

        await page.GotoAsync("shop");
        // Ссылки на этот заказ и этап: моя отметка и доступная работа (сменные задания других сценариев не мешают).
        var thisStage = page.Locator($"li:has(a[href='shop/op/{order}/1'])");
        await Assertions.Expect(thisStage.Filter(new() { HasText = "Не выполнено полностью: Не хватило времени" })).ToHaveCountAsync(1);
        await Assertions.Expect(thisStage.Filter(new() { HasText = "можно отметить 4 шт" })).ToHaveCountAsync(1);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));

        // Ошиблась на одну — уменьшает свою сегодняшнюю отметку сама.
        await ClickUntilAsync(thisStage.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Ошибся — уменьшить") }),
            page.GetByLabel("На сколько уменьшить"));
        await page.GetByLabel("На сколько уменьшить").FillAsync("1");
        await page.GetByLabel("Причина исправления").FillAsync("ошиблась при вводе (e2e)");
        await page.GetByRole(AriaRole.Button, new() { Name = "Исправить", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Отметка исправлена.");
        await Assertions.Expect(page.Locator(".shop-totals li").Filter(new() { HasText = "Вязание" })).ToContainTextAsync("5 шт");
    }

    /// <summary>Сменное задание: начальник цеха назначает вязальщице норму, она видит процент на телефоне и сообщает причину.</summary>
    [SqlFact]
    public async Task Workshop_head_assigns_shift_task_and_worker_reports_shortfall()
    {
        var order = await app.CreateReleasedOrderAsync(10);
        await using (var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true }))
        {
            var page = await context.NewPageAsync();
            await LoginAsync(page);
            await page.GotoAsync("production/shift-tasks");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Сменные задания" })).ToBeVisibleAsync();
            // Поля, заполненные до того, как страница стала интерактивной, теряются при подключении Blazor: повторяем заполнение и отправку,
            // пока сервер не подтвердит назначение.
            var assigned = page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Задание назначено." });
            for (var attempt = 0; attempt < 15 && !await assigned.IsVisibleAsync(); attempt++)
            {
                await page.GetByLabel("Сотрудник", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Петрова А. С. (0101)" });
                await page.GetByLabel("Заказ", new() { Exact = true }).SelectOptionAsync(order.ToString());
                await page.GetByLabel("План на смену").FillAsync("8");
                await page.GetByRole(AriaRole.Button, new() { Name = "Назначить", Exact = true }).ClickAsync();
                try
                {
                    await assigned.WaitForAsync(new() { Timeout = 1500 });
                }
                catch (TimeoutException)
                {
                }
            }

            await Assertions.Expect(assigned).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Петрова А. С." }).Last).ToContainTextAsync("0%");
        }

        await using (var phone = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }, IsMobile = true, HasTouch = true,
        }))
        {
            var page = await phone.NewPageAsync();
            await LoginAsync(page, AppFixture.WorkerEmail);
            await page.GotoAsync("shop");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Задания на смену" })).ToBeVisibleAsync();
            var task = page.Locator(".shop-list li").Filter(new() { HasText = "норма 8, сделано 0 шт" });
            await Assertions.Expect(task).ToContainTextAsync("Выполнено 0%");
            await ClickUntilAsync(task.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Почему не выполнено") }), page.GetByLabel("Причина невыполнения"));
            await page.GetByLabel("Причина невыполнения").SelectOptionAsync(new SelectOptionValue { Label = "Нет пряжи или материалов" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Сообщить" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Причина передана мастеру.");
            await Assertions.Expect(task).ToContainTextAsync("Причина: Нет пряжи или материалов");
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
        }
    }

    /// <summary>Настройки производства организации: начальник цеха видит правила без права менять; баллы — в отчёте и на телефоне.</summary>
    [SqlFact]
    public async Task Production_settings_are_read_only_for_head_and_points_are_visible()
    {
        await app.EnablePointsAsync();
        await using (var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true }))
        {
            var page = await context.NewPageAsync();
            await LoginAsync(page);
            await page.GotoAsync("settings/production");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Настройки производства" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Допуск отклонения расхода от нормы, %")).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" })).ToHaveCountAsync(0);

            await page.GotoAsync("production/scores");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Баллы и показатели" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("thead th").Filter(new() { HasTextRegex = new Regex("^Баллы$") })).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByText("Баллы: выполненное задание")).ToBeVisibleAsync();
        }

        await using (var phone = await app.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }, IsMobile = true, HasTouch = true,
        }))
        {
            var page = await phone.NewPageAsync();
            await LoginAsync(page, AppFixture.WorkerEmail);
            await page.GotoAsync("shop");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Мои показатели за месяц" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".shop-totals li").Filter(new() { HasText = "Баллы" })).ToHaveCountAsync(1);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
        }
    }

    /// <summary>Маршрутный лист: QR-код и текстовый код на каждый этап.</summary>
    [SqlFact]
    public async Task Workshop_head_prints_route_sheet_with_qr_codes()
    {
        var order = await app.CreateReleasedOrderAsync(5);
        await using var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = app.BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        await page.GotoAsync($"production-orders/{order}");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Маршрутный лист с QR-кодами" })).ToBeVisibleAsync();
        await page.GotoAsync($"print/production-orders/{order}/route");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Маршрутный лист к производственному заказу") })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("td.qr img")).ToHaveCountAsync(5);
        await Assertions.Expect(page.GetByText($"ОП-{order}-4", new() { Exact = true })).ToBeVisibleAsync();
        Assert.StartsWith("data:image/png;base64,", await page.Locator("td.qr img").First.GetAttributeAsync("src"));
    }

    private Task LoginAsync(IPage page) => LoginAsync(page, AppFixture.UserEmail);

    private async Task LoginAsync(IPage page, string email)
    {
        await page.GotoAsync("account/login");
        await page.GetByLabel("Эл. почта").FillAsync(email);
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
