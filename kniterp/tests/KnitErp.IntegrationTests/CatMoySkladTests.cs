using System.Text.Json;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Exchange.MoySklad;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Подделка МойСклад для тестов (D310): записанные примеры JSON API 1.2 с вымышленными данными, страницы по offset/limit,
/// ответ 401 на неизвестный токен и сбой по требованию — для проверки продолжения загрузки.
/// </summary>
public sealed class FakeMoySkladClient : IMoySkladClient
{
    public const string Token = "test-token-0000000000000000abcd";
    public Dictionary<string, List<MsRow>> Entities { get; } = [];
    public List<MsPriceType> PriceTypes { get; } = [];
    public string? FailOnEntity { get; set; }

    /// <summary>Сбой не из МойСклад (ошибка разбора, ошибка кода): загрузка не должна остаться «идёт».</summary>
    public string? CrashOnEntity { get; set; }

    /// <summary>Вызывается при чтении страницы сущности — например, «пользователь нажал Остановить» из другой вкладки.</summary>
    public Func<string, Task>? OnList { get; set; }
    public int Calls { get; private set; }

    public static FakeMoySkladClient FromSamples()
    {
        var fake = new FakeMoySkladClient();
        foreach (var (entity, json) in Samples)
        {
            fake.Entities[entity] = JsonSerializer.Deserialize<MsPage<MsRow>>(json, MoySkladIds.Json)!.Rows!.ToList();
        }

        fake.PriceTypes.AddRange(JsonSerializer.Deserialize<List<MsPriceType>>(PriceTypesJson, MoySkladIds.Json)!);
        return fake;
    }

    public Task<string?> CheckAsync(string token, CancellationToken ct = default) =>
        token == Token ? Task.FromResult<string?>("ООО «Тестовый аккаунт»") : throw new MoySkladException("доступ запрещён", 401);

    public async Task<MsPage<MsRow>> ListAsync(string token, string entity, int offset, int limit, string? expand = null, CancellationToken ct = default)
    {
        if (OnList is { } hook)
        {
            await hook(entity);
        }

        return await ListCoreAsync(token, entity, offset, limit);
    }

    private Task<MsPage<MsRow>> ListCoreAsync(string token, string entity, int offset, int limit)
    {
        Calls++;
        Assert.Equal(Token, token);
        Assert.Contains(entity, MoySkladEntities.Allowed);
        if (FailOnEntity == entity)
        {
            FailOnEntity = null;
            throw new MoySkladException("ответ 503", 503);
        }

        if (CrashOnEntity == entity)
        {
            CrashOnEntity = null;
            throw new InvalidOperationException("внутренние подробности: stack, connection string");
        }

        var rows = Entities.GetValueOrDefault(entity) ?? [];
        return Task.FromResult(new MsPage<MsRow>(new MsMeta(null, null, rows.Count, limit, offset), rows.Skip(offset).Take(limit).ToList()));
    }

