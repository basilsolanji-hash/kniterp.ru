using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D322, D323: автоцены от закупки и прайс-лист поставщика.</summary>
public sealed class CatPriceAutomationTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 945_000_000;

    [SqlFact]
    public async Task Price_rule_follows_purchase_price_and_supplier_price_list_updates_it()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var vatRate = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard);
        var retail = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null, vatRate.Id));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null, vatRate.Id));
        await s.Nomenclature.AddBarcodeAsync(scarf, "4600000000008");
        await s.Suppliers.SetAsync(hat, supplier, "P-100", null, true);
        var card = await s.Nomenclature.GetItemAsync(hat);
        await s.Nomenclature.SaveItemDetailsAsync(hat, card.Details with { PurchasePrice = 500 }, new Dictionary<long, string?>(), card.RowVersion);

        // Правило: розница = закупка × 2,2, окончание на 99 — сразу пересчёт.
        var rule = (await s.PriceRules.ListAsync()).Single(r => r.PriceTypeId == retail);
        Assert.Equal(1, await s.PriceRules.SaveAsync(retail, 2.2m, PriceRounding.NinetyNine, true, rule.RowVersion));
        Assert.Equal(1099m, (await s.Nomenclature.GetItemAsync(hat)).Prices.Single(p => p.IsDefault).Price); // 500 × 2,2 = 1100 → 1099

        // Новая закупочная в карточке — цена по правилу сама.
        card = await s.Nomenclature.GetItemAsync(hat);
        await s.Nomenclature.SaveItemDetailsAsync(hat, card.Details with { PurchasePrice = 850 }, new Dictionary<long, string?>(), card.RowVersion);
        Assert.Equal(1899m, (await s.Nomenclature.GetItemAsync(hat)).Prices.Single(p => p.IsDefault).Price);
        Assert.Equal(PriceChangeSource.Rule, (await s.Trade.GetAsync(hat)).History.First().Source);

        // Прайс-лист с шапкой поставщика: шапка — по артикулу, шарф — по штрихкоду; цены с НДС.
        var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
        [
            new SheetData("Прайс", ["ООО «Пряжа», прайс на октябрь", "", "", ""],
            [
                ["", "", "", ""],
                ["Артикул", "Наименование", "Штрихкод", "Цена, руб."],
                ["P-100", "Шапка вязаная", "", "1 220,00"],
                ["S-7", "Шарф", "4600000000008", "610"],
                ["Z-1", "Неизвестный товар", "", "100"],
                ["P-100", "Шапка (повтор)", "", "1300"],
                ["X-2", "Брак цены", "", "дорого"],
            ]),
        ]));
        var plan = await s.PriceLists.PreviewAsync(supplier, file, pricesIncludeVat: true);
        Assert.Equal((2, 1, 2), (plan.ToUpdate, plan.NotFound, plan.Errors));
        var percent = vatRate.CurrentPercent ?? 0;
        var hatRow = plan.Rows.Single(r => r.ItemId == hat && r.Action == PriceListAction.Update);
        Assert.Equal(("артикул поставщика", decimal.Round(1220 / (1 + percent / 100m), 2, MidpointRounding.AwayFromZero)), (hatRow.MatchedBy, hatRow.NewPurchasePrice));
        Assert.Equal("штрихкод", plan.Rows.Single(r => r.ItemId == scarf).MatchedBy);

        var result = await s.PriceLists.ApplyAsync(plan);
        Assert.Equal((2, 1), (result.Updated, result.Linked)); // артикул S-7 запомнен у шарфа
        Assert.Equal(2, result.AutoPrices);
        Assert.Equal("S-7", (await s.Suppliers.ListAsync(scarf)).Single().SupplierArticle);

        // Заголовка нет — понятная ошибка.
        var bad = new MemoryStream(SqlTestHost.Spreadsheet.Write([new SheetData("Л", ["что-то", "ещё"], [["1", "2"]])]));
        Assert.Equal("catalog.price_list.header", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.PriceLists.PreviewAsync(supplier, bad, false))).Code);

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(e => e.OrganizationId == org.OrganizationId && e.EntityType == "SupplierPriceList"));
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
