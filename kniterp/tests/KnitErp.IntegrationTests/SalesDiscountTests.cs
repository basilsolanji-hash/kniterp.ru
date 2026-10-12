using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Этап 4 продаж (D1106): скидки по соглашению, по количеству, по сумме заказа, акции на группу; порядок применения и запрет суммирования
/// из настроек; расшифровка «откуда» в строке; до подтверждения — пересчёт сам, после — только явным действием с причиной.
/// </summary>
public sealed class SalesDiscountTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 817_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Discounts_combine_in_configured_order_with_breakdown()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        // Соглашение 5 %, акция на «Свитеры» (подгруппа «Зима» входит) 10 %, от 10 шт. — 3 %, заказ от 50 000 — 2 %.
        await s.Agreements.CreateAsync(new SalesAgreementTerms("Опт", f.Customer, null, null, 5, Day.AddMonths(-1), null, null, null, null, null, null, null));
        await s.Discounts.CreateAsync(Rule(DiscountKind.Promotion, "Осень", 10, group: f.Sweaters));
        await s.Discounts.CreateAsync(Rule(DiscountKind.Quantity, "От 10 шт.", 3, qty: 10));
        await s.Discounts.CreateAsync(Rule(DiscountKind.Quantity, "От 50 шт.", 6, qty: 50));
        await s.Discounts.CreateAsync(Rule(DiscountKind.OrderAmount, "Крупный заказ", 2, amount: 50_000));
        await s.Discounts.CreateAsync(Rule(DiscountKind.Promotion, "Прошлая", 50, group: f.Sweaters, from: Day.AddYears(-1), to: Day.AddDays(-1)));

        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        await s.Sales.AddOrderLineAsync(order, f.Sweater, 10, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        // Последовательно: 1 − 0,95 × 0,97 × 0,90 = 17,065 % → 17,07 %; сумма 10 000 ниже порога 50 000.
        Assert.Equal(17.07m, line.DiscountPercent);
        Assert.Equal("Соглашение СГ-000001 5%; От 10 шт. 3%; Осень 10%", line.DiscountSources);

        // Шарф не в группе акции: только соглашение. 60 шт. × 1 000 → заказ 70 000 ≥ 50 000 — всем строкам ещё 2 %, у шарфа «от 50» — 6 %.
        await s.Sales.AddOrderLineAsync(order, f.Scarf, 60, (await s.Sales.GetOrderAsync(order)).RowVersion);
        var dto = await s.Sales.GetOrderAsync(order);
        var scarf = dto.Lines.Single(l => l.ItemId == f.Scarf);
        Assert.Equal("Соглашение СГ-000001 5%; От 50 шт. 6%; Крупный заказ 2%", scarf.DiscountSources);
        Assert.Equal(decimal.Round((1 - 0.95m * 0.94m * 0.98m) * 100, 2), scarf.DiscountPercent);
        Assert.Contains("Крупный заказ 2%", dto.Lines.Single(l => l.ItemId == f.Sweater).DiscountSources);

        // Ручная скидка — последней, сверх правил.
        await s.Sales.SetOrderLineAsync(order, f.Scarf, 60, 1000m, scarf.VatPercent, 1, dto.RowVersion);
        scarf = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Scarf);
        Assert.Equal((1m, true), (scarf.ManualDiscountPercent, scarf.DiscountSources!.EndsWith("Ручная 1%")));

        // Запрет суммирования: только наибольшая из правил (акция 10 %), ручная — сверх неё; порядок меняет расшифровку.
        await s.Discounts.SetSettingsAsync(DiscountCombineMode.Best, SalesDiscountSettings.DefaultOrder);
        Assert.True(await s.Sales.RepriceOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion) >= 1);
        line = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Sweater);
        Assert.Equal((10m, "Осень 10%"), (line.DiscountPercent, line.DiscountSources));
        await s.Discounts.SetSettingsAsync(DiscountCombineMode.Sequential,
            [DiscountKind.OrderAmount, DiscountKind.Promotion, DiscountKind.Quantity, DiscountKind.Agreement]);
        await s.Sales.RepriceOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        line = (await s.Sales.GetOrderAsync(order)).Lines.Single(l => l.ItemId == f.Sweater);
        Assert.Equal("Крупный заказ 2%; Осень 10%; От 10 шт. 3%; Соглашение СГ-000001 5%", line.DiscountSources);
        Assert.Equal("sales.discount.order", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Discounts.SetSettingsAsync(DiscountCombineMode.Sequential, [DiscountKind.Agreement, DiscountKind.Agreement, DiscountKind.Quantity, DiscountKind.Promotion]))).Code);
    }

    [SqlFact]
    public async Task Confirmed_order_discounts_change_only_by_explicit_action_with_reason()
    {
        var f = await SetUpAsync();
        long order, rule;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            await s.Sales.AddOrderLineAsync(order, f.Sweater, 2, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);

            // Новая акция после подтверждения сама заказ не меняет.
            rule = await s.Discounts.CreateAsync(Rule(DiscountKind.Promotion, "Чёрная пятница", 20, group: f.Sweaters));
            Assert.Equal(0m, (await s.Sales.GetOrderAsync(order)).Lines.Single().DiscountPercent);

            var dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal("sales.discount.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.ReapplyDiscountsAsync(order, " ", dto.RowVersion))).Code);
            Assert.Equal(1, await s.Sales.ReapplyDiscountsAsync(order, "Клиент попал в акцию по договорённости", dto.RowVersion));
            dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((20m, 1_600m), (dto.Lines.Single().DiscountPercent, dto.Total!.Value));
            Assert.True(await s.Db.AuditEntries.AnyAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.SalesDiscountsReapplied
                                                            && e.Reason!.Contains("по договорённости")));

            // После отгрузки — уже нельзя.
            await s.Sales.CreateShipmentAsync(order);
            Assert.Equal("sales.discount.reapply_documents", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Sales.ReapplyDiscountsAsync(order, "ещё раз", (await s.Sales.GetOrderAsync(order)).RowVersion))).Code);
        }

        // Менеджер продаж не меняет правила; чужая организация правил не видит.
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            Assert.Single(await m.Discounts.ListAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Discounts.CreateAsync(Rule(DiscountKind.Promotion, "Своя", 90, group: f.Sweaters)));
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            Assert.Empty(await o.Discounts.ListAsync());
            await Assert.ThrowsAsync<NotFoundException>(() => o.Discounts.SetArchivedAsync(rule, true, [0, 0, 0, 0, 0, 0, 0, 1]));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Discounts.CreateAsync(Rule(DiscountKind.Promotion, "Чужая группа", 5, group: f.Sweaters)));
        }
    }

    private static SalesDiscountRuleTerms Rule(DiscountKind kind, string name, decimal percent, long? group = null, decimal? qty = null, decimal? amount = null,
        DateOnly? from = null, DateOnly? to = null) =>
        new(kind, name, percent, amount, qty, group, null, from ?? Day.AddMonths(-1), to);

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Scarf, long Sweaters, long Customer, long Manager);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, scarf, sweaters, customer, manager;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweaters = await s.Nomenclature.CreateGroupAsync(null, "Свитеры");
            var winter = await s.Nomenclature.CreateGroupAsync(sweaters, "Зима");
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null, GroupId: winter));
            scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, unit, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            var baseType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [baseType] = 1000 });
            await s.Nomenclature.SetPricesAsync(scarf, new Dictionary<long, decimal?> { [baseType] = 1000 });
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, scarf, sweaters, customer, manager);
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
