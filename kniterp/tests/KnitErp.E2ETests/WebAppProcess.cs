using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// KnitErp.Web отдельным процессом для сквозных тестов: свободный порт, HTTPS с временным самоподписанным сертификатом (cookie — только Secure),
/// режим разработки, своя база. Общий для модулей помощник — фикстура модуля готовит данные и вызывает <see cref="StartAsync"/>.
/// То же самое внутри <see cref="AppFixture"/> производства; при следующей правке её можно перевести на этот класс.
/// </summary>
public sealed class WebAppProcess : IAsyncDisposable
{
    private readonly List<string> _log = [];
    private Process? _app;
    private string? _certificateFile;

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<WebAppProcess> StartAsync(string connectionString)
    {
        var app = new WebAppProcess();
        await app.RunAsync(connectionString);
        return app;
    }

    /// <summary>Последние строки вывода приложения — в сообщение об ошибке.</summary>
    public string Log
    {
        get
        {
            lock (_log)
            {
                return string.Join("\n", _log.TakeLast(400));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }

        if (_certificateFile is not null)
        {
            File.Delete(_certificateFile);
        }
    }

    /// <summary>Chromium: PLAYWRIGHT_CHROMIUM_PATH, иначе из PLAYWRIGHT_BROWSERS_PATH, иначе установленный Playwright.</summary>
    public static Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = ChromiumPath() });

    private async Task RunAsync(string connectionString)
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        BaseUrl = $"https://localhost:{port}/";
        var (certificate, certificatePassword) = CreateCertificate();
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
        info.Environment["ConnectionStrings__KnitErp"] = connectionString;
        info.Environment["Webhooks__Disabled"] = "true";
        info.Environment["Automation__WorkerDisabled"] = "true"; // D145: автоматика и курсы ЦБ в e2e не работают сами (без сети, без автозакрытия)
        // D125: ссылки для сотрудников строятся только от адреса сервера из настроек — в тестах это адрес запущенного приложения.
        info.Environment["App__PublicUrl"] = BaseUrl.TrimEnd('/');
        info.Environment["Kestrel__Certificates__Default__Path"] = certificate;
        info.Environment["Kestrel__Certificates__Default__Password"] = certificatePassword;
        _app = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить KnitErp.Web.");
        _app.OutputDataReceived += (_, e) => Append(e.Data);
        _app.ErrorDataReceived += (_, e) => Append(e.Data);
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler);
        for (var i = 0; i < 120; i++)
        {
            if (_app.HasExited)
            {
                throw new InvalidOperationException($"KnitErp.Web завершился с кодом {_app.ExitCode}:\n{Log}");
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

        throw new TimeoutException($"KnitErp.Web не ответил за 2 минуты:\n{Log}");
    }

    private void Append(string? line)
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
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";

    private (string Path, string Password) CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var password = Guid.NewGuid().ToString("N");
        _certificateFile = Path.Combine(Path.GetTempPath(), $"kniterp-e2e-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(_certificateFile, cert.Export(X509ContentType.Pfx, password));
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
