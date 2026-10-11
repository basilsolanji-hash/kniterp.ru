using System.Xml.Linq;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Exchange;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D105: выгрузка в 1С (EnterpriseData 1.8) — состав, суммы из заказов, постоянные ссылки, права.</summary>
public sealed class EnterpriseDataTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 790_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly XNamespace Ed = "http://v8.1c.ru/edi/edi_stnd/EnterpriseData/1.8";

    [SqlFact]
    public async Task Exports_documents_with_order_prices_and_stable_references()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"ООО «Трикотаж {inn}»", $"Трикотаж {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        long receipt, shipment, transfer, reversed;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            var shop = await s.Warehouses.CreateWarehouseAsync("Цех", null);
            var wool = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", "7707083893", "773601001", true, true, null));
            var reasons = await s.Db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && !r.RequiresComment)
                .GroupBy(r => r.Kind).Select(g => new { g.Key, Id = g.Min(r => r.Id) }).ToDictionaryAsync(x => x.Key, x => x.Id);

            var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, yarn, null, null, true, null));
            await s.Purchases.SetOrderLineAsync(po, wool, 10m, 1200m, 20m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            receipt = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(yarn, null, supplier, reasons[StockOperationKind.Receipt], Day, null, po), [(wool, 4m)]);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            var so = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, supplier, yarn, null, null, true, null));
            await s.Sales.SetOrderLineAsync(so, wool, 2m, 3000m, 20m, (await s.Sales.GetOrderAsync(so)).RowVersion);
            await s.Sales.ConfirmOrderAsync(so, (await s.Sales.GetOrderAsync(so)).RowVersion);
            shipment = await s.Documents.CreateWithLinesAsync(StockOperationKind.Shipment,
                new StockDocumentHeader(yarn, null, supplier, reasons[StockOperationKind.Shipment], Day, null, SalesOrderId: so), [(wool, 2m)]);
            await s.Documents.PostAsync(shipment, (await s.Documents.GetAsync(shipment)).RowVersion);

            transfer = await s.Documents.CreateWithLinesAsync(StockOperationKind.Transfer,
                new StockDocumentHeader(yarn, shop, null, reasons[StockOperationKind.Transfer], Day, null), [(wool, 1m)]);
            await s.Documents.PostAsync(transfer, (await s.Documents.GetAsync(transfer)).RowVersion);

            reversed = await s.Documents.CreateWithLinesAsync(StockOperationKind.Receipt,
                new StockDocumentHeader(yarn, null, supplier, reasons[StockOperationKind.Receipt], Day, null), [(wool, 1m)]);
            await s.Documents.PostAsync(reversed, (await s.Documents.GetAsync(reversed)).RowVersion);
            await s.Documents.ReverseAsync(reversed, "Ошибка", (await s.Documents.GetAsync(reversed)).RowVersion);

            var file = await s.Exchange.ExportAsync(Day.AddDays(-1), Day.AddDays(1), null);
            Assert.EndsWith(".xml", file.FileName);
            Assert.Equal(3, file.Summary.DocumentsTotal);
            Assert.Equal(1, file.Summary.SkippedReversed);
            var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(file.Xml));
            Assert.Equal("Message", xml.Root!.Name.LocalName);
            var body = xml.Root.Element(Ed + "Body")!;
            Assert.Single(body.Elements(Ed + "Справочник.Номенклатура"));
            Assert.Single(body.Elements(Ed + "Справочник.Контрагенты"));
            Assert.Equal(2, body.Elements(Ed + "Справочник.Склады").Count());
            Assert.Single(body.Elements(Ed + "Справочник.Организации"));

            // Поступление: 4 из 10 кг по 1200 с НДС → 4800, НДС 800 (20/120).
            var inDoc = Assert.Single(body.Elements(Ed + "Документ.ПоступлениеТоваровУслуг"));
            Assert.Equal("4800.00", inDoc.Element(Ed + "Сумма")!.Value);
            var row = inDoc.Element(Ed + "Товары")!.Element(Ed + "Строка")!;
            Assert.Equal(("4", "1200.00", "НДС20", "800.00"),
                (row.Element(Ed + "Количество")!.Value, row.Element(Ed + "Цена")!.Value, row.Element(Ed + "СтавкаНДС")!.Value, row.Element(Ed + "СуммаНДС")!.Value));
            Assert.Equal("7707083893", inDoc.Element(Ed + "Контрагент")!.Element(Ed + "ИНН")!.Value);
            Assert.Equal("6000.00", body.Element(Ed + "Документ.РеализацияТоваровУслуг")!.Element(Ed + "Сумма")!.Value);
            var move = body.Element(Ed + "Документ.ПеремещениеТоваров")!;
            Assert.Equal("Цех", move.Element(Ed + "СкладПолучатель")!.Element(Ed + "Наименование")!.Value);
            Assert.Null(move.Element(Ed + "Сумма"));

            // Повторная выгрузка — те же ссылки (1С обновит документ, а не создаст дубль).
            var again = XDocument.Parse(System.Text.Encoding.UTF8.GetString((await s.Exchange.ExportAsync(Day, Day, null)).Xml));
            string RefOf(XDocument d) => d.Root!.Element(Ed + "Body")!.Element(Ed + "Документ.ПоступлениеТоваровУслуг")!
                .Element(Ed + "КлючевыеСвойства")!.Element(Ed + "Ссылка")!.Value;
            Assert.Equal(RefOf(xml), RefOf(again));
            Assert.Equal(EnterpriseDataExportService.StableId(org.OrganizationId, "doc", receipt).ToString(), RefOf(xml));
            Assert.Equal(5, Guid.Parse(RefOf(xml)).Version);

            Assert.Equal("exchange.period", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Exchange.ExportAsync(Day, Day.AddDays(-1), null))).Code);
            Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == KnitErp.Domain.Audit.AuditActions.ExchangeExported));
        }

        // Кладовщик без права «Цены и суммы» выгрузку не получает.
        long keeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            keeper = (await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == keeper)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(keeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Exchange.ExportAsync(Day, Day, null));
        }
    }

    private static string NextValidInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }
}