    public Task<IReadOnlyList<MsPriceType>> PriceTypesAsync(string token, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MsPriceType>>(PriceTypes);

    /// <summary>Отчёт «Остатки» (D333): строки с meta.href позиции и stock.</summary>
    public List<MsRow> Stock { get; } = [];

    public Task<MsPage<MsRow>> StockAsync(string token, int offset, int limit, CancellationToken ct = default)
    {
        Assert.Equal(Token, token);
        Assert.InRange(limit, 1, MoySkladEntities.MaxBatch);
        return Task.FromResult(new MsPage<MsRow>(new MsMeta(null, null, Stock.Count, limit, offset), Stock.Skip(offset).Take(limit).ToList()));
    }

    /// <summary>Отчёт «Остатки по складам» (D336).</summary>
    public List<MsRow> StockByStore { get; } = [];

    public Task<MsPage<MsRow>> StockByStoreAsync(string token, int offset, int limit, CancellationToken ct = default)
    {
        Assert.Equal(Token, token);
        return Task.FromResult(new MsPage<MsRow>(new MsMeta(null, null, StockByStore.Count, limit, offset), StockByStore.Skip(offset).Take(limit).ToList()));
    }

    /// <summary>Отправленные изменения цен (D327) — и применены к строкам, как сделал бы МойСклад.</summary>
    public List<(string Entity, MsPriceUpdate Update)> PriceUpdates { get; } = [];

    /// <summary>Объекты, которые «МойСклад» отклонит при массовом изменении цен.</summary>
    public HashSet<string> RejectIds { get; } = [];

    public Task<IReadOnlyList<MsRow>> UpdatePricesAsync(string token, string entity, IReadOnlyList<MsPriceUpdate> updates, CancellationToken ct = default)
    {
        Assert.Equal(Token, token);
        Assert.Contains(entity, MoySkladEntities.Writable);
        Assert.InRange(updates.Count, 1, MoySkladEntities.MaxBatch);
        var rows = Entities[entity];
        var result = new List<MsRow>();
        foreach (var u in updates)
        {
            if (RejectIds.Contains(u.Id))
            {
                result.Add(new MsRow()); // как МойСклад: элемент массива с errors и без id
                continue;
            }

            PriceUpdates.Add((entity, u));
            var i = rows.FindIndex(r => r.Id == u.Id);
            var old = rows[i];
            rows[i] = old with
            {
                Updated = "2026-10-12 12:00:00.000",
                SalePrices = u.SalePrices.Select(p => new MsPrice(p.Value, old.SalePrices!.First(x => x.PriceType?.RefId == p.PriceTypeId).PriceType)).ToList(),
                BuyPrice = u.BuyPrice is { } b ? new MsPrice(b, null) : old.BuyPrice,
            };
            result.Add(rows[i]);
        }

        return Task.FromResult<IReadOnlyList<MsRow>>(result);
    }

    public const string PriceTypesJson = """
        [{"id":"pt-sale","name":"Цена продажи","externalCode":"cbcf493b"},{"id":"pt-opt","name":"Оптовая","externalCode":"opt-1"}]
        """;

    private static string H(string entity, string id) => $"https://api.moysklad.ru/api/remap/1.2/{entity}/{id}";

    public static readonly (string Entity, string Json)[] Samples =
    [
        (MoySkladEntities.Uom, $$$$"""
            {"meta":{"size":2},"rows":[
              {"id":"uom-pcs","name":"шт","description":"Штука","code":"796"},
              {"id":"uom-box","name":"кор","description":"Коробка","code":"BOX"}]}
            """),
        (MoySkladEntities.Country, """{"meta":{"size":1},"rows":[{"id":"c-ru","name":"Россия","code":"643"}]}"""),
        (MoySkladEntities.Folder, $$$$"""
            {"meta":{"size":2},"rows":[
              {"id":"f-root","name":"Трикотаж","pathName":"","updated":"2026-10-01 10:00:00.000"},
              {"id":"f-hats","name":"Шапки","pathName":"Трикотаж","productFolder":{"meta":{"href":"{{{{H("entity/productfolder", "f-root")}}}}"}},"updated":"2026-10-01 10:00:00.000"}]}
            """),
        (MoySkladEntities.Product, $$$$"""
            {"meta":{"size":2},"rows":[
              {"id":"p-hat","updated":"2026-10-01 10:00:00.000","name":"Шапка вязаная","code":"ШП-1","externalCode":"ext-hat","article":"HAT-01",
               "vat":22,"vatEnabled":true,"productFolder":{"meta":{"href":"{{{{H("entity/productfolder", "f-hats")}}}}"}},
               "uom":{"meta":{"href":"{{{{H("entity/uom", "uom-pcs")}}}}"}},"country":{"meta":{"href":"{{{{H("entity/country", "c-ru")}}}}"}},
               "salePrices":[{"value":150000,"priceType":{"meta":{"href":"{{{{H("context/companysettings/pricetype", "pt-sale")}}}}"}}},
                             {"value":120000,"priceType":{"meta":{"href":"{{{{H("context/companysettings/pricetype", "pt-opt")}}}}"}}}],
               "buyPrice":{"value":60000},"minPrice":{"value":100000},"weight":0.15,"volume":0.002,"tnved":"6505009000",
               "trackingType":"LP_CLOTHES","paymentItemType":"GOOD",
               "barcodes":[{"ean13":"4600000000008"},{"gtin":"04601234567893"}],
               "packs":[{"id":"pk-1","uom":{"meta":{"href":"{{{{H("entity/uom", "uom-box")}}}}"}},"quantity":12,"barcodes":[{"ean13":"4600000000015"}]}]},
              {"id":"p-scarf","updated":"2026-10-01 10:00:00.000","name":"Шарф","code":"ШФ-1","vat":22,"vatEnabled":true,
               "uom":{"meta":{"href":"{{{{H("entity/uom", "uom-pcs")}}}}"}},"salePrices":[{"value":90000,"priceType":{"meta":{"href":"{{{{H("context/companysettings/pricetype", "pt-sale")}}}}"}}}]}]}
            """),
        (MoySkladEntities.Service, $$$$"""
            {"meta":{"size":1},"rows":[
              {"id":"s-delivery","updated":"2026-10-01 10:00:00.000","name":"Доставка по городу","code":"ДОСТ","vat":22,"vatEnabled":true,
               "paymentItemType":"SERVICE","salePrices":[{"value":50000,"priceType":{"meta":{"href":"{{{{H("context/companysettings/pricetype", "pt-sale")}}}}"}}}]}]}
            """),
        (MoySkladEntities.Variant, $$$$"""
            {"meta":{"size":1},"rows":[
              {"id":"v-hat-red","updated":"2026-10-01 10:00:00.000","name":"Шапка вязаная (красная)","code":"ШП-1-К",
               "characteristics":[{"name":"Цвет","value":"Красный"}],"product":{"meta":{"href":"{{{{H("entity/product", "p-hat")}}}}"}},
               "barcodes":[{"ean13":"4600000000022"}]}]}
            """),
        (MoySkladEntities.Bundle, $$$$"""
            {"meta":{"size":1},"rows":[
              {"id":"b-set","updated":"2026-10-01 10:00:00.000","name":"Набор шапка + шарф","code":"НБ-1","vat":22,"vatEnabled":true,
               "uom":{"meta":{"href":"{{{{H("entity/uom", "uom-pcs")}}}}"}},
               "salePrices":[{"value":200000,"priceType":{"meta":{"href":"{{{{H("context/companysettings/pricetype", "pt-sale")}}}}"}}}],
               "components":{"rows":[{"assortment":{"meta":{"href":"{{{{H("entity/product", "p-hat")}}}}","type":"product"}},"quantity":1},
                                     {"assortment":{"meta":{"href":"{{{{H("entity/product", "p-scarf")}}}}","type":"product"}},"quantity":1}]}}]}
            """),
    ];
}

/// <summary>
/// Загрузка из МойСклад (D310): подключение с токеном (зашифрован, виден хвост), предпросмотр, фоновая загрузка, сопоставление полей,
/// повторная загрузка без дублей, защита ручных правок, перезапись по флагу, продолжение после сбоя, отчёт, права и изоляция.
/// </summary>
public sealed class CatMoySkladTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 935_000_000;

