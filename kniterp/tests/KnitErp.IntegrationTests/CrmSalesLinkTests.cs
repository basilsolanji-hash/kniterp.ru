using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Связка сделки с продажами (D684, D684): счёт, документы клиенту, оплата в сделке, повторный заказ по циклу клиента.</summary>
public sealed class CrmSalesLinkTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Deal_issues_invoice_sends_documents_and_shows_payment()
    {
        var (org, deal, order, customer) = await ConfirmedDealOrderAsync();
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var invoice = await crm.DealSales.InvoiceAsync(deal, order);
            Assert.Equal(invoice, await crm.DealSales.InvoiceAsync(deal, order)); // повторно — тот же действующий счёт
            var dto = await crm.Deals.GetAsync(deal);
            var row = dto.Orders.Single();
            Assert.Equal(invoice, row.InvoiceId);
            Assert.StartsWith("СЧ", row.InvoiceNumber ?? "СЧ");
            Assert.Equal(CrmPaymentState.None, row.Payment);

            // Документы клиенту: письмо в очереди продаж, запись в ленте сделки.
            var draft = await crm.DealSales.DraftAsync(deal, order, SalesMailDocument.Invoice);
            await crm.DealSales.SendAsync(deal, new SalesMailRequest(order, "buyer.link@example.org", draft.Subject, draft.Body, draft.Selected));
            Assert.True(await crm.Db.SalesMailMessages.AnyAsync(m => m.OrganizationId == org.OrganizationId && m.SalesOrderId == order));
            var feed = await crm.Activities.FeedAsync(CrmTarget.Deal, deal);
            Assert.Contains(feed.Items, i => i.Activity is { } a && a.Subject.StartsWith("Документы клиенту"));

            // Чужая связка сделка/заказ — «не найдено».
            var other = await crm.Deals.CreateAsync(new CrmDealValues("Другая сделка", customer));
            await Assert.ThrowsAsync<NotFoundException>(() => crm.DealSales.InvoiceAsync(other, order));
        }

        // Оплата в модуле продаж видна в сделке.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Sales.CreatePaymentAsync(DateOnly.FromDateTime(host.Clock.UtcNow), customer, order, 5_000m, "предоплата");
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var row = (await crm.Deals.GetAsync(deal)).Orders.Single();
            Assert.Equal((5_000m, CrmPaymentState.Partial), (row.Paid!.Value, row.Payment!.Value));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Sales.CreatePaymentAsync(DateOnly.FromDateTime(host.Clock.UtcNow), customer, order, 13_000m, null);
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(CrmPaymentState.Paid, (await crm.Deals.GetAsync(deal)).Orders.Single().Payment);
        }

        // Менеджер без доступа к сделке не выставит по ней счёт.
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using (var m = host.Crm(manager, org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => m.DealSales.InvoiceAsync(deal, order));
        }
    }

    [SqlFact]
    public async Task Repeat_sale_reminder_follows_client_order_cycle()
    {
        var (org, deal, order, customer) = await ConfirmedDealOrderAsync(previousOrdersDaysAgo: [90, 45]);
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var dto = await crm.Deals.GetAsync(deal);
        Assert.Equal(CrmDealOutcome.Won, dto.Outcome);
        Assert.Equal(45, dto.ClientCycleDays); // заказы 90 и 45 дней назад и сегодня — цикл 45 дней
        var task = await crm.Db.CrmActivities.AsNoTracking()
            .SingleAsync(a => a.OrganizationId == org.OrganizationId && a.TargetId == deal && a.Target == CrmTarget.Deal && a.Subject.StartsWith("Предложить повторный заказ"));
        var expectedDue = host.Clock.UtcNow.AddDays(45 - CrmEvents.RepeatLeadDays);
        Assert.InRange(task.DueAtUtc!.Value, expectedDue.AddDays(-1), expectedDue.AddDays(1));
        Assert.Contains("раз в 45 дн.", task.Text);
        Assert.NotEqual(0, order + customer);
    }

    private async Task<(KnitErp.Application.Organizations.CreatedOrganization Org, long Deal, long Order, long Customer)> ConfirmedDealOrderAsync(
        int[]? previousOrdersDaysAgo = null)
    {
        var org = await host.CreateCrmOrganizationAsync();
        long customer, store, item;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Склад продукции", null);
            item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-СЛ", "Свитер связки", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand($"ООО «Связка {Guid.NewGuid():N}"[..26] + "»", null, null, false, true, null));
            foreach (var days in previousOrdersDaysAgo ?? [])
            {
                var date = DateOnly.FromDateTime(host.Clock.UtcNow.AddDays(-days));
                var prev = await s.Sales.CreateOrderAsync(new SalesOrderHeader(date, customer, store, null, null, true, null));
                await s.Sales.SetOrderLineAsync(prev, item, 1, 100m, null, (await s.Sales.GetOrderAsync(prev)).RowVersion);
                await s.Sales.ConfirmOrderAsync(prev, (await s.Sales.GetOrderAsync(prev)).RowVersion);
            }
        }

        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера к сезону", customer));
        await crm.Deals.SetLineAsync(deal, item, 10, 1800m, (await crm.Deals.GetAsync(deal)).RowVersion);
        var order = await crm.Deals.CreateOrderAsync(deal, store);
        await crm.Deals.ConfirmOrderAsync(deal, order);
        return (org, deal, order, customer);
    }
}
