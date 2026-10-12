using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Каналы и отчёты маркетплейсов (D1107, D1168–D1170): свойства канала, разбор файлов Wildberries и Ozon (собраны через
/// ISpreadsheetFormat.Write), ошибки строк, сопоставление по внешним связям и штрихкоду, несопоставленные и решения, повтор без
/// дублей, аннулирование, своё сопоставление столбцов, права и чужая организация.
/// </summary>
public sealed class MarketplaceReportTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 816_300_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    private static readonly string[] WbHeader =
    [
        "№", "Код номенклатуры", "Артикул поставщика", "Название", "Баркод", "Тип документа", "Обоснование для оплаты", "Дата продажи", "Кол-во",
        "Вайлдберриз реализовал Товар (Пр)", "Вознаграждение Вайлдберриз (ВВ), без НДС", "К перечислению Продавцу за реализованный Товар",
        "Услуги по доставке товара покупателю",
    ];

    private static byte[] Wb(params string[][] rows) =>
        SqlTestHost.Spreadsheet.Write([new SheetData("Детализация", WbHeader, rows, new HashSet<int> { 9, 10, 11, 12 })]);

    private static readonly string[][] WbRows =
    [
        ["1", "123456", "SW-RED", "Свитер красный", "", "Продажа", "Продажа", "01.10.2026", "2", "3000", "450", "2550", "0"],
        ["2", "777", "HAT", "Шапка", "HAT-0001", "Продажа", "Продажа", "2026-10-02", "1", "800", "120", "680", "0"],
        ["3", "123456", "SW-RED", "Свитер красный", "", "Возврат", "Возврат", "03.10.2026", "1", "1500", "225", "1275", "0"],
        ["4", "123456", "SW-RED", "Свитер красный", "", "", "Логистика", "03.10.2026", "0", "0", "0", "0", "150"],
        ["5", "999", "SCARF", "Шарф", "", "Продажа", "Продажа", "04.10.2026", "3", "2100", "315", "1785", "0"],
        ["6", "123456", "SW-RED", "Свитер красный", "", "Продажа", "Продажа", "05.10.2026", "много", "1500", "225", "1275", "0"],
        ["", "", "", "Итого", "", "", "", "", "", "", "", "", ""],
    ];

    [SqlFact]
    public async Task Wildberries_report_preview_match_decide_save_without_duplicates_and_cancel()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var file = Wb(WbRows);

        // Предпросмотр: свитер — по связи с Wildberries (код номенклатуры), шапка — по штрихкоду, шарф — «нужно решение»; строка 7 — ошибка числа.
        var preview = await s.Marketplace.PreviewAsync(f.Wb, file);
        Assert.Equal((MarketplaceKind.Wildberries, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 4), (long?)null), (preview.Marketplace, preview.From!.Value, preview.To!.Value, preview.DuplicateId));
        var t = preview.Totals;
        Assert.Equal((6m, 5_900m, 1m, 1_500m, 660m, 150m, 3_740m, 5, 1), (t.SalesQuantity, t.SalesAmount, t.ReturnsQuantity, t.ReturnsAmount, t.Commission, t.Logistics,
            t.Payout, t.Lines, t.Unmatched));
        var error = Assert.Single(preview.Errors);
        Assert.Equal((7, "Кол-во", "много", false), (error.Row, error.Column, error.Value, error.IsDate));
        Assert.Equal((f.Sweater, MarketplaceMatch.ExternalLink), (preview.Lines[0].ItemId!.Value, preview.Lines[0].MatchedBy));
        Assert.Equal((f.Hat, MarketplaceMatch.Barcode), (preview.Lines[1].ItemId!.Value, preview.Lines[1].MatchedBy));
        Assert.Equal((MarketplaceLineKind.Return, MarketplaceLineKind.Service, "Логистика"), (preview.Lines[2].Kind, preview.Lines[3].Kind, preview.Lines[3].Note));
        var scarf = Assert.Single(preview.Unmatched);
        Assert.Equal(("999", "SCARF", 3m, 2_100m), (scarf.ExternalCode, scarf.Article, scarf.Quantity, scarf.Amount));

        // Строки с ошибками — только с подтверждением; решение по шарфу создаёт связь каталога с Wildberries.
        Assert.Equal("marketplace.row_errors", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Marketplace.SaveAsync(f.Wb, file, "wb.xlsx", null, null, null, false))).Code);
        var saved = await s.Marketplace.SaveAsync(f.Wb, file, "wb.xlsx", null, null, new Dictionary<string, long> { [scarf.Key] = f.Scarf }, true);
        Assert.Equal((1, 1), (saved.Resolved, saved.LinksCreated));
        var report = await s.Marketplace.GetAsync(saved.ReportId);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 4), 5, 0, 5_900m, 3_740m, "Wildberries"),
            (report.From, report.To, report.Totals.Lines, report.Totals.Unmatched, report.Totals.SalesAmount, report.Totals.Payout, report.Channel));
        Assert.Equal(MarketplaceMatch.Manual, report.Lines.Single(l => l.Article == "SCARF").MatchedBy);
        Assert.Contains(await s.Db.ExternalLinks.Where(x => x.OrganizationId == f.Org.OrganizationId && x.System == ExternalSystem.Wildberries).ToListAsync(),
            x => x.LocalId == f.Scarf && x.ExternalId == "999" && x.ExternalCode == "SCARF");
        Assert.Equal(1, await s.Db.AuditEntries.CountAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.MarketplaceReportImported));

        // Повтор того же файла (и того же содержимого, сохранённого заново) — без дублей.
        Assert.Equal(saved.ReportId, (await s.Marketplace.PreviewAsync(f.Wb, Wb(WbRows))).DuplicateId);
        Assert.Equal("marketplace.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Marketplace.SaveAsync(f.Wb, Wb(WbRows), "wb-copy.xlsx", null, null, null, true))).Code);
        Assert.Single(await s.Marketplace.ListAsync());

        // Следующий отчёт: шарф находится сам — по созданной связи.
        var next = await s.Marketplace.PreviewAsync(f.Wb, Wb(new[] { "1", "999", "SCARF", "Шарф", "", "Продажа", "Продажа", "10.10.2026", "1", "700", "105", "595", "0" }));
        Assert.Equal((f.Scarf, 0), (next.Lines[0].ItemId!.Value, next.Totals.Unmatched));

        // Excel строк отчёта.
        var xlsx = await s.Marketplace.ExportAsync(saved.ReportId);
        var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
        Assert.Equal(6, sheet.Count);
        Assert.Equal("Продажа", sheet[1][1]);

        // Аннулирование — с причиной; после него тот же файл загружается снова.
        report = await s.Marketplace.GetAsync(saved.ReportId);
        Assert.Equal("marketplace.cancel_reason", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Marketplace.CancelAsync(saved.ReportId, " ", report.RowVersion))).Code);
        await s.Marketplace.CancelAsync(saved.ReportId, "Загружен не тот период", report.RowVersion);
        Assert.Equal(MarketplaceReportStatus.Cancelled, (await s.Marketplace.GetAsync(saved.ReportId)).Status);
        var again = await s.Marketplace.SaveAsync(f.Wb, file, "wb.xlsx", null, null, null, true);
        Assert.NotEqual(saved.ReportId, again.ReportId);
        Assert.Equal(0, (await s.Marketplace.GetAsync(again.ReportId)).Totals.Unmatched); // шарф — уже по связи
    }

    [SqlFact]
    public async Task Ozon_report_with_own_columns_period_and_resolve_after_save()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        // Своё сопоставление: количество — в столбце «Продано, шт».
        await s.Marketplace.SetColumnsAsync(MarketplaceKind.Ozon, new Dictionary<MarketplaceField, string?> { [MarketplaceField.Quantity] = "Продано, шт" });
        Assert.Equal("Продано, шт", (await s.Marketplace.ColumnsAsync(MarketplaceKind.Ozon)).Single(c => c.Field == MarketplaceField.Quantity).Header);
        Assert.Equal(1, await s.Db.AuditEntries.CountAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.MarketplaceColumnsChanged));

        string[] header = ["Отчёт о реализации товаров", "", "", "", "", "", "", "", "", ""];
        string[] columns = ["Артикул", "SKU", "Штрихкод", "Наименование товара", "Продано, шт", "Реализовано на сумму, руб.", "Возвращено клиентом, шт.",
            "Возвращено на сумму, руб.", "Вознаграждение Ozon, руб.", "Итого к начислению, руб."];
        var file = SqlTestHost.Spreadsheet.Write([new SheetData("Отчёт", header,
        [
            columns,
            ["SW-RED", "555", "SW-0001", "Свитер", "4", "6 000,00", "1", "1 500,00", "900", "4 200"],
            ["XYZ", "888", "", "Носки", "10", "1000", "0", "0", "150", "850"],
        ])]);
        var preview = await s.Marketplace.PreviewAsync(f.Ozon, file);
        Assert.Equal((2, (DateOnly?)null), (preview.HeaderRow, preview.From));
        Assert.Equal((14m, 7_000m, 1m, 1_500m, 1_050m, 5_050m, 1), (preview.Totals.SalesQuantity, preview.Totals.SalesAmount, preview.Totals.ReturnsQuantity,
            preview.Totals.ReturnsAmount, preview.Totals.Commission, preview.Totals.Payout, preview.Totals.Unmatched));
        Assert.Equal((f.Sweater, MarketplaceMatch.Barcode), (preview.Lines[0].ItemId!.Value, preview.Lines[0].MatchedBy));

        // В файле нет дат — период обязателен.
        Assert.Equal("marketplace.no_period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Marketplace.SaveAsync(f.Ozon, file, "ozon.xlsx", null, null, null, false))).Code);
        var saved = await s.Marketplace.SaveAsync(f.Ozon, file, "ozon.xlsx", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null, false);
        var report = await s.Marketplace.GetAsync(saved.ReportId);
        Assert.Equal((1, "XYZ"), (report.Totals.Unmatched, report.Unmatched.Single().Article));

        // Решение после сохранения: позиция для строк и связь каталога с Ozon (внешний id — SKU).
        var options = await s.Marketplace.FindItemsAsync("Носк");
        Assert.Equal(f.Socks, options.Single().Id);
        var resolved = await s.Marketplace.ResolveAsync(saved.ReportId, report.Unmatched[0].Key, f.Socks, report.RowVersion);
        Assert.Equal((1, 1), (resolved.Resolved, resolved.LinksCreated));
        Assert.Equal(0, (await s.Marketplace.GetAsync(saved.ReportId)).Totals.Unmatched);
        Assert.True(await s.Db.ExternalLinks.AnyAsync(x => x.OrganizationId == f.Org.OrganizationId && x.System == ExternalSystem.Ozon && x.ExternalId == "888"
                                                          && x.LocalId == f.Socks));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.Marketplace.ResolveAsync(saved.ReportId, "x", f.Socks, report.RowVersion));

        // Файл не той площадки — нет строки заголовков; канал без площадки — отказ.
        Assert.Equal("marketplace.header", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Marketplace.PreviewAsync(f.Ozon, Wb(WbRows)))).Code);
        Assert.Equal("marketplace.channel", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Marketplace.PreviewAsync(f.Site, file))).Code);
    }

    [SqlFact]
    public async Task Channels_rights_and_other_organization()
    {
        var f = await SetUpAsync();
        long report;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var channels = await s.Marketplace.ChannelsAsync();
            var wb = channels.Single(c => c.Id == f.Wb);
            Assert.Equal((MarketplaceKind.Wildberries, FulfillmentScheme.Fbs, "ООО «Вайлдберриз»"), (wb.Marketplace!.Value, wb.Scheme!.Value, wb.Counterparty));
            Assert.Null(channels.Single(c => c.Id == f.Site).Marketplace);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
                s.Marketplace.SetChannelAsync(f.Wb, MarketplaceKind.Wildberries, FulfillmentScheme.Fbo, null, [0, 0, 0, 0, 0, 0, 0, 1]));
            await s.Marketplace.SetChannelAsync(f.Wb, MarketplaceKind.Wildberries, FulfillmentScheme.Fbo, null, wb.RowVersion);
            Assert.Equal(FulfillmentScheme.Fbo, (await s.Marketplace.ChannelsAsync()).Single(c => c.Id == f.Wb).Scheme);
            Assert.Equal("sales.channel.not_marketplace", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Marketplace.SetChannelAsync(f.Site, null, FulfillmentScheme.Fbs, null, null))).Code);
            report = (await s.Marketplace.SaveAsync(f.Wb, Wb(WbRows[0]), "wb.xlsx", null, null, null, false)).ReportId;
        }

        // Старший кладовщик: без права изменять продажи — ни загрузки, ни настроек; без права отчётов — ни списка.
        await using (var k = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Marketplace.PreviewAsync(f.Wb, Wb(WbRows[0])));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Marketplace.SetChannelAsync(f.Site, MarketplaceKind.Other, null, null, null));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Marketplace.ListAsync());
        }

        var other = await CreateOrgAsync();
        long otherItem;
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            otherItem = await o.Catalog.CreateItemAsync(new ItemCommand("Ч-1", "Чужая позиция", ItemType.Finished,
                (await o.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id, null));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.GetAsync(report));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.ExportAsync(report));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.PreviewAsync(f.Wb, Wb(WbRows[0])));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.SetChannelAsync(f.Wb, MarketplaceKind.Ozon, null, null, null));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.ResolveAsync(report, "x", f.Hat, [0, 0, 0, 0, 0, 0, 0, 1]));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Marketplace.CancelAsync(report, "чужой", [0, 0, 0, 0, 0, 0, 0, 1]));
            Assert.Empty(await o.Marketplace.ListAsync());
        }

        // Ограничения в SQL Server: строка с чужой позицией и отчёт с тем же отпечатком не записываются.
        await using (var db = host.NewDb())
        {
            var fp = await db.MarketplaceReports.Where(r => r.Id == report).Select(r => r.Fingerprint).SingleAsync();
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[marketplace_reports] (OrganizationId, ChannelId, Marketplace, PeriodFrom, PeriodTo, FileName, Fingerprint, Status, SalesQuantity, SalesAmount, ReturnsQuantity, ReturnsAmount, Commission, Logistics, Payout, LineCount, UnmatchedCount, CreatedByUserId, CreatedAtUtc) "
                + "VALUES ({0}, {1}, 1, '2026-10-01', '2026-10-01', N'x', {2}, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, {3}, SYSUTCDATETIME())",
                f.Org.OrganizationId, f.Wb, fp, f.Org.OwnerUserId));
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[marketplace_report_lines] SET [ItemId] = {0}, [MatchedBy] = 3 WHERE [ReportId] = {1}", otherItem, report));
        }
    }

    private sealed record Fixture(CreatedOrganization Org, long Sweater, long Hat, long Scarf, long Socks, long Wb, long Ozon, long Site, long Senior);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long sweater, hat, scarf, socks, wb, ozon, site, senior;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер красный", ItemType.Finished, unit, null));
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.Finished, unit, null));
            scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, unit, null));
            socks = await s.Catalog.CreateItemAsync(new ItemCommand("НС-1", "Носки", ItemType.Finished, unit, null));
            await s.Nomenclature.AddBarcodeAsync(hat, "HAT-0001", BarcodeType.Code128);
            await s.Nomenclature.AddBarcodeAsync(sweater, "SW-0001", BarcodeType.Code128);
            var partner = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Вайлдберриз»", null, null, false, true, null));
            wb = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Wildberries");
            ozon = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Ozon");
            site = await s.SalesSettings.CreateLookupAsync(LookupKind.SalesChannel, "Сайт");
            await s.Marketplace.SetChannelAsync(wb, MarketplaceKind.Wildberries, FulfillmentScheme.Fbs, partner, null);
            await s.Marketplace.SetChannelAsync(ozon, MarketplaceKind.Ozon, FulfillmentScheme.Fbo, null, null);
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == senior)).Activate();
            // Связь свитера с карточкой Wildberries: код номенклатуры 123456, артикул продавца SW-RED.
            db.ExternalLinks.Add(ExternalLink.Create(org.OrganizationId, ExternalSystem.Wildberries, ExternalObjectType.Item, sweater, "123456", "SW-RED", null, null,
                DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        return new Fixture(org, sweater, hat, scarf, socks, wb, ozon, site, senior);
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
