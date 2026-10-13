using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Purchasing;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D351 (сессия 4, Р5): заказ поставщику подставляет цену этого поставщика из его последнего заказа, показывает его артикул
/// и подсказывает основного поставщика большинства позиций.
/// </summary>
public sealed class CatSupplierOrderTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 958_000_000;
    private static readonly DateOnly Day = new(2026, 10, 1);

    [SqlFact]
    public async Task Purchase_order_uses_this_supplier_price_article_and_hints_main_supplier()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var kg = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, kg, null));
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var a = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа А»", null, null, true, false, null));
        var b = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа Б»", null, null, true, false, null));
        await s.Suppliers.SetAsync(yarn, a, "A-100", "Меринос 100", true);

        // Прошлый заказ поставщику А по 500 без НДС.
        var old = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, a, store, null, null, false, null));
        await s.Purchases.SetOrderLineAsync(old, yarn, 10, 500m, null, (await s.Purchases.GetOrderAsync(old)).RowVersion);
        await s.Purchases.ConfirmOrderAsync(old, (await s.Purchases.GetOrderAsync(old)).RowVersion);

        var forA = (await s.Purchases.GetOptionsAsync(Day, a)).Items.Single(i => i.Id == yarn);
        Assert.Equal((500m, "A-100"), (forA.Price, forA.SupplierArticle));
        var forB = (await s.Purchases.GetOptionsAsync(Day, b)).Items.Single(i => i.Id == yarn);
        Assert.Null(forB.SupplierArticle);
        Assert.NotEqual(500m, forB.Price); // у Б позиции нет — закупочная цена карточки

        // Строка заказа А — с артикулом поставщика; заказ Б — подсказка про основного поставщика А.
        Assert.Equal("A-100", (await s.Purchases.GetOrderAsync(old)).Lines.Single().SupplierArticle);
        var toB = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, b, store, null, null, false, null));
        await s.Purchases.SetOrderLineAsync(toB, yarn, 5, 480m, null, (await s.Purchases.GetOrderAsync(toB)).RowVersion);
        var dto = await s.Purchases.GetOrderAsync(toB);
        Assert.Equal("ООО «Пряжа А»", dto.MainSupplierHint);
        Assert.Null(dto.Lines.Single().SupplierArticle);
        Assert.Null((await s.Purchases.GetOrderAsync(old)).MainSupplierHint);
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
