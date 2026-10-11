using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D115: страница «Вход и резервные коды» — раздел «Пароль и сессии». Сессия Владельца общая для набора, поэтому в браузере проверяются
/// отказ при неверном текущем пароле и вёрстка на телефоне; успешные смена пароля и выход везде — в интеграционных тестах.
/// </summary>
public sealed class SecurityE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public Task Wrong_current_password_is_refused_and_phone_layout_fits() => RunAsync("security", async page =>
    {
        await page.GotoAsync("security");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Пароль и сессии" })).ToBeVisibleAsync();

        var form = page.GetByRole(AriaRole.Form, new() { Name = "Сменить пароль" });
        await form.GetByLabel("Текущий пароль").FillAsync("неверный текущий пароль");
        await form.GetByLabel("Новый пароль", new() { Exact = true }).FillAsync("вязальная машина у окна");
        await form.GetByLabel("Повторите новый пароль").FillAsync("вязальная машина у окна");
        await form.GetByLabel("Код из приложения").FillAsync("000000");
        await form.GetByRole(AriaRole.Button, new() { Name = "Сменить пароль" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Неверный текущий пароль" })).ToBeVisibleAsync();

        // Поля пароля очищены, сессия жива: страница на месте.
        await Assertions.Expect(form.GetByLabel("Текущий пароль")).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(page.GetByRole(AriaRole.Form, new() { Name = "Выйти на всех устройствах" })).ToBeVisibleAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));
    });

    private async Task RunAsync(string name, Func<IPage, Task> scenario)
    {
        var page = await app.OwnerPageAsync(phone: true);
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await scenario(page);
            Assert.Empty(errors);
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
