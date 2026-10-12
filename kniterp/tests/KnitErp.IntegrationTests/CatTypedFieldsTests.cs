using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D328: характеристики с типом значений (число, да/нет, свой справочник) и новые типы доп. полей номенклатуры
/// (многострочный текст, ссылка, контрагент) — проверка на сервере, чужие данные не принимаются.
/// </summary>
public sealed class CatTypedFieldsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 951_000_000;

    [SqlFact]
    public async Task Typed_characteristics_normalize_and_reject_wrong_values()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var colors = await s.Nomenclature.CreateCatalogAsync("Цвета пряжи");
        await s.Nomenclature.AddEntryAsync(colors, "Красный", null);
        await s.Nomenclature.AddEntryAsync(colors, "Синий", null);
        var size = await s.Modifications.CreateCharacteristicAsync("Размер", CharacteristicType.Number, null);
        var hood = await s.Modifications.CreateCharacteristicAsync("С капюшоном", CharacteristicType.Flag, null);
        var color = await s.Modifications.CreateCharacteristicAsync("Цвет", CharacteristicType.Catalog, colors);
        Assert.Equal(["Красный", "Синий"], (await s.Modifications.CharacteristicsAsync()).Single(c => c.Id == color).Options);

        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null));
        var result = await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?>
        {
            [size] = "48, 48.0", // «48.0» — тот же размер 48 (запятая разделяет значения, дробь — через точку)
            [hood] = "да",
            [color] = "красный",
        });
        Assert.Equal(1, result.Created);
        var variant = (await s.Modifications.VariantsAsync(sweater)).Modifications.Single();
        Assert.Contains("Красный", variant.Name);
        Assert.Contains("Да", variant.Name);

        Assert.Equal("catalog.characteristic.number", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [size] = "XL" }))).Code);
        Assert.Equal("catalog.characteristic.flag", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [hood] = "может быть" }))).Code);
        Assert.Equal("catalog.characteristic.not_in_catalog", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [color] = "Зелёный" }))).Code);

        // Смена типа: текстовые значения «XL» не подходят числу — отказ со списком; подходящие («48») — можно.
        var letter = await s.Modifications.CreateCharacteristicAsync("Рост");
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null));
        await s.Modifications.CreateModificationsAsync(hat, new Dictionary<long, string?> { [letter] = "XL" });
        var dto = (await s.Modifications.CharacteristicsAsync()).Single(c => c.Id == letter);
        var mismatch = await Assert.ThrowsAsync<BusinessRuleException>(() => s.Modifications.SetCharacteristicTypeAsync(letter, CharacteristicType.Number, null, dto.RowVersion));
        Assert.Equal("catalog.characteristic.values_mismatch", mismatch.Code);
        Assert.Contains("XL", mismatch.Message);
        Assert.Equal(CharacteristicType.Text, (await s.Modifications.CharacteristicsAsync()).Single(c => c.Id == letter).Type);

        // Переименование записи справочника переносится в значения модификаций; правка модификации после этого не отклоняется.
        var red = (await s.Nomenclature.EntriesAsync(colors)).Single(e => e.Name == "Красный");
        await s.Nomenclature.UpdateEntryAsync(red.Id, "Бордовый", null, red.RowVersion);
        var renamed = (await s.Modifications.VariantsAsync(sweater)).Modifications.Single();
        Assert.Equal("Бордовый", renamed.Values.Single(v => v.CharacteristicId == color).Value);
        var mod = await s.Nomenclature.GetItemAsync(renamed.Id);
        await s.Modifications.SetValuesAsync(renamed.Id, renamed.Values.ToDictionary(v => v.CharacteristicId, v => (string?)v.Value) is var vals
            ? new Dictionary<long, string?>(vals) { [size] = "50" } : null!, mod.RowVersion);
        // Справочник, подключённый к характеристике, в архив не уходит.
        var cat = (await s.Nomenclature.CatalogsAsync()).Single(c => c.Id == colors);
        Assert.Equal("catalog.user.used_by_characteristic", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SetCatalogArchivedAsync(colors, true, cat.RowVersion))).Code);

        // Чужой справочник — не найден.
        var other = await CreateOrgAsync();
        long foreignCatalog;
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            foreignCatalog = await o.Nomenclature.CreateCatalogAsync("Чужие цвета");
        }

        await Assert.ThrowsAsync<NotFoundException>(() => s.Modifications.CreateCharacteristicAsync("Оттенок", CharacteristicType.Catalog, foreignCatalog));
        Assert.Equal("catalog.characteristic.catalog", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Modifications.CreateCharacteristicAsync("Оттенок", CharacteristicType.Catalog, null))).Code);
    }

    [SqlFact]
    public async Task New_custom_field_types_validate_links_counterparties_and_multiline_text()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var care = await s.Nomenclature.CreateItemFieldAsync("Уход", CustomFieldType.LongText);
        var page = await s.Nomenclature.CreateItemFieldAsync("Страница у поставщика", CustomFieldType.Link);
        var maker = await s.Nomenclature.CreateItemFieldAsync("Производитель", CustomFieldType.Counterparty);
        var factory = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Фабрика»", null, null, true, false, null));
        var item = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null));

        async Task SaveAsync(Dictionary<long, string?> values)
        {
            var card = await s.Nomenclature.GetItemAsync(item);
            await s.Nomenclature.SaveItemDetailsAsync(item, card.Details, values, card.RowVersion);
        }

        await SaveAsync(new() { [care] = "Стирка 30°\nНе отбеливать", [page] = "https://shop.example/hat?id=1", [maker] = factory.ToString() });
        var fields = (await s.Nomenclature.GetItemAsync(item)).CustomFields.ToDictionary(f => f.FieldId);
        Assert.Equal("Стирка 30°\nНе отбеливать", fields[care].Value);
        Assert.Equal("https://shop.example/hat?id=1", fields[page].Value);
        Assert.Equal((factory.ToString(), "ООО «Фабрика»"), (fields[maker].Value, fields[maker].Display));

        // Ссылка — только http(s); контрагент — только своей организации.
        Assert.Equal("custom_field.link", (await Assert.ThrowsAsync<BusinessRuleException>(() => SaveAsync(new() { [page] = "javascript:alert(1)" }))).Code);
        Assert.Equal("custom_field.link", (await Assert.ThrowsAsync<BusinessRuleException>(() => SaveAsync(new() { [page] = "ftp://shop.example/x" }))).Code);
        var other = await CreateOrgAsync();
        long foreign;
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            foreign = await o.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Чужая»", null, null, true, false, null));
        }

        await Assert.ThrowsAsync<NotFoundException>(() => SaveAsync(new() { [maker] = foreign.ToString() }));
        Assert.Equal(factory.ToString(), (await s.Nomenclature.GetItemAsync(item)).CustomFields.Single(f => f.FieldId == maker).Value);

        // У заказа покупателя новых типов пока нет.
        Assert.Equal("custom_field.type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.SalesSettings.CreateFieldAsync("Ссылка на макет", CustomFieldType.Link))).Code);
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
