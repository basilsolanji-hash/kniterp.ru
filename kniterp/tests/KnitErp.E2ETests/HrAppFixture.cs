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
            await s.Employees.HireAsync(new KnitErp.Application.Structure.EmployeeCommand("0001", "Петрова", "Анна", "Сергеевна",
                department, position, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1)));
        }

        await using (var s = _host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(org.OwnerSetupToken, Password, Password);
        }
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
        await context.CloseAsync();
    }
}
