using KnitErp.Application.Catalog;
using KnitErp.Application.Crm;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Crm;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Отчёты CRM для пилота (D685–D689): воронка и конверсия по менеджерам, длительность этапов (среднее и медиана) по истории этапов,
/// источники лидов, план-факт по месяцам, скрытие сумм без «Цены и суммы», только свои данные без crm.all, листы Excel.
/// Данные вымышленные: +7 900 000-00-xx, @example.org.
/// </summary>
public sealed class CrmReportTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateTime Start = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
    private static readonly CrmReportFilter October = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

    /// <summary>
    /// Анна: лид «Сайт» → сделка 100 000 (2 дня на «Новой», 4 — на «Выявлении», 1 — на «КП», выиграна), лид «Сайт» без сделки,
    /// сделка «Звонок» 50 000 (6 дней на «Новой»). Борис: лид «Звонок» → сделка 30 000 (проиграна через день), сделка 20 000 без источника.
    /// </summary>
    private async Task<(CreatedOrganization Org, long Anna, long Boris)> SeedFunnelAsync()
    {
        host.Clock.UtcNow = Start; // сотрудники приглашены до первых сделок — иначе роль ещё не действует
        var org = await host.CreateCrmOrganizationAsync();
        var anna = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var boris = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var customer = await CustomerAsync(org);

        await using var a = host.Crm(anna, org.OrganizationId);
        await using var b = host.Crm(boris, org.OrganizationId);
        var options = await a.Leads.OptionsAsync();
        var site = options.Sources.Single(s => s.Name == "Сайт").Id;
        var call = options.Sources.Single(s => s.Name == "Звонок").Id;
        var reason = options.LossReasons.First().Id;

        var la1 = await a.Leads.CreateAsync(new CrmLeadValues("Свитера для сети", site, ContactName: "Покупатель 1", Phone: "+7 900 000-00-41"));
        var da1 = (await a.Leads.QualifyAsync(la1, new CrmQualifyCommand(null, customer, null, 100_000m))).DealId;
        await a.Leads.CreateAsync(new CrmLeadValues("Шапки", site, ContactName: "Покупатель 2", Email: "buyer42@example.org"));
        var da2 = await a.Deals.CreateAsync(new CrmDealValues("Пледы", customer, Amount: 50_000m, SourceId: call));
        var lb1 = await b.Leads.CreateAsync(new CrmLeadValues("Шарфы", call, ContactName: "Покупатель 3", Phone: "+7 900 000-00-43"));
        var db1 = (await b.Leads.QualifyAsync(lb1, new CrmQualifyCommand(null, customer, null, 30_000m))).DealId;
        await b.Deals.CreateAsync(new CrmDealValues("Носки", customer, Amount: 20_000m));

        async Task MoveAsync(CrmServices crm, long deal, Func<CrmStageDto, bool> stage, long? lossReason = null)
        {
            var dto = await crm.Deals.GetAsync(deal);
            await crm.Deals.MoveAsync(deal, dto.Stages.Single(stage).Id, dto.RowVersion, lossReason);
        }

        host.Clock.UtcNow = Start.AddDays(1);
        await MoveAsync(b, db1, s => s.Kind == CrmStageKind.Lost, reason);
        host.Clock.UtcNow = Start.AddDays(2);
        await MoveAsync(a, da1, s => s.Name == "Выявление потребности");
        host.Clock.UtcNow = Start.AddDays(6);
        await MoveAsync(a, da1, s => s.Name == "КП отправлено");
        await MoveAsync(a, da2, s => s.Name == "Выявление потребности");
        host.Clock.UtcNow = Start.AddDays(7);
        await MoveAsync(a, da1, s => s.Kind == CrmStageKind.Won);
        return (org, anna, boris);
    }

    [SqlFact]
    public async Task Funnel_conversion_durations_and_sources_by_managers()
    {
        var clock = host.Clock.UtcNow;
        try
        {
            var (org, anna, boris) = await SeedFunnelAsync();
            await using var head = host.Crm(org.OwnerUserId, org.OrganizationId);
            var r = await head.Reports.BuildAsync(October);

            // Общая воронка: 4 сделки на «Новой», 2 дошли до «Выявления», выигрыш прошёл все рабочие этапы.
            Assert.Equal([4, 2, 1, 1, 1, 1], r.Funnel.Select(s => s.Reached));
            Assert.Equal(200_000m, r.Funnel[0].Amount);
            Assert.Equal([50m, 50m, 100m, 100m, 100m], r.Funnel.Take(5).Select(s => s.ConversionToNext!.Value));

            // Воронка по менеджерам — те же этапы.
            var af = r.ManagerFunnel.Single(m => m.UserId == anna).Steps;
            var bf = r.ManagerFunnel.Single(m => m.UserId == boris).Steps;
            Assert.Equal([2, 2, 1, 1, 1, 1], af.Select(c => c.Reached));
            Assert.Equal((150_000m, 100m, 50m), (af[0].Amount!.Value, af[0].ConversionToNext!.Value, af[1].ConversionToNext!.Value));
            Assert.Equal(100_000m, af[^1].Amount);
            Assert.Equal([2, 0, 0, 0, 0, 0], bf.Select(c => c.Reached));
            Assert.Equal(0m, bf[0].ConversionToNext);
            Assert.Null(bf[1].ConversionToNext);

            // Конверсия лид → сделка → выигрыш.
            Assert.Equal((3, 2, 1, 4, 1), (r.Conversion.Leads, r.Conversion.LeadDeals, r.Conversion.LeadWon, r.Conversion.Deals, r.Conversion.Won));
            Assert.Equal((66.7m, 33.3m, 25m), (r.Conversion.LeadToDeal!.Value, r.Conversion.LeadToWon!.Value, r.Conversion.DealToWon!.Value));
            var ac = r.ManagerConversion.Single(m => m.Id == anna);
            var bc = r.ManagerConversion.Single(m => m.Id == boris);
            Assert.Equal((2, 1, 1, 2, 1, 50m, 50m), (ac.Leads, ac.LeadDeals, ac.LeadWon, ac.Deals, ac.Won, ac.LeadToDeal!.Value, ac.DealToWon!.Value));
            Assert.Equal((1, 1, 0, 2, 0, 0m), (bc.Leads, bc.LeadDeals, bc.LeadWon, bc.Deals, bc.Won, bc.DealToWon!.Value));
            Assert.Equal((66.7m, 25m), (r.LeadToDeal!.Value, r.DealToWon!.Value));

            // Длительность этапов: выходы с этапа в октябре — «Новая» 1, 2, 6 дней; «Выявление» 4; «КП» 1; цикл выигранной — 7 дней.
            var stages = r.Durations.ToDictionary(d => d.Stage, d => d.Days);
            Assert.Equal((3, 3m, 2m), (stages["Новая"].Count, stages["Новая"].AvgDays!.Value, stages["Новая"].MedianDays!.Value));
            Assert.Equal((1, 4m, 4m), (stages["Выявление потребности"].Count, stages["Выявление потребности"].AvgDays!.Value, stages["Выявление потребности"].MedianDays!.Value));
            Assert.Equal((1, 1m), (stages["КП отправлено"].Count, stages["КП отправлено"].MedianDays!.Value));
            Assert.Equal((0, null), (stages["Образцы / расчёт"].Count, stages["Образцы / расчёт"].AvgDays));
            Assert.Equal((1, 7m, 7m), (r.Cycle.Count, r.Cycle.AvgDays!.Value, r.Cycle.MedianDays!.Value));
            Assert.Equal(7m, r.AvgCycleDays);
            Assert.Equal(3m, r.Funnel[0].AvgDays);
            var ad = r.ManagerDurations.Single(m => m.UserId == anna);
            Assert.Equal((2, 4m, 4m), (ad.Stages[0].Count, ad.Stages[0].AvgDays!.Value, ad.Stages[0].MedianDays!.Value));
            Assert.Equal(7m, ad.Cycle.AvgDays);
            var bd = r.ManagerDurations.Single(m => m.UserId == boris);
            Assert.Equal((1, 1m), (bd.Stages[0].Count, bd.Stages[0].AvgDays!.Value));
            Assert.Equal(0, bd.Cycle.Count);

            // Источники: лиды, сделки, выигрыши, конверсия; без источника — отдельной строкой.
            var site = r.Sources.Single(s => s.Name == "Сайт");
            var call = r.Sources.Single(s => s.Name == "Звонок");
            var none = r.Sources.Single(s => s.Id == null);
            Assert.Equal((2, 1, 1, 1, 50m, 100m), (site.Leads, site.LeadDeals, site.Deals, site.Won, site.LeadToDeal!.Value, site.DealToWon!.Value));
            Assert.Equal((1, 1, 2, 0, 100m, 0m), (call.Leads, call.LeadDeals, call.Deals, call.Won, call.LeadToDeal!.Value, call.DealToWon!.Value));
            Assert.Equal((0, 1, (decimal?)null), (none.Leads, none.Deals, none.LeadToDeal));
            Assert.Equal(0m, site.Revenue); // оплат по заказам ещё нет

            // Причины проигрыша и отбор по менеджеру.
            Assert.Equal(1, r.LossReasons.Single().Deals);
            var onlyBoris = await head.Reports.BuildAsync(October with { ResponsibleUserId = boris });
            Assert.Equal(boris, onlyBoris.ManagerFunnel.Single().UserId);
            Assert.Equal(2, onlyBoris.Funnel[0].Reached);
        }
        finally
        {
            host.Clock.UtcNow = clock;
        }
    }

    [SqlFact]
    public async Task Manager_without_crm_all_sees_only_own_rows_and_no_amounts_without_price_right()
    {
        var clock = host.Clock.UtcNow;
        try
        {
            var (org, anna, boris) = await SeedFunnelAsync();
            await using (var a = host.Crm(anna, org.OrganizationId))
            {
                var r = await a.Reports.BuildAsync(October);
                Assert.Equal([2, 2, 1, 1, 1, 1], r.Funnel.Select(s => s.Reached));
                Assert.Equal(anna, r.ManagerFunnel.Single().UserId);
                Assert.Equal(anna, r.ManagerConversion.Single().Id);
                Assert.Equal(anna, r.ManagerDurations.Single().UserId);
                Assert.All(r.Managers, m => Assert.Equal(anna, m.UserId));
                Assert.Equal((2, 2), (r.Conversion.Leads, r.Conversion.Deals));
                Assert.Equal(2, r.Durations[0].Days.Count); // выход Бориса с «Новой» не виден
                Assert.DoesNotContain(r.Sources, s => s.Id == null); // сделка Бориса без источника
                Assert.Empty(r.LossReasons);

                // Отбор чужого менеджера не открывает его строки.
                var foreign = await a.Reports.BuildAsync(October with { ResponsibleUserId = boris });
                Assert.Empty(foreign.ManagerFunnel);
                Assert.Equal(0, foreign.Conversion.Deals);
            }

            // Без «Цены и суммы» (токен с crm.view и crm.all): количества есть, суммы — null, план-факта нет.
            await using var noPrice = host.Crm(org.OwnerUserId, org.OrganizationId, [Permissions.CrmView, Permissions.CrmAll]);
            var n = await noPrice.Reports.BuildAsync(October);
            Assert.False(n.CanSeeAmounts);
            Assert.Equal(4, n.Funnel[0].Reached);
            Assert.All(n.Funnel, s => Assert.Null(s.Amount));
            Assert.All(n.ManagerFunnel.SelectMany(m => m.Steps), c => Assert.Null(c.Amount));
            Assert.All(n.Sources, s => Assert.Null(s.Revenue));
            Assert.All(n.ManagerConversion.Append(n.Conversion), c => Assert.Null(c.WonAmount));
            Assert.All(n.Managers, m => Assert.Null(m.Plan ?? m.Fact ?? m.WonAmount));
            Assert.Null(n.WonAmount);
            Assert.Null(n.Plan);
            Assert.Empty(n.PlanFactMonths);
            Assert.Empty(n.ManagerPlanFact);
            Assert.Equal(2, n.ManagerFunnel.Count);
            var xlsx = await noPrice.Reports.ExportAsync(October);
            var funnelSheet = SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "Воронка", 100)!;
            Assert.Equal("", funnelSheet[1].ElementAtOrDefault(2) ?? "");
        }
        finally
        {
            host.Clock.UtcNow = clock;
        }
    }

    /// <summary>
    /// План-факт по месяцам (факт — выигранные сделки): сентябрь — без общего плана, план = сумма планов менеджеров; октябрь — общий план
    /// организации. Месяцы периода берутся целиком; итог периода — только месяцы, целиком попавшие в период (D631).
    /// </summary>
    [SqlFact]
    public async Task Plan_fact_by_months_and_managers()
    {
        var clock = host.Clock.UtcNow;
        try
        {
            host.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
            var org = await host.CreateCrmOrganizationAsync();
            var anna = await host.CrmUserAsync(org, SystemRoles.SalesManager);
            var boris = await host.CrmUserAsync(org, SystemRoles.SalesManager);
            var customer = await CustomerAsync(org);
            await using (var head = host.Crm(org.OwnerUserId, org.OrganizationId))
            {
                await head.Settings.SaveAutomationAsync(new CrmAutomationValues(PlanFact: CrmPlanFact.WonDeals), null);
                await head.Settings.SetPlanAsync(2026, 9, anna, null, 100_000m);
                await head.Settings.SetPlanAsync(2026, 10, anna, null, 200_000m);
                await head.Settings.SetPlanAsync(2026, 10, null, null, 500_000m);
                await head.Settings.SetPlanAsync(2026, 11, boris, null, 50_000m);
            }

            host.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
            async Task WinAsync(long user, decimal amount, DateTime atUtc)
            {
                await using var crm = host.Crm(user, org.OrganizationId);
                host.Clock.UtcNow = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
                var id = await crm.Deals.CreateAsync(new CrmDealValues($"Сделка {amount}", customer, Amount: amount));
                host.Clock.UtcNow = atUtc;
                var dto = await crm.Deals.GetAsync(id);
                await crm.Deals.MoveAsync(id, dto.Stages.Single(s => s.Kind == CrmStageKind.Won).Id, dto.RowVersion);
            }

            await WinAsync(anna, 60_000m, new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc));
            await WinAsync(anna, 80_000m, new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc));
            await WinAsync(boris, 40_000m, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
            await WinAsync(boris, 1_000m, new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)); // вне периода

            var filter = new CrmReportFilter(new DateOnly(2026, 9, 10), new DateOnly(2026, 10, 31));
            await using (var head = host.Crm(org.OwnerUserId, org.OrganizationId))
            {
                var r = await head.Reports.BuildAsync(filter);
                Assert.Equal([(2026, 9, 100_000m, 60_000m, 60m), (2026, 10, 500_000m, 120_000m, 24m)],
                    r.PlanFactMonths.Select(m => (m.Year, m.Month, m.Plan, m.Fact, m.Done!.Value)));
                Assert.Equal((500_000m, 180_000m), (r.Plan!.Value, r.Fact!.Value));
                var a = r.ManagerPlanFact.Single(m => m.UserId == anna);
                Assert.Equal([(100_000m, 60_000m), (200_000m, 80_000m)], a.Months.Select(m => (m.Plan, m.Fact)));
                Assert.Equal((300_000m, 140_000m, 46.7m), (a.Plan, a.Fact, a.Done!.Value));
                var b = r.ManagerPlanFact.Single(m => m.UserId == boris);
                Assert.Equal([(0m, 0m), (0m, 40_000m)], b.Months.Select(m => (m.Plan, m.Fact)));
                Assert.Null(b.Done);
                Assert.Equal((200_000m, 140_000m), (r.Managers.Single(m => m.UserId == anna).Plan!.Value, r.Managers.Single(m => m.UserId == anna).Fact!.Value));

                // Excel: все разрезы — отдельными листами.
                var xlsx = await head.Reports.ExportAsync(filter);
                foreach (var sheet in new[] { "Воронка", "Воронка по менеджерам", "Конверсия", "Источники", "Длительность этапов", "Причины", "Менеджеры", "План-факт" })
                {
                    Assert.NotNull(SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), sheet, 200));
                }

                var plan = SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "План-факт", 200)!;
                Assert.Equal(["Менеджер", "Месяц", "План", "Факт", "Выполнение, %"], plan[0]);
                Assert.Contains(plan, row => row[0] == "Вся организация" && row[1] == "10.2026" && Num(row[2]) == 500_000m && Num(row[3]) == 120_000m && Num(row[4]) == 24m);
                Assert.Equal(1 + 2 + 2 + 2, plan.Count);
                var durations = SqlTestHost.Spreadsheet.ReadNamedSheet(new MemoryStream(xlsx), "Длительность этапов", 200)!;
                Assert.Contains(durations, row => row[0] == "Вся организация" && row[1] == "Цикл выигранной сделки" && Num(row[2]) == 3m);
            }

            // Менеджер без crm.all — только свой план и факт.
            await using var mine = host.Crm(anna, org.OrganizationId);
            var own = await mine.Reports.BuildAsync(filter);
            Assert.Equal([(100_000m, 60_000m), (200_000m, 80_000m)], own.PlanFactMonths.Select(m => (m.Plan, m.Fact)));
            Assert.Equal(anna, own.ManagerPlanFact.Single().UserId);
            Assert.Equal((200_000m, 140_000m), (own.Plan!.Value, own.Fact!.Value));
        }
        finally
        {
            host.Clock.UtcNow = clock;
        }
    }

    /// <summary>Число из ячейки Excel в любом виде чтения («500,000.», «24»).</summary>
    private static decimal Num(string cell) =>
        decimal.Parse(cell.Replace(",", "").TrimEnd('.'), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<long> CustomerAsync(CreatedOrganization org)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        return await s.Counterparties.CreateAsync(new CounterpartyCommand($"ООО «Покупатель {Guid.NewGuid():N}"[..30] + "»", null, null, false, true, null));
    }
}
