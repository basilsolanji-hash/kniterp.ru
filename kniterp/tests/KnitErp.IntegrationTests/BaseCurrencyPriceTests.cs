using System.Text;
using System.Xml.Linq;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Сессия № 1, этап 2а (D151–D152): валюта цен номенклатуры. В заказ — в рублях по курсу ЦБ на дату заказа с пометкой исходной цены
/// и курса; курс не на дату — последний известный с пометкой; курса нет — подстановка запрещена. Фид и прайс-лист — только в рублях,
/// без курса позиция не выгружается. Excel — столбцы «Валюта» и «Валюта закупочной цены». Закупочная цена в валюте — подсказка заказа
/// поставщику по курсу. Кнопка «Обновить курсы ЦБ сейчас». Курсы и данные вымышленные.
/// </summary>
public sealed class BaseCurrencyPriceTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 963_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Sales_order_takes_foreign_price_in_rubles_by_rate_on_order_date()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await ImportCbrAsync("08.10.2026", ("USD", 1, "92,1500"));
        var usd = await ItemAsync(s, f, "USD-1", 12m, "USD");
        var cny = await ItemAsync(s, f, "CNY-1", 100m, "CNY"); // курса юаня нет вовсе

        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        await s.Sales.AddOrderLineAsync(order, usd, 2, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        Assert.Equal(1105.8m, line.Price); // 12 × 92,15; курс на 08.10 — последний известный к 09.10
        Assert.Contains("12 $ по 92,15", line.PriceNote);
        Assert.Contains("курс на 08.10.2026", line.PriceNote);
        Assert.True((await s.Sales.QuoteForOrderAsync(order, [usd])).Items[usd].StaleRate);

        // Курса нет — подстановка запрещена с понятным сообщением; цену в рублях можно ввести вручную.
        var missing = await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Sales.AddOrderLineAsync(order, cny, 1, (await s.Sales.GetOrderAsync(order)).RowVersion));
        Assert.Equal("currency.rate.missing", missing.Code);
        Assert.True((await s.Sales.QuoteForOrderAsync(order, [cny])).Items[cny].NoRate);

        // Курс на дату заказа — без пометки «курс на…»; ручная цена в рублях снимает пометку о валюте.
        await ImportCbrAsync("09.10.2026", ("USD", 1, "93,0000"));
        Assert.Equal(1, await s.Sales.RepriceOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion));
        line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        Assert.Equal((1116m, "12 $ по 93,00"), (line.Price, line.PriceNote));
        await s.Sales.SetOrderLineAsync(order, usd, 2, 1000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Null((await s.Sales.GetOrderAsync(order)).Lines.Single().PriceNote);
    }

    [SqlFact]
    public async Task Feed_price_list_and_purchase_hint_are_in_rubles()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await ImportCbrAsync("09.10.2026", ("EUR", 1, "100,0000"));
        var eur = await ItemAsync(s, f, "EUR-1", 15.5m, "EUR");
        var rub = await ItemAsync(s, f, "RUB-1", 700m, null);
        var noRate = await ItemAsync(s, f, "TRY-1", 50m, "TRY");

        // Фид: цена в рублях, без валюты площадки; позиция без курса не выгружается.
        var feed = await s.Feeds.CreateAsync(new CatalogFeedCommand("Маркет", f.PriceType, null, null, false));
        var xml = XDocument.Parse(Encoding.UTF8.GetString((await s.Feeds.BuildAsync(feed.Key, "https://shop.test/"))!.Xml));
        var offers = xml.Descendants("offer").ToDictionary(o => (string)o.Attribute("id")!, o => (string)o.Element("price")!);
        Assert.Equal("1550", offers[eur.ToString()]);
        Assert.Equal("700", offers[rub.ToString()]);
        Assert.False(offers.ContainsKey(noRate.ToString()));
        Assert.All(xml.Descendants("currencyId"), c => Assert.Equal("RUR", c.Value));

        // Прайс-лист (Excel для площадок) — так же.
        var list = await s.PriceList.BuildAsync(new PriceListFilter(f.PriceType));
        Assert.Equal(1550m, list.Rows.Single(r => r.ItemId == eur).Price);
        Assert.DoesNotContain(list.Rows, r => r.ItemId == noRate);

        // Закупочная цена 10 € — подсказка в заказе поставщику 1 000 ₽ с пометкой.
        var item = await s.Nomenclature.GetItemAsync(eur);
        await s.Nomenclature.SaveItemDetailsAsync(eur, item.Details with { PurchasePrice = 10m, PurchaseCurrency = "EUR" }, new Dictionary<long, string?>(), item.RowVersion);
        var options = await s.Purchases.GetOptionsAsync(Day, null);
        var hint = options.Items.Single(i => i.Id == eur);
        Assert.Equal((1000m, "10 € по 100,00"), (hint.Price!.Value, hint.PriceNote));
    }

    [SqlFact]
    public async Task Excel_has_currency_columns_and_rejects_unknown_currency()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var usd = await ItemAsync(s, f, "XL-1", 12m, "USD");
        var exported = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(await s.ItemExchange.ExportAsync()), 100);
        var header = exported[0].ToList();
        var row = exported.Skip(1).Single(r => r[0] == "XL-1");
        Assert.Equal("USD", row[header.IndexOf(ItemExchangeService.CurrencyColumn)]);
        Assert.Equal("RUB", row[header.IndexOf(ItemExchangeService.PurchaseCurrencyColumn)]);

        // Файлом: валюта цен — EUR; неизвестная валюта — ошибка строки.
        var priceColumn = ItemExchangeService.PricePrefix + (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Name;
        IReadOnlyList<string> cols = [.. ItemExchangeService.Columns, priceColumn, ItemExchangeService.CurrencyColumn];
        var file = SqlTestHost.Spreadsheet.Write([new SheetData("Номенклатура", cols,
            [["XL-1", "Позиция XL-1", "Готовая продукция", "шт", "", "13", "eur"], ["XL-2", "Позиция XL-2", "Готовая продукция", "шт", "", "5", "XYZ"]])]);
        var plan = await s.ItemExchange.PreviewAsync(new MemoryStream(file));
        Assert.Equal(1, plan.ErrorRows);
        await s.ItemExchange.ApplyAsync(plan.Rows.Where(r => r.Errors.Count == 0).ToList(), plan.Bundles);
        var price = (await s.Nomenclature.GetItemAsync(usd)).Prices.Single(p => p.IsDefault);
        Assert.Equal((13m, "EUR"), (price.Price!.Value, price.CurrencyCode));

        // Карточка: валюта меняется без смены цены; неизвестная — отказ.
        await s.Nomenclature.SetPricesAsync(usd, new Dictionary<long, decimal?> { [f.PriceType] = 13m }, default, new Dictionary<long, string> { [f.PriceType] = "CNY" });
        Assert.Equal("CNY", (await s.Nomenclature.GetItemAsync(usd)).Prices.Single(p => p.IsDefault).CurrencyCode);
        Assert.Equal("catalog.price.currency", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SetPricesAsync(usd, new Dictionary<long, decimal?> { [f.PriceType] = 13m }, default, new Dictionary<long, string> { [f.PriceType] = "ABC" }))).Code);
    }

    [SqlFact]
    public async Task Refresh_cbr_rates_now_needs_rights_and_is_throttled()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        Assert.False(s.CurrencyRates.CanRefresh); // в тестовом хосте источника ЦБ нет
        Assert.Equal("currency.cbr.disabled", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.CurrencyRates.RefreshAsync())).Code);

        var user = new TestUser { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId, ClientAddress = host.ClientAddress };
        var service = new CurrencyRateService(s.Db, new KnitErp.Application.Access.AccessGuard(s.Db, user, host.Clock), user, host.Clock, new FakeCbr());
        var (_, changed) = await service.RefreshAsync();
        Assert.True(changed >= 1);
        Assert.Equal("currency.cbr.too_often", (await Assert.ThrowsAsync<BusinessRuleException>(() => service.RefreshAsync())).Code);
    }

    [SqlFact]
    public void Return_path_for_close_button_is_local_only()
    {
        Assert.Equal("money", ReturnPath.Safe("money"));
        Assert.Equal("purchase-orders/5?tab=1", ReturnPath.Safe("/purchase-orders/5?tab=1"));
        Assert.Null(ReturnPath.Safe("https://evil.test"));
        Assert.Null(ReturnPath.Safe("//evil.test"));
        Assert.Null(ReturnPath.Safe("money');alert(1);//"));
        Assert.Null(ReturnPath.Safe(" "));
    }

    private sealed class FakeCbr : ICbrRateSource
    {
        public bool Enabled => true;

        public Task<byte[]> FetchAsync(DateOnly date, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetBytes(
            $"<ValCurs Date=\"{date:dd.MM.yyyy}\"><Valute><CharCode>KZT</CharCode><Nominal>100</Nominal><Value>17,1000</Value></Valute></ValCurs>"));
    }

    private async Task ImportCbrAsync(string date, params (string Code, int Nominal, string Value)[] rows)
    {
        await using var db = host.NewDb();
        await new CbrRateImporter(db, host.Clock).ImportAsync(Encoding.UTF8.GetBytes($"<ValCurs Date=\"{date}\">" +
            string.Concat(rows.Select(r => $"<Valute><CharCode>{r.Code}</CharCode><Nominal>{r.Nominal}</Nominal><Value>{r.Value}</Value></Valute>")) + "</ValCurs>"));
    }

    private static async Task<long> ItemAsync(Services s, Fixture f, string code, decimal price, string? currency)
    {
        var id = await s.Catalog.CreateItemAsync(new ItemCommand(code, "Позиция " + code, ItemType.Finished, f.Piece, null));
        await s.Nomenclature.SetPricesAsync(id, new Dictionary<long, decimal?> { [f.PriceType] = price }, default,
            currency is null ? null : new Dictionary<long, string> { [f.PriceType] = currency });
        return id;
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Customer, long Piece, long PriceType);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var type = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        return new Fixture(org, store, customer, piece, type);
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
