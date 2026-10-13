using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D1142: прайс-лист по виду цены, группе с подгруппами и наличию; остатки — только со складскими правами (D118).</summary>
public sealed class SalesPriceListTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Price_list_filters_by_type_group_and_stock()
    {
        var org = await CreateOrgAsync();
        long manager, wholesale, knit, sweaters;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            knit = await s.Nomenclature.CreateGroupAsync(null, "Трикотаж");
            sweaters = await s.Nomenclature.CreateGroupAsync(knit, "Свитеры");
            var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null, GroupId: sweaters));
            var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШР-1", "Шарф", ItemType.Finished, unit, null, GroupId: knit));
            var other = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, unit, null));
            _ = await s.Catalog.CreateItemAsync(new ItemCommand("БЦ-1", "Без цены", ItemType.Finished, unit, null, GroupId: knit));
            var baseType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            wholesale = await s.Nomenclature.CreatePriceTypeAsync("Опт", true);
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [baseType] = 3000, [wholesale] = 2500 });
            await s.Nomenclature.SetPricesAsync(scarf, new Dictionary<long, decimal?> { [baseType] = 900 });
            await s.Nomenclature.SetPricesAsync(other, new Dictionary<long, decimal?> { [baseType] = 400 });

            long? reason;
            await using (var db = host.NewDb())
            {
                reason = await db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt).Select(r => (long?)r.Id).FirstAsync();
            }

            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
            await s.Documents.SetLineAsync(receipt, sweater, 7, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            // Основной вид цены: три позиции с ценой (без цены — нет), группы подзаголовками, остаток виден Владельцу.
            var all = await s.PriceList.BuildAsync(new PriceListFilter());
            Assert.Equal(["ПР-1", "СВ-1", "ШР-1"], all.Rows.Select(r => r.Code).Order());
            Assert.True(all.StockShown);
            Assert.Equal(7m, all.Rows.Single(r => r.Code == "СВ-1").Stock);

            // Группа «Трикотаж» — с подгруппой «Свитеры»; «только в наличии» — свитер.
            Assert.Equal(["СВ-1", "ШР-1"], (await s.PriceList.BuildAsync(new PriceListFilter(GroupId: knit))).Rows.Select(r => r.Code).Order());
            Assert.Equal(["СВ-1"], (await s.PriceList.BuildAsync(new PriceListFilter(GroupId: knit, InStockOnly: true))).Rows.Select(r => r.Code));

            // Вид цены «Опт»: только свитер, 2 500.
            var opt = await s.PriceList.BuildAsync(new PriceListFilter(wholesale));
            Assert.Equal(("Опт", 2500m), (opt.PriceType, opt.Rows.Single().Price));

            // Excel и PDF.
            var xlsx = await s.PriceList.ExcelAsync(new PriceListFilter(), SqlTestHost.Spreadsheet, id => $"https://erp.test/catalog/photos/{id}");
            var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
            Assert.Equal(["Группа", "Код", "Артикул", "Наименование", "Ед."], sheet[0].Take(5));
            Assert.Equal(4, sheet.Count);
            var pdf = SqlTestHost.PdfRenderer.PriceList(all);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            await db.SaveChangesAsync();
        }

        // Менеджер продаж без складских прав: цены видит, остатков нет, отбор «в наличии» отклонён.
        await using (var m = host.As(manager, org.OrganizationId))
        {
            var list = await m.PriceList.BuildAsync(new PriceListFilter());
            Assert.False(list.StockShown);
            Assert.All(list.Rows, r => Assert.Null(r.Stock));
            Assert.True(list.PhotosShown);
            Assert.Equal("sales.price_list.stock",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => m.PriceList.BuildAsync(new PriceListFilter(InStockOnly: true)))).Code);
            Assert.False((await m.PriceList.OptionsAsync()).CanSeeStock);
        }

        // Чужая организация: вид цены и группа — как несуществующие.
        var foreign = await CreateOrgAsync();
        await using (var o = host.As(foreign.OwnerUserId, foreign.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.PriceList.BuildAsync(new PriceListFilter(wholesale)));
            await Assert.ThrowsAsync<NotFoundException>(() => o.PriceList.BuildAsync(new PriceListFilter(GroupId: sweaters)));
        }
    }

    private static int _innSeed = 819_000_000;

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
