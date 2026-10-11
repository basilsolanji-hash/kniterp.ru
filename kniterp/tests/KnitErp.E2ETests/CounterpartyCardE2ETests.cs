using System.Text.RegularExpressions;
using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D500–D501 (сессия контрагентов): карточка контрагента с договорами; меню на телефоне не прокручивает страницу под собой.
/// Перенесено с Playwright на Node без изменения проверок (D218). Владелец — из <see cref="HrAppFixture"/>. Данные вымышленные.
/// </summary>
public sealed class CounterpartyCardE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    [SqlFact]
    public Task Contract_is_added_closed_and_reopened() => RunAsync("counterparty-card", false, async page =>
    {
        var customer = $"ООО «Клиент {Stamp}»";
        await OpenNewCustomerCardAsync(page, customer);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = customer })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("Договоры") })).ToHaveAttributeAsync("aria-selected", "true");

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить договор" }).ClickAsync();
        await page.GetByLabel("Номер").FillAsync($"Д-{Stamp}");
        await page.GetByLabel("Отсрочка оплаты, дней").FillAsync("30");
        await page.GetByLabel("Предмет договора").FillAsync("Поставка джемперов");
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Договор добавлен");
        var row = page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex($"Д-{Stamp}") });
        await Assertions.Expect(row).ToContainTextAsync("30 дн.");
        await Assertions.Expect(row).ToContainTextAsync("Действует");

        await row.GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Да", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Договор закрыт");
        await Assertions.Expect(row).ToContainTextAsync("Закрыт");
        await row.GetByRole(AriaRole.Button, new() { Name = "Открыть снова" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Договор снова действует");

        // Вкладки истории открываются без ошибок.
        await page.GetByRole(AriaRole.Tab, new() { Name = "Заказы" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Заказов нет.").First).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new() { Name = "Документы" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Счета покупателю" })).ToBeVisibleAsync();
    });

    [SqlFact]
    public Task Phone_card_has_no_horizontal_scroll_and_menu_scrolls_itself() => RunAsync("counterparty-phone", true, async page =>
    {
        await OpenNewCustomerCardAsync(page, $"ООО «Телефон {Stamp}»");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"));

        // Страница прокручена вниз, затем открыто меню.
        await page.EvaluateAsync("() => window.scrollTo(0, 200)");
        var before = await page.EvaluateAsync<double>("() => window.scrollY");
        await page.GetByRole(AriaRole.Button, new() { Name = "Меню" }).ClickAsync();
        var nav = page.Locator("#nav-body");
        await Assertions.Expect(nav).ToBeVisibleAsync();

        var state = await page.EvaluateAsync<MenuState>("""
            () => {
              const side = document.querySelector('.side');
              const body = document.getElementById('nav-body');
              return {
                bodyOverflow: getComputedStyle(document.body).overflow,
                sidePosition: getComputedStyle(side).position,
                sideHeight: side.getBoundingClientRect().height,
                viewport: window.innerHeight,
                navOverscroll: getComputedStyle(body).overscrollBehaviorY,
                navScrollable: body.scrollHeight > body.clientHeight,
              };
            }
            """);
        Assert.Equal("hidden", state.BodyOverflow);
        Assert.Equal("fixed", state.SidePosition);
        Assert.True(Math.Abs(state.SideHeight - state.Viewport) <= 1);
        Assert.Equal("contain", state.NavOverscroll);

        // Прокрутка колесом над меню прокручивает меню, а не страницу.
        var box = (await nav.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
        for (var i = 0; i < 5; i++)
        {
            await page.Mouse.WheelAsync(0, 600);
        }

        await page.WaitForTimeoutAsync(200);
        Assert.Equal(before, await page.EvaluateAsync<double>("() => window.scrollY"));
        if (state.NavScrollable)
        {
            Assert.True(await nav.EvaluateAsync<double>("el => el.scrollTop") > 0);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).First.ClickAsync();
        await Assertions.Expect(nav).ToBeHiddenAsync();
        Assert.NotEqual("hidden", await page.EvaluateAsync<string>("() => getComputedStyle(document.body).overflow"));
    });

    /// <summary>Состояние меню из браузера (Playwright заполняет свойства, нужен конструктор без параметров).</summary>
    private sealed class MenuState
    {
        public string BodyOverflow { get; set; } = string.Empty;
        public string SidePosition { get; set; } = string.Empty;
        public double SideHeight { get; set; }
        public double Viewport { get; set; }
        public string NavOverscroll { get; set; } = string.Empty;
        public bool NavScrollable { get; set; }
    }

    /// <summary>Покупатель через форму списка и переход в его карточку по ссылке из списка.</summary>
    private static async Task OpenNewCustomerCardAsync(IPage page, string name)
    {
        await page.GotoAsync("counterparties?new=1");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(300);
        await page.GetByLabel("Наименование").FillAsync(name);
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Поставщик" }).UncheckAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Покупатель" }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = name })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/counterparties/\d+$"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
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
