using System.Diagnostics;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace KnitErp.IntegrationTests;

/// <summary>
/// ТЗ каталога, п. 7: список номенклатуры на 50 000 позиций открывается быстрее 1 с — серверные страницы и индексы.
/// Позиции вставляются одним INSERT … SELECT по образцу созданной позиции (вымышленные коды и названия).
/// </summary>
public sealed class CatListPerformanceTests(SqlTestHost host, ITestOutputHelper output) : IClassFixture<SqlTestHost>
{
    private const int Count = 50_000;
    private static int _innSeed = 936_000_000;

    [SqlFact]
    public async Task Fifty_thousand_items_page_search_and_group_under_one_second()
    {
        var org = await CreateOrgAsync();
        long groupId;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            await s.Catalog.CreateItemAsync(new ItemCommand("ОБР-0", "Образец", ItemType.Finished, piece, null));
            groupId = await s.Nomenclature.CreateGroupAsync(null, "Шапки");
        }

        await using (var db = host.NewDb())
        {
            db.Database.SetCommandTimeout(300);
            var template = await db.Items.Where(i => i.OrganizationId == org.OrganizationId).Select(i => i.Id).SingleAsync();
            var columns = await db.Database.SqlQueryRaw<string>(
                    "SELECT c.name AS Value FROM sys.columns c WHERE c.object_id = OBJECT_ID('kniterp.items') AND c.is_identity = 0 AND c.is_computed = 0 " +
                    "AND TYPE_NAME(c.system_type_id) <> 'timestamp'")
                .ToListAsync();
            string Pick(string c) => c switch
            {
                "Code" => "CONCAT(N'ПЕРФ-', n.n)",
                "Name" => "CONCAT(N'Позиция для проверки скорости ', RIGHT(CONCAT('00000', n.n), 5))",
                "Article" => "CONCAT(N'ART-', n.n)",
                "GroupId" => $"CASE WHEN n.n % 10 = 0 THEN {groupId} ELSE NULL END",
                _ => $"t.[{c}]",
            };
            var sql = $"""
                WITH n AS (SELECT TOP ({Count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO [kniterp].[items] ({string.Join(", ", columns.Select(c => $"[{c}]"))})
                SELECT {string.Join(", ", columns.Select(Pick))} FROM n CROSS JOIN [kniterp].[items] t WHERE t.[Id] = {template}
                """;
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        await using var c = host.As(org.OwnerUserId, org.OrganizationId);
        await c.Catalog.ListItemsAsync(new ItemFilter(Take: 100)); // прогрев плана запроса

        async Task<ItemListDto> Timed(string what, ItemFilter filter)
        {
            var sw = Stopwatch.StartNew();
            var list = await c.Catalog.ListItemsAsync(filter);
            sw.Stop();
            output.WriteLine($"{what}: {sw.ElapsedMilliseconds} мс, найдено {list.Total}");
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{what}: {sw.ElapsedMilliseconds} мс");
            return list;
        }

        var first = await Timed("первая страница", new ItemFilter(Take: 100));
        Assert.Equal((Count + 1, 100), (first.Total, first.Items.Count));

        var middle = await Timed("страница 251", new ItemFilter(Take: 100, Skip: 25_000));
        Assert.Equal(100, middle.Items.Count);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(middle.Items.Select(i => i.Id)));

        var last = await Timed("последняя страница", new ItemFilter(Take: 100, Skip: Count));
        Assert.Single(last.Items);

        Assert.Equal(1, (await Timed("поиск по коду", new ItemFilter(Search: "ПЕРФ-49999"))).Total);
        Assert.Equal(1, (await Timed("поиск по артикулу", new ItemFilter(Search: "ART-12345"))).Total);
        Assert.Equal(1, (await Timed("поиск без учёта регистра", new ItemFilter(Search: "перф-49999"))).Total);
        Assert.Equal(Count, (await Timed("поиск по части названия", new ItemFilter(Search: "ПРОВЕРКИ СКОРОСТИ", Take: 100))).Total);
        Assert.Equal(Count / 10, (await Timed("группа", new ItemFilter(GroupId: groupId, Take: 100))).Total);

        // Фид YML на 50 000 позиций с ценой (D314): строится за секунды, файл кэшируется на 5 минут.
        var priceType = (await c.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        await using (var db = host.NewDb())
        {
            db.Database.SetCommandTimeout(300);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [kniterp].[item_prices] ([OrganizationId], [ItemId], [PriceTypeId], [Price])
                SELECT [OrganizationId], [Id], {priceType}, 100 + [Id] % 1000 FROM [kniterp].[items] WHERE [OrganizationId] = {org.OrganizationId}
                """);
        }

        var feed = await c.Feeds.CreateAsync(new CatalogFeedCommand("Проверка скорости", priceType, null, null, false));
        var swFeed = Stopwatch.StartNew();
        var file = await c.Feeds.BuildAsync(feed.Key, "https://shop.example/");
        swFeed.Stop();
        output.WriteLine($"фид YML: {swFeed.ElapsedMilliseconds} мс, предложений {file!.Offers}, {file.Xml.Length / 1024} КБ");
        Assert.Equal(Count + 1, file.Offers);
        Assert.True(swFeed.ElapsedMilliseconds < 15_000, $"фид: {swFeed.ElapsedMilliseconds} мс");

        // Страницы не теряют и не повторяют строк: соседние страницы стыкуются по порядку.
        var p1 = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 50, Skip: 1000));
        var p2 = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 50, Skip: 1050));
        var both = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 100, Skip: 1000));
        Assert.Equal(both.Items.Select(i => i.Id), p1.Items.Concat(p2.Items).Select(i => i.Id));
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
