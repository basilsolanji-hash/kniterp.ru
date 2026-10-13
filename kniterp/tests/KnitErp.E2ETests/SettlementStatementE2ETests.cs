using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D143: акт сверки с покупателем в браузере — вводный остаток, аванс, отгрузка и возврат строками, итоги и вывод о задолженности;
/// 1440 и 390 px, без горизонтальной прокрутки страницы, 0 ошибок JS. Данные вымышленные, готовятся сервисами (<see cref="HrAppFixture"/>).
/// </summary>
public sealed class SettlementStatementE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public async Task Customer_statement_shows_opening_advance_shipment_and_return_on_desktop_and_phone()
    {
        var name = HrAppFixture.SettlementCustomerName;
        var customer = app.SettlementCustomerId;
        var page = await app.OwnerPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) =>
        {
            if (m.Type == "error")
            {
                errors.Add(m.Text);
            }
        };

        try
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync($"settlement-statement?counterparty={customer}");
            var table = page.Locator("table.compact");
            await Assertions.Expect(table).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex(Regex.Escape(name)) })).ToBeVisibleAsync();

            // Строки по шагам: остаток 5 000 — дебет, аванс 2 000 — кредит, отгрузка 10 000 — дебет, возврат 3 000 — кредит.
            await Assertions.Expect(Row(page, "Вводный остаток расчётов")).ToContainTextAsync(Money("5 000,00"));
            await Assertions.Expect(Row(page, "Оплата")).ToContainTextAsync(Money("2 000,00"));
            await Assertions.Expect(Row(page, "Отгрузка")).ToContainTextAsync(Money("10 000,00"));
            await Assertions.Expect(Row(page, "Возврат от покупателя")).ToContainTextAsync(Money("3 000,00"));
            await Assertions.Expect(Row(page, "Обороты за период")).ToContainTextAsync(Money("15 000,00"));
            await Assertions.Expect(Row(page, "Обороты за период")).ToContainTextAsync(Money("5 000,00"));
            await Assertions.Expect(Row(page, "Сальдо на конец")).ToContainTextAsync(Money("10 000,00"));
            await Assertions.Expect(page.GetByText(new Regex($"задолженность {Regex.Escape(name)} перед .+: 10\\s000,00"))).ToBeVisibleAsync();
            Assert.True(await NoHorizontalScrollAsync(page));

            // Телефон: та же таблица, страница не шире экрана (широкая таблица прокручивается в своём блоке).
            await page.SetViewportSizeAsync(390, 844);
            await page.ReloadAsync();
            await Assertions.Expect(Row(page, "Сальдо на конец")).ToContainTextAsync(Money("10 000,00"));
            Assert.True(await NoHorizontalScrollAsync(page));
            Assert.Empty(errors);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "settlement-statement-failure.png"), FullPage = true });
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static ILocator Row(IPage page, string text) => page.Locator("table.compact tr").Filter(new() { HasText = text }).First;

    /// <summary>Суммы в ru-RU разделены неразрывным пробелом.</summary>
    private static Regex Money(string amount) => new(Regex.Escape(amount).Replace("\\ ", "\\s"));

    private static Task<bool> NoHorizontalScrollAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1");
}
