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
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
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

    /// <summary>D93, D94: выдача в цех перемещением, возврат и отходы не больше остатка заказа, баланс, области прав, откат при ошибке.</summary>
    [SqlFact]
    public async Task Materials_issue_return_and_waste_keep_order_balance()
    {
        var f = await SetUpAsync();
        long order, second;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 30));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            second = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.ProductionOrders.ReleaseAsync(second, (await s.ProductionOrders.GetAsync(second)).RowVersion);

            var issue = await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 10m), (f.Buttons, 150m)));
            var doc = await s.Documents.GetAsync(issue);
            Assert.Equal((StockOperationKind.Transfer, StockDocumentStatus.Posted, f.YarnStore, f.Wip, "Выдача в производство"),
                (doc.Kind, doc.Status, doc.WarehouseId, doc.TargetWarehouseId!.Value, doc.ReasonName));
            Assert.Contains("ПЗ-", doc.Comment);
            await s.Materials.MoveAsync(second, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 5m)));

            // Ошибки не оставляют ни черновика, ни связи: материала нет в нормах, не хватает на складе, склад цеха как источник.
            var before = await s.Db.StockDocuments.CountAsync();
            Assert.Equal("production.material.not_in_order", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Hat, 1m))))).Code);
            Assert.Equal("stock.balance.insufficient", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 1000m))))).Code);
            Assert.Equal("production.material.same_warehouse", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.Wip, (f.Yarn, 1m))))).Code);
            Assert.Equal(before, await s.Db.StockDocuments.CountAsync());
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var reasons = (await s.Materials.MaterialsAsync(order)).WasteReasons;
            var defect = reasons.Single(r => r.Name == "Брак");
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Return, f.YarnStore, (f.Yarn, 0.5m)));
            Assert.Equal("stock.document.comment_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Waste, null, (f.Yarn, 0.2m)) with { ReasonId = defect.Id }))).Code);
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Waste, null, (f.Yarn, 0.2m)) with { ReasonId = defect.Id, Comment = "Распуск полотна" });

            // В цехе 15 кг пряжи (10 + 5 второго заказа), но у первого заказа осталось только 9,3 — больше не вернуть и не списать.
            Assert.Equal("production.material.exceeds_remaining", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Return, f.YarnStore, (f.Yarn, 9.4m))))).Code);
            var m = await s.Materials.MaterialsAsync(order);
            var yarn = m.Lines.Single(l => l.ItemId == f.Yarn);
            Assert.Equal((9.888m, 10m, 0m, 0.2m, 0.5m, 9.3m), (yarn.Planned, yarn.Issued, yarn.Consumed, yarn.Waste, yarn.Returned, yarn.Remaining));
            Assert.Null(yarn.Normative); // ничего не выпущено — нормы на сделанное ещё нет
            Assert.Equal(3, m.Documents.Count);
            Assert.True(m.CanMove);
            var stock = await s.Db.StockMovements.Where(x => x.WarehouseId == f.Wip && x.ItemId == f.Yarn).SumAsync(x => x.Quantity);
            Assert.Equal(14.3m, stock);

            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.False(dto.CanCancel);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionOrders.CancelAsync(order, "нет", dto.RowVersion));
        }

        // Старший кладовщик склада пряжи выдаёт со своего склада, но не распоряжается складом цеха.
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 1m)));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Return, f.YarnStore, (f.Yarn, 1m))));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Materials.MaterialsAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 1m))));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(4, await db.AuditEntries.CountAsync(e => e.EntityId == order.ToString() && e.Action == "production.material.moved"));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[production_order_documents] WHERE [ProductionOrderId] = {0}", order));
        }
    }

    /// <summary>D95: выпуск не больше упакованного, расход по норме и по факту, статусы, закрытие с остатками и без, фактическая себестоимость.</summary>
    [SqlFact]
    public async Task Output_consumes_by_fact_updates_status_and_close_requires_empty_workshop()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 20));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 7m), (f.Buttons, 100m)));
            foreach (var stage in new[] { ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming })
            {
                await s.Floor.RecordAsync(order, Mark(stage, 20));
            }

            await s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 18));
            await s.Floor.RecordAsync(order, Mark(ProductionStage.QualityControl, 2) with { Kind = ProductionOperationKind.Defect, Reason = "Дыры" });
            Assert.Equal("production.output.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.OutputAsync(order, new OutputCommand(1, Day, null, null)))).Code);
            await s.Floor.RecordAsync(order, Mark(ProductionStage.Packing, 18));

            // Расход по норме без отхода на 10 выпущенных и 2 брака: пряжа 0,32 × 12 = 3,84 кг, пуговицы 5 × 12 = 60.
            var proposal = (await s.Materials.ProposeConsumptionAsync(order, 10)).ToDictionary(p => p.ItemId);
            Assert.Equal((3.84m, 60m), (proposal[f.Yarn].Proposed, proposal[f.Buttons].Proposed));
            var result = await s.Materials.OutputAsync(order, new OutputCommand(10, Day, null, null));
            Assert.NotNull(result.ConsumptionDocumentId);
            var output = await s.Documents.GetAsync(result.OutputDocumentId);
            Assert.Equal((StockOperationKind.Receipt, f.Store, "Выпуск из производства", 10m),
                (output.Kind, output.WarehouseId, output.ReasonName, output.Lines.Single(l => l.ItemId == f.Sweater).Quantity));
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((ProductionOrderStatus.PartiallyDone, 10m, true), (dto.Status, dto.Released, dto.CanClose));
            Assert.Equal(8m, (await s.Floor.ProgressAsync(order)).ReadyForOutput);
            Assert.Equal("production.output.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.OutputAsync(order, new OutputCommand(9, Day, null, null)))).Code);

            // Расход по факту: пряжи больше остатка заказа в цехе — нельзя; 2,6 кг и 40 пуговиц — можно.
            Assert.Equal("production.material.exceeds_remaining", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.OutputAsync(order, new OutputCommand(8, Day, [new(f.Yarn, 3.2m), new(f.Buttons, 40m)], null)))).Code);
            Assert.Equal("production.order.materials_left", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.ProductionOrders.CloseAsync(order, "срочно", (await s.ProductionOrders.GetAsync(order)).RowVersion))).Code);
            result = await s.Materials.OutputAsync(order, new OutputCommand(8, Day, [new(f.Yarn, 2.6m), new(f.Buttons, 40m)], "вторая партия"));
            // Пряжа: израсходовано 6,44, норма на 20 (18 + 2 брака) — 6,592; в цехе ещё 0,56 кг.
            var yarn = (await s.Materials.MaterialsAsync(order)).Lines.Single(l => l.ItemId == f.Yarn);
            Assert.Equal((6.44m, 0.56m, 6.592m), (yarn.Consumed, yarn.Remaining, yarn.Normative!.Value));
            Assert.Equal(ProductionOrderStatus.PartiallyDone, (await s.ProductionOrders.GetAsync(order)).Status);

            // Остаток пряжи — отходами; после этого в цехе пусто, но заказ выполнен не полностью — закрытие только с причиной.
            var waste = (await s.Materials.MaterialsAsync(order)).WasteReasons.Single(r => r.Name == "Брак").Id;
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Waste, null, (f.Yarn, 0.56m)) with { ReasonId = waste, Comment = "Обрезки" });
            yarn = (await s.Materials.MaterialsAsync(order)).Lines.Single(l => l.ItemId == f.Yarn);
            Assert.Equal((0m, 6.19m), (yarn.Remaining, yarn.DeviationPercent!.Value)); // (6,44 + 0,56 − 6,592) / 6,592
            Assert.Contains((await s.Materials.MaterialsAsync(order)).Warnings, w => w.Contains("ПР-1") && w.Contains("6,19"));
            await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.ProductionOrders.CloseAsync(order, null, (await s.ProductionOrders.GetAsync(order)).RowVersion));

            // Фактическая себестоимость по выданному: 7 × 1 000 + 100 × 5 = 7 500 ₽, на изделие 416,67; плановая на 20 — 7 092.
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((7_500m, 416.67m, 7_092m, 18m), (dto.ActualCost!.Value, dto.ActualUnitCost!.Value, dto.PlannedCost!.Value, dto.Released));

            await s.ProductionOrders.CloseAsync(order, "Брак 2 шт, клиент согласен на 18", dto.RowVersion);
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((ProductionOrderStatus.Closed, "Брак 2 шт, клиент согласен на 18", false), (dto.Status, dto.CloseReason, dto.CanClose));
            Assert.Equal("production.order.not_in_work", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Waste, null, (f.Yarn, 0.1m)) with { ReasonId = waste, Comment = "x" }))).Code);
            Assert.Equal("production.order.not_in_work", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.RecordAsync(order, Mark(ProductionStage.Knitting, 1)))).Code);

            var store = await s.Db.StockMovements.Where(x => x.WarehouseId == f.Store && x.ItemId == f.Sweater).SumAsync(x => x.Quantity);
            Assert.Equal(18m, store);
        }

        // Кладовщик склада пряжи не выпускает: склад цеха и склад готовой продукции — не его.
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.Null((await s.ProductionOrders.GetAsync(order)).ActualCost); // цены не видит
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.EntityId == order.ToString() && e.Action == "production.output.released"));
            Assert.Equal(1, await db.AuditEntries.CountAsync(e => e.EntityId == order.ToString() && e.Action == "production.order.closed"));
        }
    }

    /// <summary>D96: требование-накладная (М-11) на выдачу, накладная (МХ-18) на выпуск, акты отходов и расхода; цены — только с правом.</summary>
    [SqlFact]
    public async Task Requisition_and_output_note_print_with_okei_and_prices_by_permission()
    {
        var f = await SetUpAsync();
        long order, issue, waste, output;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            issue = await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 3.3m), (f.Buttons, 50m)));
            var reason = (await s.Materials.MaterialsAsync(order)).WasteReasons.Single(r => r.Name == "Брак").Id;
            waste = await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Waste, null, (f.Yarn, 0.1m)) with { ReasonId = reason, Comment = "обрезки" });
            foreach (var stage in ProductionFlow.Stages)
            {
                await s.Floor.RecordAsync(order, Mark(stage, 10));
            }

            output = (await s.Materials.OutputAsync(order, new OutputCommand(10, Day, null, null))).OutputDocumentId;

            var m11 = await s.ProductionPrint.DocumentAsync(issue);
            Assert.Equal(("М-11", "0315006", "ТРЕБОВАНИЕ-НАКЛАДНАЯ", "Склад пряжи", "Цех (НЗП)"), (m11.Form, m11.Okud, m11.Title, m11.Sender, m11.Receiver));
            var yarn = m11.Lines.Single(l => l.Code == "ПР-1");
            Assert.Equal(("166", 3.3m, 1000m, 3300m), (yarn.UnitCode, yarn.Quantity, yarn.Price!.Value, yarn.Amount!.Value));
            Assert.Equal(3550m, m11.Total); // 3 300 + 50 × 5
            var mx18 = await s.ProductionPrint.DocumentAsync(output);
            Assert.Equal(("МХ-18", "0335018", "Склад готовой продукции", 10m), (mx18.Form, mx18.Okud, mx18.Receiver, mx18.Lines.Single().Quantity));
            Assert.Equal("796", mx18.Lines.Single().UnitCode);
            Assert.Equal(354.6m, mx18.Lines.Single().Price); // плановая себестоимость единицы: (3,296 × 1 000 + 50 × 5) / 10
            // Акты (дополнение D96): отходы — с причиной и пояснением; расход при выпуске — норма на выпущенное и факт.
            var act = await s.ProductionPrint.DocumentAsync(waste);
            Assert.Equal((ProductionPrintService.WasteActForm, true, "Брак", "Производственный заказ " + mx18.OrderNumber + ": обрезки"),
                (act.Form, act.IsAct, act.Reason, act.Comment));
            Assert.Equal(0.1m, act.Lines.Single().Quantity);
            var consumptionId = (await s.Materials.MaterialsAsync(order)).Documents.Single(d => d.Purpose == ProductionDocumentPurpose.Consumption).Id;
            var used = await s.ProductionPrint.DocumentAsync(consumptionId);
            Assert.Equal(ProductionPrintService.ConsumptionActForm, used.Form);
            Assert.Contains("СВ-1 Свитер — 10", used.Produced);
            var yarnUsed = used.Lines.Single(l => l.Code == "ПР-1");
            Assert.Equal((3.2m, 3.2m), (yarnUsed.Norm!.Value, yarnUsed.Quantity)); // норма без отхода 3,2 кг на 10 шт; факт — по норме
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var m11 = await s.ProductionPrint.DocumentAsync(issue);
            Assert.False(m11.PricesVisible);
            Assert.All(m11.Lines, l => Assert.Null(l.Price));
            Assert.Null(m11.Total);
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.ProductionPrint.DocumentAsync(issue));
        }
    }

    /// <summary>D97: документы заказа сторнируются только из заказа; сторно выпуска пересчитывает статус, выдачу — после расхода не сторнировать.</summary>
    [SqlFact]
    public async Task Reversal_goes_through_order_and_recalculates_status()
    {
        var f = await SetUpAsync();
        long order, issue;
        OutputResultDto result;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            issue = await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 3.3m), (f.Buttons, 50m)));
            foreach (var stage in ProductionFlow.Stages)
            {
                await s.Floor.RecordAsync(order, Mark(stage, 10));
            }

            result = await s.Materials.OutputAsync(order, new OutputCommand(10, Day, null, null));
            Assert.Equal(ProductionOrderStatus.Done, (await s.ProductionOrders.GetAsync(order)).Status);

            // В складском разделе документ заказа не сторнируется.
            var output = await s.Documents.GetAsync(result.OutputDocumentId);
            Assert.False(output.CanReverse);
            Assert.Equal("stock.document.production_linked", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Documents.ReverseAsync(result.OutputDocumentId, "ошибка", output.RowVersion))).Code);
            Assert.True((await s.Documents.GetAsync(issue)).Status == StockDocumentStatus.Posted);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Выдачу не сторнировать, пока пряжа израсходована.
            Assert.Equal("production.reverse.issue_used", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.ReverseDocumentAsync(order, issue, "ошибка"))).Code);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Materials.ReverseDocumentAsync(order, result.OutputDocumentId, " "));

            await s.Materials.ReverseDocumentAsync(order, result.OutputDocumentId, "Выпуск не тем числом");
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((ProductionOrderStatus.Released, 0m), (dto.Status, dto.Released));
            Assert.Equal(10m, (await s.Floor.ProgressAsync(order)).ReadyForOutput);
            Assert.Equal(0m, await s.Db.StockMovements.Where(x => x.WarehouseId == f.Store && x.ItemId == f.Sweater).SumAsync(x => x.Quantity));

            await s.Materials.ReverseDocumentAsync(order, result.ConsumptionDocumentId!.Value, "Расход по ошибочному выпуску");
            await s.Materials.ReverseDocumentAsync(order, issue, "Выдали не ту партию");
            var yarn = (await s.Materials.MaterialsAsync(order)).Lines.Single(l => l.ItemId == f.Yarn);
            Assert.Equal((0m, 0m, 0m), (yarn.Issued, yarn.Consumed, yarn.Remaining));
            Assert.Equal("stock.document.status", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Materials.ReverseDocumentAsync(order, issue, "ещё раз"))).Code);
            Assert.Equal(StockDocumentStatus.Reversed, (await s.Documents.GetAsync(issue)).Status);

            // Снова выпуск после сторно — работает как обычно.
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 3.3m), (f.Buttons, 50m)));
            await s.Materials.OutputAsync(order, new OutputCommand(4, Day, null, null));
            Assert.Equal(ProductionOrderStatus.PartiallyDone, (await s.ProductionOrders.GetAsync(order)).Status);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            // Кладовщик склада пряжи не сторнирует выпуск на складе готовой продукции.
            var outputs = (await s.Materials.MaterialsAsync(order)).Documents.Where(d => d.Purpose == ProductionDocumentPurpose.Output && d.Status == StockDocumentStatus.Posted);
            await Assert.ThrowsAsync<NotFoundException>(() => s.Materials.ReverseDocumentAsync(order, outputs.Single().Id, "нет"));
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(3, await db.AuditEntries.CountAsync(e => e.EntityId == order.ToString() && e.Action == "production.document.reversed"));
        }
    }

    /// <summary>D98: отчёт — заказы в работе с просрочкой, брак по причинам, отклонения сверх допуска, себестоимость только с правом, Excel.</summary>
    [SqlFact]
    public async Task Production_report_shows_work_defects_deviations_and_costs()
    {
        var f = await SetUpAsync();
        long late, done;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Просроченный заказ: срок вчера (часы тестов — 09.10.2026), ничего не выпущено.
            late = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 5) with { OrderDate = Day.AddDays(-3), DueDate = Day.AddDays(-1) });
            await s.ProductionOrders.ReleaseAsync(late, (await s.ProductionOrders.GetAsync(late)).RowVersion);
            await s.Floor.RecordAsync(late, Mark(ProductionStage.Knitting, 3));

            done = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.ProductionOrders.ReleaseAsync(done, (await s.ProductionOrders.GetAsync(done)).RowVersion);
            await s.Materials.MoveAsync(done, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 3.6m), (f.Buttons, 50m)));
            foreach (var stage in new[] { ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming })
            {
                await s.Floor.RecordAsync(done, Mark(stage, 10));
            }

            await s.Floor.RecordAsync(done, Mark(ProductionStage.QualityControl, 9));
            await s.Floor.RecordAsync(done, Mark(ProductionStage.QualityControl, 1) with { Kind = ProductionOperationKind.Defect, Reason = "Дыры" });
            await s.Floor.RecordAsync(late, Mark(ProductionStage.Assembly, 3));
            await s.Floor.RecordAsync(done, Mark(ProductionStage.Packing, 9));
            // Расход по факту 3,6 кг при норме на 9 + 1 брак 3,296 кг — больше допуска.
            await s.Materials.OutputAsync(done, new OutputCommand(9, Day, [new(f.Yarn, 3.6m), new(f.Buttons, 50m)], null));

            var report = await s.ProductionReports.BuildAsync(new ProductionReportFilter(Day.AddDays(-7), Day));
            Assert.Equal(late, report.InWork.First().Id); // просроченные — первыми
            Assert.True(report.InWork.First().Overdue);
            var doneRow = report.InWork.Single(r => r.Id == done);
            Assert.Equal((false, 9m, 1m, 0m, ProductionOrderStatus.PartiallyDone), (doneRow.Overdue, doneRow.Released, doneRow.Defect, doneRow.ReadyForOutput, doneRow.Status));
            Assert.Equal(new[] { 10m, 10m, 10m, 9m, 9m }, doneRow.StageDone);
            var defect = Assert.Single(report.Defects);
            Assert.Equal(("Дыры", 1m, 1), (defect.Reason, defect.Quantity, defect.Orders));
            var yarn = report.Deviations.Single(d => d.OrderId == done && d.MaterialCode == "ПР-1");
            Assert.Equal((3.296m, 3.6m, 9.22m, true), (yarn.Normative, yarn.Actual, yarn.DeviationPercent, yarn.OverTolerance));
            var cost = report.Costs!.Single(c => c.OrderId == done);
            // План на единицу: (3,296 × 1 000 + 50 × 5) / 10 = 354,6; факт: (3 600 + 250) / 9 = 427,78.
            Assert.Equal((354.6m, 427.78m, 20.64m), (cost.PlannedUnitCost!.Value, cost.ActualUnitCost!.Value, cost.DiffPercent!.Value));

            var xlsx = await s.ProductionReports.ExportAsync(new ProductionReportFilter(Day.AddDays(-7), Day));
            var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
            Assert.Equal("Номер", sheet[0][0]);
            Assert.Equal(3, sheet.Count); // заголовок и два заказа
            Assert.Equal("production.report.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionReports.BuildAsync(new ProductionReportFilter(Day, Day.AddDays(-1))))).Code);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var report = await s.ProductionReports.BuildAsync(new ProductionReportFilter(Day.AddDays(-7), Day));
            Assert.Null(report.Costs);
            Assert.Equal(2, report.InWork.Count);
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            var report = await s.ProductionReports.BuildAsync(new ProductionReportFilter(Day.AddDays(-7), Day));
            Assert.Empty(report.InWork);
            Assert.Empty(report.Defects);
        }
    }

    /// <summary>D99: теги и обсуждение производственного заказа — видят те, кому виден заказ; чужая организация — «не найдено».</summary>
    [SqlFact]
    public async Task Production_order_has_tags_and_discussion()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10));
            await s.Tags.AssignAsync(TaggedEntity.ProductionOrder, order, null, "Срочно", TagColor.Red);
            var tag = Assert.Single(await s.Tags.ForEntityAsync(TaggedEntity.ProductionOrder, order));
            var (_, objects) = await s.Tags.ObjectsAsync(tag.Id);
            var obj = Assert.Single(objects);
            Assert.Equal(($"production-orders/{order}", "СВ-1 — Свитер"), (obj.Href, obj.Subtitle));
            Assert.StartsWith("ПЗ-", obj.Title);
            await s.Discussions.AddAsync(TaggedEntity.ProductionOrder, order, "Пряжу на этот заказ привезут в среду", [f.Senior]);
        }

        // Старший кладовщик видит заказ — видит и обсуждение, может ответить.
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.Single((await s.Discussions.ThreadAsync(TaggedEntity.ProductionOrder, order)).Comments);
            await s.Discussions.AddAsync(TaggedEntity.ProductionOrder, order, "Принято, выдам в четверг", null);
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.ThreadAsync(TaggedEntity.ProductionOrder, order));
            await Assert.ThrowsAnyAsync<Exception>(() => s.Tags.AssignAsync(TaggedEntity.ProductionOrder, order, null, "Чужой", TagColor.Blue));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal(2, (await s.Discussions.ThreadAsync(TaggedEntity.ProductionOrder, order)).Comments.Count);
        }
    }

    /// <summary>D90 (дополнение): черновики по заказу покупателя одной кнопкой — остаток, срок и склады из заказа; повтор и гонка не дублируют.</summary>
    [SqlFact]
    public async Task Production_orders_from_sales_order_in_one_click()
    {
        var f = await SetUpAsync();
        long sales;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            sales = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, Day.AddDays(14), null, false, null));
            await s.Sales.SetOrderLineAsync(sales, f.Sweater, 30, 2500m, 22m, (await s.Sales.GetOrderAsync(sales)).RowVersion);
            await s.Sales.SetOrderLineAsync(sales, f.Hat, 5, 900m, 22m, (await s.Sales.GetOrderAsync(sales)).RowVersion);
            await s.Sales.SetOrderLineAsync(sales, f.Buttons, 100, 10m, 22m, (await s.Sales.GetOrderAsync(sales)).RowVersion);

            var draft = await s.ProductionOrders.ForSalesOrderAsync(sales);
            Assert.Equal((false, SalesProductionHints.NotConfirmed), (draft.CanCreate, draft.CreateHint));
            await s.Sales.ConfirmOrderAsync(sales, (await s.Sales.GetOrderAsync(sales)).RowVersion);

            // Уже есть заказ на 10 свитеров — новый черновик только на остаток 20.
            await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10) with { Source = ProductionSource.SalesOrder, SalesOrderId = sales });
            var plan = await s.ProductionOrders.ForSalesOrderAsync(sales);
            var lines = plan.Lines.ToDictionary(l => l.Code);
            Assert.Equal((30m, 10m, 20m, true), (lines["СВ-1"].Ordered, lines["СВ-1"].Planned, lines["СВ-1"].Remaining, lines["СВ-1"].CanPlan));
            Assert.Equal(SalesProductionHints.NoTechCard, lines["ША-1"].Hint);
            Assert.Equal(SalesProductionHints.Purchased, lines["ФУ-1"].Hint);
            Assert.True(plan.CanCreate);
        }

        // Двойное нажатие в двух вкладках: черновик создаст только одна.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            var service = new SalesProductionService(host.Factory, new TestUser { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId }, host.Clock);
            try
            {
                return (await service.CreateAsync(sales)).Created.Count;
            }
            catch (BusinessRuleException ex) when (ex.Code == "production.sales.all_planned")
            {
                return -1;
            }
        }));
        Assert.Equal(new[] { -1, 1 }, results.Order().ToArray());

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var plan = await s.ProductionOrders.ForSalesOrderAsync(sales);
            Assert.Equal((false, SalesProductionHints.AllPlanned, 30m), (plan.CanCreate, plan.CreateHint, plan.Lines.Single(l => l.Code == "СВ-1").Planned));
            var created = await s.ProductionOrders.GetAsync(plan.Orders.Last().Id);
            Assert.Equal((ProductionOrderStatus.Draft, 20m, Day.AddDays(14), f.Store, f.Wip, ProductionSource.SalesOrder),
                (created.Status, created.Quantity, created.DueDate!.Value, created.OutputWarehouseId, created.WipWarehouseId, created.Source));
            Assert.Equal("production.sales.all_planned", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionOrders.CreateForSalesOrderAsync(sales))).Code);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.False((await s.ProductionOrders.ForSalesOrderAsync(sales)).CanCreate);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ProductionOrders.CreateForSalesOrderAsync(sales));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.ProductionOrders.ForSalesOrderAsync(sales));
        }
    }

    /// <summary>D90 (дополнение «сборка из полуфабрикатов»): цикл запрещён, потребность по уровням, заказ на полуфабрикат, цена по факту выпуска.</summary>
    [SqlFact]
    public async Task Multi_level_production_with_semi_finished_items()
    {
        var f = await SetUpAsync();
        long sleeve, jacket, jacketCard, order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var pcs = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            sleeve = await s.Catalog.CreateItemAsync(new ItemCommand("РУ-1", "Рукав", ItemType.SemiFinished, pcs, null));
            jacket = await s.Catalog.CreateItemAsync(new ItemCommand("КФ-1", "Кофта", ItemType.Finished, pcs, null));
            var sleeveCard = await s.TechCards.CreateAsync(sleeve, 1, null);
            await s.TechCards.SetLineAsync(sleeveCard, f.Yarn, 0.2m, 0m, (await s.TechCards.GetAsync(sleeveCard)).RowVersion);
            await s.TechCards.ActivateAsync(sleeveCard, (await s.TechCards.GetAsync(sleeveCard)).RowVersion);
            jacketCard = await s.TechCards.CreateAsync(jacket, 1, null);
            await s.TechCards.SetLineAsync(jacketCard, f.Yarn, 0.3m, 0m, (await s.TechCards.GetAsync(jacketCard)).RowVersion);
            await s.TechCards.SetLineAsync(jacketCard, sleeve, 2m, 0m, (await s.TechCards.GetAsync(jacketCard)).RowVersion);
            await s.TechCards.ActivateAsync(jacketCard, (await s.TechCards.GetAsync(jacketCard)).RowVersion);

            // Кофта в составе рукава — замкнутый круг.
            var draft = await s.TechCards.CopyAsync(sleeveCard);
            Assert.Equal("techcard.cycle", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.TechCards.SetLineAsync(draft, jacket, 1m, 0m, (await s.TechCards.GetAsync(draft)).RowVersion))).Code);

            // 10 кофт по уровням: пряжа 0,3 × 10 + рукава 20 × 0,2 = 7 кг; без раскрытия — 3 кг пряжи и 20 рукавов.
            var flat = (await s.TechCards.RequirementAsync(jacketCard, 10)).ToDictionary(r => r.Code);
            Assert.Equal((3m, 20m), (flat["ПР-1"].Gross, flat["РУ-1"].Gross));
            var deep = Assert.Single(await s.TechCards.RequirementAsync(jacketCard, 10, explode: true));
            Assert.Equal(("ПР-1", 7m), (deep.Code, deep.Gross));

            order = await s.ProductionOrders.CreateAsync(Command(f, jacket, 10));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            var dto = await s.ProductionOrders.GetAsync(order);
            Assert.True(dto.Materials.Single(m => m.ItemId == sleeve).CanProduce);
            Assert.False(dto.Materials.Single(m => m.ItemId == f.Yarn).CanProduce);
            Assert.Equal("production.order.no_techcard", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionOrders.CreateChildAsync(order, f.Yarn))).Code);

            var child = await s.ProductionOrders.CreateChildAsync(order, sleeve);
            var childDto = await s.ProductionOrders.GetAsync(child);
            Assert.Equal((sleeve, 20m, order, dto.Number, f.Store, ProductionOrderStatus.Draft),
                (childDto.ItemId, childDto.Quantity, childDto.ParentOrderId!.Value, childDto.ParentOrderNumber, childDto.OutputWarehouseId, childDto.Status));
            Assert.Equal("production.order.child_covered", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionOrders.CreateChildAsync(order, sleeve))).Code);
            dto = await s.ProductionOrders.GetAsync(order);
            Assert.Equal((20m, child), (dto.Materials.Single(m => m.ItemId == sleeve).ChildPlanned, Assert.Single(dto.Children!).Id));

            // Выпуск рукавов: выдали 5 кг пряжи на 20 рукавов — фактическая себестоимость рукава 5 × 1 000 / 20 = 250 ₽ (по техкарте было бы 200).
            await s.ProductionOrders.ReleaseAsync(child, childDto.RowVersion);
            await s.Materials.MoveAsync(child, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 5m)));
            foreach (var stage in ProductionFlow.Stages)
            {
                await s.Floor.RecordAsync(child, Mark(stage, 20));
            }

            await s.Materials.OutputAsync(child, new OutputCommand(20, Day, [new(f.Yarn, 5m)], null));
            var next = await s.ProductionOrders.CreateAsync(Command(f, jacket, 1));
            Assert.Equal(250m, (await s.ProductionOrders.GetAsync(next)).Materials.Single(m => m.ItemId == sleeve).Price);

            // Рукава лежат на складе готовой продукции и выдаются в цех кофты обычной выдачей.
            await s.Materials.MoveAsync(order, Move(ProductionDocumentPurpose.Issue, f.Store, (sleeve, 20m)));
            Assert.Equal(20m, (await s.Materials.MaterialsAsync(order)).Lines.Single(l => l.ItemId == sleeve).Remaining);
        }
    }

    /// <summary>D94 (дополнение «планирование»): потребность открытых заказов, свободный остаток без склада цеха, ожидаемые поставки, черновики заказов поставщикам.</summary>
    [SqlFact]
    public async Task Material_planning_finds_shortage_and_creates_purchase_drafts()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Запущен на 100 свитеров (пряжа 32,96 кг, пуговиц 500), 10 кг уже выдано в цех; черновик на 200 (65,92 кг и 1 000 пуговиц).
            var released = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 100));
            await s.ProductionOrders.ReleaseAsync(released, (await s.ProductionOrders.GetAsync(released)).RowVersion);
            await s.Materials.MoveAsync(released, Move(ProductionDocumentPurpose.Issue, f.YarnStore, (f.Yarn, 10m)));
            await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 200));

            // Ожидается 300 пуговиц по подтверждённому заказу поставщику.
            var supplier = (await s.Db.PurchaseOrders.FirstAsync(o => o.OrganizationId == f.Org.OrganizationId)).SupplierId;
            var po = await s.Purchases.CreateOrderAsync(new PurchaseOrderHeader(Day, supplier, f.YarnStore, null, null, false, null));
            await s.Purchases.SetOrderLineAsync(po, f.Buttons, 300, 5m, null, (await s.Purchases.GetOrderAsync(po)).RowVersion);
            await s.Purchases.ConfirmOrderAsync(po, (await s.Purchases.GetOrderAsync(po)).RowVersion);

            var plan = await s.Planning.PlanAsync();
            var yarn = plan.Lines.Single(l => l.ItemId == f.Yarn);
            Assert.Equal((88.88m, 40m, 0m, 48.88m, 2), (yarn.Need, yarn.Free, yarn.Incoming, yarn.Shortage, yarn.Orders));
            Assert.Equal(1220m, yarn.LastPrice); // цена строки последней закупки — с НДС, как в том заказе
            var buttons = plan.Lines.Single(l => l.ItemId == f.Buttons);
            Assert.Equal((1500m, 500m, 300m, 700m), (buttons.Need, buttons.Free, buttons.Incoming, buttons.Shortage));
            Assert.True(plan.CanOrder);

            var result = await s.Planning.CreatePurchaseOrdersAsync(f.YarnStore);
            var draft = await s.Purchases.GetOrderAsync(Assert.Single(result.Orders));
            Assert.Equal(PurchaseOrderStatus.Draft, draft.Status);
            Assert.Equal((48.88m, 700m), (draft.Lines.Single(l => l.ItemId == f.Yarn).Quantity, draft.Lines.Single(l => l.ItemId == f.Buttons).Quantity));
            Assert.Empty(result.WithoutSupplier);

            // Черновики уже ожидаются — повторно заказывать нечего.
            Assert.All((await s.Planning.PlanAsync()).Lines, l => Assert.Equal(0m, l.Shortage));
            Assert.Equal("production.planning.no_shortage", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Planning.CreatePurchaseOrdersAsync(f.YarnStore))).Code);
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var plan = await s.Planning.PlanAsync();
            Assert.False(plan.CanOrder);
            Assert.All(plan.Lines, l => Assert.Null(l.LastPrice));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Planning.CreatePurchaseOrdersAsync(f.YarnStore));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            Assert.Empty((await s.Planning.PlanAsync()).Lines);
        }
    }

    private static MaterialMoveCommand Move(ProductionDocumentPurpose purpose, long? warehouse, params (long Item, decimal Quantity)[] lines) =>
        new(purpose, warehouse, null, null, Day, lines.Select(l => new MaterialLineCommand(l.Item, l.Quantity)).ToList());

    private static ProductionOrderCommand Command(Fixture f, long item, decimal quantity) =>
        new(item, quantity, Day, null, ProductionSource.Plan, null, f.Wip, f.Store, null);

    [SqlFact]
    public async Task Worker_marks_operation_from_terminal_and_output_goes_to_hr()
    {
        var f = await SetUpAsync();
        var (worker, employee) = await AddWorkerAsync(f, grant: true);
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 20));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
        }

        await using (var s = host.As(worker, f.Org.OrganizationId))
        {
            var op = await s.ShopFloor.OperationAsync(order, ProductionStage.Knitting);
            Assert.Equal((20m, 0m, 0m, true, ShopHint.None, "Петрова А. С.", $"ОП-{order}-1"),
                (op.Available, op.Done, op.MyToday, op.CanMark, op.Hint, op.EmployeeName, op.Code));
            Assert.Equal(Day, op.Today);

            // Частично: 12 из 20 с причиной из списка и комментарием.
            await s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(12, "Поломка оборудования", " станок № 3 "));
            op = await s.ShopFloor.OperationAsync(order, ProductionStage.Knitting);
            Assert.Equal((8m, 12m, 12m), (op.Available, op.Done, op.MyToday));
            Assert.Equal("production.stage.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(9, null, null)))).Code);
            Assert.Equal("production.shop.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(1, "Плохое настроение", null)))).Code);
            Assert.Equal("production.shop.comment", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(1, null, new string('я', 301))))).Code);
            await s.ShopFloor.MarkAsync(order, ProductionStage.Assembly, new ShopMarkCommand(12, null, null));

            var day = await s.ShopFloor.MyDayAsync();
            Assert.Equal((Day, 2, "Петрова А. С.", ShopHint.None), (day.Date, day.Entries.Count, day.EmployeeName, day.Hint));
            Assert.Equal("Не выполнено полностью: Поломка оборудования. станок № 3", day.Entries.Single(e => e.Stage == ProductionStage.Knitting).Reason);
            var tasks = day.Tasks.Where(t => t.OrderId == order).ToDictionary(t => t.Stage, t => t.Available);
            Assert.Equal(new Dictionary<ProductionStage, decimal> { [ProductionStage.Knitting] = 8, [ProductionStage.Steaming] = 12 }, tasks);
            Assert.Empty((await s.ShopFloor.MyDayAsync(Day.AddDays(-1))).Entries);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Отметка мастера с карточки — без исполнителя: в выработку сотрудников не попадает.
            await s.Floor.RecordAsync(order, new ProductionOperationCommand(ProductionStage.Knitting, ProductionOperationKind.Done, 3, null, null, Day));
            var history = (await s.Floor.ProgressAsync(order)).History;
            Assert.Equal(2, history.Count(h => h.Employee == "Петрова А. С."));
            Assert.Null(history.First().Employee);
        }

        await using (var db = host.NewDb())
        {
            var stored = await db.ProductionOperations.Where(o => o.ProductionOrderId == order).OrderBy(o => o.Id).ToListAsync();
            Assert.Equal(new long?[] { employee, employee, null }, stored.Select(o => o.EmployeeId).ToArray());
            Assert.All(stored, o => Assert.Equal(Day, o.OccurredOn));
            var audit = await db.AuditEntries.Where(e => e.Action == "production.stage.marked" && e.EntityId == order.ToString())
                .Select(e => e.After).ToListAsync();
            Assert.Equal(2, audit.Count(a => a!.Contains("исполнитель Петрова А. С. (таб. № 0101)")));

            var output = await new ProductionWorkOutputSource(db).OutputAsync(f.Org.OrganizationId, Day, Day, CancellationToken.None);
            Assert.Equal(
                new[] { (employee, Day, 1L, "Вязание", (long?)f.Sweater, 12m, "шт"), (employee, Day, 2L, "Сборка", (long?)f.Sweater, 12m, "шт") },
                output.Select(o => (o.EmployeeId, o.Date, o.OperationId, o.OperationName, o.ItemId, o.Quantity, o.Unit)).ToArray());
            Assert.Empty(await new ProductionWorkOutputSource(db).OutputAsync(f.Org.OrganizationId, Day.AddDays(1), Day.AddDays(5), CancellationToken.None));
        }
    }

    [SqlFact]
    public async Task Terminal_requires_right_linked_employee_order_in_work_and_own_organization()
    {
        var f = await SetUpAsync();
        var other = await SetUpAsync();
        var (worker, _) = await AddWorkerAsync(f, grant: true);
        var (noGrant, _) = await AddWorkerAsync(f, grant: false);
        long draft, released, foreign;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            draft = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 5));
            released = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 5));
            await s.ProductionOrders.ReleaseAsync(released, (await s.ProductionOrders.GetAsync(released)).RowVersion);

            // Владелец без карточки сотрудника видит операцию, но отметить с терминала не может.
            var op = await s.ShopFloor.OperationAsync(released, ProductionStage.Knitting);
            Assert.Equal((false, ShopHint.NoEmployee), (op.CanMark, op.Hint));
            Assert.Equal("production.shop.no_employee", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShopFloor.MarkAsync(released, ProductionStage.Knitting, new ShopMarkCommand(1, null, null)))).Code);
        }

        await using (var s = host.As(other.Org.OwnerUserId, other.Org.OrganizationId))
        {
            foreign = await s.ProductionOrders.CreateAsync(Command(other, other.Sweater, 5));
            await s.ProductionOrders.ReleaseAsync(foreign, (await s.ProductionOrders.GetAsync(foreign)).RowVersion);
        }

        await using (var s = host.As(worker, f.Org.OrganizationId))
        {
            Assert.Equal((false, ShopHint.NotInWork), ((await s.ShopFloor.OperationAsync(draft, ProductionStage.Knitting)).CanMark,
                (await s.ShopFloor.OperationAsync(draft, ProductionStage.Knitting)).Hint));
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.ShopFloor.MarkAsync(draft, ProductionStage.Knitting, new ShopMarkCommand(1, null, null)));
            Assert.Equal(ShopHint.NothingAvailable, (await s.ShopFloor.OperationAsync(released, ProductionStage.Assembly)).Hint);
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShopFloor.OperationAsync(foreign, ProductionStage.Knitting));
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShopFloor.MarkAsync(foreign, ProductionStage.Knitting, new ShopMarkCommand(1, null, null)));
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShopFloor.OperationAsync(released, (ProductionStage)9));
            Assert.DoesNotContain((await s.ShopFloor.MyDayAsync()).Tasks, t => t.OrderId == foreign);
        }

        await using (var s = host.As(noGrant, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ShopFloor.MyDayAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ShopFloor.MarkAsync(released, ProductionStage.Knitting, new ShopMarkCommand(1, null, null)));
        }

        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ShopFloor.OperationAsync(released, ProductionStage.Knitting));
        }

        await using (var db = host.NewDb())
        {
            Assert.False(await db.ProductionOperations.AnyAsync(o => o.ProductionOrderId == released || o.ProductionOrderId == foreign));
        }
    }

    [SqlFact]
    public async Task Worker_mark_is_corrected_today_by_worker_or_master_and_output_follows()
    {
        var f = await SetUpAsync();
        var (worker, employee) = await AddWorkerAsync(f, grant: true);
        var (colleague, _) = await AddWorkerAsync(f, grant: true, "0103", "Сидорова");
        long order, mark, masterMark;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 20));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            masterMark = await s.Floor.RecordAsync(order, new ProductionOperationCommand(ProductionStage.Knitting, ProductionOperationKind.Done, 2, null, null, Day));
        }

        await using (var s = host.As(worker, f.Org.OrganizationId))
        {
            mark = await s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(12, null, null));
            // Ошиблась на 2 — уменьшает сама в тот же день, с причиной.
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Floor.CorrectAsync(order, mark, 2, " "));
            await s.Floor.CorrectAsync(order, mark, 2, "ошиблась при вводе");
            Assert.Equal("production.stage.correct_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.CorrectAsync(order, mark, 11, "ещё"))).Code);
            var entry = (await s.ShopFloor.MyDayAsync()).Entries.Single(e => e.Id == mark);
            Assert.Equal((10m, true), (entry.Remaining!.Value, entry.CanCorrect));
            Assert.Equal(10m, (await s.ShopFloor.OperationAsync(order, ProductionStage.Knitting)).MyToday);
            // Отметку мастера без исполнителя так не исправить.
            Assert.Equal("production.stage.correct_target", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Floor.CorrectAsync(order, masterMark, 1, "чужая"))).Code);
        }

        await using (var s = host.As(colleague, f.Org.OrganizationId))
        {
            // Чужую отметку рабочий не исправляет — нужно право изменения заказов.
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Floor.CorrectAsync(order, mark, 1, "не моя"));
        }

        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(1);
        try
        {
            await using (var s = host.As(worker, f.Org.OrganizationId))
            {
                // На следующий день своя вчерашняя отметка — только через мастера.
                await Assert.ThrowsAsync<AccessDeniedException>(() => s.Floor.CorrectAsync(order, mark, 1, "вчера"));
                Assert.False((await s.ShopFloor.MyDayAsync(Day)).Entries.Single(e => e.Id == mark).CanCorrect);
            }

            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                Assert.True((await s.Floor.ProgressAsync(order)).History.Single(h => h.Id == mark).CanCorrect);
                await s.Floor.CorrectAsync(order, mark, 1, "пересчёт мастером");
                var h = (await s.Floor.ProgressAsync(order)).History.Single(x => x.Id == mark);
                Assert.Equal(9m, h.Remaining);
                await s.Floor.RecordAsync(order, new ProductionOperationCommand(ProductionStage.Assembly, ProductionOperationKind.Done, 11, null, null, Day.AddDays(1)));
                // Ниже ушедшего на сборку не уменьшить: сделано на вязании 2 + 9 = 11, на сборке 11.
                Assert.Equal("production.stage.correction_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                    s.Floor.CorrectAsync(order, mark, 1, "ещё"))).Code);
            }
        }
        finally
        {
            host.Clock.UtcNow = host.Clock.UtcNow.AddDays(-1);
        }

        await using (var db = host.NewDb())
        {
            var corrections = await db.ProductionOperations.Where(o => o.CorrectsOperationId == mark).OrderBy(o => o.Id).ToListAsync();
            Assert.Equal(new[] { (Day, -2m), (Day.AddDays(1), -1m) }, corrections.Select(c => (c.OccurredOn, c.Quantity)).ToArray());
            Assert.All(corrections, c => Assert.Equal((ProductionOperationKind.Correction, (long?)employee), (c.Kind, c.EmployeeId)));
            // Выработка: вчера 10 (12 − 2), сегодня −1 — прошлый день задним числом не меняется.
            var output = await new ProductionWorkOutputSource(db).OutputAsync(f.Org.OrganizationId, Day, Day.AddDays(1), CancellationToken.None);
            Assert.Equal(new[] { (Day, 10m), (Day.AddDays(1), -1m) },
                output.Where(o => o.EmployeeId == employee).Select(o => (o.Date, o.Quantity)).ToArray());
            // Ссылку на отметку может иметь только исправление с исполнителем — и в обход приложения.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_operations] SET [CorrectsOperationId] = {0} WHERE [Id] = {1}", mark, masterMark));
        }
    }

    [SqlFact]
    public async Task Shift_task_plan_fact_percent_and_shortfall_reason()
    {
        var f = await SetUpAsync();
        var other = await SetUpAsync();
        var (worker, employee) = await AddWorkerAsync(f, grant: true);
        var (colleague, colleagueEmployee) = await AddWorkerAsync(f, grant: true, "0103", "Сидорова");
        long order, draft, task;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 20));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            draft = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 5));

            var board = await s.ShiftTasks.BoardAsync();
            Assert.Equal((Day, true), (board.Date, board.CanAssign));
            Assert.Contains(board.Employees, e => e.Id == employee && e.Name == "Петрова А. С.");
            Assert.Contains(board.Orders, o => o.Id == order);
            Assert.DoesNotContain(board.Orders, o => o.Id == draft);

            task = await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Knitting, 10, "до обеда"));
            Assert.Equal("production.shift.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Knitting, 5, null)))).Code);
            Assert.Equal("production.shift.past", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day.AddDays(-1), employee, order, ProductionStage.Knitting, 5, null)))).Code);
            Assert.Equal("production.order.not_in_work", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, draft, ProductionStage.Knitting, 5, null)))).Code);
            Assert.Equal("production.shift.planned", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, colleagueEmployee, order, ProductionStage.Knitting, 0, null)))).Code);
            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day.AddDays(1), colleagueEmployee, order, ProductionStage.Assembly, 4, null));
        }

        await using (var s = host.As(worker, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Assembly, 1, null)));
            await s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(6, "Не хватило времени", null));
            var mine = Assert.Single((await s.ShopFloor.MyDayAsync()).Shift!);
            Assert.Equal((task, 10m, 6m, 60, false, "до обеда"), (mine.Id, mine.Planned, mine.Fact, mine.Percent, mine.Done, mine.Comment));

            Assert.Equal("production.shop.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.ReportShortfallAsync(task, "Погода", null, mine.RowVersion))).Code);
            await s.ShiftTasks.ReportShortfallAsync(task, "Поломка оборудования", "станок № 3", mine.RowVersion);
            // Старая версия строки — конфликт, а не тихая перезапись.
            await Assert.ThrowsAnyAsync<Exception>(() => s.ShiftTasks.ReportShortfallAsync(task, "Другая причина", null, mine.RowVersion));
        }

        await using (var s = host.As(colleague, f.Org.OrganizationId))
        {
            // Чужое задание сотрудник не видит; своё на завтра — причину указывать рано.
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShiftTasks.ReportShortfallAsync(task, "Другая причина", null, []));
            Assert.Empty((await s.ShopFloor.MyDayAsync()).Shift!);
            var tomorrow = Assert.Single((await s.ShopFloor.MyDayAsync(Day.AddDays(1))).Shift!);
            Assert.Equal("production.shift.future", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ShiftTasks.ReportShortfallAsync(tomorrow.Id, "Другая причина", null, tomorrow.RowVersion))).Code);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var t = (await s.ShiftTasks.BoardAsync()).Tasks.Single(x => x.Id == task);
            Assert.Equal((6m, 60, "Поломка оборудования", "станок № 3"), (t.Fact, t.Percent, t.ShortfallReason, t.ShortfallComment));
            await s.ShiftTasks.CancelAsync(task, t.RowVersion);
            Assert.True((await s.ShiftTasks.BoardAsync()).Tasks.Single(x => x.Id == task).IsCancelled);
            // После отмены можно назначить заново.
            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Knitting, 8, null));
        }

        await using (var s = host.As(other.Org.OwnerUserId, other.Org.OrganizationId))
        {
            Assert.Empty((await s.ShiftTasks.BoardAsync(Day)).Tasks);
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShiftTasks.CancelAsync(task, []));
            await Assert.ThrowsAsync<NotFoundException>(() => s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Knitting, 1, null)));
        }

        await using (var db = host.NewDb())
        {
            var actions = await db.AuditEntries.Where(e => e.EntityType == "ShiftTask" && e.EntityId == task.ToString()).Select(e => e.Action).ToListAsync();
            Assert.Equal(new[] { "production.shift.assigned", "production.shift.cancelled", "production.shift.shortfall" }, actions.Order().ToArray());
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_shift_tasks] SET [PlannedQuantity] = 0 WHERE [Id] = {0}", task));
        }
    }

    [SqlFact]
    public async Task Organization_settings_drive_tolerance_and_quality_discipline_points()
    {
        var f = await SetUpAsync();
        var other = await SetUpAsync();
        var (worker, employee) = await AddWorkerAsync(f, grant: true);
        var (colleague, colleagueEmployee) = await AddWorkerAsync(f, grant: true, "0103", "Сидорова");
        long order, foreignEmployee;
        await using (var s = host.As(other.Org.OwnerUserId, other.Org.OrganizationId))
        {
            var structure = await s.Structure.GetAsync();
            var department = structure.Departments.FirstOrDefault()?.Id ?? await s.Structure.CreateDepartmentAsync("Цех", null);
            foreignEmployee = await s.Employees.HireAsync(new KnitErp.Application.Structure.EmployeeCommand("0901", "Чужая", "Анна", null, department,
                await s.Structure.CreatePositionAsync("Швея"), Day.AddYears(-1)));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var settings = await s.ProductionSettings.GetAsync();
            Assert.Equal((5m, false, 1, 0, -1, -1, true, (byte[]?)null), (settings.TolerancePercent, settings.PointsEnabled, settings.PointsTaskDone,
                settings.PointsTaskMissedWithReason, settings.PointsTaskMissedNoReason, settings.PointsPerDefectUnit, settings.CanEdit, settings.RowVersion));
            Assert.Equal("production.settings.tolerance", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(60, true, 1, 0, -1, -1), null))).Code);
            Assert.Equal("production.settings.points", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(2, true, 101, 0, -1, -1), null))).Code);
            await s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(2, true, 2, 0, -3, -1), null);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(3, true, 2, 0, -3, -1), null));
            settings = await s.ProductionSettings.GetAsync();
            Assert.Equal((2m, true, 2, -3), (settings.TolerancePercent, settings.PointsEnabled, settings.PointsTaskDone, settings.PointsTaskMissedNoReason));

            order = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 20));
            await s.ProductionOrders.ReleaseAsync(order, (await s.ProductionOrders.GetAsync(order)).RowVersion);
            Assert.Equal(2m, (await s.Materials.MaterialsAsync(order)).TolerancePercent);

            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, order, ProductionStage.Knitting, 10, null));
            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day.AddDays(1), employee, order, ProductionStage.Knitting, 3, null));
            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, colleagueEmployee, order, ProductionStage.Knitting, 5, null));
        }

        await using (var s = host.As(other.Org.OwnerUserId, other.Org.OrganizationId))
        {
            Assert.Equal(5m, (await s.ProductionSettings.GetAsync()).TolerancePercent);
        }

        await using (var s = host.As(worker, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(9, false, 0, 0, 0, 0), null));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Scores.BoardAsync(Day, Day));
            await s.ShopFloor.MarkAsync(order, ProductionStage.Knitting, new ShopMarkCommand(10, null, null));
        }

        await using (var s = host.As(colleague, f.Org.OrganizationId))
        {
            var task = Assert.Single((await s.ShopFloor.MyDayAsync()).Shift!);
            await s.ShiftTasks.ReportShortfallAsync(task.Id, "Ждал предыдущий этап", null, task.RowVersion);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            foreach (var stage in new[] { ProductionStage.Assembly, ProductionStage.Steaming })
            {
                await s.Floor.RecordAsync(order, new ProductionOperationCommand(stage, ProductionOperationKind.Done, 10, null, null, Day));
            }

            await Assert.ThrowsAsync<NotFoundException>(() => s.Floor.RecordAsync(order, new ProductionOperationCommand(ProductionStage.QualityControl,
                ProductionOperationKind.Defect, 1, null, "петли", Day, foreignEmployee)));
            Assert.Equal("production.stage.culprit_kind", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Floor.RecordAsync(order,
                new ProductionOperationCommand(ProductionStage.QualityControl, ProductionOperationKind.Done, 1, null, null, Day, employee)))).Code);
            await s.Floor.RecordAsync(order, new ProductionOperationCommand(ProductionStage.QualityControl, ProductionOperationKind.Defect, 2, null,
                "спущенные петли", Day, employee));
            var progress = await s.Floor.ProgressAsync(order);
            Assert.Contains(progress.Employees!, e => e.Id == employee);
            Assert.Equal("Петрова А. С.", progress.History.First().Culprit);
        }

        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(2);
        try
        {
            await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
            {
                var board = await s.Scores.BoardAsync(Day, Day.AddDays(2));
                var w = board.Rows.Single(r => r.EmployeeId == employee);
                // 1 выполнено × 2 + 1 без причины × (−3) + 2 единицы брака × (−1) = −3.
                Assert.Equal((1, 0, 1, 0, 50, 2m, (int?)-3), (w.TasksDone, w.MissedWithReason, w.MissedNoReason, w.InProgress, w.AveragePercent, w.DefectUnits, w.Points));
                var c = board.Rows.Single(r => r.EmployeeId == colleagueEmployee);
                Assert.Equal((0, 1, 0, (int?)0), (c.TasksDone, c.MissedWithReason, c.MissedNoReason, c.Points));
                Assert.Equal("production.score.period", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Scores.BoardAsync(Day, Day.AddDays(100)))).Code);

                await s.ProductionSettings.SaveAsync(new ProductionSettingsCommand(2, false, 2, 0, -3, -1), (await s.ProductionSettings.GetAsync()).RowVersion);
                Assert.Null((await s.Scores.BoardAsync(Day, Day.AddDays(2))).Rows.Single(r => r.EmployeeId == employee).Points);
            }

            await using (var s = host.As(worker, f.Org.OrganizationId))
            {
                var mine = await s.Scores.MineAsync();
                Assert.Equal((employee, 1, 1, 2m, (int?)null), (mine!.EmployeeId, mine.TasksDone, mine.MissedNoReason, mine.DefectUnits, mine.Points));
            }
        }
        finally
        {
            host.Clock.UtcNow = host.Clock.UtcNow.AddDays(-2);
        }

        await using (var db = host.NewDb())
        {
            Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.Action == "production.settings.changed" && e.OrganizationId == f.Org.OrganizationId));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[production_settings] SET [TolerancePercent] = 80 WHERE [OrganizationId] = {0}", f.Org.OrganizationId));
        }
    }

    [SqlFact]
    public async Task Dashboard_shows_production_orders_progress_late_defects_and_missed_shift_tasks()
    {
        var f = await SetUpAsync();
        var (worker, employee) = await AddWorkerAsync(f, grant: true);
        long lateOrder, readyOrder;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            lateOrder = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 10) with { DueDate = Day.AddDays(1) });
            readyOrder = await s.ProductionOrders.CreateAsync(Command(f, f.Sweater, 5) with { DueDate = Day.AddDays(5) });
            foreach (var id in new[] { lateOrder, readyOrder })
            {
                await s.ProductionOrders.ReleaseAsync(id, (await s.ProductionOrders.GetAsync(id)).RowVersion);
            }

            await s.Floor.RecordAsync(lateOrder, new ProductionOperationCommand(ProductionStage.Knitting, ProductionOperationKind.Done, 10, null, null, Day));
            await s.Floor.RecordAsync(lateOrder, new ProductionOperationCommand(ProductionStage.Assembly, ProductionOperationKind.Done, 4, null, null, Day));
            // Готовый заказ: брак 1 на контроле качества довязан и прошёл маршрут заново — упаковано 5 из 5.
            foreach (var stage in new[] { ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming })
            {
                await s.Floor.RecordAsync(readyOrder, new ProductionOperationCommand(stage, ProductionOperationKind.Done, 5, null, null, Day));
            }

            await s.Floor.RecordAsync(readyOrder, new ProductionOperationCommand(ProductionStage.QualityControl, ProductionOperationKind.Defect, 1, null, "дыра", Day));
            await s.Floor.RecordAsync(readyOrder, new ProductionOperationCommand(ProductionStage.QualityControl, ProductionOperationKind.Done, 4, null, null, Day));
            foreach (var stage in new[] { ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming, ProductionStage.QualityControl })
            {
                await s.Floor.RecordAsync(readyOrder, new ProductionOperationCommand(stage, ProductionOperationKind.Done, 1, null, null, Day));
            }

            await s.Floor.RecordAsync(readyOrder, new ProductionOperationCommand(ProductionStage.Packing, ProductionOperationKind.Done, 5, null, null, Day));
            await s.ShiftTasks.AssignAsync(new ShiftTaskCommand(Day, employee, lateOrder, ProductionStage.Assembly, 6, null));
        }

        // Через два дня: первый заказ просрочен, вчерашнее задание не выполнено и без причины.
        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(2);
        try
        {
            await using var db = host.NewDb();
            var owner = new TestUser { UserId = f.Org.OwnerUserId, OrganizationId = f.Org.OrganizationId };
            var guard = new AccessGuard(db, owner, host.Clock);
            var dashboard = new KnitErp.Application.Organizations.DashboardService(db, guard, host.Clock,
                new KnitErp.Application.Organizations.LaunchReadinessService(db, guard, host.Clock),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                [new ProductionDashboardSource()]);
            // Задание было на Day: на главном экране «сегодня» = Day + 2, вчера = Day + 1 — задания нет; источник на Day + 1 его показывает.
            var data = (await dashboard.GetAsync(refresh: true)).Production!.Production!;
            Assert.Equal((2, 1), (data.OrdersInWork, data.OrdersLate));
            var late = data.Orders.First();
            Assert.Equal((lateOrder, true, 0m, "Сборка"), (late.Id, late.Late, late.Percent, late.Operation));
            var ready = data.Orders.Single(o => o.Id == readyOrder);
            Assert.Equal((100m, false, "Упаковка"), (ready.Percent, ready.Late, ready.Operation));
            Assert.Contains(data.Alerts, a => a.Text == ProductionDashboardSource.LateText && a.Count == 1 && a.Critical);
            Assert.Contains(data.Alerts, a => a.Text == ProductionDashboardSource.DefectText && a.Count == 1);
            Assert.DoesNotContain(data.Alerts, a => a.Text == ProductionDashboardSource.MissedTasksText);
            Assert.Null(data.OutputToday);

            var ctx = await guard.CurrentAsync();
            var yesterdayView = await new ProductionDashboardSource().GetAsync(db, ctx, Day.AddDays(1), CancellationToken.None);
            Assert.Contains(yesterdayView!.Alerts, a => a.Text == ProductionDashboardSource.MissedTasksText && a.Count == 1);
        }
        finally
        {
            host.Clock.UtcNow = host.Clock.UtcNow.AddDays(-2);
        }

        // Рабочий без права просмотра заказов — данных производства нет; другая организация не видит чужие заказы.
        await using (var db = host.NewDb())
        {
            var workerGuard = new AccessGuard(db, new TestUser { UserId = worker, OrganizationId = f.Org.OrganizationId }, host.Clock);
            Assert.Null(await new ProductionDashboardSource().GetAsync(db, await workerGuard.CurrentAsync(), Day, CancellationToken.None));

            var other = await SetUpAsync();
            var guard = new AccessGuard(db, new TestUser { UserId = other.Org.OwnerUserId, OrganizationId = other.Org.OrganizationId }, host.Clock);
            var foreign = await new ProductionDashboardSource().GetAsync(db, await guard.CurrentAsync(), Day, CancellationToken.None);
            Assert.Equal((0, 0), (foreign!.OrdersInWork, foreign.Orders.Count));
        }
    }

    /// <summary>Рабочий «Сотрудник» с карточкой сотрудника (вязальщица) и, если нужно, отдельной выдачей права отмечать этапы.</summary>
    private async Task<(long User, long Employee)> AddWorkerAsync(Fixture f, bool grant, string? personnelNumber = null, string lastName = "Петрова")
    {
        long user, employee;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var structure = await s.Structure.GetAsync();
            var department = structure.Departments.FirstOrDefault()?.Id ?? await s.Structure.CreateDepartmentAsync("Вязальный цех", null);
            var position = await s.Structure.CreatePositionAsync($"Вязальщица {Guid.NewGuid():N}"[..20]);
            var number = personnelNumber ?? (grant ? "0101" : "0102");
            employee = await s.Employees.HireAsync(new KnitErp.Application.Structure.EmployeeCommand(number, lastName, "Анна", "Сергеевна", department, position, Day.AddYears(-1)));
            user = (await s.Access.InviteAsync(new InviteUserCommand($"worker-{Guid.NewGuid():N}@test.local", "Вязальщица", SystemRoles.Employee, null,
                EmployeeId: employee))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == user)).Activate();
            if (grant)
            {
                db.RoleAssignments.Add(RoleAssignment.ForPermission(f.Org.OrganizationId, user, Permissions.ProductionStageMark, false, f.Org.OwnerUserId,
                    host.Clock.UtcNow, "мастер цеха"));
            }

            await db.SaveChangesAsync();
        }

        return (user, employee);
    }

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
