using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Вкладки одного сквозного сценария со снимком при падении. Контексты закрываются в конце сценария, а не на выходе из блока
/// <c>await using</c>: иначе к моменту падения страницы уже закрыты и снимать нечего. При падении для каждой открытой страницы
/// сохраняются снимок всей страницы (PNG) и её адрес с текстом (TXT) в папку KNITERP_E2E_ARTIFACTS (по умолчанию временная);
/// CI выкладывает эту папку артефактом.
/// </summary>
public sealed class ScenarioBrowser
{
    private readonly IBrowser _browser;
    private readonly List<IBrowserContext> _contexts = [];

    private ScenarioBrowser(IBrowser browser) => _browser = browser;

    public static string ArtifactsDirectory => Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();

    public static async Task RunAsync(IBrowser browser, Func<ScenarioBrowser, Task> scenario, [CallerMemberName] string name = "")
    {
        var run = new ScenarioBrowser(browser);
        try
        {
            await scenario(run);
        }
        catch
        {
            await run.CaptureAsync(name);
            throw;
        }
        finally
        {
            foreach (var context in run._contexts)
            {
                await context.CloseAsync();
            }
        }
    }

    public async Task<ScenarioContext> NewContextAsync(BrowserNewContextOptions options)
    {
        var context = await _browser.NewContextAsync(options);
        _contexts.Add(context);
        return new ScenarioContext(context);
    }

    private async Task CaptureAsync(string name)
    {
        var dir = ArtifactsDirectory;
        Directory.CreateDirectory(dir);
        var n = 0;
        foreach (var page in _contexts.SelectMany(c => c.Pages).Where(p => !p.IsClosed))
        {
            var file = Path.Combine(dir, $"{name}-{++n}-failure");
            try
            {
                await page.ScreenshotAsync(new() { Path = file + ".png", FullPage = true });
                await File.WriteAllTextAsync(file + ".txt", page.Url + "\n" + await page.InnerTextAsync("body"));
            }
            catch (PlaywrightException)
            {
                // Снимок — вспомогательный: страница могла зависнуть; исходная ошибка теста важнее.
            }
        }
    }
}

/// <summary>Контекст браузера сценария: закрывает его <see cref="ScenarioBrowser"/> после снимка, поэтому выход из блока его не закрывает.</summary>
public sealed class ScenarioContext(IBrowserContext context) : IAsyncDisposable
{
    public IBrowserContext Context => context;

    public Task<IPage> NewPageAsync() => context.NewPageAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
