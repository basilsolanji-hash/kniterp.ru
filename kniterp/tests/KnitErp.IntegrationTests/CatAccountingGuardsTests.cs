using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D339 (аудит модуля «Товары»): позиция в учёте не меняет единицу и код (сервис, загрузка Excel и триггер базы), точность единицы
/// не уменьшается при записанных количествах, модификация — единица и НДС как у основной, архив — без остатка, модификаций и комплектов,
/// цены архивной позиции не меняются.
/// </summary>
public sealed class CatAccountingGuardsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 953_000_000;

    [SqlFact]
    public async Task Item_in_accounting_keeps_unit_and_code_unit_precision_and_archive_are_guarded()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var kg = await s.Catalog.CreateUnitAsync(new UnitCommand("KGX", "Килограмм тест", "кгт", 3));
        var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, kg, null));
        var fresh = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-2", "Пряжа новая", ItemType.RawMaterial, kg, null));
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        await ReceiveAsync(s, org.OrganizationId, store, yarn, 2.5m);

        // Единица и код позиции в учёте не меняются; наименование — можно.
        var card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-1"))).Items.Single();
        Assert.Equal("catalog.unit.in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, piece, null), card.RowVersion))).Code);
        Assert.Equal("catalog.code.in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-9", "Пряжа", ItemType.RawMaterial, kg, null), card.RowVersion))).Code);
        await s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-1", "Пряжа мериносовая", ItemType.RawMaterial, kg, null), card.RowVersion);

        // Новая позиция без истории — единицу сменить можно.
        var freshCard = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-2"))).Items.Single();
        await s.Catalog.UpdateItemAsync(fresh, new ItemCommand("ПР-2", "Пряжа новая", ItemType.RawMaterial, piece, null), freshCard.RowVersion);

        // Триггер базы: прямой UPDATE в обход сервиса тоже не проходит.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [kniterp].[items] SET [UnitId] = {piece} WHERE [Id] = {yarn}"));
            Assert.Equal(51310, ex.Number);
        }

        // Точность единицы с записанными дробными количествами не уменьшается; увеличивается — можно.
        var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Id == kg);
        Assert.Equal("catalog.unit.precision_in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateUnitAsync(kg, new UnitCommand("KGX", "Килограмм тест", "кгт", 0), unit.RowVersion))).Code);
        await s.Catalog.UpdateUnitAsync(kg, new UnitCommand("KGX", "Килограмм тест", "кгт", 4), unit.RowVersion);

        // Архив: с остатком — нет.
        card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-1"))).Items.Single();
        Assert.Equal("catalog.archive.has_stock", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Catalog.ArchiveItemAsync(yarn, card.RowVersion))).Code);

        // Компонент действующего комплекта — нет.
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null,
            (await s.VatRates.ActiveAsync()).First().Id));
        await s.Bundles.SetComponentAsync(bundle, fresh, 2);
        freshCard = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-2"))).Items.Single();
        Assert.Equal("catalog.archive.in_bundle", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Catalog.ArchiveItemAsync(fresh, freshCard.RowVersion))).Code);

        // Цены архивной позиции не меняются.
        var plain = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-3", "Пряжа старая", ItemType.RawMaterial, kg, null));
        await s.Catalog.ArchiveItemAsync(plain, (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-3"))).Items.Single().RowVersion);
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        Assert.Equal("catalog.archived", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SetPricesAsync(plain, new Dictionary<long, decimal?> { [price] = 100 }))).Code);

        // Загрузка Excel тоже не меняет единицу позиции в учёте.
        var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
            [new SheetData("Номенклатура", ItemExchangeService.Columns, [["ПР-1", "Пряжа мериносовая", ItemTypes.Name(ItemType.RawMaterial), "шт", ""]])]));
        var plan = await s.ItemExchange.PreviewAsync(file);
        Assert.Contains(plan.Rows.Single().Errors, e => e.Contains("уже в учёте"));
    }

    [SqlFact]
    public async Task Modification_follows_parent_unit_and_vat()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var pair = await s.Catalog.CreateUnitAsync(new UnitCommand("PRX", "Пара тест", "прт", 0));
        var rates = await s.VatRates.ActiveAsync();
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, rates[0].Id));
        var size = await s.Modifications.CreateCharacteristicAsync("Размер");
        await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [size] = "46, 48" });
        var variants = (await s.Modifications.VariantsAsync(sweater)).Modifications;

        // У модификации единицу не сменить.
        var v = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: variants[0].Code))).Items.Single();
        Assert.Equal("catalog.variant.unit_vat", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(v.Id, new ItemCommand(v.Code, v.Name, ItemType.Finished, pair, null, v.VatRateId), v.RowVersion))).Code);

        // У основной — меняется вместе с модификациями (истории нет).
        var parent = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-1"))).Items.Single(i => i.Id == sweater);
        var otherVat = rates.Count > 1 ? rates[1].Id : rates[0].Id;
        await s.Catalog.UpdateItemAsync(sweater, new ItemCommand("СВ-1", "Свитер", ItemType.Finished, pair, null, otherVat), parent.RowVersion);
        await using var db = host.NewDb();
        Assert.All(await db.Items.Where(i => i.ParentItemId == sweater).ToListAsync(), i => Assert.Equal((pair, otherVat), (i.UnitId, i.VatRateId)));
    }

    private static async Task ReceiveAsync(Services s, long orgId, long store, long item, decimal qty)
    {
        var reason = await s.Db.OperationReasons.Where(r => r.OrganizationId == orgId && r.Kind == StockOperationKind.Receipt).Select(r => (long?)r.Id).FirstAsync();
        var doc = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
        await s.Documents.SetLineAsync(doc, item, qty, (await s.Documents.GetAsync(doc)).RowVersion);
        await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
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
