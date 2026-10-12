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
