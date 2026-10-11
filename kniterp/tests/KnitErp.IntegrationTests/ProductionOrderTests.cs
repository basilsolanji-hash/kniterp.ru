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

    /// <summary>D96: требование-накладная (М-11) на выдачу и накладная (МХ-18) на выпуск; цены — только с правом, отходы не печатаются.</summary>
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
            Assert.Equal("production.print.purpose", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.ProductionPrint.DocumentAsync(waste))).Code);
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

    private static MaterialMoveCommand Move(ProductionDocumentPurpose purpose, long? warehouse, params (long Item, decimal Quantity)[] lines) =>
        new(purpose, warehouse, null, null, Day, lines.Select(l => new MaterialLineCommand(l.Item, l.Quantity)).ToList());

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
