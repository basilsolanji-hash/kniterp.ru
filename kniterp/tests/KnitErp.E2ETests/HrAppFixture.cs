using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Приложение для сквозных тестов модуля «Сотрудники»: своя тестовая база (KNITERP_TEST_SQL), организация с Владельцем, который входит
/// с паролем и подключает 2FA в браузере (как настоящий Владелец), и один принятый сотрудник для проверок на телефоне.
/// Состояние входа Владельца сохраняется один раз и раздаётся новым вкладкам. Пароль случайный на каждый запуск, данные вымышленные.
/// </summary>
public sealed class HrAppFixture : IAsyncLifetime
{
    public const string OwnerEmail = "owner-hr@e2e.local";

    private readonly SqlTestHost _host = new();
    private WebAppProcess? _app;
    private IPlaywright? _playwright;
    private string? _ownerState;
    private CreatedOrganization? _org;
    private long _department;
    private long _employee;

    public string BaseUrl => _app?.BaseUrl ?? string.Empty;
    public string Password { get; } = "E2e-" + Guid.NewGuid().ToString("N")[..16] + "!a";
    public IBrowser Browser { get; private set; } = default!;

    /// <summary>D143: покупатель с вводным остатком, авансом, отгрузкой и возвратом — для акта сверки.</summary>
    public const string SettlementCustomerName = "ООО «Покупатель для сверки»";

    public long SettlementCustomerId { get; private set; }

    /// <summary>D149: подтверждённый заказ поставщику (4 кг пряжи «ПР-E2E» по артикулу поставщика «E2E-ART») и XML УПД к нему.</summary>
    public long UpdOrderId { get; private set; }

    public byte[] UpdXml { get; private set; } = [];

