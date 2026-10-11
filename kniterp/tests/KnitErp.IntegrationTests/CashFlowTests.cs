using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Organizations;
using KnitErp.Application.Structure;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D86: статьи ДДС, отчёт о движении денег, кассовая книга КО-4, подотчётные лица и авансовые отчёты.</summary>
public sealed class CashFlowTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 850_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Cash_flow_items_are_seeded_editable_and_checked_in_operations()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var items = await s.CashFlowItems.ListAsync();
        Assert.Equal(CashFlowItem.Defaults.Count, items.Count);

        // Своя статья; дубль и правка системной — нельзя; архивная не выбирается.
        var repair = await s.CashFlowItems.CreateAsync(CashFlowDirection.Out, "Ремонт оборудования");
        Assert.Equal("cash_flow_item.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CashFlowItems.CreateAsync(CashFlowDirection.Out, "Ремонт оборудования"))).Code);
        var system = items.Single(i => i.SystemCode == CashFlowItem.OtherExpense);
        Assert.Equal("cash_flow_item.system", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CashFlowItems.RenameAsync(system.Id, "Другое", system.RowVersion))).Code);

        var bank = await BankAsync(s);
        var founders = items.Single(i => i.Name == "Взносы учредителей").Id;
        var income = await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day, MoneyOperationKind.Income, bank, null, 5_000m, null, "Взнос", null, founders));
        Assert.Equal("Взносы учредителей", (await s.MoneyOperations.GetAsync(income)).CashFlowItem);
        var noItem = await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day, MoneyOperationKind.Expense, bank, null, 10m, null, "Мелочь", null));
        Assert.Equal("Прочие выплаты", (await s.MoneyOperations.GetAsync(noItem)).CashFlowItem);

        // Статья не того направления; статья оплат — только для оплат; перемещение без статьи; сотрудник — только с подотчётной статьёй.
        Assert.Equal("cash_flow_item.direction_mismatch", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Expense, bank, null, 1m, null, "x", null, founders)))).Code);
        Assert.Equal("cash_flow_item.payments_only", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Income, bank, null, 1m, null, "x", null,
                items.Single(i => i.SystemCode == CashFlowItem.CustomerPayments).Id)))).Code);
        var cash = await s.LegalEntities.AddCashAsync((await s.LegalEntities.ListAsync()).Single().Id, "Касса");
        Assert.Equal("money.transfer.item", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Transfer, bank, cash, 1m, null, "x", null, repair)))).Code);
        var employee = await HireAsync(s, "001", "Иванова", "Анна");
        Assert.Equal("money.accountable.employee", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Expense, bank, null, 1m, null, "x", null, repair, employee)))).Code);

        // Архив своей статьи; переименование; чужая организация — «не найдено».
        var repairRow = (await s.CashFlowItems.ListAsync()).Single(i => i.Id == repair);
        await s.CashFlowItems.SetArchivedAsync(repair, true, repairRow.RowVersion);
        Assert.Equal("cash_flow_item.archived", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Expense, bank, null, 1m, null, "x", null, repair)))).Code);
        Assert.DoesNotContain(await s.CashFlowItems.ListAsync(), i => i.Id == repair);
        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.CashFlowItems.RenameAsync(repair, "Чужое", repairRow.RowVersion));
    }

    [SqlFact]
    public async Task Accountable_issue_report_and_return_settle_the_employee()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var cash = await s.LegalEntities.AddCashAsync((await s.LegalEntities.ListAsync()).Single().Id, "Касса");
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(-1), MoneyOperationKind.Income, cash, null, 10_000m, null, "Из банка", null));
        var anna = await HireAsync(s, "001", "Иванова", "Анна");

        // Выдача под отчёт 3 000: статья подставляется сама, «Выдать» — ФИО сотрудника, РКО.
        var issue = await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day, MoneyOperationKind.Expense, cash, null, 3_000m, null,
            "Под отчёт на хозяйственные нужды", null, null, anna));
        var issueRow = await s.MoneyOperations.GetAsync(issue);
        Assert.Equal(("Выдача под отчёт", "Иванова Анна", true), (issueRow.CashFlowItem, issueRow.Party, issueRow.Number.StartsWith("РКО")));
        Assert.Equal(3_000m, (await s.Accountables.BalancesAsync()).Single().Debt);

        // Авансовый отчёт: черновик, две строки 1 800 + 700, утверждение — долг 500.
        var options = await s.Accountables.OptionsAsync();
        var item = options.ExpenseItems.First(i => i.SystemCode == null).Id;
        var report = await s.Accountables.CreateReportAsync(anna, Day.AddDays(2), null, "Хозяйственные нужды");
        var r = await s.Accountables.GetReportAsync(report);
        Assert.StartsWith("АО-", r.Number);
        await s.Accountables.AddLineAsync(report, Day.AddDays(1), "Кассовый чек № 12", "Пряжа на образцы", 1_800m, item, r.RowVersion);
        await s.Accountables.AddLineAsync(report, Day.AddDays(1), "Кассовый чек № 13", "Иглы", 700m, item, (await s.Accountables.GetReportAsync(report)).RowVersion);
        r = await s.Accountables.GetReportAsync(report);
        Assert.Equal((2_500m, 3_000m, 3_000m, 500m), (r.Total, r.IssuedBefore, r.DebtBefore, r.DebtAfter));
        var incomeItem = (await s.CashFlowItems.ListAsync()).Single(i => i.SystemCode == CashFlowItem.OtherIncome).Id;
        Assert.Equal("expense_report.item", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Accountables.AddLineAsync(report, Day, "x", null, 1m, incomeItem, r.RowVersion))).Code);
        await s.Accountables.ApproveAsync(report, r.RowVersion);
        Assert.Equal(500m, (await s.Accountables.BalancesAsync()).Single().Debt);

        // Утверждённый не правится — ни сервисом, ни в обход (триггер базы).
        r = await s.Accountables.GetReportAsync(report);
        Assert.False(r.CanEdit);
        Assert.Equal("expense_report.not_draft", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Accountables.AddLineAsync(report, Day, "x", null, 1m, item, r.RowVersion))).Code);
        await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [kniterp].[expense_report_lines] SET [Amount] = 1 WHERE [ExpenseReportId] = {report}"));

        // Возврат больше долга — нельзя; возврат 500 закрывает долг; ПКО.
        Assert.Equal("money.accountable.return_exceeds", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day.AddDays(2), MoneyOperationKind.Income, cash, null, 600m, null, "Возврат", null, null, anna)))).Code);
        var back = await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(2), MoneyOperationKind.Income, cash, null, 500m, null,
            "Возврат неиспользованной подотчётной суммы", null, null, anna));
        Assert.Equal("Возврат подотчётных сумм", (await s.MoneyOperations.GetAsync(back)).CashFlowItem);
        Assert.Equal(0m, (await s.Accountables.BalancesAsync()).Single().Debt);

        // Отмена отчёта с причиной — расходы не признаны, сотрудник снова должен 2 500.
        await s.Accountables.CancelAsync(report, "чеки не те", r.RowVersion);
        Assert.Equal(2_500m, (await s.Accountables.BalancesAsync()).Single().Debt);
        Assert.Single(await s.Accountables.ListReportsAsync(anna));
        Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId
                                                         && a.Action == KnitErp.Domain.Audit.AuditActions.ExpenseReportApproved));

        // Чужая организация — «не найдено».
        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Accountables.GetReportAsync(report));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Accountables.CreateReportAsync(anna, Day, null, null));
    }

    [SqlFact]
    public async Task Cash_flow_report_reconciles_with_balances_and_cash_book_has_daily_sheets()
    {
        var f = await SeedAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var entity = (await s.LegalEntities.ListAsync()).Single().Id;
        var bank = await BankAsync(s);
        var cash = await s.LegalEntities.AddCashAsync(entity, "Касса цеха");
        var cashRow = (await s.LegalEntities.ListAsync()).Single().Accounts.Single(a => a.Id == cash);
        await s.LegalEntities.SetOpeningAsync(cash, Day.AddDays(-5), 1_000m, cashRow.RowVersion);

        // До периода: оплата покупателя 400 на счёт.
        await s.Sales.CreatePaymentAsync(Day.AddDays(-3), f.Customer, null, 400m, null, null, bank);

        // В периоде: оплата покупателя 2 000 в кассу (ПКО), поставщику 300 из кассы (РКО), сдача 1 500 в банк (РКО), взнос 5 000 на счёт,
        // начальный остаток счёта 700 введён внутри периода.
        await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 2_000m, null, null, cash);
        await s.Purchases.CreatePaymentAsync(Day, f.Supplier, null, 300m, null, cash);
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(1), MoneyOperationKind.Transfer, cash, bank, 1_500m, null,
            "Сдача наличных в банк", null));
        var founders = (await s.CashFlowItems.ListAsync()).Single(i => i.Name == "Взносы учредителей").Id;
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(1), MoneyOperationKind.Income, bank, null, 5_000m, null, "Взнос", null, founders));
        var bankRow = (await s.LegalEntities.ListAsync()).Single().Accounts.Single(a => a.Id == bank);
        await s.LegalEntities.SetOpeningAsync(bank, Day.AddDays(-1), 700m, bankRow.RowVersion);

        var report = await s.CashReports.CashFlowAsync(Day.AddDays(-2), Day.AddDays(2));
        Assert.Equal((2_000m + 5_000m, 300m), (report.In, report.Out));
        Assert.Contains(report.Lines, l => l.Item == "Поступления от покупателей" && l.Amount == 2_000m);
        Assert.Contains(report.Lines, l => l.Item == "Оплата поставщикам" && l.Amount == 300m);
        Assert.DoesNotContain(report.Lines, l => l.Amount == 1_500m);
        Assert.Equal(700m, report.OpeningEntered);
        Assert.Equal(report.Opening + report.OpeningEntered + report.In - report.Out, report.Closing);
        Assert.Equal((await s.Money.BalancesAsync(Day.AddDays(2))).Sum(b => b.Balance), report.Closing);

        // Кассовая книга: два листа (09.10 и 10.10), остаток переходит; номера листов по порядку с начала года.
        var book = await s.CashReports.CashBookAsync(cash, Day.AddDays(-2), Day.AddDays(2));
        Assert.Equal(2, book.Sheets.Count);
        var first = book.Sheets[0];
        Assert.Equal((Day, 1, 1_000m, 2_000m, 300m, 2_700m), (first.Date, first.SheetNumber, first.Opening, first.In, first.Out, first.Closing));
        Assert.Equal((1, 1), (first.IncomeOrders, first.ExpenseOrders));
        Assert.StartsWith("ПКО-", first.Lines[0].Number);
        var second = book.Sheets[1];
        Assert.Equal((2, 2_700m, 1_500m, 1_200m), (second.SheetNumber, second.Opening, second.Out, second.Closing));
        Assert.StartsWith("РКО-", second.Lines.Single().Number);

        // Книга — только по кассе; период не больше года.
        Assert.Equal("cash_book.not_cash", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.CashReports.CashBookAsync(bank, Day, Day))).Code);
        Assert.Equal("money.report.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CashReports.CashBookAsync(cash, Day, Day.AddDays(400)))).Code);

        // Отбор ДДС по чужому юрлицу — «не найдено».
        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.CashReports.CashFlowAsync(Day, Day, entity));
        await Assert.ThrowsAsync<NotFoundException>(() => o.CashReports.CashBookAsync(cash, Day, Day));
    }

    [SqlFact]
    public async Task Overdue_advance_reports_and_cash_over_limit_are_reminded()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var entity = (await s.LegalEntities.ListAsync()).Single().Id;
        var cash = await s.LegalEntities.AddCashAsync(entity, "Касса");
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(-60), MoneyOperationKind.Income, cash, null, 50_000m, null, "Взнос", null));
        var anna = await HireAsync(s, "001", "Иванова", "Анна");
        var boris = await HireAsync(s, "002", "Петров", "Борис");

        // Анна: аванс 05.10 на срок до 06.10 → отчитаться до 09.10 (3 рабочих дня) — сегодня 09.10, ещё не просрочено.
        // Борис: аванс 60 дней назад без срока → 30 дней + 3 рабочих — просрочено.
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(-4), MoneyOperationKind.Expense, cash, null, 1_000m, null,
            "Под отчёт", null, null, anna, Day.AddDays(-3)));
        await s.MoneyOperations.CreateAsync(new MoneyOperationCommand(Day.AddDays(-60), MoneyOperationKind.Expense, cash, null, 2_000m, null,
            "Под отчёт", null, null, boris));
        Assert.Equal("money.advance_due", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Expense, cash, null, 1m, null, "x", null, null, anna, Day.AddDays(-1))))).Code);
        Assert.Equal("money.advance_due", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.MoneyOperations.CreateAsync(
            new MoneyOperationCommand(Day, MoneyOperationKind.Expense, cash, null, 1m, null, "x", null, null, null, Day.AddDays(5))))).Code);

        var rows = await s.Accountables.BalancesAsync();
        Assert.Equal(Day, rows.Single(r => r.EmployeeId == anna).Deadline);
        var alerts = await s.Accountables.AlertsAsync();
        Assert.Equal(boris, alerts.OverdueAdvances.Single().EmployeeId);
        Assert.Equal(3_000m, alerts.AccountableDebt);

        // Лимит кассы 40 000 при остатке 47 000 — предупреждение; без лимита — нет.
        Assert.Empty(alerts.CashOverLimit);
        var row = (await s.LegalEntities.ListAsync()).Single().Accounts.Single(a => a.Id == cash);
        await s.LegalEntities.SetCashLimitAsync(cash, 40_000m, row.RowVersion);
        var over = (await s.Accountables.AlertsAsync()).CashOverLimit.Single();
        Assert.Equal((47_000m, 40_000m), (over.Balance, over.Limit));
        var bank = await BankAsync(s);
        var bankRow = (await s.LegalEntities.ListAsync()).Single().Accounts.Single(a => a.Id == bank);
        Assert.Equal("money.cash_limit.not_cash", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.LegalEntities.SetCashLimitAsync(bank, 1m, bankRow.RowVersion))).Code);

        // Главный экран и уведомления показывают то же.
        var dash = await s.Dashboard.GetAsync(refresh: true);
        Assert.Single(dash.Finance!.MoneyAlerts!.OverdueAdvances);
        var notes = await s.Notifications.ListAsync();
        Assert.Contains(notes, n => n.Text.Contains("Просрочен авансовый отчёт") && n.Text.Contains("Петров"));
        Assert.Contains(notes, n => n.Text.Contains("Остаток кассы выше лимита"));

        // Утверждённый отчёт Бориса на всю сумму снимает просрочку.
        var report = await s.Accountables.CreateReportAsync(boris, Day, null, null);
        var item = (await s.Accountables.OptionsAsync()).ExpenseItems.First().Id;
        await s.Accountables.AddLineAsync(report, Day, "Чек № 1", null, 2_000m, item, (await s.Accountables.GetReportAsync(report)).RowVersion);
        await s.Accountables.ApproveAsync(report, (await s.Accountables.GetReportAsync(report)).RowVersion);
        Assert.Empty((await s.Accountables.AlertsAsync()).OverdueAdvances);
    }

    private static async Task<long> BankAsync(Services s)
    {
        var entity = (await s.LegalEntities.ListAsync()).Single();
        return entity.Accounts.FirstOrDefault(a => a.Kind == KnitErp.Domain.Organizations.MoneyAccountKind.Bank)?.Id
               ?? await s.LegalEntities.AddAccountAsync(entity.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "40702810938000000001", null));
    }

    private static async Task<long> HireAsync(Services s, string number, string last, string first)
    {
        var shop = await s.Structure.CreateDepartmentAsync($"Цех {number}", null);
        var position = await s.Structure.CreatePositionAsync($"Мастер {number}");
        return await s.Employees.HireAsync(new EmployeeCommand(number, last, first, null, shop, position, new DateOnly(2020, 1, 1)));
    }

    private sealed record Fixture(CreatedOrganization Org, long Customer, long Supplier);

    private async Task<Fixture> SeedAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        var supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", null, null, true, false, null));
        return new Fixture(org, customer, supplier);
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
