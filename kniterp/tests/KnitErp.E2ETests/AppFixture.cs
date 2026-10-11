using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Application.Production;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Приложение для сквозных тестов: отдельная тестовая база (как в интеграционных тестах, KNITERP_TEST_SQL), данные готовятся
/// сервисами, затем KnitErp.Web запускается отдельным процессом в режиме разработки, а Chromium из Playwright работает с ним по HTTP.
/// Пароль пользователя — случайный на каждый запуск: паролей в репозитории нет.
/// Браузер: PLAYWRIGHT_CHROMIUM_PATH, иначе Chromium из PLAYWRIGHT_BROWSERS_PATH, иначе браузер, установленный Playwright.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string UserEmail = "nachalnik-ceha@e2e.local";

    private readonly SqlTestHost _host = new();
    private readonly List<string> _log = [];
    private long _userId;
    private long _organizationId;
    private Process? _app;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = string.Empty;
    public string Password { get; } = "E2e-" + Guid.NewGuid().ToString("N")[..16] + "!a";
    public IBrowser Browser { get; private set; } = default!;
    public long Sweater { get; private set; }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestHost.EnvVar)))
        {
            return;
        }

        await _host.InitializeAsync();
        await SeedAsync();
        await StartAppAsync();
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = ChromiumPath() });
    }

    public async Task DisposeAsync()
    {
        if (_playwright is not null)
        {
            await Browser.DisposeAsync();
            _playwright.Dispose();
        }

        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }

        if (_certificateFile is not null)
        {
            File.Delete(_certificateFile);
        }

        await _host.DisposeAsync();
    }

    /// <summary>Организация, склады, свитер с действующей техкартой и начальник цеха (Руководитель подразделения) с паролем.</summary>
    private async Task SeedAsync()
    {
        CreatedOrganization org;
        await using (var s = _host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                "Общество с ограниченной ответственностью «Тест-Трикотаж»", "ООО «Тест-Трикотаж»", TestInn(), null, false,
                "Europe/Moscow", "owner@e2e.local", "Владелец", "RU"));
        }

        string token;
        await using (var s = _host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var pcs = units.Single(u => u.Symbol == "шт").Id;
            var kg = units.Single(u => u.Symbol == "кг").Id;
            await s.Warehouses.CreateWarehouseAsync(ProductionOrderService.DefaultWipWarehouseName, null);
            await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            Sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, pcs, null));
            var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, kg, null));
            var card = await s.TechCards.CreateAsync(Sweater, 10, null);
            await s.TechCards.SetLineAsync(card, yarn, 3.2m, 3m, (await s.TechCards.GetAsync(card)).RowVersion);
            await s.TechCards.ActivateAsync(card, (await s.TechCards.GetAsync(card)).RowVersion);
            var invitation = await s.Access.InviteAsync(new InviteUserCommand(UserEmail, "Начальник цеха", SystemRoles.DepartmentHead, null));
            token = invitation.SetupToken!;
            _userId = invitation.UserId;
            _organizationId = org.OrganizationId;
        }

        await using (var s = _host.As(null, null))
        {
            await s.SignIn.AcceptInvitationAsync(token, Password, Password);
        }
    }

    /// <summary>Запущенный заказ на свитера от имени начальника цеха — для сценариев цеха.</summary>
    public async Task<long> CreateReleasedOrderAsync(decimal quantity)
    {
        await using var s = _host.As(_userId, _organizationId);
        var options = await s.ProductionOrders.OptionsAsync();
        var output = options.Warehouses.Single(w => w.Name == "Склад готовой продукции").Id;
        var id = await s.ProductionOrders.CreateAsync(new ProductionOrderCommand(Sweater, quantity, DateOnly.FromDateTime(DateTime.UtcNow), null,
            KnitErp.Domain.Production.ProductionSource.Plan, null, options.DefaultWipWarehouseId!.Value, output, null));
        await s.ProductionOrders.ReleaseAsync(id, (await s.ProductionOrders.GetAsync(id)).RowVersion);
        return id;
    }

    private async Task StartAppAsync()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        // Cookie приложения — только Secure, поэтому HTTPS с временным самоподписанным сертификатом localhost (файл во временной папке).
        BaseUrl = $"https://localhost:{port}/";
        var (certificate, certificatePassword) = CreateCertificate();
        await using var db = _host.NewDb();
        var root = FindRoot();
        var info = new ProcessStartInfo("dotnet",
            ["run", "--no-build", "--no-launch-profile", "-c", Configuration, "--project", Path.Combine(root, "src", "KnitErp.Web")])
        {
            WorkingDirectory = Path.Combine(root, "src", "KnitErp.Web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = BaseUrl;
        info.Environment["ConnectionStrings__KnitErp"] = db.Database.GetConnectionString();
        info.Environment["Webhooks__Disabled"] = "true";
        info.Environment["Kestrel__Certificates__Default__Path"] = certificate;
        info.Environment["Kestrel__Certificates__Default__Password"] = certificatePassword;
        _app = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить KnitErp.Web.");
        _app.OutputDataReceived += (_, e) => Log(e.Data);
        _app.ErrorDataReceived += (_, e) => Log(e.Data);
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler);
        for (var i = 0; i < 120; i++)
        {
            if (_app.HasExited)
            {
                throw new InvalidOperationException($"KnitErp.Web завершился с кодом {_app.ExitCode}:\n{AppLog}");
            }

            try
            {
                if ((await http.GetAsync(BaseUrl + "account/login")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"KnitErp.Web не ответил за 2 минуты:\n{AppLog}");
    }

    /// <summary>Вымышленный ИНН юрлица с верной контрольной цифрой.</summary>
    private static string TestInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = "8390000" + Random.Shared.Next(10, 99);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    /// <summary>Последние строки вывода приложения — в сообщение об ошибке запуска.</summary>
    private string AppLog
    {
        get
        {
            lock (_log)
            {
                return string.Join("\n", _log.TakeLast(40));
            }
        }
    }

    private void Log(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_log)
        {
            _log.Add(line);
        }
    }

    /// <summary>Конфигурация сборки тестов (Debug локально, Release в CI) — приложение запускается из той же.</summary>
    private static string Configuration =>
        AppContext.BaseDirectory.Contains($"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}") ? "Release" : "Debug";

    private string? _certificateFile;

    private (string Path, string Password) CreateCertificate()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var password = Guid.NewGuid().ToString("N");
        _certificateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kniterp-e2e-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(_certificateFile, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
        return (_certificateFile, password);
    }

    private static string? ChromiumPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var browsers = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(browsers) || !Directory.Exists(browsers))
        {
            return null;
        }

        return Directory.EnumerateDirectories(browsers, "chromium-*")
            .Select(d => Path.Combine(d, "chrome-linux", "chrome"))
            .Where(File.Exists)
            .OrderDescending()
            .FirstOrDefault();
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден KnitErp.slnx");
    }
}
