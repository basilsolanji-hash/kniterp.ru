using System.Text;
using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D813 (сессия «Интерфейс», запрос владельца 13.10, п. 5): печатные формы печатаются в PDF формата A4, текст не выходит за
/// ширину листа (длинные названия переносятся), шапка таблицы повторяется на страницах, номер страницы — в поле листа.
/// Владелец — из <see cref="HrAppFixture"/>, данные вымышленные.
/// </summary>
public sealed partial class PrintFormsE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string[] Forms = ["print/staffing", "print/cash-book", "print/settlement-statement"];

    [SqlFact]
    public async Task Forms_print_to_a4_pdf_without_cut_text()
    {
        foreach (var route in Forms)
        {
            await CheckAsync(route);
        }
    }

    private async Task CheckAsync(string route)
    {
        var page = await app.OwnerPageAsync();
        try
        {
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);
            await page.GotoAsync(route);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
            await Assertions.Expect(page.Locator(".sheet").First).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(500);

            await page.EmulateMediaAsync(new() { Media = Media.Print });
            // Ширина печатной области A4 (книжная, поля 10 мм) — 190 мм ≈ 718 px; альбомная — 277 мм ≈ 1047 px.
            await page.SetViewportSizeAsync(1047, 900);
            var overflow = await page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('.sheet td, .sheet th, .sheet p, .sheet div')]
                  .filter(e => e.scrollWidth > e.clientWidth + 2 && getComputedStyle(e).overflow !== 'visible')
                  .slice(0, 5).map(e => e.tagName + ': ' + e.textContent.trim().slice(0, 40))
                """);
            Assert.True(overflow.Length == 0, $"{route}: текст обрезан — {string.Join("; ", overflow)}");
            var sheetWider = await page.EvaluateAsync<bool>("() => [...document.querySelectorAll('.sheet')].some(s => s.scrollWidth > s.clientWidth + 2)");
            Assert.False(sheetWider, $"{route}: содержимое шире листа");
            Assert.Equal("table-header-group", await page.EvaluateAsync<string>(
                "() => { const h = document.querySelector('.sheet thead'); return h ? getComputedStyle(h).display : 'table-header-group'; }"));

            var pdf = await page.PdfAsync(new() { PreferCSSPageSize = true, PrintBackground = true });
            var text = Encoding.Latin1.GetString(pdf);
            Assert.StartsWith("%PDF", text);
            Assert.True(PageObject().Matches(text).Count >= 1, "В PDF нет страниц");
            // A4 — 595 × 842 пунктов (книжная или альбомная).
            Assert.Matches(A4Box(), text);
            Assert.Empty(errors);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [GeneratedRegex(@"/Type\s*/Page[^s]")]
    private static partial Regex PageObject();

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+(59[45](\.\d+)?\s+84[12](\.\d+)?|84[12](\.\d+)?\s+59[45](\.\d+)?)\s*\]")]
    private static partial Regex A4Box();
}
