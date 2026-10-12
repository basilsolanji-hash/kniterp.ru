using System.Xml.Linq;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Фид каталога YML (D314): ключ показывается один раз и хранится отпечатком; в фиде — позиции с ценой выбранного вида, наличие
/// (остаток по складу), модификации с group_id и характеристиками, комплект по компонентам; услуги и позиции без цены — нет;
/// выключенный и чужой ключ — нет файла; управляют только Владелец и Администратор.
/// </summary>
public sealed class CatFeedTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static int _innSeed = 938_000_000;

    [SqlFact]
    public async Task Yml_feed_contains_priced_goods_variants_bundles_with_availability()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var group = await s.Nomenclature.CreateGroupAsync(null, "Шапки & шарфы");
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-1", "Шапка <вязаная>", ItemType.Finished, piece, null, vat, group));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null, vat));
        var noPrice = await s.Catalog.CreateItemAsync(new ItemCommand("БЦ-1", "Без цены", ItemType.Finished, piece, null, vat));
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null, vat));
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, vat));
        foreach (var (id, p) in new[] { (hat, 1500m), (scarf, 900m), (service, 300m), (bundle, 2200m), (sweater, 2500m) })
        {
            await s.Nomenclature.SetPricesAsync(id, new Dictionary<long, decimal?> { [price] = p });
        }

        await s.Bundles.SetComponentAsync(bundle, hat, 1);
        await s.Bundles.SetComponentAsync(bundle, scarf, 2);
        var color = await s.Modifications.CreateCharacteristicAsync("Цвет");
        await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [color] = "красный, синий" });

        // Остатки: шапок 5, шарфов 4 → комплектов 2.
        await using (var db = host.NewDb())
        {
            var reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
            var doc = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(doc, hat, 5, (await s.Documents.GetAsync(doc)).RowVersion);
            await s.Documents.SetLineAsync(doc, scarf, 4, (await s.Documents.GetAsync(doc)).RowVersion);
            await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
        }

        var issued = await s.Feeds.CreateAsync(new CatalogFeedCommand("Яндекс Маркет", price, null, null, false));
        Assert.StartsWith(CatalogFeed.KeyPrefix, issued.Key);
        await using (var db = host.NewDb())
        {
            var stored = await db.CatalogFeeds.SingleAsync(f => f.Id == issued.Id);
            Assert.Equal((issued.Key[..CatalogFeed.PrefixLength], 32), (stored.Prefix, stored.KeyHash.Length));
            Assert.DoesNotContain(issued.Key, System.Text.Encoding.UTF8.GetString(stored.KeyHash));
        }

        var file = (await s.Feeds.BuildAsync(issued.Key, "https://shop.example/"))!;
        var yml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(file.Xml));
        var offers = yml.Descendants("offer").ToDictionary(o => long.Parse(o.Attribute("id")!.Value));
        Assert.Equal("RUR", yml.Descendants("currency").Single().Attribute("id")!.Value);
        Assert.Equal("Шапки & шарфы", yml.Descendants("category").Single().Value);

        Assert.Equal(("Шапка <вязаная>", "1500", "true", "5", group.ToString()), (offers[hat].Element("name")!.Value, offers[hat].Element("price")!.Value,
            offers[hat].Attribute("available")!.Value, offers[hat].Element("count")!.Value, offers[hat].Element("categoryId")!.Value));
        Assert.Equal("2", offers[bundle].Element("count")!.Value);
        Assert.DoesNotContain(service, offers.Keys);
        Assert.DoesNotContain(noPrice, offers.Keys);
        Assert.DoesNotContain(sweater, offers.Keys); // продаются модификации
        var variants = offers.Values.Where(o => o.Attribute("group_id")?.Value == sweater.ToString()).ToList();
        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.Equal(("false", "Цвет"), (v.Attribute("available")!.Value, v.Element("param")!.Attribute("name")!.Value)));

        // «Только в наличии» и склад без остатков.
        var empty = await s.Warehouses.CreateWarehouseAsync("Пустой", null);
        var onlyHere = await s.Feeds.CreateAsync(new CatalogFeedCommand("Сайт", price, empty, null, true));
        Assert.Equal(0, (await s.Feeds.BuildAsync(onlyHere.Key, "https://shop.example/"))!.Offers);
        var inStock = await s.Feeds.CreateAsync(new CatalogFeedCommand("Сайт 2", price, null, group, true));
        Assert.Equal([hat], XDocument.Parse(System.Text.Encoding.UTF8.GetString((await s.Feeds.BuildAsync(inStock.Key, "x"))!.Xml))
            .Descendants("offer").Select(o => long.Parse(o.Attribute("id")!.Value)));

        // Выключенный фид и неизвестный ключ — нет файла.
        var feed = (await s.Feeds.ListAsync()).Single(f => f.Id == issued.Id);
        await s.Feeds.SetActiveAsync(feed.Id, false, feed.RowVersion);
        Assert.Null(await s.Feeds.BuildAsync(issued.Key, "x"));
        Assert.Null(await s.Feeds.BuildAsync(CatalogFeed.KeyPrefix + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "x"));
        Assert.Null(await s.Feeds.BuildAsync("../etc/passwd", "x"));
    }

    [SqlFact]
    public async Task Feed_offers_carry_photo_links_served_only_for_active_feed_of_same_organization()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46];
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-Ф", "Свитер", ItemType.Finished, piece, null, vat));
        var hat = await s.Catalog.CreateItemAsync(new ItemCommand("Ш-Ф", "Шапка\u000Bзимняя", ItemType.Finished, piece, null, vat));
        await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 2500 });
        await s.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [price] = 900 });
        var main = await s.Modifications.AddPhotoAsync(sweater, png);
        var back = await s.Modifications.AddPhotoAsync(sweater, jpeg);
        var color = await s.Modifications.CreateCharacteristicAsync("Цвет");
        await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [color] = "красный" });

        var issued = await s.Feeds.CreateAsync(new CatalogFeedCommand("Маркет", price, null, null, false));
        var yml = XDocument.Parse(System.Text.Encoding.UTF8.GetString((await s.Feeds.BuildAsync(issued.Key, "https://shop.example/"))!.Xml));
        var offers = yml.Descendants("offer").ToList();
        var variant = offers.Single(o => o.Attribute("group_id")?.Value == sweater.ToString());
        // Модификация без своих фото — фото основной позиции, по порядку; позиция без фото — без picture.
        Assert.Equal([$"https://shop.example/feeds/photos/{issued.Id}.{main}", $"https://shop.example/feeds/photos/{issued.Id}.{back}"],
            variant.Elements("picture").Select(p => p.Value));
        Assert.Empty(offers.Single(o => o.Attribute("id")!.Value == hat.ToString()).Elements("picture"));
        // Символ, недопустимый в XML (вертикальная табуляция из Word), выброшен, а не ломает весь фид.
        Assert.Equal("Шапказимняя", offers.Single(o => o.Attribute("id")!.Value == hat.ToString()).Element("name")!.Value);
        // picture — после name, как требует схема YML.
        Assert.Equal("name", ((XElement)variant.Element("picture")!.PreviousNode!).Name.LocalName);

        var served = (await s.Feeds.PhotoAsync($"{issued.Id}.{main}"))!.Value;
        Assert.Equal("image/png", served.ContentType);
        Assert.Equal(png, served.Content);
        Assert.Null(await s.Feeds.PhotoAsync("мусор"));
        Assert.Null(await s.Feeds.PhotoAsync(null));

        // Чужое фото по «своему» фиду (подделанный токен) — нет.
        var other = await CreateOrgAsync();
        long foreignPhoto;
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            var u = (await o.Catalog.ListUnitsAsync()).Single(x => x.Code == "796").Id;
            var v = (await o.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
            foreignPhoto = await o.Modifications.AddPhotoAsync(await o.Catalog.CreateItemAsync(new ItemCommand("Ч-1", "Чужое", ItemType.Finished, u, null, v)), png);
        }

        Assert.Null(await s.Feeds.PhotoAsync($"{issued.Id}.{foreignPhoto}"));

        // Выключенный фид — фото больше не отдаются.
        var feed = (await s.Feeds.ListAsync()).Single(f => f.Id == issued.Id);
        await s.Feeds.SetActiveAsync(feed.Id, false, feed.RowVersion);
        Assert.Null(await s.Feeds.PhotoAsync($"{issued.Id}.{main}"));
        Assert.False(await s.Feeds.IsLiveAsync(issued.Key));
        // Отметка запроса сменила rowversion — включение со старой версией всё равно проходит (явное значение, не конфликт).
        await s.Feeds.SetActiveAsync(feed.Id, true, feed.RowVersion);
        Assert.True(await s.Feeds.IsLiveAsync(issued.Key));
    }

    [SqlFact]
    public async Task Only_owner_or_admin_manage_feeds_and_other_organization_objects_are_rejected()
    {
        var org = await CreateOrgAsync();
        long accountant;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Feeds.ListAsync());
        }

        var other = await CreateOrgAsync();
        long foreignPrice;
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            foreignPrice = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Feeds.CreateAsync(new CatalogFeedCommand("Чужая цена", foreignPrice, null, null, false)));
            Assert.Empty(await s.Feeds.ListAsync());
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
