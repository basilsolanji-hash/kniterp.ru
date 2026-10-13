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
                "Общество с ограниченной ответственностью «Кадры-Трикотаж»", "ООО «Кадры-Трикотаж»", TestInn(), null, false,
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
