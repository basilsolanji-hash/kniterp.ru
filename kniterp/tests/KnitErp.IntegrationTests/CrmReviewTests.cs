using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Замечания проверки CRM (docs/sessions/base.md, D121) — D680–D683. Данные вымышленные.</summary>
public sealed class CrmReviewTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Offer_prices_are_hidden_in_feed_without_price_right_and_mail_is_validated_and_limited()
    {
        var (org, deal, offerId, _) = await DealWithOfferAsync();
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            await crm.Offers.SendAsync(offerId, "buyer.review@example.org", null);
            var full = await crm.Activities.FeedAsync(CrmTarget.Deal, deal);
            Assert.Contains(full.Items, i => i.Activity is { Kind: CrmActivityKind.Email } && (i.Text ?? "").Contains("1 800,00"));
            // Активность КП — без суммы в тексте (D680).
            Assert.DoesNotContain(full.Items, i => i.Activity is { Kind: CrmActivityKind.OfferSent } && (i.Text ?? "").Contains("180"));
        }

        // Без права «Цены и суммы» (токен с crm.view): текст письма с КП скрыт, сумм в ленте нет.
        await using (var noPrices = host.Crm(org.OwnerUserId, org.OrganizationId, [Permissions.CrmView]))
        {
            var feed = await noPrices.Activities.FeedAsync(CrmTarget.Deal, deal);
            Assert.All(feed.Items, i => Assert.DoesNotContain("1 800,00", i.Text ?? ""));
            Assert.All(feed.Items, i => Assert.DoesNotContain("180 000", i.Text ?? ""));
            Assert.Contains(feed.Items, i => i.Text == CrmActivityService.HiddenPricesText && i.Activity?.Text == CrmActivityService.HiddenPricesText);
        }

        // Лимит КП письмом (D680): 30 в час от сотрудника.
        await using (var db = host.NewDb())
        {
            for (var i = 0; i < CrmOfferService.OffersPerUserPerHour; i++)
            {
                db.CrmActivities.Add(CrmActivity.Create(org.OrganizationId, CrmActivityKind.Email, CrmTarget.Deal, deal, "КП", "x", null, org.OwnerUserId,
                    org.OwnerUserId, host.Clock.UtcNow, done: true, source: CrmActivitySource.Mail, offerId: offerId));
            }

            await db.SaveChangesAsync();
        }

        var before = host.Mail.Messages.Count;
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("crm.offer.quota", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Offers.SendAsync(offerId, "buyer.review@example.org", null))).Code);
        }

        Assert.Equal(before, host.Mail.Messages.Count); // письмо не ушло
    }

    [SqlFact]
    public async Task Offer_pdf_is_rendered_and_attached_to_mail()
    {
        var (org, _, offerId, _) = await DealWithOfferAsync(lines: 3);
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var file = await crm.Offers.PdfAsync(offerId);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.StartsWith("КП КП-", file.FileName);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(file.Content, 0, 4));
        await crm.Offers.SendAsync(offerId, "pdf.review@example.org", null);
        var letter = host.Mail.Messages.Last(m => m.To == "pdf.review@example.org");
        Assert.Equal(file.FileName, Assert.Single(letter.Attachments!).FileName);

        // Без права цен и из чужой организации PDF не отдаётся.
        await using (var noPrices = host.Crm(org.OwnerUserId, org.OrganizationId, [Permissions.CrmView]))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => noPrices.Offers.PdfAsync(offerId));
        }

        var other = await host.CreateCrmOrganizationAsync();
        await using var foreign = host.Crm(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => foreign.Offers.PdfAsync(offerId));
    }

    [SqlFact]
    public async Task Long_offer_mail_still_gets_a_valid_feed_record()
    {
        var (org, deal, offerId, _) = await DealWithOfferAsync(lines: 60);
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        await crm.Offers.SendAsync(offerId, "long.review@example.org", new string('Т', 3000));
        var letter = host.Mail.Messages.Last(m => m.To == "long.review@example.org");
        Assert.True(letter.Text.Length > CrmActivity.TextMaxLength); // письмо — полностью
        var feed = await crm.Activities.FeedAsync(CrmTarget.Deal, deal);
        var record = feed.Items.Single(i => i.Activity is { Kind: CrmActivityKind.Email });
        Assert.True(record.Text!.Length <= CrmActivity.TextMaxLength); // в ленте — обрезано, но запись есть
    }

    [SqlFact]
    public async Task Composite_keys_reject_references_to_another_organization()
    {
        var (org, deal, offerId, item) = await DealWithOfferAsync();
        var (other, otherDeal, otherOffer, otherItem) = await DealWithOfferAsync();
        await using var db = host.NewDb();
        // Активность одной организации не может ссылаться на КП другой.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[crm_activities] SET [OfferId] = {0} WHERE [OrganizationId] = {1} AND [OfferId] = {2}", otherOffer, org.OrganizationId, offerId));
        // Строка сделки — на номенклатуру своей организации.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[crm_deal_lines] SET [ItemId] = {0} WHERE [DealId] = {1}", otherItem, deal));
        Assert.True(await db.Set<CrmDealLine>().AnyAsync(l => l.DealId == deal && l.OrganizationId == org.OrganizationId));
        Assert.NotEqual(other.OrganizationId, org.OrganizationId);
        Assert.NotEqual(item, otherItem);
    }

    [SqlFact]
    public async Task Repeat_request_and_call_rules()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId, [Permissions.CrmLeadsCreate]);
        var first = await crm.Intake.CreateLeadAsync(new CrmIntakeLead("Заявка с сайта", Phone: "+7 900 000-00-81"));
        var repeat = await crm.Intake.CreateLeadAsync(new CrmIntakeLead("Ещё заявка", Phone: "+7 900 000-00-81"));
        Assert.True(repeat.Repeat);
        Assert.Equal(first.LeadId, repeat.LeadId); // внутри CRM — тот же лид; наружу API отдаёт только «принято» (D681, e2e)
        Assert.Equal("crm.call.duration", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-82", true, -1)))).Code);
        Assert.Equal("crm.call.duration", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-82", true, CrmIntakeService.MaxCallSeconds + 1)))).Code);
        Assert.True((await crm.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-82", true, CrmIntakeService.MaxCallSeconds))).NewLead);
        Assert.False((await crm.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-82", false, 0))).NewLead);
    }

    [SqlFact]
    public async Task Order_confirmation_wins_deal_through_module_event_and_links_order()
    {
        var (org, deal, offerId, _) = await DealWithOfferAsync();
        long store;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            store = await s.Warehouses.CreateWarehouseAsync("Склад продукции", null);
        }

        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var order = await crm.Deals.CreateOrderFromOfferAsync(offerId, store);
        var (_, otherDeal, _, _) = await DealWithOfferAsync();
        await using (var db = host.NewDb())
        {
            Assert.Equal(deal, await db.SalesOrders.Where(o => o.Id == order).Select(o => o.CrmDealId).SingleAsync());
            // Заказ базы — только на сделку своей организации (D683, составной ключ).
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[sales_orders] SET [CrmDealId] = {0} WHERE [Id] = {1}", otherDeal, order));
        }

        // Подтверждение из модуля продаж (как из карточки заказа) — сделка выиграна через ISalesOrderEvents.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        }

        Assert.Equal(CrmDealOutcome.Won, (await crm.Deals.GetAsync(deal)).Outcome);
    }

    private async Task<(KnitErp.Application.Organizations.CreatedOrganization Org, long Deal, long Offer, long Item)> DealWithOfferAsync(int lines = 1)
    {
        var org = await host.CreateCrmOrganizationAsync();
        long customer;
        var items = new List<long>();
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            for (var i = 0; i < lines; i++)
            {
                items.Add(await s.Catalog.CreateItemAsync(new ItemCommand($"СВ-{i}", $"Свитер ревизии с длинным названием для проверки длины письма {i}",
                    ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null)));
            }

            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand($"ООО «Ревизия {Guid.NewGuid():N}"[..28] + "»", null, null, false, true, null));
        }

        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера ревизии", customer));
        foreach (var item in items)
        {
            await crm.Deals.SetLineAsync(deal, item, 100, 1800m, (await crm.Deals.GetAsync(deal)).RowVersion);
        }

        var offer = await crm.Offers.CreateAsync(new CrmOfferCommand(deal));
        return (org, deal, offer, items[0]);
    }
}
