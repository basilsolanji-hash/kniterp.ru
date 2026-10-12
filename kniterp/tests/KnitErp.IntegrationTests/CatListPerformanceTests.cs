using System.Diagnostics;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Warehousing;
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

        // Аудит D339–D343: у всех 50 000 позиций есть движение — карточка, проверка «в учёте» (точность и единица) и
        // предупреждения архива не просматривают всю историю.
        var store = await c.Warehouses.CreateWarehouseAsync("Склад скорости", null);
        var sample = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ОБР-0"))).Items.Single();
        var reason = await c.Db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt).Select(r => r.Id).FirstAsync();
        var receipt = await c.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
        await c.Documents.SetLineAsync(receipt, sample.Id, 5, (await c.Documents.GetAsync(receipt)).RowVersion);
        await c.Documents.PostAsync(receipt, (await c.Documents.GetAsync(receipt)).RowVersion);
        await using (var db = host.NewDb())
        {
            db.Database.SetCommandTimeout(300);
            var movementColumns = await db.Database.SqlQueryRaw<string>(
                    "SELECT c.name AS Value FROM sys.columns c WHERE c.object_id = OBJECT_ID('kniterp.stock_movements') AND c.is_identity = 0 AND c.is_computed = 0 " +
                    "AND TYPE_NAME(c.system_type_id) <> 'timestamp'")
                .ToListAsync();
            // Имена столбцов — из sys.columns, числа — Id из базы: ввода пользователя нет. Копии — вне цепочки подписей (ChainSeq NULL).
            var copy = $"""
                INSERT INTO [kniterp].[stock_movements] ({string.Join(", ", movementColumns.Select(x => $"[{x}]"))})
                SELECT {string.Join(", ", movementColumns.Select(x => x switch { "ItemId" => "i.[Id]", "ChainSeq" or "ChainHash" => "NULL", _ => $"m.[{x}]" }))}
                FROM [kniterp].[stock_movements] m CROSS JOIN [kniterp].[items] i
                WHERE m.[ItemId] = {sample.Id} AND i.[OrganizationId] = {org.OrganizationId} AND i.[Id] <> {sample.Id}
                """;
            await db.Database.ExecuteSqlRawAsync(copy);
        }

        var target = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ПЕРФ-25000"))).Items.Single();
        await c.Nomenclature.GetItemAsync(target.Id); // прогрев

        async Task<T> Measure<T>(string what, int limitMs, Func<Task<T>> action)
        {
            var sw = Stopwatch.StartNew();
            var result = await action();
            sw.Stop();
            output.WriteLine($"{what}: {sw.ElapsedMilliseconds} мс");
            Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what}: {sw.ElapsedMilliseconds} мс");
            return result;
        }

        var card = await Measure("карточка позиции", 500, () => c.Nomenclature.GetItemAsync(target.Id));
        Assert.Equal(5m, card.Stock);
        var warnings = await Measure("предупреждения архива", 500, () => c.Catalog.ArchiveItemAsync(target.Id, target.RowVersion));
        Assert.Contains(warnings, w => w.Contains("остаток 5"));
        var pieceUnit = (await c.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        Assert.NotNull(await Measure("единица в учёте (проверка точности)", 1000, () => c.Db.ItemUsageAsync(org.OrganizationId, null, pieceUnit)));
        Assert.NotNull(await Measure("позиция в учёте (проверка единицы)", 500, () => c.Db.ItemUsageAsync(org.OrganizationId, target.Id, null)));

        // Страницы не теряют и не повторяют строк: соседние страницы стыкуются по порядку.
        var p1 = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 50, Skip: 1000));
        var p2 = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 50, Skip: 1050));
        var both = await c.Catalog.ListItemsAsync(new ItemFilter(Take: 100, Skip: 1000));
        Assert.Equal(both.Items.Select(i => i.Id), p1.Items.Concat(p2.Items).Select(i => i.Id));
    }

    [SqlFact]
    public async Task Excel_import_of_max_rows_previews_and_applies_in_seconds()
    {
        // Аудит каталога (D347): файл на предел строк (5000) — проверка и загрузка укладываются в секунды; повторная загрузка
        // того же файла ничего не меняет.
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var file = SqlTestHost.Spreadsheet.Write([new SheetData("Номенклатура", ItemExchangeService.Columns,
            [.. Enumerable.Range(1, ItemExchangeService.MaxRows).Select(n => (IReadOnlyList<string>)[$"ИМП-{n}", $"Пряжа для загрузки {n}", ItemTypes.Name(ItemType.RawMaterial), "шт", ""])])]);

        var sw = Stopwatch.StartNew();
        var plan = await s.ItemExchange.PreviewAsync(new MemoryStream(file));
        output.WriteLine($"проверка файла {ItemExchangeService.MaxRows} строк: {sw.ElapsedMilliseconds} мс");
        Assert.Equal(0, plan.ErrorRows);
        sw.Restart();
        var result = await s.ItemExchange.ApplyAsync(plan.Rows, plan.Bundles);
        output.WriteLine($"загрузка {ItemExchangeService.MaxRows} строк: {sw.ElapsedMilliseconds} мс");
        Assert.Equal(ItemExchangeService.MaxRows, result.Created);
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"загрузка: {sw.ElapsedMilliseconds} мс"); // до D347 — ~5,5 мин

        sw.Restart();
        var again = await s.ItemExchange.PreviewAsync(new MemoryStream(file));
        output.WriteLine($"повторная проверка: {sw.ElapsedMilliseconds} мс, без изменений {again.Unchanged}");
        Assert.Equal(ItemExchangeService.MaxRows, again.Unchanged);
    }

    [SqlFact]
    public async Task Supplier_price_list_and_counterparty_import_at_limits()
    {
        // Аудит каталога (D348): прайс поставщика на 20 000 строк (по штрихкодам) и загрузка 5000 контрагентов — секунды, не минуты.
        const int priceRows = SupplierPriceListService.MaxRows;
        var org = await CreateOrgAsync();
        long supplier;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            await s.Catalog.CreateItemAsync(new ItemCommand("ПР-0", "Пряжа образец", ItemType.RawMaterial, piece, null));
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Поставщик пряжи»", null, null, true, false, null));
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
                "Code" => "CONCAT(N'ПР-', n.n)",
                "Name" => "CONCAT(N'Пряжа ', n.n)",
                _ => $"t.[{c}]",
            };
            var sql = $"""
                WITH n AS (SELECT TOP ({priceRows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO [kniterp].[items] ({string.Join(", ", columns.Select(c => $"[{c}]"))})
                SELECT {string.Join(", ", columns.Select(Pick))} FROM n CROSS JOIN [kniterp].[items] t WHERE t.[Id] = {template};
                INSERT INTO [kniterp].[item_barcodes] ([OrganizationId], [ItemId], [Type], [Code])
                SELECT [OrganizationId], [Id], 4, CONCAT('PL', SUBSTRING([Code], 4, 10)) FROM [kniterp].[items]
                WHERE [OrganizationId] = {org.OrganizationId} AND [Id] <> {template};
                """;
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        await using var c = host.As(org.OwnerUserId, org.OrganizationId);
        var file = SqlTestHost.Spreadsheet.Write([new SheetData("Прайс", ["Артикул", "Наименование", "Штрихкод", "Цена"],
            [.. Enumerable.Range(1, priceRows).Select(n => (IReadOnlyList<string>)[$"A-{n}", $"Пряжа {n}", $"PL{n}", (100 + n % 50).ToString()])])]);
        var sw = Stopwatch.StartNew();
        var plan = await c.PriceLists.PreviewAsync(supplier, new MemoryStream(file), false);
        output.WriteLine($"прайс {priceRows} строк, проверка: {sw.ElapsedMilliseconds} мс, к обновлению {plan.ToUpdate}");
        Assert.Equal(priceRows, plan.ToUpdate);
        sw.Restart();
        var applied = await c.PriceLists.ApplyAsync(plan);
        output.WriteLine($"прайс {priceRows} строк, загрузка: {sw.ElapsedMilliseconds} мс, связано артикулов {applied.Linked}");
        Assert.Equal((priceRows, priceRows), (applied.Updated, applied.Linked));
        Assert.True(sw.ElapsedMilliseconds < 40_000, $"прайс: {sw.ElapsedMilliseconds} мс");

        var parties = SqlTestHost.Spreadsheet.Write([new SheetData("Лист", CounterpartyExchangeService.Columns,
            [.. Enumerable.Range(1, TableImport.MaxRows).Select(n => (IReadOnlyList<string>)[$"ООО «Клиент {n}»", "", "", "", "да", "", ""])])]);
        sw.Restart();
        var partyPlan = await c.CounterpartyExchange.PreviewAsync(new MemoryStream(parties));
        output.WriteLine($"контрагенты {TableImport.MaxRows}, проверка: {sw.ElapsedMilliseconds} мс");
        sw.Restart();
        var partyResult = await c.CounterpartyExchange.ApplyAsync(partyPlan.Rows);
        output.WriteLine($"контрагенты {TableImport.MaxRows}, загрузка: {sw.ElapsedMilliseconds} мс");
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"контрагенты: {sw.ElapsedMilliseconds} мс");
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
