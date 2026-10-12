using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// CRM, этапы 2–5 и автоматизация (D610–D669): контакты и согласия, удаление ПДн, выгрузка; активности, лента, напоминания,
/// следующий шаг, КП → заказ → производство, письмо клиенту; отчёты и план; заявки и звонки из API; рассылки и отписка; импорт.
/// Данные вымышленные: +7 900 000-00-xx, @example.org.
/// </summary>
public sealed class CrmFullCycleTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Contact_channels_consent_erasure_and_export()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        long id;
        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            id = await crm.Contacts.CreateAsync(new CrmContactValues("Тестова Ирина", "Закупщик", Phone: "+7 900 000-00-11", Email: "irina@example.org"));
            var dto = await crm.Contacts.GetAsync(id);
            Assert.Equal((CrmProcessingBasis.Contract, false), (dto.Consent!.Basis, dto.Consent.MarketingConsent));
            await crm.Contacts.AddChannelAsync(id, CrmChannelKind.Phone, "8 900 000-00-12", "мобильный", dto.RowVersion);
            Assert.Equal("crm.contact.channel_duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await crm.Contacts.AddChannelAsync(id, CrmChannelKind.Phone, "+79000000012", null, (await crm.Contacts.GetAsync(id)).RowVersion))).Code);
            // Дубликат находится и по дополнительному телефону.
            Assert.Single(await crm.Contacts.DuplicatesAsync("+7 900 000-00-12", null));
            Assert.Equal("crm.consent.note", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await crm.Contacts.SetConsentAsync(id, new CrmConsentValues(CrmProcessingBasis.Consent, new DateOnly(2026, 10, 1), null, true),
                    (await crm.Contacts.GetAsync(id)).RowVersion))).Code);
            await crm.Contacts.SetConsentAsync(id, new CrmConsentValues(CrmProcessingBasis.Consent, new DateOnly(2026, 10, 1), "письменно", true),
                (await crm.Contacts.GetAsync(id)).RowVersion);
            dto = await crm.Contacts.GetAsync(id);
            Assert.True(dto.Consent!.MarketingConsent);
            Assert.False(dto.CanErase);
            Assert.False(dto.CanExport);
            await Assert.ThrowsAsync<AccessDeniedException>(() => crm.Contacts.ExportAsync(false));
            await Assert.ThrowsAsync<AccessDeniedException>(() => crm.Contacts.ErasePersonalDataAsync(id, "запрос"));
            await crm.Leads.CreateAsync(new CrmLeadValues("Заявка Ирины", ContactName: "Тестова Ирина", Phone: "+7 900 000-00-12"));
        }

        await using (var head = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            await head.Contacts.CreateAsync(new CrmContactValues("Без согласия", Email: "nope@example.org"));
            var all = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(await head.Contacts.ExportAsync(false)), 100);
            var mailing = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(await head.Contacts.ExportAsync(true)), 100);
            Assert.Equal((3, 2), (all.Count, mailing.Count)); // заголовок + строки; для рассылки — только с согласием
            Assert.Contains(mailing, r => r.Contains("irina@example.org"));

            await head.Contacts.ErasePersonalDataAsync(id, "заявление от 11.10.2026");
            var erased = await head.Contacts.GetAsync(id);
            Assert.True(erased.IsErased);
            Assert.StartsWith(CrmContact.ErasedName, erased.Values.FullName);
            Assert.Null(erased.Values.Phone);
            Assert.Empty(erased.Channels!);
            var lead = (await head.Leads.ListAsync(new CrmLeadFilter())).Rows.Single(r => r.Title == "Заявка Ирины");
            Assert.Equal(CrmContact.ErasedName, lead.ContactName);
            Assert.Equal("crm.contact.erased", (await Assert.ThrowsAsync<BusinessRuleException>(() => head.Contacts.ErasePersonalDataAsync(id, "повтор"))).Code);
            var audit = await head.Db.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == org.OrganizationId && a.Action.StartsWith("crm.")).ToListAsync();
            Assert.Contains(audit, a => a.Action == AuditActions.CrmContactErased);
            Assert.Equal(2, audit.Count(a => a.Action == AuditActions.CrmExported));
            Assert.DoesNotContain(audit, a => (a.Before + a.After).Contains("irina@example.org") || (a.Before + a.After).Contains("79000000012"));
        }
    }

    [SqlFact]
    public async Task Activities_feed_reminders_next_step_and_repeat_sale()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var other = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var customer = await CustomerAsync(org);
        await using var crm = host.Crm(manager, org.OrganizationId);
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера", customer, Amount: 10_000m));
        // Б4: плановая дата закрытия подставилась (нет выигранных — через 30 дней).
        Assert.Equal(new DateOnly(2026, 10, 9).AddDays(30), (await crm.Deals.GetAsync(deal)).Values.ExpectedCloseDate);

        // Сделка без следующего шага → напоминание.
        Assert.Single((await crm.Activities.MyTasksAsync()).DealsWithoutNextStep);
        var call = await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Call, "Позвонить закупщику",
            DueLocal: new DateTime(2026, 10, 10, 11, 0, 0)));
        Assert.Empty((await crm.Activities.MyTasksAsync()).DealsWithoutNextStep);
        host.Clock.UtcNow = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        try
        {
            var tasks = await crm.Activities.MyTasksAsync();
            Assert.Equal(call, tasks.Overdue.Single().Id);
            await using (var s = host.As(manager, org.OrganizationId))
            {
                var bell = await s.Notifications.ListAsync();
                Assert.Contains(bell, n => n.Text.Contains("Просрочена задача CRM: Позвонить закупщику"));
            }

            var a = tasks.Overdue.Single();
            await crm.Activities.CompleteAsync(call, "Договорились об образцах", a.RowVersion);
            var done = (await crm.Activities.FeedAsync(CrmTarget.Deal, deal)).Items.First(i => i.Activity is not null).Activity!;
            Assert.True(done.IsDone);
            Assert.Equal("crm.activity.done", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Activities.UpdateAsync(call, "Иначе", null, null, manager, done.RowVersion))).Code);
            await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Activities.ReopenAsync(call, " ", done.RowVersion));
            await crm.Activities.ReopenAsync(call, "Клиент не подтвердил", done.RowVersion);
            Assert.Contains(await crm.Db.AuditEntries.AsNoTracking().Where(x => x.Action == AuditActions.CrmActivityReopened).Select(x => x.Before).ToListAsync(),
                b => b!.Contains("Договорились об образцах"));

            // Чужой менеджер не видит активность чужой сделки.
            await using (var o = host.Crm(other, org.OrganizationId))
            {
                await Assert.ThrowsAsync<NotFoundException>(() => o.Activities.FeedAsync(CrmTarget.Deal, deal));
                await Assert.ThrowsAsync<NotFoundException>(() => o.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Note, "x")));
            }

            // Следующий шаг: переход на «КП отправлено» предлагает «Позвонить: обсудить КП» через 3 дня — только предложение.
            var dto = await crm.Deals.GetAsync(deal);
            var offerStage = dto.Stages.Single(s => s.Name == "КП отправлено");
            var suggestion = await crm.Deals.MoveAsync(deal, offerStage.Id, dto.RowVersion);
            Assert.Equal(("Позвонить: обсудить КП", 3), (suggestion!.Subject, suggestion.Days));
            Assert.Equal(1, await crm.Db.CrmActivities.CountAsync(x => x.TargetId == deal && x.Target == CrmTarget.Deal));

            // Выигрыш → задача «Предложить повторный заказ» через 60 дней; вебхук-событие crm.deal.won.
            dto = await crm.Deals.GetAsync(deal);
            Assert.Null(await crm.Deals.MoveAsync(deal, dto.Stages.Single(s => s.Kind == CrmStageKind.Won).Id, dto.RowVersion));
            Assert.Contains(await crm.Db.CrmActivities.Where(x => x.TargetId == deal && x.Target == CrmTarget.Deal).Select(x => x.Subject).ToListAsync(),
                s => s.StartsWith("Предложить повторный заказ"));
            Assert.True(await crm.Db.AuditEntries.AnyAsync(x => x.Action == AuditActions.CrmDealWon && x.EntityId == deal.ToString()));
            Assert.Contains(KnitErp.Domain.Integrations.WebhookEvents.All, e => e.Event == "crm.deal.won");
        }
        finally
        {
            host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        }
    }

    [SqlFact]
    public async Task Offer_is_printed_mailed_turned_into_order_and_production()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var customer = await CustomerAsync(org);
        long store, sweater, priceType;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Склад продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-КП", "Свитер КП", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
        }

        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        priceType = (await crm.Offers.PriceTypesAsync()).First().Id;
        await using (var db = host.NewDb())
        {
            db.ItemPrices.Add(ItemPrice.Create(org.OrganizationId, sweater, priceType, 1800m));
            await db.SaveChangesAsync();
        }

        var contact = await crm.Contacts.CreateAsync(new CrmContactValues("Покупатель Пётр", CounterpartyId: customer, Email: "petr@example.org"));
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера с логотипом", customer, contact));
        Assert.Equal("crm.offer.empty", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Offers.CreateAsync(new CrmOfferCommand(deal)))).Code);
        await crm.Deals.SetLineAsync(deal, sweater, 100, 1500m, (await crm.Deals.GetAsync(deal)).RowVersion);

        // КП по прайсу: цена из вида цен, снимок позиций, сделка сама переходит на «КП отправлено» (Б2).
        var offerId = await crm.Offers.CreateAsync(new CrmOfferCommand(deal, priceType));
        var offer = await crm.Offers.GetAsync(offerId);
        Assert.StartsWith("КП-", offer.Number);
        Assert.Equal((180_000m, 1800m, "Покупатель Пётр"), (offer.Amount, offer.Lines.Single().Price, offer.Contact));
        var afterOffer = await crm.Deals.GetAsync(deal);
        Assert.Equal("КП отправлено", afterOffer.Stages.Single(s => s.Id == afterOffer.StageId).Name);
        await using (var db = host.NewDb())
        {
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[crm_offers] SET [Amount] = 1 WHERE [Id] = {0}", offerId));
        }

        // Письмо с КП контакту.
        await crm.Offers.SendAsync(offerId, "petr@example.org", null);
        var letter = await host.Mail.WaitForAsync("petr@example.org");
        Assert.Contains(offer.Number, letter!.Value.Subject);
        Assert.Contains("Свитер КП", letter.Value.Text);
        Assert.Contains("180 000,00", letter.Value.Text);

        // Письмо из карточки (Б6) сохраняется в ленте.
        await crm.Activities.SendEmailAsync(CrmTarget.Deal, deal, "petr@example.org", "Образцы", "Образцы отправлены СДЭК.");
        var feed = await crm.Activities.FeedAsync(CrmTarget.Deal, deal);
        Assert.Contains(feed.Items, i => i.Kind == CrmFeedKind.Offer && i.PrintHref == $"print/crm-offer/{offerId}");
        Assert.Contains(feed.Items, i => i.Activity is { Kind: CrmActivityKind.Email, Subject: "Образцы" });

        // Заказ по КП: цены из КП, сделка → «Согласование»; подтверждение из сделки → «Выиграна».
        var order = await crm.Deals.CreateOrderFromOfferAsync(offerId, store);
        var o = await crm.Sales.GetOrderAsync(order);
        Assert.Equal((1800m, 180_000m), (o.Lines.Single().Price!.Value, o.Total!.Value));
        Assert.Contains("КП-", o.Comment);
        var afterOrder = await crm.Deals.GetAsync(deal);
        Assert.Equal("Согласование", afterOrder.Stages.Single(s => s.Id == afterOrder.StageId).Name);
        await crm.Deals.ConfirmOrderAsync(deal, order);
        Assert.Equal(CrmDealOutcome.Won, (await crm.Deals.GetAsync(deal)).Outcome);
        feed = await crm.Activities.FeedAsync(CrmTarget.Deal, deal);
        Assert.Contains(feed.Items, i => i.Kind == CrmFeedKind.Order && i.Title.Contains("подтверждён"));

        // Производство по заказу — механизм базы (D90): без техкарты позиция пропускается с причиной, но вызов работает из сделки.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var production = await s.ProductionOrders.ForSalesOrderAsync(order);
            Assert.Equal(order, production.SalesOrderId);
        }
    }

    [SqlFact]
    public async Task Reports_plan_fact_and_dashboard()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var customer = await CustomerAsync(org);
        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            var lead = await crm.Leads.CreateAsync(new CrmLeadValues("Лид 1", ContactName: "Клиент", Phone: "+7 900 000-00-21", SourceId: null));
            var q = await crm.Leads.QualifyAsync(lead, new CrmQualifyCommand(null, customer, null, 50_000m));
            var d2 = await crm.Deals.CreateAsync(new CrmDealValues("Сделка 2", customer, Amount: 20_000m));
            var dto = await crm.Deals.GetAsync(q.DealId);
            host.Clock.UtcNow = host.Clock.UtcNow.AddDays(2);
            await crm.Deals.MoveAsync(q.DealId, dto.Stages[1].Id, dto.RowVersion);
            dto = await crm.Deals.GetAsync(q.DealId);
            await crm.Deals.MoveAsync(q.DealId, dto.Stages.Single(s => s.Kind == CrmStageKind.Won).Id, dto.RowVersion);
            var d2dto = await crm.Deals.GetAsync(d2);
            var reason = (await crm.Leads.OptionsAsync()).LossReasons.First();
            await crm.Deals.MoveAsync(d2, d2dto.Stages.Single(s => s.Kind == CrmStageKind.Lost).Id, d2dto.RowVersion, reason.Id);
            host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        }

        await using var head = host.Crm(org.OwnerUserId, org.OrganizationId);
        await head.Settings.SaveAutomationAsync(new CrmAutomationValues(PlanFact: CrmPlanFact.WonDeals), null);
        await head.Settings.SetPlanAsync(2026, 10, manager, null, 100_000m);
        var report = await head.Reports.BuildAsync(new CrmReportFilter(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal((1, 1, 2, 1, 1), (report.Leads, report.QualifiedLeads, report.DealsCreated, report.Won, report.Lost));
        Assert.Equal(50_000m, report.WonAmount);
        Assert.Equal(2, report.Funnel[0].Reached);
        Assert.Equal(50m, report.Funnel[0].ConversionToNext);
        Assert.Equal(1, report.LossReasons.Single().Deals);
        var m = report.Managers.Single(x => x.UserId == manager);
        Assert.Equal((100_000m, 50_000m, 1), (m.Plan!.Value, m.Fact!.Value, m.Won));
        var xlsx = await head.Reports.ExportAsync(new CrmReportFilter(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.NotNull(SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "Менеджеры", 100));

        await using var mine = host.Crm(manager, org.OrganizationId);
        var dash = await mine.Reports.DashboardAsync();
        Assert.Equal((0, 50_000m, 100_000m), (dash!.OpenDeals, dash.WonThisMonth!.Value, dash.PlanThisMonth!.Value));
    }

    [SqlFact]
    public async Task Api_intake_dedups_assigns_round_robin_and_logs_calls()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var anna = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var boris = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using (var head = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            await head.Settings.SaveAutomationAsync(new CrmAutomationValues(AssignMode: CrmAssignMode.RoundRobin), null);
        }

        // Токен сайта: только crm.leads.create.
        await using var api = host.Crm(org.OwnerUserId, org.OrganizationId, [Permissions.CrmLeadsCreate]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => api.Leads.ListAsync(new CrmLeadFilter()));
        var first = await api.Intake.CreateLeadAsync(new CrmIntakeLead(null, "Иван", "+7 900 000-00-31", Need: "Нужно 200 шапок к зиме", Source: "Ozon"));
        var second = await api.Intake.CreateLeadAsync(new CrmIntakeLead("Пледы", "Олег", Email: "oleg@example.org", Source: "Ozon"));
        var repeat = await api.Intake.CreateLeadAsync(new CrmIntakeLead("Ещё раз", Phone: "8 900 000 00 31", Need: "Уточните срок"));
        Assert.False(first.Repeat);
        Assert.True(repeat.Repeat);
        Assert.Equal(first.LeadId, repeat.LeadId);
        Assert.Equal("crm.intake.contact", (await Assert.ThrowsAsync<BusinessRuleException>(() => api.Intake.CreateLeadAsync(new CrmIntakeLead("Без контакта")))).Code);

        await using var db = host.NewDb();
        var leads = await db.CrmLeads.AsNoTracking().Where(l => l.OrganizationId == org.OrganizationId).OrderBy(l => l.Id).ToListAsync();
        Assert.Equal("200 шапок", leads[0].Title); // название из текста заявки (А1)
        Assert.Equal(new[] { anna, boris }.Order(), leads.Select(l => l.ResponsibleUserId).Order()); // по очереди (А6)
        Assert.Single(await db.CrmReferences.Where(r => r.OrganizationId == org.OrganizationId && r.Name == "Ozon").ToListAsync());
        Assert.Equal(2, await db.CrmActivities.CountAsync(a => a.OrganizationId == org.OrganizationId && a.Kind == CrmActivityKind.Task));
        Assert.Single(await db.CrmActivities.Where(a => a.OrganizationId == org.OrganizationId && a.Kind == CrmActivityKind.RepeatRequest).ToListAsync());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.CrmLeadCreated));

        // Звонок АТС: известный номер → активность лида; повтор с тем же id — без дубля; новый номер → новый лид.
        var call = await api.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-31", true, 95, "pbx-1"));
        Assert.Equal((CrmTarget.Lead, first.LeadId, false), (call.Target, call.TargetId, call.NewLead));
        Assert.True((await api.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-31", true, 95, "pbx-1"))).Duplicate);
        var unknown = await api.Intake.RegisterCallAsync(new CrmCallEvent("+7 900 000-00-39", true, 0, "pbx-2"));
        Assert.True(unknown.NewLead);
    }

    [SqlFact]
    public async Task Mailing_only_to_consented_and_unsubscribe()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var yes = await crm.Contacts.CreateAsync(new CrmContactValues("Согласен", Email: "yes@example.org"));
        await crm.Contacts.CreateAsync(new CrmContactValues("Не согласен", Email: "no@example.org"));
        await crm.Contacts.SetConsentAsync(yes, new CrmConsentValues(CrmProcessingBasis.Contract, null, null, true), (await crm.Contacts.GetAsync(yes)).RowVersion);
        var id = await crm.Mailings.CreateAsync("Новая коллекция", "Весенняя коллекция трикотажа готова.", null);
        Assert.Equal(1, await crm.Mailings.AudienceCountAsync(null));
        var queued = await crm.Mailings.SendAsync(id, (await crm.Mailings.GetAsync(id)).RowVersion);
        Assert.Equal(1, queued);
        Assert.Equal((CrmMailingStatus.Sending, 1), ((await crm.Mailings.GetAsync(id)).Status, (await crm.Mailings.GetAsync(id)).Pending));
        await using (var worker = host.NewDb())
        {
            await CrmMailingService.ProcessQueueAsync(worker, host.Clock, host.Mail);
        }

        Assert.Equal((CrmMailingStatus.Sent, 0, 1), ((await crm.Mailings.GetAsync(id)).Status, (await crm.Mailings.GetAsync(id)).Pending, (await crm.Mailings.GetAsync(id)).Delivered));
        var letter = await host.Mail.WaitForAsync("yes@example.org");
        var html = host.Mail.Messages.Last(x => x.To == "yes@example.org");
        Assert.StartsWith("Здравствуйте, Согласен!", html.Text);
        Assert.Contains("<a href=\"https://erp.test/account/crm-unsubscribe/", html.Html);
        Assert.StartsWith("https://erp.test/account/crm-unsubscribe/", html.UnsubscribeUrl);
        Assert.Contains("/account/crm-unsubscribe/", letter!.Value.Text);
        Assert.DoesNotContain(host.Mail.Sent, m => m.To == "no@example.org");
        await using (var fresh = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("crm.mailing.sent", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await fresh.Mailings.UpdateAsync(id, "x", "y", null, (await fresh.Mailings.GetAsync(id)).RowVersion))).Code);
        }

        var token = letter.Value.Text.Split("/account/crm-unsubscribe/")[1].Trim();
        await using (var db = host.NewDb())
        {
            Assert.True(await CrmMailingService.UnsubscribeAsync(db, Guid.ParseExact(token, "N"), host.Clock.UtcNow));
            Assert.False(await CrmMailingService.UnsubscribeAsync(db, Guid.NewGuid(), host.Clock.UtcNow));
        }

        Assert.Equal(0, await crm.Mailings.AudienceCountAsync(null));
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using var m = host.Crm(manager, org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => m.Mailings.ListAsync());
    }

    [SqlFact]
    public async Task Templates_fill_card_values_and_reject_unknown_variables()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        Assert.Equal("crm.template.variable", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Templates.CreateAsync("Плохой", "Тема", "Здравствуйте, {{Фамилия}}!"))).Code);
        var id = await crm.Templates.CreateAsync("Спасибо за заявку", "{{Организация}}: заявка «{{Сделка}}»",
            "Здравствуйте, {{Имя}}! Сумма {{Сумма}}, менеджер {{Менеджер}}, компания {{Компания}}.");
        Assert.Equal("crm.template.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Templates.CreateAsync("Спасибо за заявку", "x", "y"))).Code);
        var customer = await CustomerAsync(org);
        var contact = await crm.Contacts.CreateAsync(new CrmContactValues("Мария Покупкина", Email: "maria@example.org"));
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Пледы к зиме", customer, contact, 25000m));
        var mail = await crm.Templates.RenderAsync(id, CrmTarget.Deal, deal);
        Assert.Contains("заявка «Пледы к зиме»", mail.Subject);
        Assert.Contains("Здравствуйте, Мария Покупкина! Сумма 25", mail.Text);
        Assert.DoesNotContain("{{", mail.Text);

        // Письмо из карточки — HTML с ответом менеджеру.
        await crm.Activities.SendEmailAsync(CrmTarget.Deal, deal, "maria@example.org", mail.Subject, mail.Text);
        var sent = host.Mail.Messages.Last(x => x.To == "maria@example.org");
        Assert.NotNull(sent.Html);
        Assert.NotNull(sent.ReplyTo);

        // Шаблоны — справочник: в архив, а не удаление; менеджер пользуется, но не правит; чужая карточка — 404.
        var t = (await crm.Templates.ListAsync()).Single();
        await crm.Templates.SetArchivedAsync(id, true, t.RowVersion);
        Assert.Empty(await crm.Templates.ListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => crm.Templates.RenderAsync(id, CrmTarget.Deal, deal));
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using var m = host.Crm(manager, org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => m.Templates.CreateAsync("Мой", "x", "y"));
        var other = await host.CreateCrmOrganizationAsync();
        await using var o = host.Crm(other.OwnerUserId, other.OrganizationId);
        var foreign = await o.Templates.CreateAsync("Чужой", "x", "y");
        await Assert.ThrowsAsync<NotFoundException>(() => crm.Templates.RenderAsync(foreign, CrmTarget.Deal, deal));
    }

    [SqlFact]
    public async Task Scheduled_mailing_goes_out_on_time_and_skips_unsubscribed()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var a = await crm.Contacts.CreateAsync(new CrmContactValues("Анна", Email: "anna.sched@example.org"));
        var b = await crm.Contacts.CreateAsync(new CrmContactValues("Борис", Email: "boris.sched@example.org"));
        foreach (var c in new[] { a, b })
        {
            await crm.Contacts.SetConsentAsync(c, new CrmConsentValues(CrmProcessingBasis.Contract, null, null, true), (await crm.Contacts.GetAsync(c)).RowVersion);
        }

        var id = await crm.Mailings.CreateAsync("Акция {{Организация}}", "{{Имя}}, для вас скидка.", null);
        var tz = await CrmEvents.TimeZoneAsync(crm.Db, org.OrganizationId, default);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(host.Clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz));
        Assert.Equal("crm.mailing.schedule", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await crm.Mailings.ScheduleAsync(id, nowLocal.AddMinutes(1), (await crm.Mailings.GetAsync(id)).RowVersion))).Code);
        await crm.Mailings.ScheduleAsync(id, nowLocal.AddHours(2), (await crm.Mailings.GetAsync(id)).RowVersion);
        Assert.Equal("crm.mailing.scheduled", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await crm.Mailings.UpdateAsync(id, "x", "y", null, (await crm.Mailings.GetAsync(id)).RowVersion))).Code);

        await using (var worker = host.NewDb())
        {
            await CrmMailingService.ProcessQueueAsync(worker, host.Clock, host.Mail); // время не пришло
        }

        Assert.Equal(CrmMailingStatus.Scheduled, (await crm.Mailings.GetAsync(id)).Status);
        var saved = host.Clock.UtcNow;
        try
        {
            host.Clock.UtcNow = saved.AddHours(3);
            await using (var worker = host.NewDb())
            {
                // Первый проход ставит в очередь и отправляет пачку из одного письма; Борис отписывается до второго прохода.
                await CrmMailingService.ProcessQueueAsync(worker, host.Clock, host.Mail, batch: 1);
            }

            var contactB = await crm.Contacts.GetAsync(b);
            await crm.Contacts.SetConsentAsync(b, new CrmConsentValues(CrmProcessingBasis.Contract, null, null, false), contactB.RowVersion);
            await using (var worker = host.NewDb())
            {
                await CrmMailingService.ProcessQueueAsync(worker, host.Clock, host.Mail);
            }
        }
        finally
        {
            host.Clock.UtcNow = saved;
        }

        var dto = await crm.Mailings.GetAsync(id);
        Assert.Equal((CrmMailingStatus.Sent, 2, 1), (dto.Status, dto.Recipients, dto.Delivered));
        var letter = host.Mail.Messages.Single(x => x.To == "anna.sched@example.org");
        Assert.StartsWith("Анна, для вас скидка.", letter.Text);
        Assert.DoesNotContain(host.Mail.Messages, x => x.To == "boris.sched@example.org");
    }

    [SqlFact]
    public async Task Inbound_email_lands_in_contact_deal_or_new_lead_once()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var login = $"sales{org.OrganizationId}@example.org";
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("crm.mailbox.host", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Mailbox.ConnectAsync("127.0.0.1", 993, login, "secret", true))).Code);
            Assert.Equal("crm.mailbox.port", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Mailbox.ConnectAsync("imap.example.org", 25, login, "secret", true))).Code);
            Assert.Equal("crm.mailbox.connect", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Mailbox.ConnectAsync("imap.example.org", 993, login, "wrong", true))).Code);
            await crm.Mailbox.ConnectAsync("imap.example.org", 993, login, "app-password-test", true);
            Assert.True((await crm.Mailbox.StatusAsync()).Connected);
            await using var raw = host.NewDb();
            var stored = await raw.Database.SqlQueryRaw<string>("SELECT [Password] AS [Value] FROM [kniterp].[crm_mailboxes] WHERE [OrganizationId] = {0}",
                org.OrganizationId).SingleAsync();
            Assert.DoesNotContain("app-password-test", stored);

            var customer = await CustomerAsync(org);
            var contact = await crm.Contacts.CreateAsync(new CrmContactValues("Ирина Заказова", Email: "irina.in@example.org"));
            await crm.Deals.CreateAsync(new CrmDealValues("Шапки для сети", customer, contact, 10000m));
        }

        CrmTestKit.Imap.Add(login, new CrmInboundEmail(1, "<m1@example.org>", "irina.in@example.org", "Ирина", "Re: КП", "Согласны, выставляйте счёт."));
        CrmTestKit.Imap.Add(login, new CrmInboundEmail(2, "<m2@example.org>", "new.client@example.org", "Пётр", "Запрос", "Нужно 500 шарфов. ООО «Шарф-Опт»"));
        CrmTestKit.Imap.Add(login, new CrmInboundEmail(3, "<m3@example.org>", login, null, "Копия", "Своё письмо"));
        CrmTestKit.Imap.Add(login, new CrmInboundEmail(4, "<m1@example.org>", "irina.in@example.org", "Ирина", "Re: КП", "Повтор того же письма"));
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(2, await crm.Mailbox.CheckNowAsync());
            Assert.Equal(0, await crm.Mailbox.CheckNowAsync()); // курсор не даёт прочитать письма дважды
            var deal = (await crm.Deals.ListAsync(new CrmDealFilter())).Rows.Single();
            var feed = await crm.Activities.FeedAsync(CrmTarget.Deal, deal.Id);
            Assert.Contains(feed.Items, i => i.Text == "Согласны, выставляйте счёт.");
            var lead = (await crm.Leads.ListAsync(new CrmLeadFilter())).Rows.Single();
            Assert.Equal(("500 шарфов", "Пётр"), (lead.Title, lead.ContactName));

            // D677: удаление ПДн по запросу обезличивает и переписку — текст письма клиента в ленте сделки не остаётся.
            var contact = (await crm.Contacts.ListAsync("Ирина")).Rows.Single();
            await crm.Contacts.ErasePersonalDataAsync(contact.Id, "заявление от 12.10.2026");
            var erased = await crm.Activities.FeedAsync(CrmTarget.Deal, deal.Id);
            Assert.DoesNotContain(erased.Items, i => (i.Text ?? "").Contains("выставляйте") || i.Title.Contains("Ирина"));
        }

        // Права подключившего отозваны — ящик не читается, ошибка видна в настройках.
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using (var m = host.Crm(manager, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Mailbox.ConnectAsync("imap.example.org", 993, login, "x", true));
            Assert.Null((await m.Mailbox.StatusAsync()).LastError);
        }
    }

    [SqlFact]
    public async Task Import_from_amocrm_maps_columns_stages_and_external_ids()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        byte[] File(params IReadOnlyList<string>[] rows) =>
            SqlTestHost.Spreadsheet.Write([new SheetData("Сделки", rows[0], rows.Skip(1).ToList())]);
        IReadOnlyList<string> header = ["ID", "Название сделки", "Бюджет", "Этап сделки", "Компания", "Основной контакт", "Рабочий телефон (контакт)"];
        var file = File(header,
            ["101", "Опт свитеров", "120000", "Согласование", "ООО «Импорт-1»", "Клиент Один", "+7 900 000-00-41"],
            ["102", "Шапки", "5000", "Успешно реализовано", "ООО «Импорт-2»", "", ""],
            ["103", "Пледы", "7000", "Закрыто и не реализовано", "ООО «Импорт-2»", "", ""]);
        var plan = await crm.Import.PreviewAsync(CrmImportKind.Deals, new MemoryStream(file));
        Assert.Equal(("amoCRM", 3, 0), (plan.System, plan.ToCreate, plan.Errors));
        Assert.Equal("Бюджет", plan.Mapping["amount"]);
        Assert.Equal((3, 0), await crm.Import.ApplyAsync(plan));
        var deals = await crm.Deals.ListAsync(new CrmDealFilter());
        Assert.Equal(3, deals.Total);
        Assert.Contains(deals.Rows, d => d.Title == "Шапки" && d.Outcome == CrmDealOutcome.Won);
        Assert.Contains(deals.Rows, d => d.Title == "Пледы" && d.Outcome == CrmDealOutcome.Lost);
        Assert.Contains(deals.Rows, d => d.Title == "Опт свитеров" && d.Stage == "Согласование" && d.Amount == 120_000m);

        // Повторная загрузка с изменённой суммой — обновление, а не дубли.
        var again = await crm.Import.PreviewAsync(CrmImportKind.Deals, new MemoryStream(File(header,
            ["101", "Опт свитеров", "150000", "Согласование", "ООО «Импорт-1»", "Клиент Один", "+7 900 000-00-41"])));
        Assert.Equal(1, again.ToUpdate);
        await crm.Import.ApplyAsync(again);
        Assert.Equal(3, (await crm.Deals.ListAsync(new CrmDealFilter())).Total);
        Assert.Equal(150_000m, (await crm.Deals.ListAsync(new CrmDealFilter(Search: "Опт"))).Rows.Single().Amount);

        // Ошибки — в протоколе, ничего не загружается.
        var bad = await crm.Import.PreviewAsync(CrmImportKind.Deals, new MemoryStream(File(header, ["104", "Ошибка", "abc", "Нет такого этапа", "ООО", "", ""])));
        Assert.Equal(1, bad.Errors);
        await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Import.ApplyAsync(bad));
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using var m = host.Crm(manager, org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => m.Import.PreviewAsync(CrmImportKind.Leads, new MemoryStream(file)));
    }

    [SqlFact]
    public async Task Qualification_fills_requisites_by_inn()
    {
        var org = await host.CreateCrmOrganizationAsync();
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var lead = await crm.Leads.CreateAsync(new CrmLeadValues("Заявка", ContactName: "Тест", Phone: "+7 900 000-00-51", CompanyName: "фабрика", Inn: "7707083893"));
        var result = await crm.Leads.QualifyAsync(lead, new CrmQualifyCommand());
        var c = await crm.Db.Counterparties.AsNoTracking().SingleAsync(x => x.Id == result.CounterpartyId);
        Assert.Equal(("ООО «ТФ»", "773601001", "г. Москва, ул. Тестовая, д. 1"), (c.Name, c.Kpp, c.Address));
    }

    [SqlFact]
    public async Task Max_chat_connects_receives_and_replies()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        CrmMessengerConnectResult connected;
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("crm.messenger.bad_token", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Messenger.ConnectAsync(CrmMessengerKind.Max, "bad"))).Code);
            connected = await crm.Messenger.ConnectAsync(CrmMessengerKind.Max, "max-token-test");
            Assert.True(connected.Subscribed);
            Assert.Contains(connected.WebhookUrl, CrmTestKit.Max.Subscriptions);
            Assert.Contains("/hooks/crm/max/", connected.WebhookUrl);
            Assert.True((await crm.Messenger.StatusAsync()).Channels.Single(c => c.Kind == CrmMessengerKind.Max).Connected);
            // Токен в базе зашифрован, секрет — только хэшем.
            await using var raw = host.NewDb();
            var stored = await raw.Database.SqlQueryRaw<string>("SELECT [Token] AS [Value] FROM [kniterp].[crm_messenger_channels] WHERE [OrganizationId] = {0}",
                org.OrganizationId).SingleAsync();
            Assert.DoesNotContain("max-token-test", stored);
        }

        var secret = connected.WebhookUrl.Split('/')[^1];
        await using (var db = host.NewDb())
        {
            Assert.Null(await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.Max, "wrong"));
            Assert.Null(await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.Telegram, secret)); // секрет MAX не открывает Telegram
            var hook = (await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.Max, secret))!;
            Assert.True(await CrmMessengerService.InboundAsync(db, host.Clock, hook,
                new CrmMessengerUpdate("770001", "Анна Клиентова", "anna_max", "Здравствуйте! Нужно 150 шарфов к ноябрю. Тел. +7 900 000-00-61", "mid-1")));
            Assert.True(await CrmMessengerService.InboundAsync(db, host.Clock, hook, new CrmMessengerUpdate("770001", "Анна Клиентова", null, "Повтор", "mid-1")));
            Assert.True(await CrmMessengerService.InboundAsync(db, host.Clock, hook, new CrmMessengerUpdate("770001", "Анна Клиентова", null, "И ещё 50 шапок", "mid-2")));
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var lead = (await crm.Leads.ListAsync(new CrmLeadFilter())).Rows.Single();
            Assert.Equal(("150 шарфов", "Анна Клиентова"), (lead.Title, lead.ContactName));
            var chat = (await crm.Messenger.ChatsAsync()).Single();
            Assert.Equal(("MAX", "MAX:770001", 2, true), (chat.Channel, chat.Peer, chat.Messages, chat.AwaitingReply)); // повтор mid-1 не задвоился
            await crm.Messenger.ReplyToPeerAsync(chat.Peer, "Добрый день! Посчитаем к вечеру.");
            Assert.Contains(CrmTestKit.Max.Sent, m => m.UserId == "770001" && m.Text == "Добрый день! Посчитаем к вечеру.");
            var messages = await crm.Messenger.MessagesAsync(chat.Peer);
            Assert.Equal(new[] { true, true, false }, messages.Select(m => m.Incoming));
            Assert.False((await crm.Messenger.ChatsAsync()).Single().AwaitingReply);
        }

        await using (var m = host.Crm(manager, org.OrganizationId))
        {
            // Лид распределён Владельцу (подключившему) — менеджер чужой чат не видит.
            Assert.Empty(await m.Messenger.ChatsAsync());
            await Assert.ThrowsAsync<NotFoundException>(() => m.Messenger.ReplyToPeerAsync("MAX:770001", "x"));
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Messenger.ConnectAsync(CrmMessengerKind.Max, "max-token-test"));
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            await crm.Messenger.DisconnectAsync(CrmMessengerKind.Max);
            Assert.False((await crm.Messenger.StatusAsync()).AnyConnected);
        }

        await using (var db = host.NewDb())
        {
            Assert.Null(await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.Max, secret));
        }
    }

    [SqlFact]
    public async Task Telegram_and_WhatsApp_chats_are_separate_and_reply_through_own_bot()
    {
        var org = await host.CreateCrmOrganizationAsync();
        CrmMessengerConnectResult tg, wa;
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal("crm.messenger.whatsapp",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Messenger.ConnectAsync(CrmMessengerKind.WhatsApp, "wa-token"))).Code);
            tg = await crm.Messenger.ConnectAsync(CrmMessengerKind.Telegram, "123456:tg-token-test");
            wa = await crm.Messenger.ConnectAsync(CrmMessengerKind.WhatsApp, "wa-token-test", "1098765432", "app-secret-test");
            Assert.True(tg.Subscribed);
            Assert.Null(tg.VerifyToken);
            // WhatsApp: вебхук задаётся в кабинете Meta — адрес и Verify token показываются один раз.
            Assert.False(wa.Subscribed);
            Assert.Equal(wa.WebhookUrl.Split('/')[^1], wa.VerifyToken);
            Assert.Equal(2, (await crm.Messenger.StatusAsync()).Channels.Count(c => c.Connected));
        }

        await using (var db = host.NewDb())
        {
            var tgHook = (await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.Telegram, tg.WebhookUrl.Split('/')[^1]))!;
            var waHook = (await CrmMessengerService.FindHookAsync(db, CrmMessengerKind.WhatsApp, wa.VerifyToken))!;
            Assert.Equal("app-secret-test", waHook.AppSecret);
            // Один и тот же id в разных мессенджерах — разные собеседники.
            Assert.True(await CrmMessengerService.InboundAsync(db, host.Clock, tgHook, new CrmMessengerUpdate("79000000071", "Ольга", "@olga", "Нужны 40 свитеров", "79000000071:1")));
            Assert.True(await CrmMessengerService.InboundAsync(db, host.Clock, waHook,
                new CrmMessengerUpdate("79000000071", "Ольга", null, "Это Ольга из WhatsApp", "wamid.1", "+79000000071")));
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var chats = await crm.Messenger.ChatsAsync();
            Assert.Equal(new[] { "Telegram:79000000071", "WhatsApp:79000000071" }, chats.Select(c => c.Peer).Order());
            await crm.Messenger.ReplyToPeerAsync("WhatsApp:79000000071", "Здравствуйте!");
            await crm.Messenger.ReplyToPeerAsync("Telegram:79000000071", "Привет из Telegram");
            Assert.Contains(CrmTestKit.WhatsApp.Sent, m => m == ("79000000071", "Здравствуйте!"));
            Assert.Contains(CrmTestKit.Telegram.Sent, m => m == ("79000000071", "Привет из Telegram"));
            Assert.DoesNotContain(CrmTestKit.Telegram.Sent, m => m.Text == "Здравствуйте!");
            Assert.All(await crm.Messenger.ChatsAsync(), c => Assert.False(c.AwaitingReply));
            await crm.Messenger.DisconnectAsync(CrmMessengerKind.WhatsApp);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Messenger.ReplyToPeerAsync("WhatsApp:79000000071", "ещё"));
            Assert.Equal("crm.messenger.not_connected", ex.Code);
        }
    }

    private async Task<long> CustomerAsync(KnitErp.Application.Organizations.CreatedOrganization org)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        return await s.Counterparties.CreateAsync(new CounterpartyCommand($"ООО «Покупатель {Guid.NewGuid():N}"[..30] + "»", null, null, false, true, null));
    }
}
