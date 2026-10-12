using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D127: «одно устройство» в браузере — вход со второго устройства разрешён, первое видит баннер с отсчётом и через 30 с попадает
/// на страницу входа с объяснением; форма «Ограничения входа» показывает текущий адрес и требует повторного подтверждения.
/// </summary>
public sealed class SignInRestrictionE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    [SqlFact]
    public async Task Second_device_sign_in_warns_first_device_and_closes_it_after_30_seconds()
    {
        var email = await app.CreateSingleDeviceWorkerAsync();
        var first = await app.AnonymousPageAsync();
        var second = await app.AnonymousPageAsync();
        var errors = new List<string>();
        first.PageError += (_, e) => errors.Add(e);
        try
        {
            await SignInAsync(first, email);
            await first.WaitForFunctionAsync("() => window.Blazor !== undefined");
            await SignInAsync(second, email);

            // Вкладка первого устройства узнаёт о закрытии при быстрой проверке (раз в 10 с) — баннер без кнопки закрытия.
            var banner = first.GetByTestId("session-ending");
            await Assertions.Expect(banner).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Assertions.Expect(banner).ToContainTextAsync("Выполнен вход с другого устройства. Этот сеанс закроется через");
            Assert.Equal(0, await banner.Locator("button").CountAsync());

            // Через 30 с (и не позже 10 с после) — страница входа с причиной.
            await first.WaitForURLAsync(url => url.Contains("/account/login"), new() { Timeout = 45_000 });
            await Assertions.Expect(first.GetByRole(AriaRole.Alert)).ToContainTextAsync("Сеанс закрыт: выполнен вход с другого устройства.");

            // Прежняя cookie больше не пускает, второе устройство работает.
            await first.GotoAsync("");
            await first.WaitForURLAsync(url => url.Contains("/account/login"), new() { Timeout = 15_000 });
            await second.GotoAsync("");
            await second.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.DoesNotContain("/account/login", second.Url);
            Assert.Empty(errors);
        }
        catch
        {
            await SnapshotAsync(first, "single-device-first");
            await SnapshotAsync(second, "single-device-second");
            throw;
        }
        finally
        {
            await first.Context.CloseAsync();
            await second.Context.CloseAsync();
        }
    }

    [SqlFact]
    public async Task Restrictions_form_shows_current_address_and_asks_for_password_on_save()
    {
        await app.CreateSingleDeviceWorkerAsync();
        var page = await app.OwnerPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync("users");
            await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
            var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Сменщица Одна" }).First;
            await Assertions.Expect(row.GetByText("ограничен вход")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            // Список приходит из предварительной отрисовки: щелчок до подключения вкладки теряется — повторяем, пока не откроется панель.
            var panel = page.GetByRole(AriaRole.Region, new() { Name = "Ограничения входа" });
            for (var attempt = 0; attempt < 20 && await panel.CountAsync() == 0; attempt++)
            {
                await row.GetByRole(AriaRole.Button, new() { Name = "Ограничения входа" }).ClickAsync();
                await page.WaitForTimeoutAsync(500);
            }

            await Assertions.Expect(panel.GetByTestId("single-device")).ToBeCheckedAsync();
            await Assertions.Expect(panel.GetByText("Ваш текущий адрес:")).ToBeVisibleAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Добавить мой текущий адрес" }).ClickAsync();
            await Assertions.Expect(panel.GetByTestId("networks")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"/(32|128)$"));

            await panel.GetByRole(AriaRole.Button, new() { Name = "Сохранить ограничения" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("Подтвердите паролем или кодом");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Alert)).ToContainTextAsync("Действие не выполнено: подтверждение отменено.");
            Assert.Empty(errors);
        }
        catch
        {
            await SnapshotAsync(page, "restrictions-form");
            throw;
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private async Task SignInAsync(IPage page, string email)
    {
        await page.GotoAsync("account/login");
        await page.GetByLabel("Эл. почта").FillAsync(email);
        await page.GetByLabel("Пароль").FillAsync(app.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/account/"), new() { Timeout = 30_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private static async Task SnapshotAsync(IPage page, string name)
    {
        var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}-failure.png"), FullPage = true });
    }
}
