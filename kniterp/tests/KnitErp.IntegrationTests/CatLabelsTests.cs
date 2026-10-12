using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D316: шаблоны этикеток (образец, изменение, основной, архив) и данные для печати; права и изоляция организаций.</summary>
public sealed class CatLabelsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 940_000_000;

    [SqlFact]
    public async Task Templates_and_print_data_with_price_visibility_and_isolation()
    {
        var org = await CreateOrgAsync();
        long accountant;
        long hat, sweater, red;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            var group = await s.Nomenclature.CreateGroupAsync(null, "Шапки");
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null, null, group));
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null));
            await s.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [price] = 1490 });
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 2500 });
            await s.Codes.GenerateBarcodeAsync(hat);
            var color = await s.Modifications.CreateCharacteristicAsync("Цвет");
            await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [color] = "красный" });
            red = (await s.Modifications.VariantsAsync(sweater)).Modifications.Single().Id;

            // Без шаблонов печатается первый образец.
            var fallback = await s.Labels.PrintAsync([hat], null);
            Assert.Equal((0L, LabelTemplate.Presets[0].Name), (fallback.Template.Id, fallback.Template.Name));

            var first = await s.Labels.CreateFromPresetAsync("a4-70x37");
            var second = await s.Labels.CreateFromPresetAsync("price-58x40");
            var list = await s.Labels.TemplatesAsync();
            Assert.Equal(first, list.Single(t => t.IsDefault).Id); // первый — основной

            await s.Labels.SetDefaultAsync(second);
            Assert.Equal(second, (await s.Labels.TemplatesAsync()).Single(t => t.IsDefault).Id);
            var t2 = (await s.Labels.TemplatesAsync()).Single(t => t.Id == second);
            await s.Labels.UpdateAsync(second, new LabelTemplateCommand("Мой ценник", 50, 30, LabelSheet.Roll, 1, 1, 1.5m, null,
                [new LabelElement(LabelField.Name, null, 9, true, LabelAlign.Left, false, 0), new LabelElement(LabelField.Price, "Цена: ", 14, true, LabelAlign.Center, false, 0),
                 new LabelElement(LabelField.Characteristics, null, 7, false, LabelAlign.Left, false, 0), new LabelElement(LabelField.Barcode, null, 7, false, LabelAlign.Center, false, 10)]),
                t2.RowVersion);
            Assert.Equal("catalog.archived", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            {
                var a = (await s.Labels.TemplatesAsync()).Single(t => t.Id == first);
                await s.Labels.SetArchivedAsync(first, true, a.RowVersion);
                await s.Labels.SetDefaultAsync(first);
            })).Code);

            // Данные: в порядке ids, основной шаблон, цена, EAN-13, характеристики модификации, группа.
            var data = await s.Labels.PrintAsync([red, hat], null);
            Assert.Equal(("Мой ценник", 50m), (data.Template.Name, data.Template.WidthMm));
            Assert.Equal([red, hat], data.Items.Select(i => i.Id));
            var h = data.Items[1];
            Assert.Equal((1490m, "2000000000015", "Шапки", "₽"), (h.Price, h.Barcode, h.Group, data.Currency));
            Assert.Equal(("Цвет: красный", 2500m, "СВ-1-"), (data.Items[0].Characteristics, data.Items[0].Price, data.Items[0].Barcode![..5]));
            Assert.Equal("Шапка", (await s.Labels.SampleAsync("ш-1", null)).Name);

            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        // Бухгалтер печатает, но шаблоны не меняет.
        await using (var s = host.As(accountant, org.OrganizationId))
        {
            Assert.Single((await s.Labels.PrintAsync([hat], null)).Items);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Labels.CreateFromPresetAsync("small-30x20"));
        }

        // Чужая организация: позиции и шаблоны не видны.
        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Labels.PrintAsync([hat], null));
            Assert.Empty(await s.Labels.TemplatesAsync(true));
        }
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
