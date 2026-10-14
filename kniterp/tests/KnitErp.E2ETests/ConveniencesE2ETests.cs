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
    public async Task Ctrl_s_saves_row_opens_card_unsaved_form_asks_and_filter_stays_in_address()
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

            // D818: массовое действие — отметить строку и отправить в архив одним нажатием (с подтверждением).
            await OpenAsync(page, "counterparties");
            await page.GetByRole(AriaRole.Checkbox, new() { Name = $"Выбрать: {name}" }).CheckAsync();
            await Assertions.Expect(page.GetByText("Отмечено: 1")).ToBeVisibleAsync();
            await page.Locator(".bulk-bar").GetByRole(AriaRole.Button, new() { Name = "В архив" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("В архиве: 1.");

            // D819: открытая пользователем форма — курсор сразу в первом поле.
            await page.GetByRole(AriaRole.Button, new() { Name = "Добавить контрагента" }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Наименование")).ToBeFocusedAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Отмена" }).First.ClickAsync();

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

            // D817: фильтр — в адресе страницы: после ухода и «Назад» открывается тот же отбор, без второй загрузки.
            await OpenAsync(page, "stock-documents");
            var status = page.Locator("form[data-autosubmit] label", new() { HasTextRegex = new Regex("^\\s*Статус") }).Locator("select");
            await status.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            var chosen = await status.InputValueAsync();
            await page.WaitForURLAsync(new Regex($@"/stock-documents\?.*status={chosen}"));
            await page.Locator(".side a.nav-single", new() { HasText = "Главная" }).First.ClickAsync();
            await page.WaitForURLAsync(new Regex(@"/$"));
            // Ждём, пока главная загрузится: уход со страницы во время её загрузки — отдельная находка (docs/sessions/ui.md, сессии № 1).
            // D820: «Назад» сразу, не дожидаясь данных главной, — переход ждёт конца её загрузки, вкладка не падает.
            await page.GoBackAsync();
            await page.WaitForURLAsync(new Regex(@"/stock-documents\?"));
            await Assertions.Expect(status).ToHaveValueAsync(chosen);
            // D822: ошибка сервера «ИНН: … — номер указан неверно» — поле ИНН подсвечено и связано с сообщением.
            await OpenAsync(page, "counterparties?new=1");
            await page.GetByLabel("Наименование").FillAsync($"ООО «Ошибка {Stamp}»");
            var inn = page.GetByLabel("ИНН", new() { Exact = true });
            await inn.FillAsync("123");
            await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
            await Assertions.Expect(inn).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(inn).ToBeFocusedAsync();
            await inn.FillAsync("7707083893");
            await Assertions.Expect(inn).Not.ToHaveAttributeAsync("aria-invalid", "true");
            Assert.Empty(errors);
        }
        catch
        {
            var dir = Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "conveniences-failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(dir, "conveniences-app.log"), app.AppLog);
            throw;
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
