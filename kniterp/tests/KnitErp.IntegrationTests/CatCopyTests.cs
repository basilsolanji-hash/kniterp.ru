using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.IntegrationTests;

/// <summary>D319, D320: «Создать по образцу» и подсказка о похожих позициях.</summary>
public sealed class CatCopyTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 943_000_000;

    [SqlFact]
    public async Task Copy_takes_card_prices_packs_suppliers_and_bundle_but_not_unique_codes()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var box = await s.Catalog.CreateUnitAsync(new UnitCommand("BX1", "Коробка", "кор", 0));
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        var group = await s.Nomenclature.CreateGroupAsync(null, "Шапки");
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));

        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, "Шерсть", vat, group));
        var card = await s.Nomenclature.GetItemAsync(hat);
        await s.Nomenclature.SaveItemDetailsAsync(hat, card.Details with { Article = "HAT-1", TnVedCode = "6505009000", WeightKg = 0.2m },
            new Dictionary<long, string?>(), card.RowVersion);
        await s.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [price] = 1490 });
        await s.Nomenclature.AddBarcodeAsync(hat, "4600000000008");
        await s.Packs.SetAsync(hat, box, 12, "4600000000015");
        await s.Suppliers.SetAsync(hat, supplier, "M-1", null, true);

        var copy = await s.Copies.CopyAsync(hat, "Ш-2", null);
        var c = await s.Nomenclature.GetItemAsync(copy);
        Assert.Equal(("Ш-2", "Шапка (копия)", "Шерсть", vat, group), (c.Code, c.Name, c.Description, c.VatRateId, c.Details.GroupId));
        Assert.Equal(("6505009000", 0.2m, null), (c.Details.TnVedCode, c.Details.WeightKg, c.Details.Article));
        Assert.Equal(1490m, c.Prices.Single(p => p.IsDefault).Price);
        Assert.Empty(c.Barcodes);
        var pack = (await s.Packs.ListAsync(copy)).Single();
        Assert.Equal((12m, null), (pack.Factor, pack.Barcode));
        Assert.Equal(("M-1", true), (await s.Suppliers.ListAsync(copy)).Single() is var sup ? (sup.SupplierArticle, sup.IsMain) : default);

        // Комплект копируется вместе с составом.
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null, vat));
        await s.Bundles.SetComponentAsync(bundle, hat, 2);
        var bundleCopy = await s.Copies.CopyAsync(bundle, "НБ-2", "Набор 2");
        Assert.Equal([("Ш-1", 2m)], (await s.Bundles.GetAsync(bundleCopy)).Components.Select(x => (x.Code, x.Quantity)));

        // Код занят — отказ, ничего не создано.
        Assert.Equal("catalog.item.code_taken", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Copies.CopyAsync(hat, "Ш-1", null))).Code);
        Assert.Equal(2, (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "Шапка"))).Total);

        // Похожие: то же наименование и наименование, содержащее введённое.
        Assert.Equal(["Ш-1", "Ш-2"], (await s.Catalog.SimilarAsync("шапка")).Select(i => i.Code).Order()); // от 5 знаков — и вхождение
        await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null));
        await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-2", "Шарф большой", ItemType.Finished, piece, null));
        Assert.Equal(["ШФ-1"], (await s.Catalog.SimilarAsync("шарф")).Select(i => i.Code)); // короткое — только точное
        Assert.Empty(await s.Catalog.SimilarAsync("Варежки"));
    }

    [SqlFact]
    public async Task Copy_skips_archived_price_types_and_prices_hidden_from_user()
    {
        // Ревью: архивный вид цены у образца и пользователь без права «Цены и суммы» больше не отменяют копию.
        var org = await CreateOrgAsync();
        long hat;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            var retail = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            var old = await s.Nomenclature.CreatePriceTypeAsync("Старая", true);
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null));
            await s.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [retail] = 900, [old] = 800 });
            var oldType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.Id == old);
            await s.Nomenclature.SetPriceTypeArchivedAsync(old, true, oldType.RowVersion);

            var copy = await s.Copies.CopyAsync(hat, "Ш-2", null);
            Assert.Equal([900m], (await s.Nomenclature.GetItemAsync(copy)).Prices.Where(p => p.Price is not null).Select(p => p.Price!.Value));
        }

        long keeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            keeper = (await s.Access.InviteAsync(new KnitErp.Application.Access.InviteUserCommand($"sk-{Guid.NewGuid():N}@test.local", "Кладовщик",
                KnitErp.Domain.Access.SystemRoles.SeniorStorekeeper, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Users, u => u.Id == keeper)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var k = host.As(keeper, org.OrganizationId))
        {
            var copy = await k.Copies.CopyAsync(hat, "Ш-3", null);
            Assert.True(copy > 0);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.All((await s.Nomenclature.GetItemAsync((await s.Catalog.ListItemsAsync(new ItemFilter(Search: "Ш-3"))).Items.Single().Id)).Prices,
                p => Assert.Null(p.Price)); // цен кладовщик не видел — и не скопировал
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
