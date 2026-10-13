using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D816 (сессия «Интерфейс», раздел 0 п. 6–7): объект открывается нажатием в любом месте строки; Ctrl+S сохраняет форму;
/// уход с несохранённой формой спрашивает подтверждение. Данные вымышленные.
/// </summary>
public sealed class ConveniencesE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    [SqlFact]
    public async Task Ctrl_s_saves_row_opens_card_and_unsaved_form_asks()
    {
        var page = await app.OwnerPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            // Ctrl+S сохраняет форму нового контрагента.
            var name = $"ООО «Строка {Stamp}»";
            await OpenAsync(page, "counterparties?new=1");
            await page.GetByLabel("Наименование").FillAsync(name);
            await page.Keyboard.PressAsync("Control+s");
            var link = page.GetByRole(AriaRole.Link, new() { Name = name });
            await Assertions.Expect(link).ToBeVisibleAsync();

            // Нажатие на строку вне ссылки (столбец «Роль») открывает карточку.
            var row = page.GetByRole(AriaRole.Row).Filter(new() { Has = link });
            await row.Locator("td").Nth(2).ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/counterparties/\d+$"));

            // Несохранённая форма: «Отмена» в вопросе оставляет на странице, «ОК» — уходит.
            await OpenAsync(page, "counterparties?new=1");
            await page.GetByLabel("Наименование").FillAsync($"Черновик {Stamp}");
            string? asked = null;
            void Dismiss(object? _, IDialog d) { asked = d.Message; d.DismissAsync(); }
            page.Dialog += Dismiss;
            await page.Locator(".side a.nav-single", new() { HasText = "Главная" }).First.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            page.Dialog -= Dismiss;
            Assert.Equal("Есть несохранённые изменения. Уйти без сохранения?", asked);
            Assert.Contains("counterparties", page.Url);
            void Accept(object? _, IDialog d) => d.AcceptAsync();
            page.Dialog += Accept;
            await page.Locator(".side a.nav-single", new() { HasText = "Главная" }).First.ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/$"));
            page.Dialog -= Accept;

            Assert.Empty(errors);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task OpenAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
    }
}
