using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D315: штрихкоды EAN-13 и артикулы автоматически — в карточке, массово и при создании; занятые пропускаются.</summary>
public sealed class CatAutoCodesTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 939_000_000;

    [SqlFact]
    public async Task Barcodes_and_articles_are_generated_skipping_taken_ones()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка", ItemType.Finished, piece, null));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null));
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));

        // Кнопка в карточке без настроек: префикс 20, номер 1. Следующий номер занят вручную — пропускается.
        Assert.Equal("2000000000015", await s.Codes.GenerateBarcodeAsync(hat));
        await s.Nomenclature.AddBarcodeAsync(scarf, "2000000000022");
        Assert.Equal("2000000000039", await s.Codes.GenerateBarcodeAsync(hat));

        // Массово: только позиции без штрихкода, кроме услуг.
        var more = await s.Catalog.CreateItemAsync(new ItemCommand("В-1", "Варежки", ItemType.Finished, piece, null));
        var batch = await s.Codes.GenerateMissingBarcodesAsync();
        Assert.Equal((1, "2000000000046"), (batch.Created, batch.First));
        Assert.Empty((await s.Nomenclature.GetItemAsync(service)).Barcodes);
        Assert.Equal(0, (await s.Codes.GenerateMissingBarcodesAsync()).Created);
        Assert.Single((await s.Nomenclature.GetItemAsync(more)).Barcodes);

        // При создании: EAN-13 и артикул по шаблону; занятый артикул пропускается; услуге штрихкод не выдаётся.
        await s.CatalogSettings.SaveCodesAsync(new CatalogCodesCommand(true, 25, 1, true, "KN-{N}", 4, 1), (await s.CatalogSettings.GetAsync()).RowVersion);
        var card = await s.Nomenclature.GetItemAsync(scarf);
        await s.Nomenclature.SaveItemDetailsAsync(scarf, card.Details with { Article = "KN-0001" }, new Dictionary<long, string?>(), card.RowVersion);
        var gloves = await s.Catalog.CreateItemAsync(new ItemCommand("П-1", "Перчатки", ItemType.Finished, piece, null));
        var created = await s.Nomenclature.GetItemAsync(gloves);
        Assert.Equal(("KN-0002", "2500000000010"), (created.Details.Article, created.Barcodes.Single().Code));
        var service2 = await s.Catalog.CreateItemAsync(new ItemCommand("УС-2", "Монтаж", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
        Assert.Empty((await s.Nomenclature.GetItemAsync(service2)).Barcodes);
        Assert.Equal("KN-0003", (await s.Nomenclature.GetItemAsync(service2)).Details.Article);

        // Ошибочный шаблон не меняет настройки.
        var rv = (await s.CatalogSettings.GetAsync()).RowVersion;
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.CatalogSettings.SaveCodesAsync(new CatalogCodesCommand(true, 25, 1, true, "KN-", 4, 1), rv));
        Assert.Equal("KN-{N}", (await s.CatalogSettings.GetAsync()).Codes!.ArticleTemplate);

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.CountAsync(e => e.OrganizationId == org.OrganizationId && e.Reason!.StartsWith("Штрихкод")) >= 3);
    }

    [SqlFact]
    public async Task Auto_codes_survive_other_tabs_and_parallel_creates()
    {
        // Ревью: вкладка A держит в своём контексте строку настроек, вкладка B сдвигает счётчики — A всё равно создаёт позицию
        // (без ошибки версии), а параллельные создания не берут один номер.
        var org = await CreateOrgAsync();
        long piece;
        await using (var setup = host.As(org.OwnerUserId, org.OrganizationId))
        {
            piece = (await setup.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            await setup.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, "ТВ-", 4, 1, ItemType.Finished), (await setup.CatalogSettings.GetAsync()).RowVersion);
            await setup.CatalogSettings.SaveCodesAsync(new CatalogCodesCommand(true, 25, 1, true, "KN-{N}", 4, 1), (await setup.CatalogSettings.GetAsync()).RowVersion);
        }

        await using var a = host.As(org.OwnerUserId, org.OrganizationId);
        await using var b = host.As(org.OwnerUserId, org.OrganizationId);
        var first = await a.Catalog.CreateItemAsync(new ItemCommand("", "Шапка", ItemType.Finished, piece, null));
        var second = await b.Catalog.CreateItemAsync(new ItemCommand("", "Шарф", ItemType.Finished, piece, null));
        var third = await a.Catalog.CreateItemAsync(new ItemCommand("", "Варежки", ItemType.Finished, piece, null));
        Assert.Equal(["ТВ-0001", "ТВ-0002", "ТВ-0003"],
            [(await a.Nomenclature.GetItemAsync(first)).Code, (await a.Nomenclature.GetItemAsync(second)).Code, (await a.Nomenclature.GetItemAsync(third)).Code]);
        // Настройки, прочитанные заново, сохраняются из вкладки A без ложного конфликта.
        await a.CatalogSettings.SaveCodesAsync(new CatalogCodesCommand(true, 25, 10, true, "KN-{N}", 4, 10), (await a.CatalogSettings.GetAsync()).RowVersion);

        // Параллельно из четырёх вкладок по 3 позиции — коды, штрихкоды и артикулы не повторяются.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async t =>
        {
            await using var tab = host.As(org.OwnerUserId, org.OrganizationId);
            for (var i = 0; i < 3; i++)
            {
                await tab.Catalog.CreateItemAsync(new ItemCommand("", $"Позиция {t}-{i}", ItemType.Finished, piece, null));
            }
        }));
        await using var db = host.NewDb();
        var items = await db.Items.Where(i => i.OrganizationId == org.OrganizationId).Select(i => new { i.Code, i.Article }).ToListAsync();
        Assert.Equal(15, items.Select(i => i.Code).Distinct().Count());
        Assert.Equal(15, items.Select(i => i.Article).Distinct().Count());
        Assert.Equal(15, await db.ItemBarcodes.Where(x => x.OrganizationId == org.OrganizationId).Select(x => x.Code).Distinct().CountAsync());
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
