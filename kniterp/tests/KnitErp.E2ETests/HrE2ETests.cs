using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Сквозные сценарии модуля «Сотрудники» (этапы 1–4, D200–D217) в браузере — перенос с Playwright на Node (ответ владельца № 12).
/// Каждый сценарий заводит свои подразделение и должность с меткой запуска, поэтому порядок тестов не важен. Данные вымышленные (152-ФЗ).
/// </summary>
public sealed class HrE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    /// <summary>Сегодня в часовом поясе организации (Europe/Moscow): с 00:00 до 03:00 МСК первого числа UTC ещё в прошлом месяце.</summary>
    private static DateTime Today => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Moscow").Date;

    [SqlFact]
    public Task Officer_hires_fills_personal_data_and_consent_prints_T2_and_audit_has_no_snils() => RunAsync("hr-personal-data", false, async page =>
    {
        var (department, position) = await StructureAsync(page, "Вязальный цех п", "Вязальщица п");
        await StaffAsync(page, department, position);
        var lastName = $"Тестова{Stamp}";

        await OpenAsync(page, "employees");
        await page.GetByRole(AriaRole.Button, new() { Name = "Принять сотрудника" }).ClickAsync();
        await page.GetByLabel("Табельный номер").FillAsync($"E{Stamp}");
        await page.GetByLabel("Фамилия").FillAsync(lastName);
        await page.GetByLabel("Имя", new() { Exact = true }).FillAsync("Анна");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        await page.GetByRole(AriaRole.Button, new() { Name = "Принять", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("принят");

        await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(lastName) }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/employees/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync(lastName);
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Нет действующего согласия");

        await page.GetByRole(AriaRole.Button, new() { Name = "Показать персональные данные" }).ClickAsync();
        await page.GetByLabel("СНИЛС", new() { Exact = true }).FillAsync("112-233-445 96");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить персональные данные" }).ClickAsync();
        await Expect(page.Locator("form .error")).ToContainTextAsync("СНИЛС: 11 цифр");

        await page.GetByLabel("СНИЛС", new() { Exact = true }).FillAsync("112-233-445 95");
        await page.GetByLabel("Телефон").FillAsync("+7 (900) 000-00-00");
        await page.GetByLabel("Место рождения").FillAsync("г. Тестовск");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить персональные данные" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Персональные данные сохранены");

        await page.GetByRole(AriaRole.Button, new() { Name = "Отметить согласие" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Согласие отмечено");
        await Expect(page.GetByText("Согласие действует")).ToBeVisibleAsync();
        await Expect(page.GetByText("Нет действующего согласия")).ToHaveCountAsync(0);

        var id = Regex.Match(page.Url, @"employees/(\d+)").Groups[1].Value;
        await page.GotoAsync($"print/employee-t2/{id}");
        await Expect(page.GetByText("ЛИЧНАЯ КАРТОЧКА РАБОТНИКА")).ToBeVisibleAsync();
        await Expect(page.GetByText("112-233-445 95")).ToBeVisibleAsync();
        await Expect(page.GetByText("0301002")).ToBeVisibleAsync();

        // Просмотр и печать ПДн — в журнале; самого СНИЛС в журнале нет.
        await OpenAsync(page, "audit");
        await Expect(page.GetByText("Просмотр персональных данных").First).ToBeVisibleAsync();
        await Expect(page.GetByText("Печать личной карточки Т-2").First).ToBeVisibleAsync();
        await Expect(page.GetByText("11223344595")).ToHaveCountAsync(0);
    });

    [SqlFact]
    public Task Staffing_for_one_rate_second_hire_order_is_over_staffing_and_T1_T3_print() => RunAsync("hr-orders", false, async page =>
    {
        var (department, position) = await StructureAsync(page, "Цех приказов", "Кеттлёрщица");
        await OpenAsync(page, "hr/staffing");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        await page.GetByLabel("Ставок").FillAsync("1");
        await page.GetByLabel("Оклад, руб.").FillAsync("42000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить в штат" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Строка добавлена");
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = position })).ToBeVisibleAsync();

        var first = $"Приказова{Stamp}";
        await HireOrderAsync(page, $"P{Stamp}", first, "Ольга", department, position, salary: "42000", basis: "Трудовой договор № 1");
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Приказ проведён");
        var orderUrl = page.Url;
        // Карточка сотрудника показывает историю назначений со ссылкой на приказ (D214).
        await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(first) }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/employees/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "История назначений" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Приём на работу №") })).ToBeVisibleAsync();

        await HireOrderAsync(page, $"Q{Stamp}", $"Лишняя{Stamp}", "Ольга", department, position, salary: "42000", basis: "Трудовой договор № 2");
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Сверх штата");

        await OpenAsync(page, "hr/staffing");
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(position) })).ToContainTextAsync("42 000,00");

        var id = Regex.Match(orderUrl, @"orders/(\d+)").Groups[1].Value;
        await page.GotoAsync($"print/hr-order/{id}");
        await Expect(page.GetByText("о приёме работника на работу")).ToBeVisibleAsync();
        await Expect(page.GetByText("0301001")).ToBeVisibleAsync();
        await Expect(page.GetByText("ЧЕРНОВИК")).ToHaveCountAsync(0);
        await page.GotoAsync("print/staffing");
        await Expect(page.GetByText("ШТАТНОЕ РАСПИСАНИЕ")).ToBeVisibleAsync();
        await Expect(page.GetByText(position)).ToBeVisibleAsync();
    });

    [SqlFact]
    public Task Timesheet_by_2_2_schedule_correct_day_close_corrective_and_T13() => RunAsync("hr-timesheet", false, async page =>
    {
        var (department, position) = await StructureAsync(page, "Цех табеля", "Оверлочница");
        await StaffAsync(page, department, position);
        var lastName = $"Табелева{Stamp}";
        var monthStart = $"{Today:yyyy-MM}-01";
        await HireOrderAsync(page, $"T{Stamp}", lastName, "Вера", department, position, hiredOn: monthStart);
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Приказ проведён");

        var schedule = $"2/2 по 12 ч {Stamp}";
        await OpenAsync(page, "hr/schedules");
        await page.GetByLabel("Название").FillAsync(schedule);
        await page.GetByLabel("Вид").SelectOptionAsync(new SelectOptionValue { Label = "Сменный" });
        await page.GetByLabel("Часов в смене").FillAsync("12");
        await page.GetByLabel("Первый день цикла").FillAsync(monthStart);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить график" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("График добавлен");
        var row = page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(lastName) });
        await row.GetByRole(AriaRole.Combobox).SelectOptionAsync(new SelectOptionValue { Label = schedule });
        await row.Locator("input[type=date]").FillAsync(monthStart);
        await row.GetByRole(AriaRole.Button, new() { Name = "Назначить" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("График назначен");

        await OpenAsync(page, "hr/timesheet");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await FilterAppliedAsync(page); // D812: фильтр применяется сам
        await page.GetByRole(AriaRole.Button, new() { Name = "Составить табель по графикам" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Табель составлен");
        var line = page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(lastName) });
        await Expect(line.GetByRole(AriaRole.Button, new() { Name = "Я 12" }).First).ToBeVisibleAsync();

        await line.GetByRole(AriaRole.Button, new() { Name = "Я 12" }).First.ClickAsync();
        await page.GetByLabel("Код").SelectOptionAsync("Б");
        await page.GetByLabel("Часов", new() { Exact = true }).FillAsync("0");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("День исправлен");
        await Expect(line.GetByRole(AriaRole.Button, new() { Name = "Б*" })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Закрыть табель" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Табель закрыт");
        await Expect(line.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Исправительный табель" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("исправительный");
        await Expect(page.GetByText("Исправительный № 1", new() { Exact = true }).First).ToBeVisibleAsync();

        var href = await page.GetByRole(AriaRole.Link, new() { Name = "Печать Т-13" }).GetAttributeAsync("href");
        await page.GotoAsync(href!);
        await Expect(page.GetByText("ТАБЕЛЬ УЧЁТА РАБОЧЕГО ВРЕМЕНИ (ИСПРАВИТЕЛЬНЫЙ № 1)")).ToBeVisibleAsync();
        await Expect(page.GetByText("0301008")).ToBeVisibleAsync();
    });

    [SqlFact]
    public Task Piece_rate_calculation_payslip_settings_minimum_and_reconciliation_excel() => RunAsync("hr-payroll", false, async page =>
    {
        await OpenAsync(page, "hr/piece-rates");
        await page.GetByLabel("Код операции").FillAsync(Stamp);
        await page.GetByLabel("Операция").FillAsync($"Вязка шарфа {Stamp}");
        await page.GetByLabel("Цена, руб.").FillAsync("85.5");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить расценку" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Расценка добавлена");
        await Expect(page.GetByRole(AriaRole.Cell, new() { Name = $"Вязка шарфа {Stamp}" })).ToBeVisibleAsync();

        await OpenAsync(page, "hr/payroll");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Рассчитать|Пересчитать)$") }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Рассчитано");
        await Expect(page.GetByText("Черновик")).ToBeVisibleAsync();
        var slip = page.GetByRole(AriaRole.Link, new() { Name = "Листок" }).First;
        if (await slip.CountAsync() > 0)
        {
            await page.GotoAsync((await slip.GetAttributeAsync("href"))!);
            await Expect(page.GetByText("РАСЧЁТНЫЙ ЛИСТОК")).ToBeVisibleAsync();
            await Expect(page.GetByText("ЧЕРНОВИК")).ToBeVisibleAsync();
        }

        // Настройки D216: ниже минимума ТК не сохраняется; Excel для сверки скачивается за рассчитанный месяц. В конце — значение по умолчанию.
        await OpenAsync(page, "hr/settings");
        var night = page.GetByLabel("Ночные часы, % часовой ставки");
        var save = page.GetByRole(AriaRole.Button, new() { Name = "Сохранить параметры заработка" });
        await night.FillAsync("15");
        await save.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("не меньше 20 %");
        await night.FillAsync("25");
        await save.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Параметры заработка сохранены");
        await OpenAsync(page, "hr/settings");
        await Expect(page.GetByLabel("Ночные часы, % часовой ставки")).ToHaveValueAsync(new Regex(@"^25(\.0+)?$"));

        await page.GetByLabel("Месяц").SelectOptionAsync(new SelectOptionValue { Index = 0 });
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Расчёт в Excel для сверки" }).ClickAsync());
        Assert.EndsWith(".xlsx", download.SuggestedFilename);

        await page.GetByLabel("Ночные часы, % часовой ставки").FillAsync("20");
        await save.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Параметры заработка сохранены");
    });

    /// <summary>
    /// Месяц глазами сотрудника: приём → табель → закрытие → расчёт → утверждение → приглашение → «Мой табель» и «Мой расчётный листок»;
    /// чужие разделы закрыты. Отдельный месяц (март 2027): утверждение требует закрытых табелей всей организации.
    /// </summary>
    [SqlFact]
    public Task Employee_sees_only_own_timesheet_and_payslip() => RunAsync("hr-self-service", false, async page =>
    {
        var (department, position) = await StructureAsync(page, "Цех самообслуживания", "Петельщица");
        await StaffAsync(page, department, position);
        var lastName = $"Самсонова{Stamp}";
        await HireOrderAsync(page, $"S{Stamp}", lastName, "Лидия", department, position, hiredOn: "2027-03-01", salary: "30000");
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Приказ проведён");

        await OpenAsync(page, "hr/timesheet");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Месяц").FillAsync("2027-03");
        await FilterAppliedAsync(page); // D812: фильтр применяется сам
        await page.GetByRole(AriaRole.Button, new() { Name = "Составить табель по графикам" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Табель составлен");
        await page.GetByRole(AriaRole.Button, new() { Name = "Закрыть табель" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Табель закрыт");

        await OpenAsync(page, "hr/payroll");
        await page.GetByLabel("Месяц").FillAsync("2027-03");
        await FilterAppliedAsync(page); // D812: фильтр применяется сам
        await page.GetByRole(AriaRole.Button, new() { Name = "Рассчитать" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Рассчитано");
        await Expect(page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(lastName) })).ToContainTextAsync("30 000,00");
        await page.GetByRole(AriaRole.Button, new() { Name = "Утвердить" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Расчёт утверждён");

        var email = $"worker-{Stamp}@e2e.local";
        var password = $"Сотрудник-{Stamp}-пароль";
        await OpenAsync(page, "users");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить пользователя" }).ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Сотрудник", Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = $"{lastName} Лидия" });
        await page.GetByLabel("Email для приглашения").FillAsync(email);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Роль (шаблон)" }).SelectOptionAsync(new SelectOptionValue { Label = "Сотрудник" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Пригласить" }).ClickAsync();
        var invite = await page.Locator("#invite-link").InputValueAsync();
        Assert.Contains("/account/invite?token=", invite);

        var worker = await app.AnonymousPageAsync();
        try
        {
            await worker.GotoAsync(invite);
            await worker.GetByLabel("Новый пароль").FillAsync(password);
            await worker.GetByLabel("Повторите пароль").FillAsync(password);
            await worker.GetByRole(AriaRole.Button, new() { Name = "Сохранить пароль" }).ClickAsync();
            await worker.GetByText("Пароль установлен.").WaitForAsync();
            await worker.GotoAsync("account/login");
            await worker.GetByLabel("Эл. почта").FillAsync(email);
            await worker.GetByLabel("Пароль").FillAsync(password);
            await worker.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
            await worker.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });

            // D227: перед работой — документы с данными сотрудника, подпись паролем (простая электронная подпись).
            await OpenAsync(worker, "");
            await Expect(worker.GetByRole(AriaRole.Heading, new() { Name = "Документы перед началом работы" })).ToBeVisibleAsync();
            await Expect(worker.GetByText("Согласие на обработку персональных данных").First).ToBeVisibleAsync();
            await ShotAsync(worker, "6-podpisanie-pered-dostupom");
            await worker.GetByLabel("Я прочитал(а) документы и согласен(на) с ними").CheckAsync();
            await worker.GetByLabel("Пароль для подписи").FillAsync(password);
            await worker.GetByRole(AriaRole.Button, new() { Name = "Подписать документы" }).ClickAsync();
            await Expect(worker.GetByRole(AriaRole.Heading, new() { Name = "Документы перед началом работы" })).ToHaveCountAsync(0);

            await OpenAsync(worker, "hr/timesheet");
            await Expect(worker.GetByRole(AriaRole.Heading, new() { Name = "Мой табель" })).ToBeVisibleAsync();
            await worker.GetByLabel("Месяц").FillAsync("2027-03");
            await FilterAppliedAsync(worker); // D812: фильтр применяется сам
            await Expect(worker.GetByText("табель закрыт")).ToBeVisibleAsync();

            await OpenAsync(worker, "hr/payroll");
            await Expect(worker.GetByRole(AriaRole.Heading, new() { Name = "Мой расчётный листок" })).ToBeVisibleAsync();
            await worker.GetByLabel("Месяц").FillAsync("2027-03");
            await FilterAppliedAsync(worker); // D812: фильтр применяется сам
            await Expect(worker.GetByText("Всего начислено")).ToBeVisibleAsync();
            await Expect(worker.GetByText("30 000,00").First).ToBeVisibleAsync();

            await OpenAsync(worker, "hr/orders");
            await Expect(worker.GetByRole(AriaRole.Alert)).ToContainTextAsync("Недостаточно прав");
            await Expect(worker.GetByRole(AriaRole.Button, new() { Name = "Новый приказ" })).ToHaveCountAsync(0);
            await OpenAsync(worker, "hr/staffing");
            await Expect(worker.GetByRole(AriaRole.Alert)).ToContainTextAsync("Недостаточно прав");
        }
        finally
        {
            await worker.Context.CloseAsync();
        }
    });

    /// <summary>D225: меньше ручного ввода — график подразделения, номер и оклад в приказе, все табели, готовность месяца, шаблон расценок.</summary>
    [SqlFact]
    public Task Automation_fills_number_salary_schedule_and_batches_timesheets() => RunAsync("hr-automation", false, async page =>
    {
        var (department, position) = await StructureAsync(page, "Цех автоматики", "Мотальщица");
        await OpenAsync(page, "hr/staffing");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        await page.GetByLabel("Оклад, руб.").FillAsync("43500");
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить в штат" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Строка добавлена");

        var schedule = $"5/2 автоматики {Stamp}";
        await OpenAsync(page, "hr/schedules");
        await page.GetByLabel("Название").FillAsync(schedule);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить график" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("График добавлен");
        await page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(Regex.Escape(department)) }).GetByLabel("График по умолчанию")
            .SelectOptionAsync(new SelectOptionValue { Label = schedule });
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("График по умолчанию задан");
        await ShotAsync(page, "1-grafik-podrazdeleniya");

        await OpenAsync(page, "hr/orders");
        await page.GetByRole(AriaRole.Button, new() { Name = "Новый приказ" }).ClickAsync();
        Assert.Matches(@"^\d+$", await page.GetByLabel("Табельный номер").InputValueAsync()); // номер подставлен
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        await Expect(page.GetByLabel("Оклад (тарифная ставка), руб.")).ToHaveValueAsync(new Regex(@"^43500([.,]0+)?$"));
        await Expect(page.GetByText("Оклад подставлен из штатного расписания")).ToBeVisibleAsync();
        await ShotAsync(page, "2-prikaz-nomer-i-oklad");

        await OpenAsync(page, "hr/timesheet");
        await page.GetByRole(AriaRole.Button, new() { Name = "Составить табели всех подразделений" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Табели за месяц");
        await ShotAsync(page, "3-vse-tabeli");

        await OpenAsync(page, "hr/payroll");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Готовность месяца" })).ToBeVisibleAsync();
        await Expect(page.GetByText("Табели закрыты:")).ToBeVisibleAsync();
        await ShotAsync(page, "4-gotovnost-mesyaca");

        await OpenAsync(page, "hr/piece-rates");
        await ShotAsync(page, "5-rascenki-iz-excel");
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Скачать шаблон" }).ClickAsync());
        Assert.EndsWith(".xlsx", download.SuggestedFilename);
    });

    /// <summary>D226–D227: скан паспорта в документах сотрудника, заполненность карточки и списка, документы перед доступом и их печать.</summary>
    [SqlFact]
    public Task Employee_documents_completeness_and_agreements_print() => RunAsync("hr-documents", false, async page =>
    {
        await OpenAsync(page, "employees");
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Заполненность личных дел") })).ToBeVisibleAsync();
        await ShotAsync(page, "7-zapolnennost-spiska");

        await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Петрова Анна") }).First.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/employees/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Заполненность карточки") })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Документы перед доступом" })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Показать персональные данные" }).ClickAsync();
        await page.GetByLabel("Вид документа").SelectOptionAsync(new SelectOptionValue { Label = "Скан паспорта" });
        await page.Locator("input[type=file]").Last.SetInputFilesAsync(new FilePayload
        {
            Name = "passport.pdf", MimeType = "application/pdf", Buffer = "%PDF-1.4 e2e"u8.ToArray(),
        });
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Документ загружен");
        await Expect(page.GetByText("passport.pdf")).ToBeVisibleAsync();
        await ShotAsync(page, "8-kartochka-dokumenty");

        var href = await page.GetByRole(AriaRole.Link, new() { Name = "Печать" }).First.GetAttributeAsync("href");
        await page.GotoAsync(href!);
        await Expect(page.GetByText("СОГЛАСИЕ НА ОБРАБОТКУ ПЕРСОНАЛЬНЫХ ДАННЫХ")).ToBeVisibleAsync();
        await Expect(page.GetByText("Петрова Анна Сергеевна").First).ToBeVisibleAsync();
        await ShotAsync(page, "9-pechat-soglasiya");
    });

    /// <summary>
    /// D125: кадровик выдаёт ссылку, сотрудник без входа (телефон 390 px) заполняет анкету с согласием, ссылка одноразовая,
    /// кадровик сравнивает и принимает. Не запускался в сессии, где был написан (общая машина) — проверить при сдаче.
    /// </summary>
    [SqlFact]
    public Task Employee_fills_data_by_link() => RunAsync("hr-employee-data", false, async page =>
    {
        await OpenAsync(page, "employees");
        await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Петрова Анна") }).First.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/employees/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var card = page.Url;
        var linkBox = page.Locator("#employee-data-link");
        // До подключения Blazor щелчок теряется: повторяем, пока не появится ссылка (ожидание длиннее ответа сервера — новая
        // ссылка отменяет прежнюю, второй щелчок после показа сделал бы прочитанную ссылку недействительной).
        for (var i = 0; i < 6 && !await linkBox.IsVisibleAsync(); i++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Ссылка для заполнения данных" }).ClickAsync();
            try
            {
                await linkBox.WaitForAsync(new() { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
            }
        }

        await Expect(linkBox).ToHaveValueAsync(new Regex("/account/employee-data\\?token="));
        var link = await linkBox.InputValueAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Копировать ссылку" })).ToBeVisibleAsync();

        var worker = await app.AnonymousPageAsync();
        try
        {
            await worker.SetViewportSizeAsync(390, 844);
            await worker.GotoAsync(link);
            await Expect(worker.GetByRole(AriaRole.Heading, new() { Name = "Анкета сотрудника" })).ToBeVisibleAsync();
            await Expect(worker.GetByText("Петрова Анна Сергеевна").First).ToBeVisibleAsync();
            Assert.True(await NoHorizontalScrollAsync(worker), "Горизонтальная прокрутка на анкете");
            await worker.GetByLabel("СНИЛС", new() { Exact = true }).FillAsync("112-233-445 95");
            await worker.GetByLabel("Телефон").FillAsync("+7 900 000-00-09");
            await worker.GetByLabel("Скан паспорта").SetInputFilesAsync(new FilePayload
            {
                Name = "passport.pdf", MimeType = "application/pdf", Buffer = "%PDF-1.4 e2e"u8.ToArray(),
            });
            await worker.GetByRole(AriaRole.Checkbox).CheckAsync();
            await worker.GetByRole(AriaRole.Button, new() { Name = "Отправить кадровику" }).ClickAsync();
            await Expect(worker.GetByRole(AriaRole.Status)).ToContainTextAsync("Данные отправлены кадровику");

            await worker.GotoAsync(link);
            await Expect(worker.GetByRole(AriaRole.Alert)).ToContainTextAsync("Ссылка недействительна");
        }
        finally
        {
            await worker.Context.CloseAsync();
        }

        await OpenAsync(page, card);
        await Expect(page.GetByText("Данные от сотрудника — проверить (1)")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Показать анкету" }).ClickAsync();
        await Expect(page.GetByText("112-233-445 95")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Принять", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status).First).ToContainTextAsync("Анкета принята");
    });

    /// <summary>D221: блок «Смена сегодня» на главном — неявка видна по имени (в выходной — выход сверх графика), ссылка ведёт в табель.</summary>
    [SqlFact]
    public async Task Dashboard_shows_shift_today_and_links_to_timesheet()
    {
        var workday = await app.MarkShiftTodayAsync();
        foreach (var phone in new[] { false, true })
        {
            await RunAsync(phone ? "hr-dashboard-phone" : "hr-dashboard", phone, async page =>
            {
                await OpenAsync(page, "");
                var shift = page.Locator("a.prod-shift");
                await Expect(shift).ToBeVisibleAsync();
                await Expect(shift).ToContainTextAsync("Смена сегодня");
                if (workday)
                {
                    await Expect(shift).ToContainTextAsync("не вышли");
                    await Expect(shift).ToContainTextAsync("Петрова А.");
                }

                Assert.True(await NoHorizontalScrollAsync(page), "Горизонтальная прокрутка на главном");
                await shift.ClickAsync();
                await page.WaitForURLAsync(new Regex("/hr/timesheet$"));
            });
        }
    }

    /// <summary>Телефон 390 px: страницы кадров без горизонтальной прокрутки; таблица табеля прокручивается внутри себя (ТЗ §8).</summary>
    [SqlFact]
    public Task Hr_pages_have_no_horizontal_scroll_on_phone() => RunAsync("hr-phone", true, async page =>
    {
        foreach (var path in new[] { "employees", "hr/orders", "hr/staffing", "hr/payroll", "hr/piece-rates", "hr/settings", "hr/schedules" })
        {
            await OpenAsync(page, path);
            Assert.True(await NoHorizontalScrollAsync(page), $"Горизонтальная прокрутка на /{path}");
        }

        await OpenAsync(page, "employees");
        await page.Locator("table a[href^='employees/']").First.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/employees/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        Assert.True(await NoHorizontalScrollAsync(page), "Горизонтальная прокрутка на карточке сотрудника");

        await OpenAsync(page, "hr/timesheet");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await FilterAppliedAsync(page); // D812: фильтр применяется сам
        await page.WaitForTimeoutAsync(500);
        Assert.True(await NoHorizontalScrollAsync(page), "Горизонтальная прокрутка на табеле");
    });

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);

    /// <summary>D812: фильтр применяется сам через 0,3 с после изменения поля — ждём отправку и ответ сервера.</summary>
    private static async Task FilterAppliedAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Tab"); // уход из поля — событие изменения у даты и месяца
        await page.WaitForTimeoutAsync(900);
    }

    /// <summary>Снимок экрана для показа владельцу — только если задан KNITERP_E2E_SHOTS (в CI не пишется).</summary>
    private static async Task ShotAsync(IPage page, string name)
    {
        var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_SHOTS");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
    }

    /// <summary>Вкладка Владельца; при падении — снимок и текст страницы в KNITERP_E2E_ARTIFACTS (по умолчанию временная папка).</summary>
    private async Task RunAsync(string name, bool phone, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync(phone);
        try
        {
            await scenario(page);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}-failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}-failure.txt"), page.Url + "\n" + await page.InnerTextAsync("body"));
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    /// <summary>Интерактивная страница Blazor готова: меню загрузилось (оно стартует первым), цепь SignalR подключена.</summary>
    private static async Task OpenAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
    }

    private static Task<bool> NoHorizontalScrollAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");

    /// <summary>Подразделение и должность с меткой запуска — на странице «Структура».</summary>
    private static async Task<(string Department, string Position)> StructureAsync(IPage page, string department, string position)
    {
        department = $"{department} {Stamp}";
        position = $"{position} {Stamp}";
        await OpenAsync(page, "structure");
        await page.GetByLabel("Новое подразделение").FillAsync(department);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить" }).First.ClickAsync();
        await Expect(page.GetByText(department).Filter(new() { Visible = true }).First).ToBeVisibleAsync();
        await page.GetByLabel("Новая должность").FillAsync(position);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить" }).Nth(1).ClickAsync();
        await Expect(page.GetByText(position).Filter(new() { Visible = true }).First).ToBeVisibleAsync();
        return (department, position);
    }

    /// <summary>Строка штатного расписания: с ним принять можно только на должность из штата (D203).</summary>
    private static async Task StaffAsync(IPage page, string department, string position)
    {
        await OpenAsync(page, "hr/staffing");
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить в штат" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Строка добавлена");
    }

    /// <summary>Приказ Т-1: черновик и попытка проведения. Результат (проведён или отказ) проверяет сценарий.</summary>
    private static async Task HireOrderAsync(IPage page, string number, string lastName, string firstName, string department, string position,
        string? hiredOn = null, string? salary = null, string? basis = null)
    {
        await OpenAsync(page, "hr/orders");
        await page.GetByRole(AriaRole.Button, new() { Name = "Новый приказ" }).ClickAsync();
        if (hiredOn is not null)
        {
            if (hiredOn.EndsWith("-01") && DateTime.Parse(hiredOn) <= Today)
            {
                await page.GetByLabel("Дата приказа").FillAsync(hiredOn);
            }

            await page.GetByLabel("Дата приёма").FillAsync(hiredOn);
        }

        await page.GetByLabel("Табельный номер").FillAsync(number);
        await page.GetByLabel("Фамилия").FillAsync(lastName);
        await page.GetByLabel("Имя", new() { Exact = true }).FillAsync(firstName);
        await page.GetByLabel("Подразделение").SelectOptionAsync(new SelectOptionValue { Label = department });
        await page.GetByLabel("Должность").SelectOptionAsync(new SelectOptionValue { Label = position });
        if (salary is not null)
        {
            await page.GetByLabel("Оклад (тарифная ставка), руб.").FillAsync(salary);
        }

        if (basis is not null)
        {
            await page.GetByLabel("Основание (документ)").FillAsync(basis);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Создать черновик" }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/hr/orders/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(page.GetByText("Черновик").First).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Провести" }).ClickAsync();
    }
}
