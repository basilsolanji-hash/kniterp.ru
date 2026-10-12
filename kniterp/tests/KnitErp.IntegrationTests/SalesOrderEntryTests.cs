using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Этап 3 продаж (D1130–D1137): повтор заказа с актуальными ценами, вставка из Excel с построчными ошибками, сетка «размер × цвет»,
/// значения по умолчанию из соглашения и карточки клиента, остаток без складских прав не отдаётся, отмена с причиной.
/// </summary>
public sealed class SalesOrderEntryTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 814_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Paste_grid_and_repeat_fill_order_quickly()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var color = await s.Modifications.CreateCharacteristicAsync("Цвет");
        var size = await s.Modifications.CreateCharacteristicAsync("Размер");
        await s.Modifications.CreateModificationsAsync(f.Sweater, new Dictionary<long, string?> { [color] = "красный, синий", [size] = "46, 48" });
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));

        // Сетка: строки — цвета, колонки — размеры; две ячейки — две строки заказа по цене модели.
        var grid = await s.Sales.GridAsync(order, f.Sweater);
        Assert.Equal(("Цвет", "Размер"), (grid.RowAxis, grid.ColAxis));
        Assert.Equal(["46", "48"], grid.Cols);
        Assert.Equal(4, grid.Cells.Count);
        var red46 = grid.Cells.Single(c => c.Row == "красный" && c.Col == "46");
        var blue48 = grid.Cells.Single(c => c.Row == "синий" && c.Col == "48");
        Assert.Equal(1000m, red46.Price);
        Assert.Equal(2, await s.Sales.AddGridAsync(order, new Dictionary<long, decimal> { [red46.ItemId] = 3, [blue48.ItemId] = 2, [grid.Cells.First(c => c.ItemId != red46.ItemId && c.ItemId != blue48.ItemId).ItemId] = 0 },
            (await s.Sales.GetOrderAsync(order)).RowVersion));

        // Вставка: заголовок пропускается; размер и цвет находят модификацию; ошибки — построчно, верное добавляется.
        var text = "Артикул\tРазмер\tЦвет\tКол-во\nСВ-1\t48\tсиний\t5\nНЕТ-1\t1\nСВ-1\t46\t2\nШФ-1\tмного\nШФ-1\t4\t250";
        var preview = await s.Sales.PreviewPasteAsync(order, text);
        Assert.Equal([null, "позиция «НЕТ-1» не найдена", "у СВ-1 несколько модификаций — укажите размер и цвет", "количество «много» — не число больше нуля", null],
            preview.Select(r => r.Error));
        var added = await s.Sales.AddPastedLinesAsync(order, text, (await s.Sales.GetOrderAsync(order)).RowVersion);
        Assert.Equal(2, added.Count(r => r.Error is null));
        var dto = await s.Sales.GetOrderAsync(order);
        Assert.Equal(7m, dto.Lines.Single(l => l.ItemId == blue48.ItemId).Quantity); // 2 по сетке + 5 из вставки
        Assert.Equal((4m, 250m), (dto.Lines.Single(l => l.ItemId == f.Scarf).Quantity, dto.Lines.Single(l => l.ItemId == f.Scarf).Price!.Value));
        Assert.Equal("sales.paste.empty", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.PreviewPasteAsync(order, "  "))).Code);

        // Повтор: шарф в архиве — пропущен; цена свитера — новая (по прайсу на сегодня), количество — как в образце.
        var baseType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        await s.Nomenclature.SetPricesAsync(red46.ItemId, new Dictionary<long, decimal?> { [baseType] = 1100 });
        await s.Catalog.ArchiveItemAsync(f.Scarf, (await s.Db.Items.AsNoTracking().SingleAsync(i => i.Id == f.Scarf)).RowVersion);
        var repeat = await s.Sales.RepeatOrderAsync(order);
        Assert.Equal(["ШФ-1: в архиве"], repeat.Skipped);
        var copy = await s.Sales.GetOrderAsync(repeat.OrderId);
        Assert.Equal((SalesOrderStatus.Draft, f.Customer, 2), (copy.Status, copy.CustomerId, copy.Lines.Count));
        Assert.Equal((3m, 1100m), (copy.Lines.Single(l => l.ItemId == red46.ItemId).Quantity, copy.Lines.Single(l => l.ItemId == red46.ItemId).Price!.Value));
        Assert.NotEmpty(repeat.Short); // на складе ничего нет

        // Подбор: поиск по коду, цена по правилам, модель отмечена как имеющая модификации.
        var pick = await s.Sales.PickItemsAsync(order, "СВ-1");
        Assert.True(pick.Single(p => p.ItemId == f.Sweater).HasModifications);
        Assert.Equal(0m, pick.Single(p => p.ItemId == f.Sweater).Stock);

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.RepeatOrderAsync(order));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.GridAsync(order, f.Sweater));
    }

    [SqlFact]
    public async Task Defaults_hidden_stock_and_cancel_with_reason()
    {
        var f = await SetUpAsync();
        long order, second;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Соглашение со складом «Опт» и закреплённый менеджер: новый заказ получает склад по умолчанию и ответственного.
            var wholesale = await s.Warehouses.CreateWarehouseAsync("Склад опта", null);
            await s.Agreements.CreateAsync(new SalesAgreementTerms("Опт", f.Customer, null, null, 0, Day.AddMonths(-1), null, 30, 14, null, wholesale, null, null));
            await s.Agreements.SetCustomerTermsAsync(f.Customer, null, f.Manager, null);
            var defaults = await s.Sales.DefaultsForCustomerAsync(f.Customer);
            Assert.Equal((wholesale, f.Manager), (defaults.WarehouseId, defaults.ResponsibleUserId));
            Assert.Equal("предоплата 30%, отсрочка 14 дн.", defaults.PaymentTerms);
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            Assert.Equal(f.Manager, (await s.Sales.GetOrderAsync(order)).Details!.ResponsibleUserId);
            await s.Sales.AddOrderLineAsync(order, f.Scarf, 2, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.SetOrderReserveAsync(order, true, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
            second = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            await s.Sales.AddOrderLineAsync(second, f.Scarf, 1, (await s.Sales.GetOrderAsync(second)).RowVersion);
            Assert.Equal(2m, (await s.Sales.GetOrderAsync(second)).Lines.Single().ReservedByOthers);
            Assert.False((await s.Sales.GetOrderAsync(second)).StockHidden);

            // Отмена с причиной из справочника (типовые причины появляются сами) — резерв снят, причина видна.
            var reasons = await s.Sales.CancelReasonsAsync();
            Assert.Equal(SalesCancelReason.Defaults.Order(), reasons.Select(r => r.Name).Order());
            var reason = reasons.Single(r => r.Name == "Дубль заказа").Id;
            await s.Sales.CancelOrderAsync(order, reason, "клиент оформил заново", (await s.Sales.GetOrderAsync(order)).RowVersion);
            var cancelled = await s.Sales.GetOrderAsync(order);
            Assert.Equal((SalesOrderStatus.Cancelled, "Дубль заказа — клиент оформил заново"), (cancelled.Status, cancelled.CancelReason));
            Assert.Equal(0m, (await s.Sales.GetOrderAsync(second)).Lines.Single().ReservedByOthers);
            Assert.Equal("sales.cancel_reason.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreateCancelReasonAsync("Другое"))).Code);
        }

        // Менеджер продаж без складских прав: остаток в заказе сервер не отдаёт, подбор — тоже.
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            var dto = await m.Sales.GetOrderAsync(second);
            Assert.True(dto.StockHidden);
            Assert.Equal(0m, dto.Lines.Single().Stock);
            Assert.Null((await m.Sales.PickItemsAsync(second, "ШФ")).Single().Stock);
            Assert.Null((await m.Sales.ShipEstimateAsync(second)).Date);
        }
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Scarf, long Customer, long Manager);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, scarf, customer, manager;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, unit, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            var baseType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [baseType] = 1000 });
            await s.Nomenclature.SetPricesAsync(scarf, new Dictionary<long, decimal?> { [baseType] = 300 });
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, scarf, customer, manager);
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
