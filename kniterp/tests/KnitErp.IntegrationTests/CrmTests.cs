using System.Data.Common;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KnitErp.IntegrationTests;

/// <summary>
/// CRM, этап 1 (D600–D609): лиды, квалификация в одной транзакции, сделки и воронки, история этапов только на вставку,
/// заказ покупателя из сделки и автоматический выигрыш, видимость (чужая организация и чужая сделка — 404), дубликаты.
/// </summary>
public sealed class CrmTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Phone = "+7 900 000-00-01";

    [SqlFact]
    public async Task Lead_qualifies_into_deal_order_and_won_deal()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var (store, sweater) = await StockAsync(org);
        var inn = CrmTestKit.NextCounterpartyInn();

        long leadId, dealId;
        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            // Готовая воронка появляется при первом обращении (D609).
            var settings = await crm.Settings.GetAsync();
            Assert.Equal(CrmStage.Defaults.Select(d => d.Name), settings.Pipelines.Single().Stages.Select(s => s.Name));
            Assert.False(settings.CanEdit);
            var options = await crm.Leads.OptionsAsync();
            var source = options.Sources.Single(s => s.Name == "Мессенджер").Id;

            leadId = await crm.Leads.CreateAsync(new CrmLeadValues("300 свитеров к Новому году", source, ContactName: "Иванова Анна",
                Phone: "8 (900) 000-00-01", Email: "Test@Example.org", Messenger: "@test_buyer", CompanyName: "ООО «Тестовый магазин»", Inn: inn,
                ExpectedAmount: 450_000m));
            var lead = await crm.Leads.GetAsync(leadId);
            Assert.Equal("Л-000001", lead.Number);
            Assert.Equal(("+79000000001", "test@example.org", CrmLeadStatus.New), (lead.Values.Phone, lead.Values.Email, lead.Status));

            // Дубликаты: тот же телефон в другом написании, та же почта, тот же ИНН.
            var dups = await crm.Leads.DuplicatesAsync("+7 900 000 00 01", null, null);
            Assert.Equal(leadId, dups.Single().Id);
            Assert.True(dups.Single().ByPhone);
            Assert.Empty(await crm.Leads.DuplicatesAsync(Phone, null, null, exceptLeadId: leadId));
            Assert.Single(await crm.Leads.DuplicatesAsync(null, "TEST@example.org", null));

            await crm.Leads.StartWorkAsync(leadId, lead.RowVersion);
            var result = await crm.Leads.QualifyAsync(leadId, new CrmQualifyCommand());
            Assert.True(result.CounterpartyCreated);
            Assert.True(result.ContactCreated);
            dealId = result.DealId;

            Assert.Equal("crm.lead.qualified", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Leads.QualifyAsync(leadId, new CrmQualifyCommand()))).Code);
            lead = await crm.Leads.GetAsync(leadId);
            Assert.Equal((CrmLeadStatus.Qualified, dealId, "ООО «Тестовый магазин»"), (lead.Status, lead.DealId, lead.Counterparty));

            var deal = await crm.Deals.GetAsync(dealId);
            Assert.Equal(("С-000001", "Новая", 10, 450_000m), (deal.Number, deal.Stages.Single(s => s.Id == deal.StageId).Name, deal.Probability, deal.Amount));
            Assert.Equal(("Иванова Анна", leadId), (deal.Contact, deal.LeadId));
            Assert.Single(deal.History);

            // Этап «КП отправлено» → вероятность из этапа; проигрыш без причины нельзя.
            var offer = deal.Stages.Single(s => s.Name == "КП отправлено");
            host.Clock.UtcNow = host.Clock.UtcNow.AddDays(3);
            await crm.Deals.MoveAsync(dealId, offer.Id, deal.RowVersion);
            deal = await crm.Deals.GetAsync(dealId);
            Assert.Equal((60, 2), (deal.Probability, deal.History.Count));
            Assert.Equal(3m, deal.History[0].DaysInStage);
            var lost = deal.Stages.Single(s => s.Kind == CrmStageKind.Lost);
            Assert.Equal("crm.deal.loss_reason", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Deals.MoveAsync(dealId, lost.Id, deal.RowVersion))).Code);

            // Позиция каталога → строка заказа покупателя.
            await crm.Deals.SetLineAsync(dealId, sweater, 300, 1500m, (await crm.Deals.GetAsync(dealId)).RowVersion);
            Assert.Equal(450_000m, (await crm.Deals.GetAsync(dealId)).Amount);
            var orderId = await crm.Deals.CreateOrderAsync(dealId, store);
            deal = await crm.Deals.GetAsync(dealId);
            var linked = deal.Orders.Single();
            Assert.Equal((orderId, SalesOrderStatus.Draft, 450_000m), (linked.Id, linked.Status, linked.Total!.Value));
            var order = await crm.Sales.GetOrderAsync(orderId);
            Assert.Equal((result.CounterpartyId, manager), (order.CustomerId, order.Details!.ResponsibleUserId));

            // Подтверждённый заказ по сделке → сделка «Выиграна» (D607).
            await crm.Sales.ConfirmOrderAsync(orderId, order.RowVersion);
            deal = await crm.Deals.GetAsync(dealId);
            Assert.Equal((CrmDealOutcome.Won, 100, "Выиграна"), (deal.Outcome, deal.Probability, deal.Stages.Single(s => s.Id == deal.StageId).Name));
            Assert.Equal(4, deal.History.Count); // создана, КП отправлено, «Согласование» по созданному заказу (Б2), выиграна
            Assert.NotNull(deal.ClosedAtUtc);
            Assert.Equal("crm.deal.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Deals.SetProbabilityAsync(dealId, 50, deal.RowVersion))).Code);
        }

        await using var db = host.NewDb();
        // История этапов — только вставка: изменить или удалить запись нельзя даже в обход приложения.
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[crm_deal_stage_history] SET [DaysInStage] = 99 WHERE [DealId] = {0}", dealId));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM [kniterp].[crm_deal_stage_history] WHERE [DealId] = {0}", dealId));

        // Журнал: телефон и почта только маской (D612).
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == org.OrganizationId && a.Action.StartsWith("crm.")).ToListAsync();
        Assert.Contains(audit, a => a.Action == AuditActions.CrmLeadQualified);
        Assert.Contains(audit, a => a.Action == AuditActions.CrmDealOrderCreated);
        Assert.Contains(audit, a => a.After != null && a.After.Contains("+7 900 ***-**-01") && a.After.Contains("t***@example.org"));
        Assert.DoesNotContain(audit, a => (a.Before + a.After + a.Reason).Contains("79000000001") || (a.Before + a.After).Contains("test@example.org"));
        var customer = await db.Counterparties.AsNoTracking().SingleAsync(c => c.OrganizationId == org.OrganizationId && c.Inn == inn);
        Assert.True(customer.IsCustomer);
    }

    /// <summary>
    /// D128 (аудит 12.10.2026): менеджер, видящий только свои контакты, квалифицирует лид с телефоном клиента другого менеджера —
    /// получает новый контакт, чужой контакт к его сделке не привязывается и остаётся ему невидим; в журнале — отметка без подробностей.
    /// </summary>
    [SqlFact]
    public async Task Qualification_does_not_link_contact_invisible_to_manager()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var first = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var second = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        long foreign;
        await using (var crm = host.Crm(first, org.OrganizationId))
        {
            foreign = await crm.Contacts.CreateAsync(new CrmContactValues("Сидорова Мария", Phone: "+7 900 000-00-77", Email: "client77@example.org"));
        }

        await using (var crm = host.Crm(second, org.OrganizationId))
        {
            var lead = await crm.Leads.CreateAsync(new CrmLeadValues("Заявка на 50 шапок", ContactName: "Сидорова Мария", Phone: "8 900 000-00-77",
                CompanyName: "ИП Сидорова"));
            var result = await crm.Leads.QualifyAsync(lead, new CrmQualifyCommand());
            Assert.True(result.ContactCreated);
            Assert.NotEqual(foreign, result.ContactId);
            Assert.Equal(result.ContactId, (await crm.Db.CrmDeals.AsNoTracking().SingleAsync(d => d.Id == result.DealId)).ContactId);
            await Assert.ThrowsAsync<NotFoundException>(() => crm.Contacts.GetAsync(foreign));
        }

        await using var db = host.NewDb();
        var created = await db.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.CrmContactCreated
                                                                    && a.ActorUserId == second).SingleAsync();
        Assert.Contains("возможный дубль у другого ответственного", created.Reason);
    }

    [SqlFact]
    public async Task Qualification_is_one_transaction_and_finds_existing_counterparty()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var inn = CrmTestKit.NextCounterpartyInn();
        long supplier;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа и Ко»", inn, null, true, false, null));
        }

        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        // ИНН с неверной контрольной суммой: контрагент не создаётся — и ни контакт, ни сделка не остаются в базе.
        var bad = await crm.Leads.CreateAsync(new CrmLeadValues("Заявка с сайта", ContactName: "Петров Пётр", Phone: "+7 900 000-00-02",
            CompanyName: "ООО «Новый клиент»", Inn: "1234567890"));
        Assert.Equal("catalog.counterparty.inn", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Leads.QualifyAsync(bad, new CrmQualifyCommand()))).Code);
        crm.Db.ChangeTracker.Clear();
        Assert.Equal(0, await crm.Db.CrmContacts.CountAsync(c => c.OrganizationId == org.OrganizationId));
        Assert.Equal(0, await crm.Db.CrmDeals.CountAsync(d => d.OrganizationId == org.OrganizationId));
        Assert.Equal(CrmLeadStatus.New, (await crm.Leads.GetAsync(bad)).Status);

        // Без контактного лица квалифицировать нельзя.
        var noContact = await crm.Leads.CreateAsync(new CrmLeadValues("Звонок", CompanyName: "ООО «Пряжа и Ко»"));
        Assert.Equal("crm.lead.contact_required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Leads.QualifyAsync(noContact, new CrmQualifyCommand()))).Code);

        // Найден по ИНН: поставщик становится и покупателем; выбранная воронка и название сделки.
        var pipeline = await crm.Settings.CreatePipelineAsync("Маркетплейсы");
        var lead = await crm.Leads.CreateAsync(new CrmLeadValues("Партия шапок", ContactName: "Сидорова Ольга", Email: "buyer@example.org", Inn: inn));
        var result = await crm.Leads.QualifyAsync(lead, new CrmQualifyCommand(pipeline, null, "Шапки для маркетплейса", 120_000m));
        Assert.Equal(supplier, result.CounterpartyId);
        Assert.False(result.CounterpartyCreated);
        var deal = await crm.Deals.GetAsync(result.DealId);
        Assert.Equal((pipeline, "Шапки для маркетплейса", 120_000m), (deal.PipelineId, deal.Values.Title, deal.Amount));
        Assert.True((await crm.Db.Counterparties.AsNoTracking().SingleAsync(c => c.Id == supplier)).IsCustomer);

        // Второй лид с той же почтой — контакт переиспользуется.
        var again = await crm.Leads.CreateAsync(new CrmLeadValues("Повторный заказ", ContactName: "Сидорова О.", Email: "BUYER@example.org", Inn: inn));
        var second = await crm.Leads.QualifyAsync(again, new CrmQualifyCommand());
        Assert.Equal(result.ContactId, second.ContactId);
        Assert.False(second.ContactCreated);
    }

    [SqlFact]
    public async Task Visibility_follows_responsible_participants_and_crm_all()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var anna = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var boris = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var head = await host.CrmUserAsync(org, SystemRoles.SalesHead);
        var storekeeper = await host.CrmUserAsync(org, SystemRoles.Storekeeper);
        long customer;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель»", null, null, false, true, null));
        }

        long deal, lead;
        await using (var crm = host.Crm(anna, org.OrganizationId))
        {
            deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера оптом", customer, Amount: 10_000m));
            lead = await crm.Leads.CreateAsync(new CrmLeadValues("Запрос цены", ContactName: "Клиент", Phone: "+7 900 000-00-03"));
            Assert.Equal("crm.responsible.other", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Leads.CreateAsync(new CrmLeadValues("Чужой", ResponsibleUserId: boris)))).Code);
            await Assert.ThrowsAsync<AccessDeniedException>(async () => await crm.Deals.ReassignAsync(deal, boris, (await crm.Deals.GetAsync(deal)).RowVersion));
            await Assert.ThrowsAsync<AccessDeniedException>(() => crm.Settings.CreatePipelineAsync("Своя"));
        }

        await using (var crm = host.Crm(boris, org.OrganizationId))
        {
            // Без crm.all чужая сделка и чужой лид — «не найдено», в списках их нет.
            await Assert.ThrowsAsync<NotFoundException>(() => crm.Deals.GetAsync(deal));
            await Assert.ThrowsAsync<NotFoundException>(() => crm.Leads.GetAsync(lead));
            Assert.Equal(0, (await crm.Deals.ListAsync(new CrmDealFilter())).Total);
            Assert.Equal(0, (await crm.Leads.ListAsync(new CrmLeadFilter())).Total);
            Assert.Empty((await crm.Deals.BoardAsync(new CrmDealFilter())).Columns.SelectMany(c => c.Deals));
            // Подсказка дубликатов видит весь список организации (D605), но без телефона.
            Assert.Single(await crm.Leads.DuplicatesAsync("+7 900 000-00-03", null, null));
        }

        await using (var crm = host.Crm(head, org.OrganizationId))
        {
            Assert.Equal(1, (await crm.Deals.ListAsync(new CrmDealFilter())).Total);
            Assert.Equal(0, (await crm.Deals.ListAsync(new CrmDealFilter(Mine: true))).Total);
            await crm.Deals.AddParticipantAsync(deal, boris, (await crm.Deals.GetAsync(deal)).RowVersion);
        }

        await using (var crm = host.Crm(boris, org.OrganizationId))
        {
            // Наблюдатель видит сделку, но не переназначает.
            Assert.Equal("Свитера оптом", (await crm.Deals.GetAsync(deal)).Values.Title);
            Assert.Equal(1, (await crm.Deals.ListAsync(new CrmDealFilter(Mine: true))).Total);
        }

        await using (var crm = host.Crm(head, org.OrganizationId))
        {
            await crm.Deals.ReassignAsync(deal, boris, (await crm.Deals.GetAsync(deal)).RowVersion);
            var dto = await crm.Deals.GetAsync(deal);
            Assert.Equal(boris, dto.Values.ResponsibleUserId);
            Assert.Empty(dto.Participants);
            Assert.Equal(1, (await crm.Leads.ListAsync(new CrmLeadFilter())).Total);
        }

        await using (var crm = host.Crm(storekeeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => crm.Deals.GetAsync(deal));
        }

        var other = await host.CreateCrmOrganizationAsync();
        await using (var crm = host.Crm(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => crm.Deals.GetAsync(deal));
            await Assert.ThrowsAsync<NotFoundException>(() => crm.Leads.GetAsync(lead));
            await Assert.ThrowsAsync<NotFoundException>(async () => await crm.Deals.MoveAsync(deal, 1, []));
        }

        // Теги и обсуждения работают на сделке (TaggedEntity 31); чужой — «не найдено».
        // D130: новый тег заводит тот, у кого есть право на справочник тегов; менеджер ставит существующий.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Tags.CreateAsync("Опт", TagColor.Blue);
        }

        await using (var s = host.As(boris, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Tags.AssignAsync(TaggedEntity.CrmDeal, deal, null, "Розница", TagColor.Red));
            await s.Tags.AssignAsync(TaggedEntity.CrmDeal, deal, null, "Опт", TagColor.Blue);
            Assert.Equal("Опт", (await s.Tags.ForEntityAsync(TaggedEntity.CrmDeal, deal)).Single().Name);
            await s.Discussions.AddAsync(TaggedEntity.CrmDeal, deal, "Клиент просит образцы", null);
            Assert.Single((await s.Discussions.ThreadAsync(TaggedEntity.CrmDeal, deal)).Comments);
        }

        await using (var s = host.As(anna, org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Discussions.ThreadAsync(TaggedEntity.CrmDeal, deal));
        }
    }

    [SqlFact]
    public async Task Pipelines_stages_reasons_and_merge_rules()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using var crm = host.Crm(org.OwnerUserId, org.OrganizationId);
        var settings = await crm.Settings.GetAsync();
        var pipeline = settings.Pipelines.Single();
        var won = pipeline.Stages.Single(s => s.Kind == CrmStageKind.Won);
        Assert.Equal("crm.stage.final", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Settings.ArchiveStageAsync(won.Id, won.RowVersion))).Code);
        Assert.Equal("crm.stage.final_probability", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Settings.UpdateStageAsync(won.Id, "Выиграна", "green", 90, won.RowVersion))).Code);
        Assert.Equal("crm.pipeline.last", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Settings.ArchivePipelineAsync(pipeline.Id, pipeline.RowVersion))).Code);

        // Новый рабочий этап встаёт перед финальными; сдвиг влево.
        var samples = await crm.Settings.CreateStageAsync(pipeline.Id, "Пробная партия", "violet", 70);
        Assert.Equal("crm.stage.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Settings.CreateStageAsync(pipeline.Id, "пробная партия", "gray", 10))).Code);
        await crm.Settings.MoveStageAsync(samples, -1);
        var names = (await crm.Settings.GetAsync()).Pipelines.Single().Stages.Select(s => s.Name).ToList();
        Assert.Equal(["Новая", "Выявление потребности", "Образцы / расчёт", "КП отправлено", "Пробная партия", "Согласование", "Выиграна", "Проиграна"], names);

        // Второй «выигрыш» в воронке база не примет (D602).
        crm.Db.CrmStages.Add(CrmStage.Create(org.OrganizationId, pipeline.Id, "Ещё выигрыш", "green", 100, CrmStageKind.Won, 999));
        await Assert.ThrowsAsync<DbUpdateException>(() => crm.Db.SaveChangesAsync());
        crm.Db.ChangeTracker.Clear();

        // Проигрыш — с причиной из справочника; архивная причина не принимается.
        var reason = await crm.Settings.CreateReferenceAsync(CrmReferenceKind.LossReason, "Нет нужной пряжи");
        long customer;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Клиент»", null, null, false, true, null));
        }

        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Пледы", customer, Amount: 5_000m));
        var lost = pipeline.Stages.Single(s => s.Kind == CrmStageKind.Lost);
        await crm.Deals.MoveAsync(deal, lost.Id, (await crm.Deals.GetAsync(deal)).RowVersion, reason, "Нет пряжи нужного цвета");
        var dto = await crm.Deals.GetAsync(deal);
        Assert.Equal((CrmDealOutcome.Lost, "Нет нужной пряжи", 0), (dto.Outcome, dto.LossReason, dto.Probability));
        Assert.False(dto.CanCreateOrder);
        // Вернуть на рабочий этап можно — история сохраняет все переходы.
        await crm.Deals.MoveAsync(deal, pipeline.Stages[0].Id, dto.RowVersion);
        Assert.Equal((CrmDealOutcome.Open, 3), ((await crm.Deals.GetAsync(deal)).Outcome, (await crm.Deals.GetAsync(deal)).History.Count));

        // Объединение дубликатов — только с crm.all.
        var a = await crm.Leads.CreateAsync(new CrmLeadValues("Заявка 1", ContactName: "Клиент", Phone: "+7 900 000-00-04"));
        var b = await crm.Leads.CreateAsync(new CrmLeadValues("Заявка 2", Email: "dup@example.org", Need: "500 шарфов"));
        await using (var m = host.Crm(manager, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.Leads.MergeAsync(b, a));
        }

        await crm.Leads.MergeAsync(b, a);
        var merged = await crm.Leads.GetAsync(b);
        var main = await crm.Leads.GetAsync(a);
        Assert.Equal((CrmLeadStatus.Rejected, a, "Дубликат"), (merged.Status, merged.MergedIntoLeadId, merged.RejectReason));
        Assert.Equal(("dup@example.org", "500 шарфов"), (main.Values.Email, main.Values.Need));
        Assert.Equal("crm.lead.status", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Leads.ReopenAsync(b, merged.RowVersion))).Code);
        Assert.Contains(await crm.Db.AuditEntries.AsNoTracking().Where(x => x.OrganizationId == org.OrganizationId).Select(x => x.Action).ToListAsync(),
            x => x == AuditActions.CrmLeadMerged);
    }

    [SqlFact]
    public async Task Board_uses_few_queries_and_sums_columns()
    {
        var org = await host.CreateCrmOrganizationAsync();
        long customer;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Сеть магазинов»", null, null, false, true, null));
        }

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            for (var i = 1; i <= 60; i++)
            {
                await crm.Deals.CreateAsync(new CrmDealValues($"Сделка {i}", customer, Amount: 1_000m));
            }
        }

        var counter = new CrmCommandCounter();
        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId, counter))
        {
            var board = await crm.Deals.BoardAsync(new CrmDealFilter());
            var first = board.Columns[0];
            Assert.Equal((60, CrmDealService.BoardCardsPerColumn, 60_000m, 6_000m), (first.Count, first.Deals.Count, first.Total!.Value, first.Weighted!.Value));
            Assert.Equal("Сделка 60", first.Deals[0].Title);
            Assert.Equal(CrmStage.Defaults.Count, board.Columns.Count);
            // ТЗ CRM §8: открытие канбана — не больше 5 запросов к таблицам CRM.
            Assert.InRange(counter.Crm, 1, 5);
        }
    }

    [SqlFact]
    public async Task Contacts_are_visible_by_responsible_and_validate_phone()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        await using var crm = host.Crm(manager, org.OrganizationId);
        Assert.Equal("crm.phone", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Contacts.CreateAsync(new CrmContactValues("Тестова Мария", Phone: "12-34")))).Code);
        var id = await crm.Contacts.CreateAsync(new CrmContactValues("Тестова Мария", "Закупщик", Phone: "+7 900 000-00-05", Email: "maria@example.org"));
        Assert.Single((await crm.Contacts.ListAsync("900 000-00-05")).Rows);
        Assert.Single(await crm.Contacts.DuplicatesAsync("+79000000005", null));
        var dto = await crm.Contacts.GetAsync(id);
        await crm.Contacts.SetArchivedAsync(id, true, dto.RowVersion);
        Assert.Empty((await crm.Contacts.ListAsync(null)).Rows);
        await using var head = host.Crm(org.OwnerUserId, org.OrganizationId);
        Assert.Single((await head.Contacts.ListAsync(null, includeArchived: true)).Rows);
    }

    private async Task<(long Store, long Sweater)> StockAsync(KnitErp.Application.Organizations.CreatedOrganization org)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-CRM", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
        return (store, sweater);
    }

    /// <summary>Считает запросы к таблицам CRM (crm_*).</summary>
    private sealed class CrmCommandCounter : DbCommandInterceptor
    {
        public int Crm;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("[crm_", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Crm);
            }

            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("[crm_", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Crm);
            }

            return base.ScalarExecutingAsync(command, eventData, result, ct);
        }
    }
}
