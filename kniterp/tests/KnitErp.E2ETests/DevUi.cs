using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Действия карточки модели после пересмотра интерфейса 13.10: выбор стадии — под кнопкой «Другая стадия…», редкие действия — в «Ещё».
/// Нажатие в первые секунды после открытия страницы может потеряться (до подключения Blazor) — повторяем и проверяем результат.
/// </summary>
internal static class DevUi
{
    public static async Task MoveToStageAsync(IPage page, string stage)
    {
        var select = page.GetByLabel("Перевести на стадию");
        for (var i = 0; i < 10 && !await select.IsVisibleAsync(); i++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Другая стадия…", Exact = true }).ClickAsync();
            await page.WaitForTimeoutAsync(300);
        }

        await select.SelectOptionAsync(new SelectOptionValue { Label = stage });
    }

    public static async Task OpenMoreAsync(IPage page)
    {
        var menu = page.Locator(".dev-more-menu");
        for (var i = 0; i < 10 && !await menu.IsVisibleAsync(); i++)
        {
            await page.Locator("details.dev-more > summary").ClickAsync();
            await page.WaitForTimeoutAsync(300);
        }
    }
}
