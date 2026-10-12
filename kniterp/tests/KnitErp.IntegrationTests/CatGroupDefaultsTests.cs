using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.IntegrationTests;

/// <summary>D317: значения по умолчанию группы — наследование от родителя, подстановка при создании вручную и из Excel, явное не меняется.</summary>
public sealed class CatGroupDefaultsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 941_000_000;

    [SqlFact]
    public async Task New_items_inherit_group_defaults_from_nearest_group()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var rates = await s.VatRates.ActiveAsync();
        var vat = rates.First(r => r.Kind == VatRateKind.Standard).Id;
        var exempt = rates.First(r => r.Kind == VatRateKind.Exempt).Id;
        var clothes = await s.Nomenclature.CreateGroupAsync(null, "Одежда");
        var hats = await s.Nomenclature.CreateGroupAsync(clothes, "Шапки");

        await s.GroupDefaults.SaveAsync(clothes, new GroupDefaultValues(ItemType.Finished, piece, vat, "6110", "643", "Россия", TrackingType.Clothes), []);
        await s.GroupDefaults.SaveAsync(hats, new GroupDefaultValues(null, null, null, "6505009000", null, null, null), []);
        var view = await s.GroupDefaults.GetAsync(hats);
        Assert.Equal(("6505009000", vat, "643", TrackingType.Clothes), (view.Effective.TnVedCode, view.Effective.VatRateId, view.Effective.OriginCountryCode,
            view.Effective.Tracking));
        Assert.Null(view.Own.VatRateId);

        // Вручную: НДС, ТН ВЭД подгруппы, страна и маркировка родителя.
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null, null, hats));
        var card = await s.Nomenclature.GetItemAsync(hat);
        var trade = (await s.Trade.GetAsync(hat)).Trade;
        Assert.Equal((vat, "6505009000", "643", true, TrackingType.Clothes), (card.VatRateId, card.Details.TnVedCode, card.Details.OriginCountryCode,
            trade.IsMarked, trade.TrackingType));

        // Явно выбранная ставка не меняется.
        var exemptHat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-2", "Шапка без НДС", ItemType.Finished, piece, null, exempt, hats));
        Assert.Equal(exempt, (await s.Nomenclature.GetItemAsync(exemptHat)).VatRateId);

        // Из Excel: позиция в группе получает значения, если их нет в файле.
        IReadOnlyList<string> header = [.. ItemExchangeService.Columns, ItemExchangeService.GroupColumn];
        var plan = await s.ItemExchange.PreviewAsync(new MemoryStream(SqlTestHost.Spreadsheet.Write(
            [new SheetData("Номенклатура", header, [["Ш-3", "Шапка из файла", "Готовая продукция", "шт", "", "Одежда / Шапки"]])])));
        Assert.Equal(0, plan.ErrorRows);
        await s.ItemExchange.ApplyAsync(plan.Rows, plan.Bundles);
        var imported = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "Ш-3"))).Items.Single();
        var importedCard = await s.Nomenclature.GetItemAsync(imported.Id);
        Assert.Equal((vat, "6505009000", true), (importedCard.VatRateId, importedCard.Details.TnVedCode, (await s.Trade.GetAsync(imported.Id)).Trade.IsMarked));

        // Неверный ТН ВЭД — отказ, значения группы не меняются.
        var rv = (await s.GroupDefaults.GetAsync(hats)).RowVersion;
        Assert.Equal("catalog.group_defaults.tnved", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.GroupDefaults.SaveAsync(hats, new GroupDefaultValues(null, null, null, "12345", null, null, null), rv))).Code);
        Assert.Equal("6505009000", (await s.GroupDefaults.GetAsync(hats)).Own.TnVedCode);

        // Чужая группа — не найдена.
        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.GroupDefaults.GetAsync(hats));
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
