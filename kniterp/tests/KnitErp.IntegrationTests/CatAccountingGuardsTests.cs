using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D339 (аудит модуля «Товары»): позиция в учёте не меняет единицу (код — можно, D342) (сервис, загрузка Excel и триггер базы), точность единицы
/// не уменьшается при записанных количествах, модификация — единица и НДС как у основной, архив — без остатка, модификаций и комплектов,
/// цены архивной позиции не меняются.
/// </summary>
public sealed class CatAccountingGuardsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 953_000_000;

    [SqlFact]
    public async Task Item_in_accounting_keeps_unit_and_code_unit_precision_and_archive_are_guarded()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var kg = await s.Catalog.CreateUnitAsync(new UnitCommand("KGX", "Килограмм тест", "кгт", 3));
        var yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, kg, null));
        var fresh = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-2", "Пряжа новая", ItemType.RawMaterial, kg, null));
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        await ReceiveAsync(s, org.OrganizationId, store, yarn, 2.5m);

        // Единица и код позиции в учёте не меняются; наименование — можно.
        var card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-1"))).Items.Single();
        Assert.Equal("catalog.unit.in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, piece, null), card.RowVersion))).Code);
        // D342: код позиции в учёте менять можно (артикулы на фабрике меняются); вернём прежний, чтобы дальше искать по нему.
        await s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-9", "Пряжа", ItemType.RawMaterial, kg, null), card.RowVersion);
        card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-9"))).Items.Single();
        await s.Catalog.UpdateItemAsync(yarn, new ItemCommand("ПР-1", "Пряжа мериносовая", ItemType.RawMaterial, kg, null), card.RowVersion);

        // Новая позиция без истории — единицу сменить можно.
        var freshCard = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-2"))).Items.Single();
        await s.Catalog.UpdateItemAsync(fresh, new ItemCommand("ПР-2", "Пряжа новая", ItemType.RawMaterial, piece, null), freshCard.RowVersion);

        // Триггер базы: прямой UPDATE в обход сервиса тоже не проходит.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [kniterp].[items] SET [UnitId] = {piece} WHERE [Id] = {yarn}"));
            Assert.Equal(51310, ex.Number);
        }

        // Точность единицы с записанными дробными количествами не уменьшается; увеличивается — можно.
        var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Id == kg);
        Assert.Equal("catalog.unit.precision_in_use", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateUnitAsync(kg, new UnitCommand("KGX", "Килограмм тест", "кгт", 0), unit.RowVersion))).Code);
        await s.Catalog.UpdateUnitAsync(kg, new UnitCommand("KGX", "Килограмм тест", "кгт", 4), unit.RowVersion);

        // D343: архив позиции с остатком — только с подтверждением; без него ничего не меняется.
        card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-1"))).Items.Single();
        Assert.Contains("остаток 2.5", Assert.Single(await s.Catalog.ArchiveItemAsync(yarn, card.RowVersion)));
        Assert.False((await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-1"))).Items.Single().IsArchived);

        // Компонент действующего комплекта — нет.
        var bundle = await s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null,
            (await s.VatRates.ActiveAsync()).First().Id));
        await s.Bundles.SetComponentAsync(bundle, fresh, 2);
        freshCard = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-2"))).Items.Single();
        Assert.Contains("НБ-1", Assert.Single(await s.Catalog.ArchiveItemAsync(fresh, freshCard.RowVersion)));
        Assert.Empty(await s.Catalog.ArchiveItemAsync(fresh, freshCard.RowVersion, confirmed: true));
        await using (var db = host.NewDb())
        {
            var audit = await db.AuditEntries.Where(a => a.EntityId == fresh.ToString() && a.Action == KnitErp.Domain.Audit.AuditActions.CatalogArchived).SingleAsync();
            Assert.Contains("подтверждено", audit.Reason);
        }

        // Цены архивной позиции не меняются.
        var plain = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-3", "Пряжа старая", ItemType.RawMaterial, kg, null));
        await s.Catalog.ArchiveItemAsync(plain, (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ПР-3"))).Items.Single().RowVersion);
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        Assert.Equal("catalog.archived", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SetPricesAsync(plain, new Dictionary<long, decimal?> { [price] = 100 }))).Code);

        // Загрузка Excel тоже не меняет единицу позиции в учёте.
        var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
            [new SheetData("Номенклатура", ItemExchangeService.Columns, [["ПР-1", "Пряжа мериносовая", ItemTypes.Name(ItemType.RawMaterial), "шт", ""]])]));
        var plan = await s.ItemExchange.PreviewAsync(file);
        Assert.Contains(plan.Rows.Single().Errors, e => e.Contains("уже в учёте"));
    }

    [SqlFact]
    public async Task Item_in_open_order_is_not_archived()
    {
        // D340/D343: позиция в открытом заказе покупателя — предупреждение; после отмены заказа — архив без вопросов.
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-7", "Свитер", ItemType.Finished, piece, null, (await s.VatRates.ActiveAsync())[0].Id));
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var day = new DateOnly(2026, 10, 1);
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(day, customer, store, day, null, false, null));
        await s.Sales.SetOrderLineAsync(order, sweater, 2, 3000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);

        var card = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-7"))).Items.Single();
        Assert.Contains("открытом заказе покупателя", Assert.Single(await s.Catalog.ArchiveItemAsync(sweater, card.RowVersion)));

        await s.Sales.CancelOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Empty(await s.Catalog.ArchiveItemAsync(sweater, card.RowVersion));
    }

    [SqlFact]
    public async Task Price_saved_last_wins_with_true_history()
    {
        // D344 (ответ заказчика: конфликт цен убрать): сохраняется последнее изменение, но вкладка с устаревшей ценой в памяти
        // пишет в историю настоящую прежнюю цену из базы.
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-8", "Свитер", ItemType.Finished, piece, null, (await s.VatRates.ActiveAsync())[0].Id));
        var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 1000 });
        await using (var other = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await other.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 1200 });
        }

        await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 1100 });
        await using var db = host.NewDb();
        Assert.Equal(1100m, await db.ItemPrices.Where(p => p.ItemId == sweater).Select(p => p.Price).SingleAsync());
        var last = await db.ItemPriceHistory.Where(h => h.ItemId == sweater).OrderByDescending(h => h.Id).FirstAsync();
        Assert.Equal(((decimal?)1200m, (decimal?)1100m), (last.OldPrice, last.NewPrice));
    }

    [SqlFact]
    public async Task Card_stock_counts_only_visible_warehouses()
    {
        // Ревью D339: кладовщик склада пряжи видит в карточке остаток своего склада, а не всей организации.
        var org = await CreateOrgAsync();
        long yarn, keeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var kg = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-5", "Пряжа", ItemType.RawMaterial, kg, null));
            var own = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            var other = await s.Warehouses.CreateWarehouseAsync("Склад цеха", null);
            await ReceiveAsync(s, org.OrganizationId, own, yarn, 3);
            await ReceiveAsync(s, org.OrganizationId, other, yarn, 7);
            Assert.Equal(10m, (await s.Nomenclature.GetItemAsync(yarn)).Stock);
            keeper = (await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local", "Кладовщик пряжи",
                SystemRoles.Storekeeper, null, WarehouseId: own))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == keeper)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(keeper, org.OrganizationId))
        {
            Assert.Equal(3m, (await s.Nomenclature.GetItemAsync(yarn)).Stock);
        }
    }

    [SqlFact]
    public async Task Audit_log_hides_prices_without_price_right()
    {
        // D345: аудитор с запретом «Цены и суммы» видит в журнале, что цена менялась, но не её значения.
        var org = await CreateOrgAsync();
        long auditor;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-9", "Свитер", ItemType.Finished, piece, null, (await s.VatRates.ActiveAsync())[0].Id));
            var price = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [price] = 1234 });
            auditor = (await s.Access.InviteAsync(new InviteUserCommand($"auditor-{Guid.NewGuid():N}@test.local", "Аудитор",
                SystemRoles.Auditor, null))).UserId;
            Assert.Contains(await s.Audit.ListAsync(), r => r.After == "1234.00");
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == auditor)).Activate();
            db.RoleAssignments.Add(RoleAssignment.ForPermission(org.OrganizationId, auditor, Permissions.PriceView,
                isDeny: true, org.OwnerUserId, host.Clock.UtcNow, "Тест запрета"));
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(auditor, org.OrganizationId))
        {
            var rows = await s.Audit.ListAsync();
            Assert.DoesNotContain(rows, r => r.After == "1234.00");
            Assert.Contains(rows, r => r.After == KnitErp.Application.Audit.AuditQueryService.Hidden && r.Reason!.StartsWith("СВ-9: "));
        }
    }

    [SqlFact]
    public async Task Modification_follows_parent_unit_and_vat()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var pair = await s.Catalog.CreateUnitAsync(new UnitCommand("PRX", "Пара тест", "прт", 0));
        var rates = await s.VatRates.ActiveAsync();
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, rates[0].Id));
        var size = await s.Modifications.CreateCharacteristicAsync("Размер");
        await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [size] = "46, 48" });
        var variants = (await s.Modifications.VariantsAsync(sweater)).Modifications;

        // У модификации единицу не сменить.
        var v = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: variants[0].Code))).Items.Single();
        Assert.Equal("catalog.variant.unit_vat", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(v.Id, new ItemCommand(v.Code, v.Name, ItemType.Finished, pair, null, v.VatRateId), v.RowVersion))).Code);

        // У основной — меняется вместе с модификациями (истории нет).
        var parent = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СВ-1"))).Items.Single(i => i.Id == sweater);
        var otherVat = rates.Count > 1 ? rates[1].Id : rates[0].Id;
        await s.Catalog.UpdateItemAsync(sweater, new ItemCommand("СВ-1", "Свитер", ItemType.Finished, pair, null, otherVat), parent.RowVersion);
        await using var db = host.NewDb();
        Assert.All(await db.Items.Where(i => i.ParentItemId == sweater).ToListAsync(), i => Assert.Equal((pair, otherVat), (i.UnitId, i.VatRateId)));
    }

    private static async Task ReceiveAsync(Services s, long orgId, long store, long item, decimal qty)
    {
        var reason = await s.Db.OperationReasons.Where(r => r.OrganizationId == orgId && r.Kind == StockOperationKind.Receipt).Select(r => (long?)r.Id).FirstAsync();
        var doc = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, new DateOnly(2026, 10, 1), null));
        await s.Documents.SetLineAsync(doc, item, qty, (await s.Documents.GetAsync(doc)).RowVersion);
        await s.Documents.PostAsync(doc, (await s.Documents.GetAsync(doc)).RowVersion);
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
