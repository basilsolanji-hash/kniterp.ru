using System.Text;
using System.Xml.Linq;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D1140: XML УПД (формат ФНС 5.03, функция СЧФДОП) по проведённой отгрузке — для загрузки в оператора ЭДО.</summary>
public sealed class SalesUpdXmlTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 818_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Posted_shipment_gives_fns_upd_xml()
    {
        var org = await CreateOrgAsync();
        long ship, noInnShip;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер «Север»", ItemType.Finished, unit, null));
            var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", Inn(), null, false, true, null, Address: "г. Москва, ул. Тверская, 1"));
            var noInn = await s.Counterparties.CreateAsync(new CounterpartyCommand("Розничный покупатель", null, null, false, true, null));
            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt).Select(r => (long?)r.Id).FirstAsync();
            }

            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(receipt, sweater, 10, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
            ship = await ShipAsync(s, customer, store, sweater);
            noInnShip = await ShipAsync(s, noInn, store, sweater);

            var file = await s.Print.UpdXmlAsync(ship, null, host.Clock.UtcNow);
            Assert.StartsWith("ON_NSCHFDOPPR_", file.FileName);
            Assert.EndsWith(".xml", file.FileName);
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"windows-1251\"?>", Encoding.GetEncoding(1251).GetString(file.Content));
            var xml = XDocument.Load(new MemoryStream(file.Content));
            var root = xml.Root!;
            Assert.Equal(("Файл", "5.03"), (root.Name.LocalName, (string?)root.Attribute("ВерсФорм")));
            Assert.Equal(file.FileName[..^4], (string?)root.Attribute("ИдФайл"));
            var docEl = root.Element("Документ")!;
            Assert.Equal(("1115131", "СЧФДОП"), ((string?)docEl.Attribute("КНД"), (string?)docEl.Attribute("Функция")));
            var invoice = docEl.Element("СвСчФакт")!;
            Assert.NotNull(invoice.Element("СвПрод")?.Element("ИдСв")?.Element("СвЮЛУч")?.Attribute("ИННЮЛ"));
            Assert.Equal("г. Москва, ул. Тверская, 1", (string?)invoice.Element("СвПокуп")!.Element("Адрес")!.Element("АдрИнф")!.Attribute("АдрТекст"));
            Assert.Equal("643", (string?)invoice.Element("ДенИзм")!.Attribute("КодОКВ"));
            var line = docEl.Element("ТаблСчФакт")!.Element("СведТов")!;
            Assert.Equal(("1", "Свитер «Север»", "4"), ((string?)line.Attribute("НомСтр"), (string?)line.Attribute("НаимТов"), (string?)line.Attribute("КолТов")));
            // Суммы XML совпадают с печатной формой УПД.
            var upd = await s.Print.UpdAsync(ship);
            var total = docEl.Element("ТаблСчФакт")!.Element("ВсегоОпл")!;
            Assert.Equal(upd.Total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), (string?)total.Attribute("СтТовУчНалВсего"));
            Assert.Equal(upd.TotalWithoutVat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), (string?)total.Attribute("СтТовБезНДСВсего"));
            Assert.NotNull(docEl.Element("Подписант")?.Element("ФИО")?.Attribute("Фамилия"));

            Assert.Equal("sales.upd_xml.inn", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Print.UpdXmlAsync(noInnShip, null, host.Clock.UtcNow))).Code);
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Print.UpdXmlAsync(ship, null, host.Clock.UtcNow));
        }
    }

    private static async Task<long> ShipAsync(Services s, long customer, long store, long item)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, store, null, "Договор № 7", true, null));
        await s.Sales.SetOrderLineAsync(order, item, 4, 1220m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        return ship;
    }

    private static string Inn()
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

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        var inn = Inn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