    private MoySkladImportService Service(Services s, FakeMoySkladClient fake) =>
        new(s.Db, new AccessGuard(s.Db, new TestUser { UserId = _user, OrganizationId = _org }, host.Clock), new TestUser { UserId = _user, OrganizationId = _org },
            host.Clock, fake, SqlTestHost.Spreadsheet);

    private long _user;
    private long _org;

    [SqlFact]
    public async Task Connect_preview_import_and_field_mapping()
    {
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using var s = host.As(_user, _org);
        var ms = Service(s, fake);

        Assert.Equal("exchange.token.format", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.ConnectAsync("короткий"))).Code);
        Assert.Equal("exchange.moysklad.check", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.ConnectAsync("wrong-token-00000000000000000000"))).Code);
        Assert.Equal("exchange.not_connected", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.StartAsync())).Code);
        await ms.ConnectAsync("Bearer " + FakeMoySkladClient.Token);
        var status = await ms.StatusAsync();
        Assert.Equal((true, "ООО «Тестовый аккаунт»", "…abcd"), (status.Connected, status.Account, status.TokenHint));

        // Токен в базе — только зашифрованным.
        await using (var raw = new SqlConnection(s.Db.Database.GetConnectionString()))
        {
            await raw.OpenAsync();
            await using var cmd = new SqlCommand("SELECT [Token] FROM [kniterp].[external_connections]", raw);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.DoesNotContain(FakeMoySkladClient.Token, stored);
        }

        var preview = await ms.PreviewAsync();
        Assert.Equal([("Группы", 2, 2), ("Товары", 2, 2), ("Услуги", 1, 1), ("Модификации", 1, 1), ("Комплекты", 1, 1)],
            preview.Lines.Select(l => (l.Entity, l.Total, l.ToCreate)));

        var runId = await ms.StartAsync();
        Assert.Equal("exchange.run.active", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.StartAsync())).Code);
        Assert.True(await ms.ProcessNextAsync());
        Assert.False(await ms.ProcessNextAsync());
        var run = (await ms.StatusAsync()).Runs.Single();
        Assert.Equal((ImportRunStatus.Completed, 0), (run.Status, run.Errors));
        Assert.True(run.Created >= 7, $"создано {run.Created}");

        // Сопоставление полей (docs/moysklad-mapping.md).
        await using var c = host.As(_user, _org);
        var hatId = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null).Id;
        var card = await c.Nomenclature.GetItemAsync(hatId);
        Assert.Equal(("Шапка вязаная", ItemType.Finished, "шт", "HAT-01", "Трикотаж / Шапки"), (card.Name, card.Type, card.UnitSymbol, card.Details.Article, card.GroupPath));
        Assert.Equal(("643", "6505009000", 0.15m, 0.002m, 600m), (card.Details.OriginCountryCode, card.Details.TnVedCode, card.Details.WeightKg!.Value,
            card.Details.VolumeM3!.Value, card.Details.PurchasePrice!.Value));
        Assert.Equal([("Цена продажи", 1500m), ("Оптовая", 1200m)], card.Prices.Where(p => p.Price is not null).Select(p => (p.PriceType, p.Price!.Value)));
        Assert.Contains(card.Barcodes, b => b.Code == "4600000000008");
        var trade = (await c.Trade.GetAsync(hatId)).Trade;
        Assert.Equal((true, TrackingType.Clothes, "04601234567893", 1000m), (trade.IsMarked, trade.TrackingType, trade.Gtin, trade.MinPrice!.Value));
        Assert.Equal(("кор", 12m, "4600000000015"), (await c.Packs.ListAsync(hatId)).Single() is var p ? (p.UnitSymbol, p.Factor, p.Barcode) : default);
        Assert.NotNull(card.VatRateId);

        var service = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ДОСТ"))).Items.Single();
        Assert.Equal((ItemType.Service, true), (service.Type, service.VatRateId is not null));
        var variant = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1-К"))).Items.Single();
        Assert.Equal(hatId, variant.ParentItemId);
        var bundle = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "НБ-1"))).Items.Single();
        Assert.Equal([("ШП-1", 1m), ("ШФ-1", 1m)], (await c.Bundles.GetAsync(bundle.Id)).Components.Select(x => (x.Code, x.Quantity)));
        Assert.Contains((await c.Nomenclature.PriceTypesAsync()), t => t.Name == "Оптовая");
        var history = (await c.Trade.GetAsync(hatId)).History;
        Assert.All(history, h => Assert.Equal(PriceChangeSource.External, h.Source));
    }

    [SqlFact]
    public async Task Failed_row_is_rolled_back_entirely_and_bundle_keeps_old_composition()
    {
        // Ревью: строка загрузки — всё или ничего (точка сохранения на строку).
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
        }

        // Комплект в МойСклад изменён: 1,5 шапки (штучная единица) — ошибка строки; новый комплект с 0,4 шарфа — тоже.
        var set = fake.Entities[MoySkladEntities.Bundle].Single();
        var rows = set.Components!.Rows!;
        fake.Entities[MoySkladEntities.Bundle] =
        [
            set with { Updated = "2026-10-05 10:00:00.000", Components = set.Components with { Rows = [rows[0] with { Quantity = 1.5m }, rows[1]] } },
            set with { Id = "b-new", Code = "НБ-2", Name = "Новый набор", Updated = "2026-10-05 10:00:00.000",
                       Components = set.Components with { Rows = [rows[0], rows[1] with { Quantity = 0.4m }] } },
        ];
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var run = (await ms.StatusAsync()).Runs.First();
            Assert.Equal((ImportRunStatus.Completed, 2), (run.Status, run.Errors));
            Assert.Contains(await ms.IssuesAsync(run.Id), i => i.IsError && i.Text.Contains("не делится на единицу"));
        }

        await using var c = host.As(_user, _org);
        var bundle = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "НБ-1"))).Items.Single();
        Assert.Equal([("ШП-1", 1m), ("ШФ-1", 1m)], (await c.Bundles.GetAsync(bundle.Id)).Components.Select(x => (x.Code, x.Quantity)));
        // Новая позиция с ошибкой в составе не осталась «полусозданной» (без связи, цен и состава).
        Assert.Empty((await c.Catalog.ListItemsAsync(new ItemFilter(Search: "НБ-2"))).Items);

        // Исправили в МойСклад — следующая загрузка создаёт новый комплект полностью.
        fake.Entities[MoySkladEntities.Bundle][1] = fake.Entities[MoySkladEntities.Bundle][1] with
        {
            Updated = "2026-10-06 10:00:00.000", Components = set.Components with { Rows = [rows[0], rows[1] with { Quantity = 2m }] },
        };
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
        }

        var created = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "НБ-2"))).Items.Single();
        Assert.Equal([("ШП-1", 1m), ("ШФ-1", 2m)], (await c.Bundles.GetAsync(created.Id)).Components.Select(x => (x.Code, x.Quantity)));
    }

    [SqlFact]
    public async Task Preview_of_ten_thousand_linked_rows_reads_local_items_once()
    {
        // Ревью: предпросмотр не делает по запросу на каждую связанную строку.
        const int count = 10_000;
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = new FakeMoySkladClient();
        long piece;
        await using (var s = host.As(_user, _org))
        {
            piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            await Service(s, fake).ConnectAsync(FakeMoySkladClient.Token);
        }

        await using (var db = host.NewDb())
        {
            var items = Enumerable.Range(1, count).Select(i => Item.Create(_org, $"ПР-{i}", $"Позиция {i}", ItemType.Finished, piece, null, host.Clock.UtcNow)).ToList();
            db.Items.AddRange(items);
            await db.SaveChangesAsync();
            db.ExternalLinks.AddRange(items.Select((it, i) => ExternalLink.Create(_org, ExternalSystem.MoySklad, ExternalObjectType.Item, it.Id, $"p-{i}", null, "v1", "old", host.Clock.UtcNow)));
            await db.SaveChangesAsync();
        }

        fake.Entities[MoySkladEntities.Product] = Enumerable.Range(0, count).Select(i => new MsRow { Id = $"p-{i}", Name = $"Позиция {i + 1}", Code = $"ПР-{i + 1}", Updated = "v2" }).ToList();
        await using var c = host.As(_user, _org);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var preview = await Service(c, fake).PreviewAsync();
        sw.Stop();
        var products = preview.Lines.Single(l => l.Entity == "Товары");
        Assert.Equal((count, count), (products.Total, products.Skipped)); // изменены в knitERP после прошлой загрузки — не перезаписываются
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"предпросмотр {count} строк: {sw.Elapsed.TotalSeconds:0.0} с");
    }

    [SqlFact]
    public async Task Prices_are_exported_only_when_changed_and_reimport_does_not_echo_them()
    {
        // D327: выгрузка цен в МойСклад — связанные позиции, только отличающиеся, все цены продажи объекта (непривязанные — как были).
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var first = await ms.PriceExportPreviewAsync(); Assert.True(first.ToChange == 0, string.Join(",", first.Samples)); // сразу после загрузки всё совпадает
        }

        await using var c = host.As(_user, _org);
        var hat = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null).Id;
        var retail = (await c.Nomenclature.PriceTypesAsync()).Single(t => t.Name == "Цена продажи").Id;
        await c.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [retail] = 1600 });
        var card = await c.Nomenclature.GetItemAsync(hat);
        await c.Nomenclature.SaveItemDetailsAsync(hat, card.Details with { PurchasePrice = 650 }, new Dictionary<long, string?>(), card.RowVersion);

        var ms2 = Service(c, fake);
        var preview = await ms2.PriceExportPreviewAsync();
        // Шапка — новая цена и закупочная; модификация хранит свою закупочную (600), а в МойСклад она наследовала цену товара (теперь 650) —
        // её закупочная становится явной.
        Assert.Equal((5, 2, 0), (preview.Checked, preview.ToChange, preview.Sent));
        Assert.Equal(["ШП-1", "ШП-1-К"], preview.Samples.Order());
        Assert.Empty(fake.PriceUpdates); // предпросмотр ничего не отправляет

        var result = await ms2.ExportPricesAsync();
        Assert.Equal(2, result.Sent);
        var (entity, update) = fake.PriceUpdates.Single(u => u.Update.Id == "p-hat");
        Assert.Equal((MoySkladEntities.Variant, 60000m), fake.PriceUpdates.Single(u => u.Update.Id == "v-hat-red") is var v ? (v.Entity, v.Update.BuyPrice) : default);
        Assert.Equal((MoySkladEntities.Product, "p-hat", 65000m), (entity, update.Id, update.BuyPrice));
        Assert.Equal([("pt-sale", 160000m), ("pt-opt", 120000m)], update.SalePrices.Select(p => (p.PriceTypeId, p.Value)));
        Assert.Equal(0, (await ms2.PriceExportPreviewAsync()).ToChange);

        // Повторная загрузка не считает свою выгрузку изменением в МойСклад: цены knitERP на месте.
        await ms2.StartAsync();
        Assert.True(await ms2.ProcessNextAsync());
        Assert.Equal(1600m, (await c.Nomenclature.GetItemAsync(hat)).Prices.Single(p => p.PriceTypeId == retail).Price);

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(e => e.OrganizationId == _org && e.Reason == "Выгрузка цен в МойСклад"));
    }

    [SqlFact]
    public async Task Probe_shows_which_section_fails_without_saving_token()
    {
        // D330: проверка связи по разделам — с введённым токеном, ещё до подключения; ничего не сохраняет.
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        fake.FailOnEntity = MoySkladEntities.Bundle;
        await using var s = host.As(_user, _org);
        var ms = Service(s, fake);
        var probe = await ms.ProbeAsync("Bearer " + FakeMoySkladClient.Token);
        Assert.Equal(11, probe.Count);
        Assert.Equal("Версия knitERP", probe[0].Section); // D337: версия сборки — первой строкой
        Assert.All(probe.Where(p => p.Section != "Комплекты"), p => Assert.True(p.Ok, p.Section + ": " + p.Detail));
        var bundles = probe.Single(p => p.Section == "Комплекты");
        Assert.False(bundles.Ok);
        Assert.Contains("503", bundles.Detail);
        Assert.Equal("записей: 2", probe.Single(p => p.Section == "Товары").Detail);
        Assert.False((await ms.StatusAsync()).Connected); // токен не сохранён

        // Без введённого токена и без подключения — понятный отказ.
        Assert.Equal("exchange.not_connected", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.ProbeAsync())).Code);
    }

    [SqlFact]
    public async Task Import_respects_characteristic_types()
    {
        // D328: у «Цвета» тип «Справочник» — значение из МойСклад должно быть записью справочника (как в справочнике), иначе ошибка строки.
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        long colors;
        await using (var s = host.As(_user, _org))
        {
            colors = await s.Nomenclature.CreateCatalogAsync("Цвета");
            await s.Nomenclature.AddEntryAsync(colors, "Синий", null);
            await s.Modifications.CreateCharacteristicAsync("Цвет", CharacteristicType.Catalog, colors);
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var run = (await ms.StatusAsync()).Runs.First();
            Assert.Contains(await ms.IssuesAsync(run.Id), i => i.IsError && i.Entity == "Модификации" && i.Text.Contains("нет в справочнике"));
            Assert.Empty((await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1-К"))).Items);

            await s.Nomenclature.AddEntryAsync(colors, "КРАСНЫЙ", null);
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
        }

        await using var c = host.As(_user, _org);
        var variant = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1-К"))).Items.Single();
        var values = (await c.Modifications.VariantsAsync(variant.ParentItemId!.Value)).Modifications.Single().Values;
        Assert.Equal("КРАСНЫЙ", values.Single().Value); // как в справочнике, а не «Красный» из МойСклад
    }

    [SqlFact]
    public async Task Import_maps_moysklad_attributes_to_typed_custom_fields()
    {
        // D332: доп. поля МойСклад → доп. поля номенклатуры: тип по МойСклад, справочник создаётся, неподдерживаемое и опасное — предупреждение.
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        static MsAttribute A(string name, string type, string json) => new(null, name, type, JsonDocument.Parse(json).RootElement.Clone());
        var products = fake.Entities[MoySkladEntities.Product];
        var hatIndex = products.FindIndex(r => r.Id == "p-hat");
        products[hatIndex] = products[hatIndex] with
        {
            Attributes =
            [
                A("Состав", "string", "\"шерсть 40%\""),
                A("состав", "string", "\"шерсть 50%\""), // то же поле knitERP — последнее значение, без дубля
                A("Уход", "text", "\"Стирка 30°\\nНе отбеливать\""),
                A("Плотность", "double", "220.5"),
                A("Новинка", "boolean", "true"),
                A("Дата поступления", "time", "\"2026-10-01 00:00:00.000\""),
                A("Страница", "link", "\"https://shop.example/hat\""),
                A("Сезон", "customentity", "{\"meta\":{},\"name\":\"Зима\"}"),
                A("Фото", "file", "{\"meta\":{},\"filename\":\"a.jpg\"}"),
                A("Плохая ссылка", "link", "\"javascript:alert(1)\""),
            ],
        };

        long runId;
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
            runId = await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var run = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal((ImportRunStatus.Completed, 0), (run.Status, run.Errors)); // предупреждения — не ошибки строки
            var issues = await ms.IssuesAsync(runId);
            Assert.Contains(issues, i => !i.IsError && i.Entity == "Доп. поля" && i.Text.Contains("«Фото»"));
            Assert.Contains(issues, i => !i.IsError && i.Entity == "Доп. поля" && i.Text.Contains("https://"));
        }

        await using var c = host.As(_user, _org);
        var hat = (await c.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null).Id;
        var fields = (await c.Nomenclature.GetItemAsync(hat)).CustomFields.ToDictionary(f => f.Name);
        Assert.Equal((CustomFieldType.Text, "шерсть 50%"), (fields["Состав"].Type, fields["Состав"].Value));
        Assert.Equal((CustomFieldType.LongText, "Стирка 30°\nНе отбеливать"), (fields["Уход"].Type, fields["Уход"].Value));
        Assert.Equal((CustomFieldType.Number, "220.5"), (fields["Плотность"].Type, fields["Плотность"].Value));
        Assert.Equal((CustomFieldType.Flag, "true"), (fields["Новинка"].Type, fields["Новинка"].Value));
        Assert.Equal((CustomFieldType.Date, "2026-10-01"), (fields["Дата поступления"].Type, fields["Дата поступления"].Value));
        Assert.Equal((CustomFieldType.Link, "https://shop.example/hat"), (fields["Страница"].Type, fields["Страница"].Value));
        Assert.Equal((CustomFieldType.Catalog, "Зима"), (fields["Сезон"].Type, fields["Сезон"].Display));
        Assert.False(fields.ContainsKey("Фото"));
        Assert.Null(fields["Плохая ссылка"].Value);
        Assert.Contains(await c.Nomenclature.CatalogsAsync(), x => x.Name == "Сезон");
    }

    [SqlFact]
    public async Task Stock_compare_shows_linked_items_with_different_balances()
    {
        // D333: сверка остатков — только связанные позиции, отсутствующая в отчёте МойСклад — ноль; ничего не записывает.
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using var s = host.As(_user, _org);
        var ms = Service(s, fake);
        await ms.ConnectAsync(FakeMoySkladClient.Token);
        await ms.StartAsync();
        Assert.True(await ms.ProcessNextAsync());

        // В knitERP: 5 шапок на складе (поступление).
        var hat = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        await using (var db = host.NewDb())
        {
            var reason = await db.OperationReasons.Where(r => r.OrganizationId == _org && r.Kind == KnitErp.Domain.Warehousing.StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
            var doc = await s.Documents.CreateAsync(KnitErp.Domain.Warehousing.StockOperationKind.Receipt,
                new KnitErp.Domain.Warehousing.StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
            await s.Documents.SetLineAsync(doc, hat, 5, (await s.Documents.GetAsync(doc)).RowVersion);
            await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
        }

        // В МойСклад: шапок 3 (по двум строкам отчёта не бывает — но сумма на всякий случай), шарфов 0, модификации нет в отчёте.
        static MsRow R(string entity, string id, decimal stock) => new() { Meta = new MsMeta($"https://api.moysklad.ru/api/remap/1.2/{entity}/{id}?expand=supplier", "product", null, null, null), Stock = stock };
        fake.Stock.AddRange([R("entity/product", "p-hat", 3), R("entity/product", "p-scarf", 0), R("entity/product", "чужой-id", 100)]);
        var result = await ms.StockCompareAsync();
        var diff = result.Differences.Single();
        Assert.Equal((hat, 5m, 3m), (diff.ItemId, diff.KnitErp, diff.MoySklad));
        Assert.Equal(1, result.Different);
        Assert.True(result.Compared >= 3); // шапка, шарф, модификация
        Assert.Empty(fake.PriceUpdates);

        // D336: по складам — «Склад» knitERP и « склад » МойСклад (регистр и пробелы не важны); «Цех» без пары — в списке.
        var storeHref = new MsMeta("https://api.moysklad.ru/api/remap/1.2/entity/store/s-1", "store", null, null, null);
        fake.StockByStore.Add(R("entity/product", "p-hat", 0) with { StockByStore = [new MsStoreStock(storeHref, " склад ", 4), new MsStoreStock(storeHref, "Цех", 7)] });
        var perStore = await ms.StockCompareAsync(perWarehouse: true);
        var byStore = perStore.Differences.Single();
        Assert.Equal(("Склад", 5m, 4m), (byStore.Warehouse, byStore.KnitErp, byStore.MoySklad));
        Assert.Equal(["МойСклад: Цех"], perStore.Unmatched);
    }

    [SqlFact]
    public async Task Price_export_does_not_hide_moysklad_changes_and_counts_rejected()
    {
        // Ревью D327: правка в МойСклад после загрузки не «съедается» выгрузкой цен; отклонённый объект не считается отправленным.
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using var s = host.As(_user, _org);
        var ms = Service(s, fake);
        await ms.ConnectAsync(FakeMoySkladClient.Token);
        await ms.StartAsync();
        Assert.True(await ms.ProcessNextAsync());

        var hat = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null).Id;
        var scarf = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ШФ-1"))).Items.Single().Id;
        var retail = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.Name == "Цена продажи").Id;
        await s.Nomenclature.SetPricesAsync(hat, new Dictionary<long, decimal?> { [retail] = 1700 });
        await s.Nomenclature.SetPricesAsync(scarf, new Dictionary<long, decimal?> { [retail] = 999 });

        // В МойСклад шапку переименовали после загрузки; шарф МойСклад отклонит.
        var products = fake.Entities[MoySkladEntities.Product];
        var i = products.FindIndex(r => r.Id == "p-hat");
        products[i] = products[i] with { Name = "Шапка вязаная NEW", Updated = "2026-10-05 09:00:00.000" };
        fake.RejectIds.Add("p-scarf");

        var result = await ms.ExportPricesAsync();
        Assert.Equal((1, 1), (result.Sent, result.Rejected));

        await ms.StartAsync();
        Assert.True(await ms.ProcessNextAsync());
        Assert.Equal("Шапка вязаная NEW", (await s.Nomenclature.GetItemAsync(hat)).Name);
    }

    [SqlFact]
    public async Task Reimport_is_idempotent_protects_manual_changes_and_resumes()
    {
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();

        // Позиция knitERP с тем же кодом, что у шарфа в МойСклад: связывается, значения knitERP сохраняются.
        await using (var s = host.As(_user, _org))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф (наш)", ItemType.Finished, piece, null));
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);

            // Сбой на услугах: загрузка прерывается, товары уже записаны.
            fake.FailOnEntity = MoySkladEntities.Service;
            var runId = await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var failed = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal((ImportRunStatus.Failed, "product"), (failed.Status, failed.Stage));
            await ms.ResumeAsync(runId);
            Assert.True(await ms.ProcessNextAsync());
            Assert.Equal(ImportRunStatus.Completed, (await ms.StatusAsync()).Runs.Single().Status);
        }

        int itemsAfterFirst;
        await using (var db = host.NewDb())
        {
            itemsAfterFirst = await db.Items.CountAsync(i => i.OrganizationId == _org);
            Assert.Equal("Шарф (наш)", await db.Items.Where(i => i.OrganizationId == _org && i.Code == "ШФ-1").Select(i => i.Name).SingleAsync());
        }

        // Повторная загрузка без изменений в МойСклад: ничего не создаётся.
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.StartAsync();
            await ms.ProcessNextAsync();
            var run = (await ms.StatusAsync()).Runs.First();
            Assert.Equal((ImportRunStatus.Completed, 0), (run.Status, run.Created));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(itemsAfterFirst, await db.Items.CountAsync(i => i.OrganizationId == _org));
            Assert.Equal(itemsAfterFirst, await db.ExternalLinks.CountAsync(l => l.OrganizationId == _org && l.ObjectType == ExternalObjectType.Item));
        }

        // Шапку правят вручную в knitERP, в МойСклад она тоже меняется: без «перезаписать» — не затирается, с флагом — обновляется.
        long hatId;
        await using (var s = host.As(_user, _org))
        {
            var row = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ШП-1"))).Items.Single(i => i.ParentItemId is null);
            hatId = row.Id;
            await s.Catalog.UpdateItemAsync(hatId, new ItemCommand(row.Code, "Шапка (переименована у нас)", row.Type, row.UnitId, null, row.VatRateId), row.RowVersion);
        }

        var hat = fake.Entities[MoySkladEntities.Product][0];
        fake.Entities[MoySkladEntities.Product][0] = hat with { Name = "Шапка вязаная NEW", Updated = "2026-10-05 09:00:00.000" };
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            var runId = await ms.StartAsync();
            await ms.ProcessNextAsync();
            Assert.Contains(await ms.IssuesAsync(runId), i => i.Text.Contains("изменена в knitERP"));
            Assert.Equal("Шапка (переименована у нас)", (await s.Nomenclature.GetItemAsync(hatId)).Name);

            await ms.StartAsync(overwrite: true);
            await ms.ProcessNextAsync();
        }

        await using (var s = host.As(_user, _org))
        {
            Assert.Equal("Шапка вязаная NEW", (await s.Nomenclature.GetItemAsync(hatId)).Name);
            var ms = Service(s, fake);
            var report = await ms.ReportAsync((await ms.StatusAsync()).Runs.First().Id);
            Assert.True(report.Length > 1000);
        }
    }

    [SqlFact]
    public async Task Only_owner_or_admin_and_never_other_organization()
    {
        var org = await CreateOrgAsync();
        var fake = FakeMoySkladClient.FromSamples();
        long accountant;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
            (_user, _org) = (org.OwnerUserId, org.OrganizationId);
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            (_user, _org) = (accountant, org.OrganizationId);
            await Assert.ThrowsAsync<AccessDeniedException>(() => Service(s, fake).StatusAsync());
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            (_user, _org) = (other.OwnerUserId, other.OrganizationId);
            var ms = Service(s, fake);
            Assert.False((await ms.StatusAsync()).Connected);
            Assert.Equal("exchange.not_connected", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.PreviewAsync())).Code);
        }
    }

    [SqlFact]
    public async Task Unexpected_failure_and_restart_leave_resumable_run_without_internal_details()
    {
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);
            fake.CrashOnEntity = MoySkladEntities.Variant;
            await ms.StartAsync();
            Assert.True(await ms.ProcessNextAsync());
            var run = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal(ImportRunStatus.Failed, run.Status);
            Assert.DoesNotContain("connection string", run.Message);
            Assert.Contains("можно продолжить", run.Message);
        }

        // Загрузка «идёт», а процесс остановлен: при старте фоновой обработки она помечается прерванной и продолжается.
        await using (var db = host.NewDb())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[external_import_runs] SET [Status] = 2, [FinishedAtUtc] = NULL, [Message] = NULL WHERE [OrganizationId] = {_org}");
        }

        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            Assert.Equal("exchange.run.active", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.StartAsync())).Code);
            Assert.True(await ms.RecoverInterruptedAsync() >= 1);
            var run = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal(ImportRunStatus.Failed, run.Status);
            await ms.ResumeAsync(run.Id);
            Assert.True(await ms.ProcessNextAsync());
            Assert.Equal(ImportRunStatus.Completed, (await ms.StatusAsync()).Runs.Single().Status);
        }
    }

    [SqlFact]
    public async Task Cancel_queued_immediately_running_at_checkpoint_then_resume()
    {
        var org = await CreateOrgAsync();
        (_user, _org) = (org.OwnerUserId, org.OrganizationId);
        var fake = FakeMoySkladClient.FromSamples();
        long runId;
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            await ms.ConnectAsync(FakeMoySkladClient.Token);

            // В очереди — останавливается сразу, фоновому процессу нечего брать.
            runId = await ms.StartAsync();
            await ms.CancelAsync(runId);
            var queued = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal((ImportRunStatus.Failed, ExternalImportRun.CancelledMessage), (queued.Status, queued.Message));
            Assert.False(await ms.ProcessNextAsync());
            Assert.Equal("exchange.run.not_active", (await Assert.ThrowsAsync<BusinessRuleException>(() => ms.CancelAsync(runId))).Code);
            await ms.ResumeAsync(runId);
        }

        // Идущая: «Остановить» нажато из другой вкладки, пока читаются услуги; движок останавливается на контрольной точке.
        // Ревью: та же вкладка потом нажимает «Продолжить» — её копия загрузки устарела (её закрыл фоновый процесс), но перечитывается.
        await using var tab = host.As(_user, _org);
        fake.OnList = async entity =>
        {
            if (entity == MoySkladEntities.Service)
            {
                fake.OnList = null;
                await Service(tab, fake).CancelAsync(runId);
            }
        };
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            Assert.True(await ms.ProcessNextAsync());
            var stopped = (await ms.StatusAsync()).Runs.Single();
            Assert.Equal((ImportRunStatus.Failed, ExternalImportRun.CancelledMessage, false), (stopped.Status, stopped.Message, stopped.CancelRequested));
            Assert.Equal("product", stopped.Stage);
        }

        await Service(tab, fake).ResumeAsync(runId);
        await using (var s = host.As(_user, _org))
        {
            var ms = Service(s, fake);
            Assert.True(await ms.ProcessNextAsync());
            Assert.Equal(ImportRunStatus.Completed, (await ms.StatusAsync()).Runs.Single().Status);
            Assert.Single((await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ДОСТ"))).Items);
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
