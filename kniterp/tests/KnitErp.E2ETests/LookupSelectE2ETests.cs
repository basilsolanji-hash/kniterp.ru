using KnitErp.IntegrationTests;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// D815 (сессия «Интерфейс», раздел 0 п. 5): своего значения нет в списке — «+ Добавить» прямо в форме; новое значение создаётся
/// в справочнике и сразу выбрано. Владелец — из <see cref="HrAppFixture"/>, данные вымышленные.
/// </summary>
public sealed class LookupSelectE2ETests(HrAppFixture app) : IClassFixture<HrAppFixture>
{
    private static readonly string Stamp = DateTime.UtcNow.Ticks.ToString()[^6..];

    [SqlFact]
    public async Task New_position_is_added_from_the_hire_form_and_selected()
    {
        var page = await app.OwnerPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync("employees");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
            await page.GetByRole(AriaRole.Button, new() { Name = "Принять сотрудника" }).ClickAsync();

            var position = page.Locator(".lookup-select").Filter(new() { Has = page.GetByLabel("Должность", new() { Exact = true }) });
            await position.GetByRole(AriaRole.Button, new() { Name = "+ Добавить" }).ClickAsync();
            var name = $"Петельщица {Stamp}";
            await position.GetByLabel("Новое значение: Должность").FillAsync(name);
            await position.GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();

            var select = page.GetByLabel("Должность", new() { Exact = true });
            await Assertions.Expect(select.Locator("option:checked")).ToHaveTextAsync(name);
            await Assertions.Expect(position.GetByRole(AriaRole.Button, new() { Name = "+ Добавить" })).ToBeVisibleAsync();

            // Повтор того же названия не создаёт дубль — выбирается существующее.
            await position.GetByRole(AriaRole.Button, new() { Name = "+ Добавить" }).ClickAsync();
            await position.GetByLabel("Новое значение: Должность").FillAsync(name);
            await position.GetByRole(AriaRole.Button, new() { Name = "Добавить", Exact = true }).ClickAsync();
            await Assertions.Expect(select.Locator("option", new() { HasText = name })).ToHaveCountAsync(1);
            Assert.Empty(errors);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
