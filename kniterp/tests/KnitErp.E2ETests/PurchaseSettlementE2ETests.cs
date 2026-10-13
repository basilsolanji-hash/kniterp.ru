using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D133: вкладка «Расчёты с поставщиком» (вводный остаток, аннулирование с причиной) и «Настройки закупок» (допуск поступления сверх
/// заказа) — на компьютере и телефоне без горизонтальной прокрутки. Владелец — из <see cref="HrAppFixture"/>. Данные вымышленные.
/// </summary>
public sealed class PurchaseSettlementE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    [SqlFact]
    public Task Supplier_opening_balance_is_entered_and_cancelled() => RunAsync("supplier-opening", false, async page =>
    {
        var supplier = $"ООО «Поставщик {Stamp}»";
        await OpenNewSupplierCardAsync(page, supplier);
        await ClickUntilAsync(page.GetByRole(AriaRole.Tab, new() { Name = "Расчёты с поставщиком" }),
            page.GetByRole(AriaRole.Heading, new() { Name = "Вводный остаток расчётов с поставщиком" }));
        await Assertions.Expect(page.GetByText("Вводных остатков нет.")).ToBeVisibleAsync();

        await page.GetByLabel("Сумма").FillAsync("15000");
        await page.GetByLabel("Комментарий").FillAsync("Сальдо на начало учёта");
        await page.GetByRole(AriaRole.Button, new() { Name = "Ввести остаток" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Вводный остаток сохранён.");
        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Сальдо на начало учёта" });
        await Assertions.Expect(row).ToContainTextAsync("наш долг");

        await ClickUntilAsync(row.GetByRole(AriaRole.Button, new() { Name = "Аннулировать" }), page.GetByLabel("Причина аннулирования"));
        await page.GetByLabel("Причина аннулирования").FillAsync("Ошибка ввода");
        await row.GetByRole(AriaRole.Button, new() { Name = "Аннулировать" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Вводный остаток аннулирован.");
        await Assertions.Expect(page.GetByText("аннулирован: Ошибка ввода")).ToBeVisibleAsync();
    });

    [SqlFact]
    public Task Purchase_settings_tolerance_is_saved_on_phone() => RunAsync("purchase-settings", true, async page =>
    {
        await page.GotoAsync("settings/purchases");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Настройки закупок" })).ToBeVisibleAsync();
        var percent = page.GetByLabel("Допуск поступления сверх заказа, %");
        var saved = page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Настройки сохранены." });
        // Значение, введённое до подключения Blazor, теряется, а «Сохранить» сохраняет прежнее (находка для ТЗ интерфейса D8xx) —
        // повторяем, пока после перезагрузки не увидим 5.
        var stored = new Regex("^5([.,]0+)?$");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await percent.FillAsync("5");
            await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
            try
            {
                await saved.WaitForAsync(new() { Timeout = 1500 });
            }
            catch (TimeoutException)
            {
                continue;
            }

            await page.ReloadAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Настройки закупок" })).ToBeVisibleAsync();
            if (stored.IsMatch(await percent.InputValueAsync()))
            {
                break;
            }
        }

        await Assertions.Expect(percent).ToHaveValueAsync(stored);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
    });

    /// <summary>Поставщик через форму списка и переход в его карточку.</summary>
    private static async Task OpenNewSupplierCardAsync(IPage page, string name)
    {
        await page.GotoAsync("counterparties?new=1");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
        await page.GetByLabel("Наименование").FillAsync(name);
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Поставщик" }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = name })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/counterparties/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private static async Task ClickUntilAsync(ILocator target, ILocator appears)
    {
        for (var attempt = 0; attempt < 10 && !await appears.IsVisibleAsync(); attempt++)
        {
            await target.ClickAsync();
            try
            {
                await appears.WaitForAsync(new() { Timeout = 1500 });
            }
            catch (TimeoutException)
            {
            }
        }

        await Assertions.Expect(appears).ToBeVisibleAsync();
    }

    private async Task RunAsync(string name, bool phone, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync(phone);
        try
        {
            await scenario(page);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}-failure.png"), FullPage = true });
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
