using System.Text;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D119 (А1): экран «Деньги → Загрузка выписки банка» на телефоне: файл выбирается, ошибка по счёту понятна, вёрстка без горизонтальной
/// прокрутки, без ошибок JS. Создание оплат по выписке проверяется интеграционным тестом BankStatementTests.
/// </summary>
public sealed class BankStatementE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public async Task Statement_for_unknown_account_is_explained_on_phone()
    {
        var page = await app.OwnerPageAsync(phone: true);
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync("money/bank-statement");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
            await page.WaitForTimeoutAsync(300);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Загрузка выписки банка" })).ToBeVisibleAsync();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var text = "1CClientBankExchange\nВерсияФормата=1.03\nКодировка=Windows\nРасчСчет=40702810999999999999\n"
                       + "СекцияДокумент=Платежное поручение\nНомер=1\nДата=05.10.2026\nСумма=10.00\nПлательщикСчет=40702810500000000077\n"
                       + "ПолучательСчет=40702810999999999999\nНазначениеПлатежа=Тест\nКонецДокумента\nКонецФайла\n";
            await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "kl_to_1c.txt", MimeType = "text/plain", Buffer = Encoding.GetEncoding(1251).GetBytes(text),
            });
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Расчётного счёта из выписки нет");
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
            Assert.Empty(errors);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "bank-statement-failure.png"), FullPage = true });
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
