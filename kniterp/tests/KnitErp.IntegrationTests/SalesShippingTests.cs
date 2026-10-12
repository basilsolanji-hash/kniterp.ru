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
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Этап 5 продаж (D1150–D1159) и коды маркировки (D1108): сборка и «собрано» двигает этап; отгрузка нескольких заказов вместе;
/// возврат годный/брак (карантин, списание) — остатки; УКД; зачёт в аванс и возврат денег; маркировка; права и организация.
/// </summary>
public sealed class SalesShippingTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 815_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);
    private const string Gtin = "04601234567886";
    private const string OtherGtin = "04601234567893";

    [SqlFact]
    public async Task Pick_list_partial_and_full_picking_moves_stage_and_keeps_locations()
    {
        var f = await SetUpAsync();
        long first, second, list;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await StockAsync(s, f, f.Sweater, 100);
            first = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 5));
            second = await ConfirmedAsync(s, f, f.Other, (f.Sweater, 3));
            Assert.Equal("В обработке", (await s.Sales.GetOrderAsync(first)).Stage!.Name);
            var candidates = await s.Sales.OrdersToShipAsync();
            Assert.Contains(candidates, o => o.OrderId == first);

            list = await s.Sales.CreatePickListAsync([first, second], "Утро");
            Assert.Equal("sales.pick.already", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CreatePickListAsync([first], null))).Code);
        }

        // Кладовщик склада: место хранения, частичная сборка — этап не меняется, затем всё собрано — «Собран».
        await using (var k = host.As(f.Senior, f.Org.OrganizationId))
        {
            var dto = await k.Sales.GetPickListAsync(list);
            Assert.True(dto.CanPick);
            Assert.Equal(2, dto.Lines.Count);
            var line = dto.Lines.Single(l => l.OrderId == first);
            Assert.Equal(100m, line.Stock);
            await k.Sales.SetPickLocationAsync(list, line.Id, "А-01", dto.RowVersion);
            await k.Sales.SetPickedAsync(list, line.Id, 2, (await k.Sales.GetPickListAsync(list)).RowVersion);
            dto = await k.Sales.GetPickListAsync(list);
            Assert.True(dto.PartiallyPicked);
            Assert.Equal("sales.pick.quantity", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Sales.SetPickedAsync(list, line.Id, 6, dto.RowVersion))).Code);
            await k.Sales.SetPickedAsync(list, line.Id, 5, dto.RowVersion);
            await k.Sales.PickAllAsync(list, (await k.Sales.GetPickListAsync(list)).RowVersion);
            dto = await k.Sales.GetPickListAsync(list);
            Assert.Equal(PickListStatus.Picked, dto.Status);
            Assert.NotNull(dto.PickedBy);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal("Собран", (await s.Sales.GetOrderAsync(first)).Stage!.Name);
            Assert.Equal("Собран", (await s.Sales.GetOrderAsync(second)).Stage!.Name);
            var rules = await s.Stages.RulesAsync();
            Assert.NotNull(rules.Single(r => r.Trigger == StageTrigger.Picked).StageId);

            // Новое задание по позиции на этом складе — место из прошлого задания.
            var third = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 1));
            var next = await s.Sales.CreatePickListAsync([third], null);
            Assert.Equal("А-01", (await s.Sales.GetPickListAsync(next)).Lines.Single().Location);
            Assert.Single(await s.Sales.ListPickListsAsync(new PickListFilter(PickListStatus.Picked)));
            await s.Sales.CancelPickListAsync(next, (await s.Sales.GetPickListAsync(next)).RowVersion);
            Assert.Equal(PickListStatus.Cancelled, (await s.Sales.GetPickListAsync(next)).Status);
        }

        // Кладовщик чужого склада задание не видит и отметить не может; другая организация — 404.
        await using (var x = host.As(f.Stranger, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => x.Sales.GetPickListAsync(list));
            await Assert.ThrowsAsync<NotFoundException>(() => x.Sales.PickAllAsync(list, [0, 0, 0, 0, 0, 0, 0, 1]));
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.GetPickListAsync(list));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.CreatePickListAsync([first], null));
        }
    }

    [SqlFact]
    public async Task Two_orders_ship_together_stock_shipped_and_debt_per_order()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await StockAsync(s, f, f.Sweater, 20);
        var a = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 5));
        var b = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 7));
        var foreign = await ConfirmedAsync(s, f, f.Other, (f.Sweater, 1));
        Assert.Equal("sales.shipment_group.mixed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.CreateShipmentGroupAsync([a, foreign], new SalesShipmentLogistics(null, null, null, null, null)))).Code);

        var methods = await s.Sales.DeliveryMethodsAsync();
        Assert.Equal(3, methods.Count);
        var carrier = methods.Single(m => m.Kind == DeliveryKind.Carrier).Id;
        var group = await s.Sales.CreateShipmentGroupAsync([a, b], new SalesShipmentLogistics(carrier, "TRK-1", 3, 25.5m, "одна машина"));
        var dto = await s.Sales.GetShipmentGroupAsync(group);
        Assert.Equal((2, 2, 3, 25.5m, "Транспортная компания"), (dto.Documents.Count, dto.Lines.Count, dto.Packages!.Value, dto.GrossWeightKg!.Value, dto.Delivery));
        Assert.Equal("sales.shipment_group.busy", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.CreateShipmentGroupAsync([a], new SalesShipmentLogistics(null, null, null, null, null)))).Code);

        await s.Sales.PostShipmentGroupAsync(group, dto.RowVersion);
        dto = await s.Sales.GetShipmentGroupAsync(group);
        Assert.Equal(ShipmentGroupStatus.Posted, dto.Status);
        Assert.All(dto.Documents, d => Assert.Equal(StockDocumentStatus.Posted, d.Status));
        Assert.Equal(8m, await BalanceAsync(f, f.Store, f.Sweater));

        // По каждому заказу — своя отгрузка: отгружено полностью, долг по ценам заказа; общий долг покупателя — сумма.
        Assert.Equal((ShipmentState.Full, 5_000m), ((await s.Sales.GetOrderAsync(a)).Shipped, (await s.Sales.GetOrderAsync(a)).Debt!.Value));
        Assert.Equal((ShipmentState.Full, 7_000m), ((await s.Sales.GetOrderAsync(b)).Shipped, (await s.Sales.GetOrderAsync(b)).Debt!.Value));
        Assert.Equal(12_000m, (await s.Sales.BalancesAsync()).Single(x => x.CustomerId == f.Customer).Debt);

        // Трек-номер приходит позже — логистика правится и после проведения.
        await s.Sales.SetShipmentLogisticsAsync(group, new SalesShipmentLogistics(carrier, "TRK-2", 3, 25.5m, null), dto.RowVersion);
        Assert.Equal("TRK-2", (await s.Sales.GetShipmentGroupAsync(group)).TrackingNumber);
        Assert.Single(await s.Sales.ListShipmentGroupsAsync(new ShipmentGroupFilter(Search: "TRK-2")));
        Assert.Contains((await s.Sales.ShippingPanelAsync(a)).Groups, g => g.Id == group);

        // D1159: отгрузка больше остатка заказа не проводится.
        var c = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 2));
        var ship = await s.Sales.CreateShipmentAsync(c);
        await s.Documents.SetLineAsync(ship, f.Sweater, 3, (await s.Documents.GetAsync(ship)).RowVersion);
        Assert.Equal("sales.shipment.exceeds_order", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion))).Code);

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.GetShipmentGroupAsync(group));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.PostShipmentGroupAsync(group, dto.RowVersion));
    }

    [SqlFact]
    public async Task Return_good_and_defects_to_quarantine_and_write_off_with_correction()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        await StockAsync(s, f, f.Sweater, 10);
        await StockAsync(s, f, f.Hat, 10);
        var order = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 10), (f.Hat, 5));
        await ShipAsync(s, order);

        var ret = await s.Sales.CreateReturnAsync(order);
        await s.Documents.SetLineAsync(ret, f.Sweater, 4, (await s.Documents.GetAsync(ret)).RowVersion);
        var panel = (await s.Sales.StockDocumentPanelAsync(ret)).Return!;
        Assert.True(panel.CanEdit);
        Assert.Equal(5, panel.Reasons.Count);
        var writeOffReason = panel.WriteOffReasons.First().Id;
        await s.Sales.SetReturnDetailsAsync(ret, new ReturnDetailsInput(panel.Reasons.First(r => r.Name == "Брак").Id, "шов", f.Quarantine, writeOffReason), null);
        panel = (await s.Sales.StockDocumentPanelAsync(ret)).Return!;
        await s.Sales.SetReturnQualityAsync(ret, f.Sweater, ReturnQuality.Quarantine, 3, panel.RowVersion);
        panel = (await s.Sales.StockDocumentPanelAsync(ret)).Return!;
        Assert.Equal("sales.return.quality_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.SetReturnQualityAsync(ret, f.Hat, ReturnQuality.WriteOff, 6, panel.RowVersion))).Code);
        await s.Sales.SetReturnQualityAsync(ret, f.Hat, ReturnQuality.WriteOff, 2, panel.RowVersion);
        panel = (await s.Sales.StockDocumentPanelAsync(ret)).Return!;
        Assert.Equal((ReturnQuality.Quarantine, 3m), (panel.Lines.Single(l => l.ItemId == f.Sweater).Quality, panel.Lines.Single(l => l.ItemId == f.Sweater).DefectQuantity));

        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
        // Свитеры: вернули 4, годных 1 — на складе 1, в карантине 3. Шапки: вернули 5, 2 списаны — на складе 3.
        Assert.Equal(1m, await BalanceAsync(f, f.Store, f.Sweater));
        Assert.Equal(3m, await BalanceAsync(f, f.Quarantine, f.Sweater));
        Assert.Equal(3m, await BalanceAsync(f, f.Store, f.Hat));
        Assert.Equal(0m, await BalanceAsync(f, f.Quarantine, f.Hat));
        panel = (await s.Sales.StockDocumentPanelAsync(ret)).Return!;
        Assert.NotNull(panel.TransferDocumentId);
        Assert.NotNull(panel.WriteOffDocumentId);
        Assert.False(panel.CanEdit);
        Assert.True(panel.CanPrintCorrection);
        Assert.Equal(StockDocumentStatus.Posted, (await s.Documents.GetAsync(panel.WriteOffDocumentId!.Value)).Status);
        Assert.Equal(writeOffReason, (await s.Documents.GetAsync(panel.WriteOffDocumentId.Value)).ReasonId);

        // УКД: свитеры 10 → 6, шапки 5 → 0; уменьшение — по ценам заказа (1000 и 500 с НДС).
        var ukd = await s.Sales.CorrectionAsync(ret);
        var sweater = ukd.Lines.Single(l => l.Code == "СВ-1");
        Assert.Equal((10m, 6m, 10_000m, 6_000m, 4_000m), (sweater.QuantityBefore, sweater.QuantityAfter, sweater.TotalBefore, sweater.TotalAfter, sweater.TotalDecrease));
        Assert.Equal(6_500m, ukd.TotalDecrease);
        Assert.Contains("№", ukd.Shipments);

        // Возврат с оформленным браком не сторнируется, пока брак не сторнирован; отгрузка после возврата — тоже (D1159).
        Assert.Equal("sales.return.defects_posted", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.ReverseAsync(ret, "ошибка", (await s.Documents.GetAsync(ret)).RowVersion))).Code);
        var shipment = (await s.Sales.GetOrderAsync(order)).Documents.Single(d => d.Kind == StockOperationKind.Shipment).Id;
        Assert.Equal("sales.shipment.reverse_after_return", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Documents.ReverseAsync(shipment, "ошибка", (await s.Documents.GetAsync(shipment)).RowVersion))).Code);

        // Кладовщик чужого склада качество возврата не правит.
        var ret2 = await s.Sales.CreateReturnAsync(order);
        await using (var x = host.As(f.Stranger, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => x.Sales.SetReturnQualityAsync(ret2, f.Sweater, ReturnQuality.WriteOff, 1, null));
        }

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.CorrectionAsync(ret));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.SetReturnDetailsAsync(ret2, new ReturnDetailsInput(null, null, null, null), null));
    }

    [SqlFact]
    public async Task Overpayment_after_return_becomes_advance_or_is_refunded()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var entity = (await s.LegalEntities.ListAsync()).Single();
        var bank = await s.LegalEntities.AddAccountAsync(entity.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "40702810938000000001", null));
        await StockAsync(s, f, f.Sweater, 20);
        var order = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 10));
        await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 10_000m, null, null, bank);
        await ShipAsync(s, order);
        var ret = await s.Sales.CreateReturnAsync(order);
        await s.Documents.SetLineAsync(ret, f.Sweater, 3, (await s.Documents.GetAsync(ret)).RowVersion);
        await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);

        var panel = await s.Sales.ShippingPanelAsync(order);
        Assert.Equal((3_000m, 0m), (panel.Overpaid!.Value, panel.CustomerAdvance!.Value));
        Assert.Equal(-3_000m, (await s.Sales.GetOrderAsync(order)).Debt);

        // Зачёт в аванс: переплата снимается с оплат заказа — долг по заказу 0, у покупателя аванс 3 000, его зачтут в новый заказ.
        Assert.Equal(3_000m, await s.Sales.ReleaseOverpaymentAsync(order));
        Assert.Equal(0m, (await s.Sales.GetOrderAsync(order)).Debt);
        panel = await s.Sales.ShippingPanelAsync(order);
        Assert.Equal((0m, 3_000m), (panel.Overpaid!.Value, panel.CustomerAdvance!.Value));
        Assert.Equal("sales.refund.no_overpayment", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.ReleaseOverpaymentAsync(order))).Code);

        // Возврат денег 1 000 из аванса: расход со счёта, аванс 2 000, расчёты покупателя: −2 000 (наш долг ему).
        Assert.Equal("sales.refund.exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Sales.RefundAsync(order, new RefundCommand(Day, 5_000m, null, null)))).Code);
        var op = await s.Sales.RefundAsync(order, new RefundCommand(Day, 1_000m, null, "по заявлению"));
        var operation = await s.MoneyOperations.GetAsync(op);
        Assert.Equal((1_000m, bank), (operation.Amount, operation.AccountId));
        panel = await s.Sales.ShippingPanelAsync(order);
        Assert.Equal(2_000m, panel.CustomerAdvance);
        Assert.Single(panel.Refunds);
        Assert.Equal(-2_000m, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Debt);

        var next = await ConfirmedAsync(s, f, f.Customer, (f.Sweater, 5));
        Assert.Equal(2_000m, await s.Sales.AvailableAdvancesAsync(next));
        Assert.Equal(2_000m, await s.Sales.ApplyAdvancesAsync(next));

        // Отмена возврата денег — сумма снова аванс.
        await s.Sales.CancelRefundAsync(op, "ошибка суммы", (await s.Sales.ShippingPanelAsync(order)).Refunds.Single().RowVersion);
        Assert.Equal(1_000m, (await s.Sales.ShippingPanelAsync(order)).CustomerAdvance);
        Assert.Equal(-3_000m, (await s.Sales.BalancesAsync()).Single(b => b.CustomerId == f.Customer).Debt);

        // Без права на заказы и оплаты — отказ.
        await using var k = host.As(f.Senior, f.Org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.RefundAsync(order, new RefundCommand(Day, 1m, null, null)));
    }

    [SqlFact]
    public async Task Marking_codes_checked_sold_once_released_on_return_and_exported()
    {
        var f = await SetUpAsync();
        long ship, order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await StockAsync(s, f, f.Marked, 10);
            order = await ConfirmedAsync(s, f, f.Customer, (f.Marked, 2), (f.Sweater, 1));
            await StockAsync(s, f, f.Sweater, 5);
            ship = await s.Sales.CreateShipmentAsync(order);

            // Без кодов отгрузка не проводится.
            Assert.Equal("sales.marking.incomplete", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion))).Code);
            var marking = (await s.Sales.StockDocumentPanelAsync(ship)).Marking!;
            Assert.Single(marking.Lines);
            Assert.False(marking.Complete);
        }

        // Кладовщик склада сканирует: неверный GTIN, мусор, дубль — отказ; два верных кода.
        await using (var k = host.As(f.Senior, f.Org.OrganizationId))
        {
            Assert.Equal("sales.marking.gtin_mismatch", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Sales.ScanMarkingCodeAsync(ship, Code(OtherGtin, "AAAAAAAAAAAAA")))).Code);
            Assert.Equal("sales.marking.format", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Sales.ScanMarkingCodeAsync(ship, "4601234567886")))).Code);
            Assert.Equal("СВ-М", await k.Sales.ScanMarkingCodeAsync(ship, "]d2" + Code(Gtin, "SER0000000001") + "\u001d91EE10\u001d92abc"));
            Assert.Equal("sales.marking.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Sales.ScanMarkingCodeAsync(ship, Code(Gtin, "SER0000000001")))).Code);
            await k.Sales.ScanMarkingCodeAsync(ship, Code(Gtin, "SER0000000002") + "<GS>93ABCD");
            Assert.Equal("sales.marking.enough", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Sales.ScanMarkingCodeAsync(ship, Code(Gtin, "SER0000000003")))).Code);
            Assert.True((await k.Sales.StockDocumentPanelAsync(ship)).Marking!.Complete);
            await k.Documents.PostAsync(ship, (await k.Documents.GetAsync(ship)).RowVersion);
        }

        // Кладовщик чужого склада не сканирует.
        await using (var x = host.As(f.Stranger, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => x.Sales.ScanMarkingCodeAsync(ship, Code(Gtin, "SER0000000009")));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Коды для XML УПД: строка 1 (СВ-1 идёт раньше СВ-М? нет: «СВ-1» < «СВ-М») — у маркируемой позиции номер 2.
            var codes = await s.Sales.CodesForShipmentAsync(ship);
            Assert.Equal([2], codes.Keys.ToArray());
            Assert.Equal([Code(Gtin, "SER0000000001"), Code(Gtin, "SER0000000002")], codes[2].OrderBy(c => c).ToArray());
            var (csv, name) = await s.Sales.MarkingCsvAsync(ship);
            var text = System.Text.Encoding.UTF8.GetString(csv);
            Assert.Contains(Code(Gtin, "SER0000000002"), text);
            Assert.EndsWith(".csv", name);

            // Повторная продажа того же кода: при сканировании — отказ; в SQL Server — UNIQUE проданных кодов.
            var again = await ConfirmedAsync(s, f, f.Other, (f.Marked, 1));
            var ship2 = await s.Sales.CreateShipmentAsync(again);
            Assert.Equal("sales.marking.sold_twice", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Sales.ScanMarkingCodeAsync(ship2, Code(Gtin, "SER0000000001")))).Code);
            await using (var db = host.NewDb())
            {
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO [kniterp].[sales_marking_codes] (OrganizationId, StockDocumentId, ItemId, Gtin, Serial, IsSold, SoldAtUtc, ScannedByUserId, ScannedAtUtc) "
                    + "VALUES ({0}, {1}, {2}, {3}, {4}, 1, SYSUTCDATETIME(), {5}, SYSUTCDATETIME())",
                    f.Org.OrganizationId, ship2, f.Marked, Gtin, "SER0000000001", f.Org.OwnerUserId));
            }

            // Возврат: принимаются только коды, проданные по этому заказу; после возврата код свободен и продаётся снова.
            var ret = await s.Sales.CreateReturnAsync(order);
            await s.Documents.RemoveLineAsync(ret, f.Sweater, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Documents.SetLineAsync(ret, f.Marked, 1, (await s.Documents.GetAsync(ret)).RowVersion);
            Assert.Equal("sales.marking.not_sold", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Sales.ScanMarkingCodeAsync(ret, Code(Gtin, "SER0000000077")))).Code);
            await s.Sales.ScanMarkingCodeAsync(ret, Code(Gtin, "SER0000000001"));
            await s.Documents.PostAsync(ret, (await s.Documents.GetAsync(ret)).RowVersion);
            await s.Sales.ScanMarkingCodeAsync(ship2, Code(Gtin, "SER0000000001"));
            await s.Documents.PostAsync(ship2, (await s.Documents.GetAsync(ship2)).RowVersion);
            Assert.Single((await s.Sales.CodesForShipmentAsync(ship2))[1]);
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.CodesForShipmentAsync(ship));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.ScanMarkingCodeAsync(ship, Code(Gtin, "SER0000000005")));
        }
    }

    private static string Code(string gtin, string serial) => "01" + gtin + "21" + serial;

    private async Task<decimal> BalanceAsync(Fixture f, long warehouse, long item)
    {
        await using var db = host.NewDb();
        return await db.StockMovements.Where(m => m.OrganizationId == f.Org.OrganizationId && m.WarehouseId == warehouse && m.ItemId == item)
            .SumAsync(m => m.Quantity);
    }

    private static async Task<long> ConfirmedAsync(Services s, Fixture f, long customer, params (long Item, decimal Quantity)[] lines)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, f.Store, null, null, true, null));
        foreach (var (item, quantity) in lines)
        {
            await s.Sales.SetOrderLineAsync(order, item, quantity, item == f.Hat ? 500m : 1000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
        }

        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        return order;
    }

    private static async Task ShipAsync(Services s, long order)
    {
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
    }

    private async Task StockAsync(Services s, Fixture f, long item, decimal quantity)
    {
        long? reason;
        await using (var db = host.NewDb())
        {
            reason = await db.OperationReasons.Where(r => r.OrganizationId == f.Org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => (long?)r.Id).FirstAsync();
        }

        var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Store, null, null, reason, Day, null));
        await s.Documents.SetLineAsync(receipt, item, quantity, (await s.Documents.GetAsync(receipt)).RowVersion);
        await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);
    }

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Quarantine, long Sweater, long Hat, long Marked, long Customer, long Other, long Senior, long Stranger);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, quarantine, sweater, hat, marked, customer, other, senior, stranger;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            quarantine = await s.Warehouses.CreateWarehouseAsync("Карантин", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            hat = await s.Catalog.CreateItemAsync(new ItemCommand("ШП-1", "Шапка", ItemType.Finished, unit, null));
            marked = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-М", "Свитер маркированный", ItemType.Finished, unit, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            other = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Бутик»", null, null, false, true, null));
            senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
            stranger = (await s.Access.InviteAsync(new InviteUserCommand($"stranger-{Guid.NewGuid():N}@test.local", "Кладовщик карантина",
                SystemRoles.SeniorStorekeeper, null, WarehouseId: quarantine))).UserId;
        }

        await using (var db = host.NewDb())
        {
            foreach (var id in new[] { senior, stranger })
            {
                (await db.Users.SingleAsync(u => u.Id == id)).Activate();
            }

            var item = await db.Items.SingleAsync(i => i.Id == marked);
            item.SetTrade(item.Trade with { IsMarked = true, TrackingType = TrackingType.Clothes, Gtin = Gtin });
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, quarantine, sweater, hat, marked, customer, other, senior, stranger);
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
