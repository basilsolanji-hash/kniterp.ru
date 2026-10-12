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
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Этап 1 продаж (D1101–D1104): соглашения с покупателями, автоподстановка цены (соглашение → вид цены клиента → основной),
/// минимальная цена и максимальная скидка по роли, подтверждение с обходом только с правом, причиной и записью в аудит.
/// </summary>
public sealed class SalesAgreementTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 811_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Price_comes_from_agreement_then_customer_price_type_then_base()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Без соглашения и вида цены клиента — основной вид.
        var quote = await s.Agreements.QuoteAsync(f.Customer, Day, true, [f.Sweater, f.Scarf]);
        Assert.Null(quote.AgreementId);
        Assert.Equal((1000m, PriceSource.Base), (quote.Items[f.Sweater].Price!.Value, quote.Items[f.Sweater].Source));

        // Вид цены клиента «Опт» — 900; у шарфа в «Опте» цены нет — берётся основная.
        await s.Agreements.SetCustomerTermsAsync(f.Customer, f.Wholesale, null, null);
        quote = await s.Agreements.QuoteAsync(f.Customer, Day, true, [f.Sweater, f.Scarf]);
        Assert.Equal((900m, PriceSource.Customer, "Опт"), (quote.Items[f.Sweater].Price!.Value, quote.Items[f.Sweater].Source, quote.Items[f.Sweater].PriceType));
        Assert.Equal((500m, PriceSource.Base), (quote.Items[f.Scarf].Price!.Value, quote.Items[f.Scarf].Source));

        // Соглашение покупателя: «Крупный опт» 800 и скидка 5 %.
        var agreement = await s.Agreements.CreateAsync(Terms("Годовое", f.Customer, priceType: f.Large, discount: 5));
        quote = await s.Agreements.QuoteAsync(f.Customer, Day, true, [f.Sweater]);
        Assert.Equal((agreement, 800m, 5m, 760m, PriceSource.Agreement),
            (quote.AgreementId!.Value, quote.Items[f.Sweater].Price!.Value, quote.Items[f.Sweater].DiscountPercent, quote.Items[f.Sweater].Net!.Value,
                quote.Items[f.Sweater].Source));

        // Своё соглашение сильнее группового и типового; соглашение группы — через тег контрагента; просроченное и архивное не действуют.
        var vip = await s.Tags.CreateAsync("VIP", TagColor.Green);
        await s.Tags.AssignAsync(TaggedEntity.Counterparty, f.Other, vip);
        var group = await s.Agreements.CreateAsync(Terms("VIP-клиенты", null, tag: vip, discount: 7));
        var everyone = await s.Agreements.CreateAsync(Terms("Типовое", null, discount: 2));
        await s.Agreements.CreateAsync(Terms("Прошлогоднее", f.Other, discount: 30, from: Day.AddYears(-1), to: Day.AddDays(-1)));
        Assert.Equal(agreement, (await s.Agreements.QuoteAsync(f.Customer, Day, true, [f.Sweater])).AgreementId);
        Assert.Equal((group, 7m), ((await s.Agreements.QuoteAsync(f.Other, Day, true, [f.Sweater])).AgreementId, (await s.Agreements.QuoteAsync(f.Other, Day, true, [f.Sweater])).AgreementDiscount));
        var groupRow = (await s.Agreements.ListAsync()).Single(a => a.Id == group);
        await s.Agreements.SetArchivedAsync(group, true, groupRow.RowVersion);
        Assert.Equal(everyone, (await s.Agreements.QuoteAsync(f.Other, Day, true, [f.Sweater])).AgreementId);

        // История соглашения — из журнала: создание и изменение.
        var row = (await s.Agreements.GetAsync(agreement)).Agreement;
        await s.Agreements.UpdateAsync(agreement, Terms("Годовое", f.Customer, priceType: f.Large, discount: 4), row.RowVersion);
        var history = (await s.Agreements.GetAsync(agreement)).History;
        Assert.Equal([AuditActions.SalesAgreementChanged, AuditActions.SalesAgreementCreated], history.Select(h => h.Action));
        Assert.Contains("скидка 4%", history[0].After);

        // Заказ: соглашение подставляется само; строка — с ценой, скидкой и НДС по правилам; заказ без НДС — цена без НДС.
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        var dto = await s.Sales.GetOrderAsync(order);
        Assert.Equal(agreement, dto.AgreementId);
        Assert.Contains("Годовое", dto.Agreement);
        await s.Sales.AddOrderLineAsync(order, f.Sweater, 10, dto.RowVersion);
        var line = (await s.Sales.GetOrderAsync(order)).Lines.Single();
        Assert.Equal((800m, 4m, 7_680m), (line.Price!.Value, line.DiscountPercent, line.Amount!.Value));

        var net = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, false, null));
        await s.Sales.AddOrderLineAsync(net, f.Sweater, 1, (await s.Sales.GetOrderAsync(net)).RowVersion);
        var netLine = (await s.Sales.GetOrderAsync(net)).Lines.Single();
        Assert.Equal(decimal.Round(800m / (1 + netLine.VatPercent!.Value / 100m), 4), netLine.Price);

        // Пересчёт цен: после смены вида цены соглашения черновик получает новые цены одной кнопкой.
        row = (await s.Agreements.GetAsync(agreement)).Agreement;
        await s.Agreements.UpdateAsync(agreement, Terms("Годовое", f.Customer, priceType: f.Wholesale, discount: 4), row.RowVersion);
        Assert.Equal(1, await s.Sales.RepriceOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion));
        Assert.Equal(900m, (await s.Sales.GetOrderAsync(order)).Lines.Single().Price);

        // Позиция без цены ни в одном виде — понятный отказ.
        var noPrice = await s.Catalog.CreateItemAsync(new ItemCommand("БЕЗ-1", "Без цены", ItemType.Finished, f.Unit, null));
        Assert.Equal("sales.price.missing", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await s.Sales.AddOrderLineAsync(order, noPrice, 1, (await s.Sales.GetOrderAsync(order)).RowVersion))).Code);
    }

    [SqlFact]
    public async Task Price_below_minimum_needs_override_permission_reason_and_audit()
    {
        var f = await SetUpAsync();
        long order;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Минимальная цена позиции — 700 (каталог); роли «Менеджер продаж» — граница «Крупный опт» (800) и скидка до 3 %.
            var trade = await s.Trade.GetAsync(f.Sweater);
            await s.Trade.SaveAsync(f.Sweater, trade.Trade with { MinPrice = 700m }, trade.RowVersion);
            var role = (await s.Agreements.RoleSettingsAsync()).Single(r => r.Role == SystemRoles.NameOf(SystemRoles.SalesManager));
            Assert.Equal("sales.agreement.percent", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Agreements.SetRoleSettingsAsync(role.RoleId, 120, null, SalesOrderVisibility.All))).Code);
            await s.Agreements.SetRoleSettingsAsync(role.RoleId, 3, f.Large, SalesOrderVisibility.All);
            Assert.Equal((3m, "Крупный опт"), ((await s.Agreements.RoleSettingsAsync()).Single(r => r.RoleId == role.RoleId).MaxDiscountPercent,
                (await s.Agreements.RoleSettingsAsync()).Single(r => r.RoleId == role.RoleId).MinPriceType));
        }

        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            // Менеджер: цена 750 ниже границы роли 800, и это снижение на 25 % от цены 1 000 (больше допустимых 3 %) — заказ
            // сохраняется, подтвердить нельзя; причина без права не помогает.
            order = await m.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
            await m.Sales.SetOrderLineAsync(order, f.Sweater, 2, 750m, null, (await m.Sales.GetOrderAsync(order)).RowVersion);
            var dto = await m.Sales.GetOrderAsync(order);
            Assert.Equal([PriceIssueKind.BelowMinimum, PriceIssueKind.DiscountTooHigh], dto.PriceIssues!.Select(i => i.Kind).Order());
            Assert.Equal(800m, dto.PriceIssues!.Single(i => i.Kind == PriceIssueKind.BelowMinimum).Limit);
            Assert.False(dto.CanOverridePrice);
            Assert.Equal("sales.price.below_limit", (await Assert.ThrowsAsync<BusinessRuleException>(() => m.Sales.ConfirmOrderAsync(order, dto.RowVersion))).Code);
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Sales.ConfirmOrderAsync(order, dto.RowVersion, "клиент просил"));

            // Скидка 10 % от цены 1 000 (основной вид): 900 выше границы, но скидка больше допустимых 3 %.
            await m.Sales.SetOrderLineAsync(order, f.Sweater, 2, 1000m, null, 10, (await m.Sales.GetOrderAsync(order)).RowVersion);
            var issue = (await m.Sales.GetOrderAsync(order)).PriceIssues!.Single();
            Assert.Equal((PriceIssueKind.DiscountTooHigh, 10m, 3m), (issue.Kind, issue.Actual, issue.Limit));

            // В пределах: 1000 со скидкой 2 % = 980 — подтверждается без вопросов.
            await m.Sales.SetOrderLineAsync(order, f.Sweater, 2, 1000m, null, 2, (await m.Sales.GetOrderAsync(order)).RowVersion);
            Assert.Empty((await m.Sales.GetOrderAsync(order)).PriceIssues!);
            await m.Sales.SetOrderLineAsync(order, f.Sweater, 2, 650m, null, (await m.Sales.GetOrderAsync(order)).RowVersion);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Владелец: у его роли границы нет, но 650 ниже минимальной цены позиции 700. Без причины — просьба указать причину.
            var dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((PriceIssueKind.BelowMinimum, 700m), (dto.PriceIssues!.Single().Kind, dto.PriceIssues!.Single().Limit));
            Assert.True(dto.CanOverridePrice);
            Assert.Equal("sales.price.override_reason", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.ConfirmOrderAsync(order, dto.RowVersion))).Code);
            await s.Sales.ConfirmOrderAsync(order, dto.RowVersion, "Распродажа остатков, согласовано");
            dto = await s.Sales.GetOrderAsync(order);
            Assert.Equal((SalesOrderStatus.Confirmed, "Распродажа остатков, согласовано"), (dto.Status, dto.PriceOverrideReason));
            var audit = await s.Db.AuditEntries.SingleAsync(e => e.OrganizationId == f.Org.OrganizationId && e.Action == AuditActions.SalesPriceOverride);
            Assert.Equal((order.ToString(), "Распродажа остатков, согласовано"), (audit.EntityId, audit.After));
            Assert.Contains("ниже допустимой 700", audit.Before);
        }
    }

    [SqlFact]
    public async Task Agreements_need_rights_and_stay_in_organization()
    {
        var f = await SetUpAsync();
        long agreement;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            agreement = await s.Agreements.CreateAsync(Terms("Основное", f.Customer, priceType: f.Large));
            Assert.Equal("sales.agreement.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Agreements.CreateAsync(Terms("Наоборот", f.Customer, from: Day, to: Day.AddDays(-1))))).Code);
            var tag = await s.Tags.CreateAsync("Опт", TagColor.Blue);
            Assert.Equal("sales.agreement.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Agreements.CreateAsync(Terms("Двойное", f.Customer, tag: tag)))).Code);
        }

        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            // Менеджер видит соглашения и цены, но не меняет их.
            Assert.Single(await m.Agreements.ListAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Agreements.CreateAsync(Terms("Своё", f.Customer, discount: 50)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Agreements.SetCustomerTermsAsync(f.Customer, f.Large, null, null));
        }

        var other = await CreateOrgAsync();
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Agreements.GetAsync(agreement));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Agreements.QuoteAsync(f.Customer, Day, true));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Agreements.CreateAsync(Terms("Чужой покупатель", f.Customer)));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Agreements.CreateAsync(Terms("Чужой вид цены", null, priceType: f.Large)));
        }

        // Ограничение в SQL Server: скидка больше 100 %.
        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[sales_agreements] SET [DiscountPercent] = 150 WHERE [Id] = {0}", agreement));
        }
    }

    private SalesAgreementTerms Terms(string name, long? customer, long? tag = null, long? priceType = null, decimal discount = 0,
        DateOnly? from = null, DateOnly? to = null) =>
        new(name, customer, tag, priceType, discount, from ?? Day.AddMonths(-1), to, null, null, null, null, null, null);

    private sealed record Fixture(
        CreatedOrganization Org, long Store, long Unit, long Sweater, long Scarf, long Customer, long Other, long Wholesale, long Large, long Manager);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, unit, sweater, scarf, customer, other, wholesale, large, manager;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, unit, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            other = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Бутик»", null, null, false, true, null));
            var baseType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
            wholesale = await s.Nomenclature.CreatePriceTypeAsync("Опт", true);
            large = await s.Nomenclature.CreatePriceTypeAsync("Крупный опт", true);
            await s.Nomenclature.SetPricesAsync(sweater, new Dictionary<long, decimal?> { [baseType] = 1000, [wholesale] = 900, [large] = 800 });
            await s.Nomenclature.SetPricesAsync(scarf, new Dictionary<long, decimal?> { [baseType] = 500 });
            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null)))
                .UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, unit, sweater, scarf, customer, other, wholesale, large, manager);
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
