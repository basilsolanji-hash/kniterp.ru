using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Production;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Производственный заказ (D90, D91): черновик, запуск с фиксацией версии техкарты и снимком норм, плановая себестоимость,
/// отмена, модификация по техкарте модели, заказ покупателя как источник, права, чужая организация и защита в базе.
/// </summary>
public sealed class ProductionOrderTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 830_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Release_fixes_techcard_version_and_planned_cost_new_version_does_not_change_it()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var options = await s.ProductionOrders.OptionsAsync();
            Assert.Equal(f.Wip, options.DefaultWipWarehouseId);
            Assert.Equal(1, options.Products.Single(p => p.Id == f.Sweater).TechCardVersion);
            Assert.Null(options.Products.Single(p => p.Id == f.Hat).TechCardVersion);

            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 25));
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.StartsWith("ПЗ-", dto.Number);
            Assert.Equal((ProductionOrderStatus.Draft, 1, false, true, true), (dto.Status, dto.TechCardVersion!.Value, dto.MaterialsFixed, dto.CanRelease, dto.CanCancel));
            // Черновик показывает нормы действующей версии и текущие цены: пряжа 1 000 ₽/кг без НДС, пуговица 5 ₽.
            var yarn = dto.Materials.Single(m => m.ItemId == f.Yarn);
            Assert.Equal((8m, 8.24m, 1000m), (yarn.Net, yarn.Gross, yarn.Price!.Value));
            Assert.Equal(8_865m, dto.PlannedCost); // 8,24 × 1 000 + 125 × 5

            await s.ProductionOrders.UpdateAsync(order, Command(f, f.Sweater, 30) with { DueDate = Day.AddDays(10) }, dto.RowVersion);
            Assert.Equal("production.order.item_fixed", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.ProductionOrders.UpdateAsync(order, Command(f, f.Hat, 30), (await s.ProductionOrders.GetAsync(order)).RowVersion))).Code);

            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((ProductionOrderStatus.Released, true, false, true), (dto.Status, dto.MaterialsFixed, dto.CanEdit, dto.CanCancel));
            Assert.Equal(9.888m, dto.Materials.Single(m => m.ItemId == f.Yarn).Gross); // 3,2 × 3 = 9,6 кг + 3%
            Assert.Equal(10_638m, dto.PlannedCost); // 9,888 × 1 000 + 150 × 5
            Assert.Equal(354.6m, dto.PlannedUnitCost);
            Assert.Equal("production.order.not_draft", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.UpdateAsync(order, Command(f, f.Sweater, 40), dto.RowVersion))).Code);
            Assert.Equal("production.order.not_draft", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.ReleaseAsync(order, dto.RowVersion))).Code);

            // Новая версия техкарты: запущенный заказ остаётся на версии 1 с прежними нормами, новый черновик берёт версию 2.
            var v2 = await s.TechCards.CopyAsync(f.Card);
            await s.TechCards.SetLineAsync(v2, f.Yarn, 4m, 3m, (await s.TechCards.GetAsync(v2)).RowVersion);
            await s.TechCards.ActivateAsync(v2, (await s.TechCards.GetAsync(v2)).RowVersion);
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((1, f.Card, 9.888m, 10_638m), (dto.TechCardVersion!.Value, dto.TechCardId!.Value, dto.Materials.Single(m => m.ItemId == f.Yarn).Gross, dto.PlannedCost!.Value));
            var next = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            Assert.Equal(2, (await s.ProductionOrders.GetAsync(next)).TechCardVersion);

            var list = await s.ProductionOrders.ListAsync(new ProductionOrderListFilter(ProductionOrderStatus.Released));
            Assert.Equal(order, list.Single().Id);
            Assert.Equal(2, (await s.ProductionOrders.ListAsync(new ProductionOrderListFilter(Search: "СВ-1"))).Count);

            // Отмена запущенного заказа без движений — с причиной.
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionOrders.CancelAsync(order, " ", dto.RowVersion));
            await s.ProductionOrders.CancelAsync(order, "Перенос в следующий месяц", (await s.ProductionOrders.GetAsync(order)).RowVersion);
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((ProductionOrderStatus.Cancelled, "Перенос в следующий месяц", false), (dto.Status, dto.CancelReason, dto.CanCancel));
        }

        await using (var db = host.NewDb())
        {
            var actions = await db.AuditEntries.Where(e => e.EntityType == "ProductionOrder" && e.EntityId == order.ToString())
                .Select(e => e.Action).ToListAsync();
            Assert.Equal(
                new[] { "production.order.cancelled", "production.order.changed", "production.order.created", "production.order.released" },
                actions.Order().ToArray());

            // Снимок норм и зафиксированные поля не меняются и в обход приложения.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_order_materials] SET [PlannedGross] = [PlannedGross] + 1 WHERE [ProductionOrderId] = {0}", order));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[production_order_materials] WHERE [ProductionOrderId] = {0}", order));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_orders] SET [Quantity] = 1 WHERE [Id] = {0}", order));
        }
    }

    [SqlFact]
    public async Task Modification_uses_model_card_and_sales_order_source_is_checked()
    {
        var f = await SetUpAsync();
        long red, salesOrder, draftSales;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var color = await s.Modifications.CreateCharacteristicAsync("Цвет");
            await s.Modifications.CreateModificationsAsync(f.Sweater, new Dictionary<long, string?> { [color] = "красный" });
            red = (await s.Modifications.VariantsAsync(f.Sweater)).Modifications.Single().Id;
            var option = (await s.ProductionOrders.OptionsAsync()).Products.Single(p => p.Id == red);
            Assert.Equal((true, 1, true), (option.IsModification, option.TechCardVersion!.Value, option.CardFromModel));

            salesOrder = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day.AddDays(7), null, false, null));
            await s.Sales.SetOrderLineAsync(salesOrder, red, 40, 2500m, 22m, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
            draftSales = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
            await s.Sales.SetOrderLineAsync(draftSales, red, 5, 2500m, 22m, (await s.Sales.GetOrderAsync(draftSales)).RowVersion);
            Assert.Equal("production.order.sales_order_not_confirmed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, red, 40) with { Source = ProductionSource.SalesOrder, SalesOrderId = salesOrder }))).Code);
            await s.Sales.ConfirmOrderAsync(salesOrder, (await s.Sales.GetOrderAsync(salesOrder)).RowVersion);
            Assert.Equal(salesOrder, (await s.ProductionOrders.OptionsAsync()).SalesOrders.Single().Id);

            // Основной модели в заказе покупателя нет — только модификация.
            Assert.Equal("production.order.sales_order_item", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 40) with { Source = ProductionSource.SalesOrder, SalesOrderId = salesOrder }))).Code);
            Assert.Equal("production.order.sales_order_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, red, 40) with { Source = ProductionSource.SalesOrder }))).Code);
            Assert.Equal("production.order.no_techcard", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, f.Hat, 1)))).Code);
            Assert.Equal("production.order.product_type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, f.Yarn, 1)))).Code);
            Assert.Equal("production.order.same_warehouse", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CreateAsync(Command(f, red, 1) with { OutputWarehouseId = f.Wip }))).Code);

            var order = await s.ProductionOrders.CreateAsync(Command(f, red, 40) with { Source = ProductionSource.SalesOrder, SalesOrderId = salesOrder });
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((f.Card, "Свитер", 12.8m), (dto.TechCardId!.Value, dto.TechCardItemName, dto.Materials.Single(m => m.ItemId == f.Yarn).Net));
            Assert.Equal(dto.SalesOrderNumber, (await s.Sales.GetOrderAsync(salesOrder)).Number);
            Assert.Single(await s.ProductionOrders.ListAsync(new ProductionOrderListFilter(SalesOrderId: salesOrder)));
        }
    }

    [SqlFact]
    public async Task Storekeeper_sees_orders_without_prices_and_cannot_change_them_other_organization_gets_not_found()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((false, false, false), (dto.PricesVisible, dto.CanEdit, dto.CanCancel));
            Assert.Null(dto.PlannedCost);
            Assert.All(dto.Materials, m => Assert.Null(m.Price));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ProductionOrders.OptionsAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 1)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ProductionOrders.CancelAsync(order, "нет", dto.RowVersion));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.ProductionOrders.GetAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => s.ProductionOrders.ReleaseAsync(order, []));
            Assert.Empty(await s.ProductionOrders.ListAsync(new ProductionOrderListFilter()));
            // Склад и изделие чужой организации в заказ не попадают.
            await Assert.ThrowsAsync<NotFoundException>(() => s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 1)));
        }
    }

    /// <summary>D92: этапы по порядку и не больше предыдущего, контроль качества, отмена после отметок запрещена, права, гонка отметок.</summary>
    [SqlFact]
    public async Task Stages_follow_route_quality_control_and_block_cancel()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 30));
            Assert.Equal("production.order.not_in_work", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 1)))).Code);
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
        }

        // Две вкладки одновременно отмечают вязание по 20 из 30: под блокировкой заказа пройдёт только одна.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
            try
            {
                await s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 20));
                return true;
            }
            catch (BusinessRuleException ex) when (ex.Code == "production.stage.exceeds")
            {
                return false;
            }
        }));
        Assert.Equal(1, results.Count(r => r));

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 10));
            Assert.Equal("production.stage.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 1)))).Code);
            await s.Floor.RecordAsync(order, Mark(ProductionStage.Assembly, 30));
            Assert.Equal("production.stage.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 1)))).Code);
            await s.Floor.RecordAsync(order, Mark(ProductionStage.Steaming, 30));

            // Контроль качества: 24 годных, 2 брака, 4 на переделку в ВТО.
            await s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 24));
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 2) with { Kind = ProductionOperationKind.Defect }));
            await s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 2) with { Kind = ProductionOperationKind.Defect, Reason = "Затяжка петель" });
            await s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 4) with { Kind = ProductionOperationKind.Rework, ReturnTo = ProductionStage.Steaming });
            // Ошибочно отмеченное вязание исправляется вниз только в пределах не ушедшего дальше: всё ушло — нельзя.
            Assert.Equal("production.stage.correction_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 1) with { Kind = ProductionOperationKind.Correction, Reason = "ошибка" }))).Code);

            var progress = await s.Floor.ProgressAsync(order);
            var stages = progress.Stages.ToDictionary(x => x.Stage);
            Assert.Equal((30m, 26m, 24m, 2m, 4m), (stages[ProductionStage.Steaming].Done + 4, stages[ProductionStage.Steaming].Done,
                progress.Good, progress.Defect, progress.Rework));
            // Брак довязывается: вязать можно ещё 2; ВТО ждёт 4 из переделки; упаковать можно 24 годных.
            Assert.Equal((2m, 4m, 24m), (stages[ProductionStage.Knitting].Available, stages[ProductionStage.Steaming].Available,
                stages[ProductionStage.Packing].Available));
            Assert.Equal(7, progress.History.Count);
            Assert.True(progress.CanMark);

            // После отметок заказ не отменить.
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.False(dto.CanCancel);
            Assert.Equal("production.order.has_movements", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionOrders.CancelAsync(order, "передумали", dto.RowVersion))).Code);
        }

        // Старший кладовщик видит ход работ, но этапы не отмечает.
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.False((await s.Floor.ProgressAsync(order)).CanMark);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Floor.RecordAsync(order, Mark(ProductionStage.Packing, 1)));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Floor.ProgressAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Floor.RecordAsync(order, Mark(ProductionStage.Packing, 1)));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(7, await db.AuditEntries.CountAsync(e => e.EntityType == "ProductionOrder" && e.EntityId == order.ToString()
                                                                 && e.Action == "production.stage.marked"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[production_operations] WHERE [ProductionOrderId] = {0}", order));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_operations] SET [Quantity] = 1000 WHERE [ProductionOrderId] = {0}", order));
        }
    }

    private static ProductionOperationCommand Mark(ProductionStage stage, decimal quantity) =>
        new(stage, ProductionOperationKind.Done, quantity, null, null, Day);

    private static ProductionOrderCommand Command(Fixture f, long item, decimal quantity) =>
        new(item, quantity, Day, null, ProductionSource.Plan, null, f.Wip, f.Store, null);

    private sealed record Fixture(
        CreatedOrganization Org, long Wip, long Store, long YarnStore, long Sweater, long Hat, long Yarn, long Buttons, long Card, long Customer, long Senior);

    /// <summary>Склады, свитер с техкартой (на 10 шт — 3,2 кг пряжи с отходом 3% и 50 пуговиц), закупка пряжи и пуговиц, Старший кладовщик.</summary>
    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long wip, store, yarnStore, sweater, hat, yarn, buttons, card, customer, supplier;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var pcs = units.Single(u => u.Symbol == "шт").Id;
            var kg = units.Single(u => u.Symbol == "кг").Id;
            wip = await s.Warehouses.CreateWarehouseAsync(ProductionOrderService.DefaultWipWarehouseName, null);
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            yarnStore = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, pcs, null));
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("ША-1", "Шапка", ItemType.Finished, pcs, null));
            yarn = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, kg, null));
            buttons = await s.Catalog.CreateItemAsync(new ItemCommand("ФУ-1", "Пуговица", ItemType.Accessory, pcs, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));

            card = await s.TechCards.CreateAsync(sweater, 10, null);
            await s.TechCards.SetLineAsync(card, yarn, 3.2m, 3m, (await s.TechCards.GetAsync(card)).RowVersion);
            await s.TechCards.SetLineAsync(card, buttons, 50m, 0m, (await s.TechCards.GetAsync(card)).RowVersion);
            await s.TechCards.ActivateAsync(card, (await s.TechCards.GetAsync(card)).RowVersion);

            // Закупка: пряжа 1 220 ₽ с НДС 22% (1 000 ₽ без НДС), пуговицы 5 ₽ без НДС.
            var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, yarnStore, null, null, true, null));
            await s.Purchases.SetOrderLineAsync(po, yarn, 50, 1220m, 22m, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.SetOrderLineAsync(po, buttons, 500, 5m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            var receipt = await s.Purchases.CreateReceiptAsync(po);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
        }

        long senior;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: yarnStore))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == senior)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, wip, store, yarnStore, sweater, hat, yarn, buttons, card, customer, senior);
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
