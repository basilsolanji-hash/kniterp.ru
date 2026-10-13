using System.Diagnostics;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace KnitErp.IntegrationTests;

/// <summary>
/// ТЗ продаж, §9 (D1166): аналитика за год по 100 000 строк отгрузок строится не дольше 3 с — агрегирование в SQL Server и индексы.
/// Данные: 1 000 заказов по 10 строк и по 10 отгрузок на заказ (100 000 строк), размножены INSERT … SELECT по образцу проведённой отгрузки.
/// </summary>
public sealed class SalesReportPerformanceTests(SqlTestHost host, ITestOutputHelper output) : IClassFixture<SqlTestHost>
{
    private const int Orders = 1_000;
    private const int ShipmentsPerOrder = 10;
    private const int Items = 10;
    private static int _innSeed = 816_600_000;

    [SqlFact]
    public async Task Hundred_thousand_shipment_lines_report_under_three_seconds()
    {
        var org = await CreateOrgAsync();
        long templateOrder, templateDoc;
        var customers = new List<long>();
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            for (var i = 0; i < 3; i++)
            {
                customers.Add(await s.Counterparties.CreateAsync(new CounterpartyCommand($"ООО «Покупатель {i}»", null, null, false, true, null)));
            }

            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                    .Select(r => (long?)r.Id).FirstAsync();
            }

            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
            var items = new List<long>();
            for (var i = 0; i < Items; i++)
            {
                var item = await s.Catalog.CreateItemAsync(new ItemCommand($"ПФ-{i}", $"Позиция {i}", ItemType.Finished, unit, null));
                items.Add(item);
                await s.Documents.SetLineAsync(receipt, item, 1000, (await s.Documents.GetAsync(receipt)).RowVersion);
            }

            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            templateOrder = await s.Sales.CreateOrderAsync(new SalesOrderHeader(new DateOnly(2026, 10, 1), customers[0], store, null, null, true, null));
            for (var i = 0; i < Items; i++)
            {
                await s.Sales.SetOrderLineAsync(templateOrder, items[i], 10, 100m + i, 20m, (await s.Sales.GetOrderAsync(templateOrder)).RowVersion);
            }