    private readonly string _orgInn = TestInn();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestHost.EnvVar)))
        {
            return;
        }

        await _host.InitializeAsync();
        await SeedAsync();
        SettlementCustomerId = await SeedSettlementCustomerAsync(SettlementCustomerName);
        await SeedUpdOrderAsync();
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

    /// <summary>Вкладка Владельца (вход уже выполнен). Телефон — ширина 390 px (ТЗ «Сотрудники» §8).</summary>
    public async Task<IPage> OwnerPageAsync(bool phone = false)
    {
        var context = await Browser.NewContextAsync(Options(phone, _ownerState));
        return await context.NewPageAsync();
    }

    /// <summary>Вкладка без входа — для приглашённого сотрудника.</summary>
    public async Task<IPage> AnonymousPageAsync()
    {
        var context = await Browser.NewContextAsync(Options(false, null));
        return await context.NewPageAsync();
    }

    private BrowserNewContextOptions Options(bool phone, string? state) => new()
    {
        BaseURL = BaseUrl, Locale = "ru-RU", IgnoreHTTPSErrors = true, StorageState = state,
        ViewportSize = phone ? new ViewportSize { Width = 390, Height = 844 } : new ViewportSize { Width = 1280, Height = 900 },
    };

    private async Task SeedAsync()
    {
        CreatedOrganization org;
        await using (var s = _host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                "Общество с ограниченной ответственностью «Кадры-Трикотаж»", "ООО «Кадры-Трикотаж»", _orgInn, null, false,
                "Europe/Moscow", OwnerEmail, "Владелец", "RU"));
        }

        await using (var s = _host.As(org.OwnerUserId, org.OrganizationId))
        {
            var department = await s.Structure.CreateDepartmentAsync("Вязальный цех", null);
            var position = await s.Structure.CreatePositionAsync("Вязальщица");
            _employee = await s.Employees.HireAsync(new KnitErp.Application.Structure.EmployeeCommand("0001", "Петрова", "Анна", "Сергеевна",
                department, position, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1)));
            _department = department;
        }

        _org = org;

        await using (var s = _host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }
    }

    /// <summary>Класс машины в справочнике разработки (D726) — для утверждения рисунка. Возвращает название.</summary>
    public async Task<string> CreateMachineClassAsync(string name, int feeders)
    {
        await using var s = _host.As(_org!.OwnerUserId, _org.OrganizationId);
        var user = new TestUser { UserId = _org.OwnerUserId, OrganizationId = _org.OrganizationId, ClientAddress = _host.ClientAddress };
        await new KnitErp.Application.Development.DevSettingsService(s.Db, new KnitErp.Application.Access.AccessGuard(s.Db, user, _host.Clock), user, _host.Clock)
            .AddLookupAsync(KnitErp.Domain.Development.DevLookupKind.MachineClass, name, feeders);
        return name;
    }

    /// <summary>Пряжа в номенклатуре организации (сырьё, кг) — для палитры модели и норм техкарты (D751). Возвращает название.</summary>
    public async Task<string> CreateYarnAsync(string name)
    {
        await using var s = _host.As(_org!.OwnerUserId, _org.OrganizationId);
        var kg = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
        await s.Catalog.CreateItemAsync(new KnitErp.Application.Catalog.ItemCommand("ПР-" + Guid.NewGuid().ToString("N")[..6], name,
            KnitErp.Domain.Catalog.ItemType.RawMaterial, kg, null));
        return name;
    }

    /// <summary>
    /// Табель «Вязального цеха» за текущий месяц и отметка Петровой на сегодня (часовой пояс организации): в рабочий день — больничный
    /// (блок «Смена сегодня» покажет её среди не вышедших), в выходной или праздник — выход сверх графика (она на смене).
    /// Возвращает true, если сегодня рабочий день. Повторный вызов только меняет отметку.
    /// </summary>
    public async Task<bool> MarkShiftTodayAsync()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Moscow"));
        var holidays = KnitErp.Application.Workspace.ProductionCalendar.Holidays("RU", today, today.AddDays(1)).Select(h => h.Date).ToHashSet();
        var workday = KnitErp.Domain.Hr.WorkSchedule.DefaultPlan(today, holidays).Work;
        await using var hr = _host.Hr(_org!.OwnerUserId, _org.OrganizationId);
        var grid = await hr.Timesheets.GetAsync(_department, today.Year, today.Month);
        var id = grid.Id ?? await hr.Timesheets.CreateAsync(_department, today.Year, today.Month);
        if (workday)
        {
            await hr.Timesheets.SetDayAsync(id, _employee, today, "Б", 0, 0, 0);
        }
        else
        {
            await hr.Timesheets.SetDayAsync(id, _employee, today, "Я", 8, 0, 0);
        }

        return workday;
    }

    /// <summary>
    /// D127: сотрудник с паролем этого набора и включённым ограничением «одно устройство» (сохраняет Владелец через сервис).
    /// Возвращает почту для входа в браузере.
    /// </summary>
    public async Task<string> CreateSingleDeviceWorkerAsync()
    {
        var email = $"single-{Guid.NewGuid():N}@e2e.local";
        KnitErp.Application.Access.InvitationResult invitation;
        await using (var s = _host.As(_org!.OwnerUserId, _org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new KnitErp.Application.Access.InviteUserCommand(email, "Сменщица Одна", SystemRoles.Employee, null));
        }

        await using (var s = _host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(invitation.SetupToken, Password, Password);
        }

        await using (var s = _host.As(_org.OwnerUserId, _org.OrganizationId))
        {
            await s.Restrictions.SaveAsync(new KnitErp.Application.Access.SaveSignInRestrictionCommand(
                invitation.UserId, true, null, false, 0, 0, 0, "e2e", null));
        }

        return email;
    }

    /// <summary>
    /// D143: покупатель для акта сверки — вводный остаток 5 000 (30 дней назад), аванс 2 000, заказ 10 × 1 000 отгружен, возвращено 3.
    /// Сальдо на конец — 10 000 в нашу пользу. Готовится до запуска приложения: после входа Владельца имена пользователей зашифрованы ключом
    /// приложения, и сервисы теста их уже не читают.
    /// </summary>
    private async Task<long> SeedSettlementCustomerAsync(string name)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Moscow"));
        await using var s = _host.As(_org!.OwnerUserId, _org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад сверки " + Guid.NewGuid().ToString("N")[..4], null);
        var item = await s.Catalog.CreateItemAsync(new KnitErp.Application.Catalog.ItemCommand("СВ-" + Guid.NewGuid().ToString("N")[..6], "Свитер для сверки",
            KnitErp.Domain.Catalog.ItemType.Finished, piece, null));
        long reason;
        await using (var db = _host.NewDb())
        {
            reason = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.OperationReasons
                .Where(r => r.OrganizationId == _org.OrganizationId && r.Kind == KnitErp.Domain.Warehousing.StockOperationKind.Receipt).Select(r => r.Id));
        }

        var stock = await s.Documents.CreateAsync(KnitErp.Domain.Warehousing.StockOperationKind.Receipt,
            new KnitErp.Domain.Warehousing.StockDocumentHeader(store, null, null, reason, today, null));
        await s.Documents.SetLineAsync(stock, item, 10, (await s.Documents.GetAsync(stock)).RowVersion);
        await s.Documents.PostAsync(stock, (await s.Documents.GetAsync(stock)).RowVersion);

        var customer = await s.Counterparties.CreateAsync(new KnitErp.Application.Catalog.CounterpartyCommand(name, null, null, false, true, null));
        var entity = (await s.LegalEntities.ListAsync()).Single(e => e.IsDefault).Id;
        await s.Sales.AddOpeningBalanceAsync(customer, entity, today.AddDays(-30), 5_000m, null);
        await s.Sales.CreatePaymentAsync(today, customer, null, 2_000m, null);
        var order = await s.Sales.CreateOrderAsync(new KnitErp.Domain.Sales.SalesOrderHeader(today, customer, store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, item, 10, 1_000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        var ret = await s.Sales.CreateReturnAsync(order);
        await s.Documents.SetLineAsync(ret, item, 3, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
        return customer;
    }

    /// <summary>D149: поставщик с ИНН, пряжа с его артикулом, подтверждённый заказ; УПД — одна строка по артикулу, одна неизвестная.</summary>
    private async Task SeedUpdOrderAsync()
    {
        const string supplierInn = "500100732259";
        await using var s = _host.As(_org!.OwnerUserId, _org.OrganizationId);
        var kg = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад сырья УПД", null);
        await s.LegalEntities.AddCashAsync((await s.LegalEntities.ListAsync()).Single(e => e.IsDefault).Id, "Касса цеха"); // D147: для регулярных операций
        var yarn = await s.Catalog.CreateItemAsync(new KnitErp.Application.Catalog.ItemCommand("ПР-E2E", "Пряжа для УПД", KnitErp.Domain.Catalog.ItemType.RawMaterial, kg, null));
        var supplier = await s.Counterparties.CreateAsync(new KnitErp.Application.Catalog.CounterpartyCommand("ИП Петров (пряжа)", supplierInn, null, true, false, null));
        await s.Suppliers.SetAsync(yarn, supplier, "E2E-ART", null, true);
        UpdOrderId = await s.Purchases.CreateOrderAsync(new KnitErp.Domain.Purchasing.PurchaseOrderHeader(DateOnly.FromDateTime(DateTime.UtcNow), supplier, store, null, null, true, null));
        await s.Purchases.SetOrderLineAsync(UpdOrderId, yarn, 4, 500m, null, (await s.Purchases.GetOrderAsync(UpdOrderId)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(UpdOrderId, (await s.Purchases.GetOrderAsync(UpdOrderId)).RowVersion);
        UpdXml = System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Файл ВерсФорм=\"5.03\"><Документ Функция=\"СЧФДОП\"><СвСчФакт НомерДок=\"E2E-1\" ДатаДок=\"13.10.2026\">" +
            $"<СвПрод><ИдСв><СвИП ИННФЛ=\"{supplierInn}\"><ФИО Фамилия=\"Петров\" Имя=\"Пётр\"/></СвИП></ИдСв></СвПрод>" +
            $"<СвПокуп><ИдСв><СвЮЛУч НаимОрг=\"Кадры-Трикотаж\" ИННЮЛ=\"{_orgInn}\"/></ИдСв></СвПокуп></СвСчФакт><ТаблСчФакт>" +
            "<СведТов НомСтр=\"1\" НаимТов=\"Пряжа шерсть\" КолТов=\"4\" ЦенаТов=\"500\" НалСт=\"без НДС\" СтТовУчНал=\"2000\"><ДопСведТов КодТов=\"E2E-ART\" НаимЕдИзм=\"кг\"/></СведТов>" +
            "<СведТов НомСтр=\"2\" НаимТов=\"Доставка\" КолТов=\"1\" ЦенаТов=\"300\" НалСт=\"без НДС\" СтТовУчНал=\"300\"><ДопСведТов КодТов=\"DLV\"/></СведТов>" +
            "</ТаблСчФакт></Документ></Файл>");
    }

    /// <summary>Вымышленный ИНН юрлица с верной контрольной цифрой.</summary>
    private static string TestInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = "8391000" + Random.Shared.Next(10, 99);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    /// <summary>Вход Владельца: пароль, затем подключение приложения-аутентификатора (код TOTP считается из показанного ключа).</summary>
    private async Task SignInOwnerAsync()
    {
        var context = await Browser.NewContextAsync(Options(false, null));
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync("account/login");
            await page.GetByLabel("Эл. почта").FillAsync(OwnerEmail);
            await page.GetByLabel("Пароль").FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("setup-2fa"), new() { Timeout = 30_000 });
            var key = (await page.Locator(".key").InnerTextAsync()).Trim();
            await page.GetByLabel("Код из приложения").FillAsync(Totp.Compute(key.Replace(" ", ""), Totp.StepAt(DateTime.UtcNow)));
            await page.GetByRole(AriaRole.Button, new() { Name = "Подключить и войти" }).ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });
            _ownerState = await context.StorageStateAsync();
        }
        catch (TimeoutException ex)
        {
            // Вход падает в общей подготовке класса — без снимка и адреса в CI не понять, на каком шаге (страница входа или 2FA).
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"owner-sign-in-{Guid.NewGuid():N}.png"), FullPage = true });
            var alert = await page.Locator("[role=alert], .alert, .error").AllInnerTextsAsync();
            throw new TimeoutException($"Вход Владельца не завершён: {page.Url}; сообщения: {string.Join(" | ", alert)}", ex);
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
