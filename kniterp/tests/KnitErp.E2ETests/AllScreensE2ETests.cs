using System.Text.Json;
using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D890–D891 (сессия «Интерфейс», этап 4): обход всех экранов описи <c>docs/ui/screens.md</c> без параметров в адресе.
/// Владелец открывает каждый на 1440 px (светлая тема) и 390 px (тёмная): без ошибок JavaScript, без горизонтальной прокрутки,
/// с заголовком <c>h1</c>, без текста исключения; axe-core (локальная копия) — без нарушений уровня serious и critical.
/// Данные вымышленные (организация из <see cref="HrAppFixture"/>).
/// </summary>
public sealed partial class AllScreensE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    /// <summary>Печатные формы — лист бумаги без меню и заголовка страницы; ширина листа на телефоне не ужимается.</summary>
    private static bool IsPrint(string route) => route.StartsWith("/print/", StringComparison.Ordinal) || route.EndsWith("/print", StringComparison.Ordinal);

    /// <summary>
    /// Нарушения в экранах других сессий, переданные им (`docs/sessions/ui.md`, «Вопросы к модулям»). Исправили — строку убрать.
    /// </summary>
    private static readonly Dictionary<string, string> PassedToModules = new(StringComparer.Ordinal)
    {
        ["/dev/models"] = "scrollable-region-focusable", // доска моделей «Разработки»: tabindex="0" и role="region"
    };

    [SqlFact]
    public Task Every_screen_opens_on_desktop_without_errors_and_axe_violations() => WalkAsync(phone: false, scheme: "light", axe: true);

    [SqlFact]
    public Task Every_screen_fits_the_phone_in_dark_theme() => WalkAsync(phone: true, scheme: "dark", axe: true);

    private async Task WalkAsync(bool phone, string scheme, bool axe)
    {
        var page = await app.OwnerPageAsync(phone);
        List<string> problems;
        try
        {
            problems = await WalkAsync(page, scheme, axe);
        }
        finally
        {
            await page.Context.CloseAsync();
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Обход всех экранов описи вошедшим пользователем (вкладка уже открыта); возвращает найденные нарушения.</summary>
    internal static async Task<List<string>> WalkAsync(IPage page, string scheme, bool axe)
    {
        var axeScript = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "axe.min.js"));
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) =>
        {
            if (m.Type == "error")
            {
                errors.Add(m.Text);
            }
        };
        // Скрипт axe отдаётся с адреса приложения: CSP `script-src 'self'` не даёт встраивать его в страницу.
        await page.RouteAsync("**/__axe.js", r => r.FulfillAsync(new() { ContentType = "application/javascript", Body = axeScript }));
        await page.EmulateMediaAsync(new() { ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await page.GotoAsync("");
        // Главная должна подключиться полностью: иначе переход на первый экран прерывает её подключение, и браузер пишет ошибку.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.Locator("main h1").First.WaitForAsync(new() { Timeout = 10000 });
        await page.EvaluateAsync("t => localStorage.setItem('kniterp.theme', t)", scheme);

        var problems = new List<string>();
        {
            foreach (var route in Routes())
            {
                errors.Clear();
                await page.GotoAsync(route.TrimStart('/'));
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
                await page.WaitForTimeoutAsync(300);
                if (!IsPrint(route) && route != "/error")
                {
                    // Экран с загрузкой данных показывает заголовок после ответа сервера.
                    await page.Locator("main h1").First.WaitForAsync(new() { Timeout = 5000 }).ContinueWith(_ => { });
                }

                var state = await page.EvaluateAsync<ScreenState>("""
                    () => ({
                      overflow: document.documentElement.scrollWidth - window.innerWidth,
                      h1: document.querySelectorAll('main h1').length,
                      exception: /Exception|Unhandled/.test(document.body.innerText),
                    })
                    """);
                if (errors.Count > 0)
                {
                    problems.Add($"{route}: ошибки JS — {string.Join(" | ", errors.Take(2))}");
                }

                if (state.Overflow > 1 && !IsPrint(route))
                {
                    problems.Add($"{route}: горизонтальная прокрутка {state.Overflow} px");
                }

                if (state.H1 == 0 && !IsPrint(route) && route != "/error")
                {
                    problems.Add($"{route}: нет заголовка h1");
                }

                if (state.Exception)
                {
                    problems.Add($"{route}: текст исключения на странице");
                }

                if (axe)
                {
                    await page.AddScriptTagAsync(new() { Url = "__axe.js" });
                    var violations = await page.EvaluateAsync<JsonElement>("""
                        async () => (await window.axe.run(document, { resultTypes: ['violations'] })).violations
                          .filter(v => v.impact === 'serious' || v.impact === 'critical')
                          .map(v => v.id + ' — ' + (v.nodes[0]?.target?.join(' ') ?? ''))
                        """);
                    foreach (var v in violations.EnumerateArray().Select(v => v.GetString()!))
                    {
                        if (!(PassedToModules.TryGetValue(route, out var known) && v.StartsWith(known, StringComparison.Ordinal)))
                        {
                            problems.Add($"{route}: axe {v}");
                        }
                    }
                }
            }
        }
        return problems;
    }

    /// <summary>Адреса без параметров из описи экранов; вход, выход и страницы без входа проверяют свои тесты.</summary>
    private static IEnumerable<string> Routes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
        {
            dir = dir.Parent;
        }

        var inventory = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "ui", "screens.md"));
        return Row().Matches(inventory).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(r => !r.Contains('{') && !r.StartsWith("/account", StringComparison.Ordinal) && !r.Contains("logout", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\| [^|]+ \| (/[^|]*?) \|", RegexOptions.Multiline)]
    private static partial Regex Row();

    /// <summary>Состояние экрана из браузера (Playwright заполняет свойства, нужен конструктор без параметров).</summary>
    private sealed class ScreenState
    {
        public double Overflow { get; set; }
        public int H1 { get; set; }
        public bool Exception { get; set; }
    }
}