            await s.Sales.ConfirmOrderAsync(templateOrder, (await s.Sales.GetOrderAsync(templateOrder)).RowVersion);
            templateDoc = await s.Sales.CreateShipmentAsync(templateOrder);
            await s.Documents.PostAsync(templateDoc, (await s.Documents.GetAsync(templateDoc)).RowVersion);
        }

        await using (var db = host.NewDb())
        {
            db.Database.SetCommandTimeout(600);
            var o = org.OrganizationId;
            await CloneAsync(db, "sales_orders", new Dictionary<string, string>
                {
                    ["Number"] = "CONCAT(N'P-', n.n)",
                    ["CustomerId"] = $"CASE n.n % 3 WHEN 0 THEN {customers[0]} WHEN 1 THEN {customers[1]} ELSE {customers[2]} END",
                    ["OrderDate"] = "DATEADD(day, n.n % 365, '2025-10-01')",
                },
                $"(SELECT TOP ({Orders}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) n CROSS JOIN [kniterp].[sales_orders] t WHERE t.[Id] = {templateOrder}");
            await CloneAsync(db, "sales_order_lines", new Dictionary<string, string> { ["OrderId"] = "x.Id" },
                $"[kniterp].[sales_orders] x CROSS JOIN [kniterp].[sales_order_lines] t WHERE t.[OrderId] = {templateOrder} AND x.OrganizationId = {o} AND x.Number LIKE N'P-%'");
            await CloneAsync(db, "stock_documents", new Dictionary<string, string>
                {
                    ["Number"] = "CONCAT(N'PS-', x.Id, N'-', k.k)",
                    ["SalesOrderId"] = "x.Id",
                    ["CounterpartyId"] = "x.CustomerId",
                    ["DocumentDate"] = "DATEADD(day, (x.Id * 7 + k.k) % 365, '2025-10-01')",
                    ["Status"] = "1",
                    ["PostedByUserId"] = "NULL",
                    ["PostedAtUtc"] = "NULL",
                },
                $"[kniterp].[sales_orders] x CROSS JOIN (SELECT TOP ({ShipmentsPerOrder}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_objects) k "
                + $"CROSS JOIN [kniterp].[stock_documents] t WHERE t.[Id] = {templateDoc} AND x.OrganizationId = {o} AND x.Number LIKE N'P-%'");
            await CloneAsync(db, "stock_document_lines", new Dictionary<string, string> { ["DocumentId"] = "x.Id", ["Quantity"] = "1" },
                $"[kniterp].[stock_documents] x CROSS JOIN [kniterp].[stock_document_lines] t WHERE t.[DocumentId] = {templateDoc} AND x.OrganizationId = {o} AND x.Number LIKE N'PS-%'");
            var post =
                "UPDATE x SET x.[Status] = 2, x.[PostedByUserId] = t.[PostedByUserId], x.[PostedAtUtc] = t.[PostedAtUtc] FROM [kniterp].[stock_documents] x " +
                $"CROSS JOIN [kniterp].[stock_documents] t WHERE t.[Id] = {templateDoc} AND x.OrganizationId = {o} AND x.Number LIKE N'PS-%'";
            await db.Database.ExecuteSqlRawAsync(post);
            var count = $"SELECT COUNT(*) AS Value FROM [kniterp].[stock_document_lines] l JOIN [kniterp].[stock_documents] d ON d.Id = l.DocumentId WHERE d.OrganizationId = {o}";
            var lines = await db.Database.SqlQueryRaw<int>(count).SingleAsync();
            Assert.True(lines > Orders * ShipmentsPerOrder * Items, $"строк {lines}");
        }

        await using var c = host.As(org.OwnerUserId, org.OrganizationId);
        var year = new SalesReportFilter(new DateOnly(2025, 10, 1), new DateOnly(2026, 10, 31));
        await c.SalesReports.ReportAsync(year); // прогрев плана запроса

        async Task<T> Timed<T>(string what, Func<Task<T>> run, int limitMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            var result = await run();
            sw.Stop();
            output.WriteLine($"{what}: {sw.ElapsedMilliseconds} мс");
            Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what}: {sw.ElapsedMilliseconds} мс");
            return result;
        }

        var byCustomer = await Timed("по покупателям", () => c.SalesReports.ReportAsync(year));
        Assert.Equal(3, byCustomer.Rows.Count);
        Assert.Equal(Orders * ShipmentsPerOrder * Items + Items * 10, byCustomer.Total.Quantity);
        await Timed("по позициям", () => c.SalesReports.ReportAsync(year with { By = SalesDimension.Item }));
        var byMonth = await Timed("по месяцам", () => c.SalesReports.ReportAsync(year with { By = SalesDimension.Period }));
        Assert.True(byMonth.Rows.Count >= 12);
        await Timed("по неделям", () => c.SalesReports.ReportAsync(year with { By = SalesDimension.Period, Step = SalesPeriodStep.Week }));
        await Timed("по менеджерам", () => c.SalesReports.ReportAsync(year with { By = SalesDimension.Manager }));
        await Timed("ABC позиций", () => c.SalesReports.AbcAsync(year, AbcSubject.Item));
        await Timed("воронка", () => c.SalesReports.FunnelAsync(year));
        await Timed("план-факт", () => c.SalesReports.PlanFactAsync(new DateOnly(2026, 3, 1)));
        // Книга Excel — пять отчётов сразу (разрез, два ABC, план-факт, воронка): до 3 с на каждый, всего — до 6 с.
        await Timed("Excel", () => c.SalesReports.ExportAsync(year, SqlTestHost.Spreadsheet), 6000);
    }

    /// <summary>INSERT … SELECT по образцу строки t: все столбцы таблицы, кроме identity, вычисляемых и rowversion; overrides — свои выражения.</summary>
    private static async Task CloneAsync(KnitErp.Infrastructure.Persistence.KnitErpDbContext db, string table, IReadOnlyDictionary<string, string> overrides, string from)
    {
        var query = $"SELECT c.name AS Value FROM sys.columns c WHERE c.object_id = OBJECT_ID('kniterp.{table}') AND c.is_identity = 0 AND c.is_computed = 0 "
                    + "AND TYPE_NAME(c.system_type_id) <> 'timestamp'";
        var columns = await db.Database.SqlQueryRaw<string>(query).ToListAsync();
        var sql = $"INSERT INTO [kniterp].[{table}] ({string.Join(", ", columns.Select(c => $"[{c}]"))}) " +
                  $"SELECT {string.Join(", ", columns.Select(c => overrides.TryGetValue(c, out var e) ? e : $"t.[{c}]"))} FROM {from}";
        await db.Database.ExecuteSqlRawAsync(sql);
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
