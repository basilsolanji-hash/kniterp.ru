using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D225: меньше ручного ввода в кадрах — номер и оклад в приказе, график подразделения, табели сами, выход в выходной, расценки из Excel.</summary>
public sealed class HrAutomationTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly October = new(2026, 10, 1);

    [SqlFact]
    public async Task Hire_order_suggests_number_and_salary_assigns_department_schedule_and_updates_open_timesheet()
    {
        var org = await host.CreateOrganizationAsync();
        long knitting, sewing, knitter;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            knitting = await s.Structure.CreateDepartmentAsync("Вязальный цех автоматики", null);
            sewing = await s.Structure.CreateDepartmentAsync("Швейный цех автоматики", null);
            knitter = await s.Structure.CreatePositionAsync("Вязальщица автоматики");
        }

        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        await hr.Staffing.AddAsync(knitting, knitter, 2m, 42000m, 0m, October);
        await hr.Staffing.AddAsync(sewing, knitter, 1m, 39000m, 0m, October);
        var shifts = await hr.Timesheets.SaveScheduleAsync(null, new WorkScheduleCommand("2/2 по 12 ч", WorkScheduleKind.Shifts, 12, 0, 2, 2, October, true), null);
        await hr.Timesheets.SetDepartmentScheduleAsync(knitting, shifts);
        var sheet = await hr.Timesheets.CreateAsync(knitting, 2026, 10); // табель составлен до приёма

        var options = await hr.Orders.OptionsAsync();
        Assert.Equal("0001", options.NextPersonnelNumber);
        Assert.Equal(42000m, options.SalaryFor(knitting, knitter));
        Assert.Null(options.SalaryFor(knitting, 999));
        var order = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Hire, October, October, PersonnelNumber: options.NextPersonnelNumber,
            LastName: "Автоматова", FirstName: "Анна", DepartmentId: knitting, PositionId: knitter, Rate: 1m, Salary: options.SalaryFor(knitting, knitter)));
        await hr.Orders.PostAsync(order, (await hr.Orders.GetAsync(order)).RowVersion);

        var employee = (await hr.Orders.GetAsync(order)).Values.EmployeeId!.Value;
        await using (var db = host.NewDb())
        {
            var assigned = await db.EmployeeSchedules.SingleAsync(s => s.EmployeeId == employee);
            Assert.Equal((shifts, October), (assigned.ScheduleId, assigned.ValidFrom));
        }

        // Открытый табель обновился сам: новая сотрудница в нём по графику 2/2 (1–2 октября — смены по 12 ч, 3–4 — выходные).
        var row = (await hr.Timesheets.GetByIdAsync(sheet)).Rows.Single(r => r.EmployeeId == employee);
        Assert.Equal(("Я", 12m), (row.Cells[October].Code, row.Cells[October].Hours));
        Assert.Equal("В", row.Cells[October.AddDays(2)].Code);
        Assert.Equal("0002", (await hr.Orders.OptionsAsync()).NextPersonnelNumber);
        Assert.Equal("0002", (await host.As(org.OwnerUserId, org.OrganizationId).Employees.GetOptionsAsync()).NextPersonnelNumber);

        // Перевод в подразделение без графика по умолчанию — график не меняется.
        var transfer = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Transfer, October.AddDays(14), October.AddDays(14),
            EmployeeId: employee, DepartmentId: sewing, PositionId: knitter, Rate: 1m, Salary: 39000m));
        await hr.Orders.PostAsync(transfer, (await hr.Orders.GetAsync(transfer)).RowVersion);
        await using (var db = host.NewDb())
        {
            Assert.Equal(1, await db.EmployeeSchedules.CountAsync(s => s.EmployeeId == employee));
        }
    }

    [SqlFact]
    public async Task All_timesheets_are_created_and_closed_in_one_action_within_rights_and_readiness_counts_them()
    {
        var org = await host.CreateOrganizationAsync();
        var (first, _, _) = await host.HireAsync(org, "0001", "Первая", October.AddMonths(-1));
        var (second, _, _) = await host.HireAsync(org, "0002", "Вторая", October.AddMonths(-1));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        Assert.Equal((2, 0, 2, 0), Counts(await hr.Payroll.ReadinessAsync(2026, 10)));

        Assert.Equal((2, 0, 0), Batch(await hr.Timesheets.CreateAllAsync(2026, 10)));
        Assert.Equal((0, 2, 0), Batch(await hr.Timesheets.CreateAllAsync(2026, 10))); // повторно — только обновить

        var head = await host.InviteActiveAsync(org, SystemRoles.DepartmentHead, departmentId: first);
        await using (var own = host.Hr(head, org.OrganizationId))
        {
            Assert.Equal(1, (await own.Timesheets.CloseAllAsync(2026, 10)).Closed); // только своё подразделение
        }

        var ready = await hr.Payroll.ReadinessAsync(2026, 10);
        Assert.Equal((2, 1, 0, 0), Counts(ready));
        Assert.False(ready.Ready);
        Assert.Equal(1, (await hr.Timesheets.CloseAllAsync(2026, 10)).Closed);
        Assert.True((await hr.Payroll.ReadinessAsync(2026, 10)).Ready);
        Assert.Equal((0, 0, 2), Batch(await hr.Timesheets.CreateAllAsync(2026, 10))); // закрытые не трогаются
        _ = second;

        static (int, int, int, int) Counts(MonthReadinessDto r) => (r.Departments, r.Closed, r.Missing, r.OperationsWithoutRate);
        static (int, int, int) Batch(TimesheetBatchDto b) => (b.Created, b.Refilled, b.Skipped);
    }

    [SqlFact]
    public async Task Weekend_output_from_terminal_becomes_holiday_work_in_timesheet_but_manual_day_wins()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, emp) = await host.HireAsync(org, hiredOn: October.AddMonths(-1));
        var saturday = new DateOnly(2026, 10, 3);
        var sunday = saturday.AddDays(1);
        var output = new FakeWorkOutputSource(
            new WorkOutputDto(emp, saturday, 1, "Вязание", null, 5, "шт"),
            new WorkOutputDto(emp, sunday, 1, "Вязание", null, 4, "шт"),
            new WorkOutputDto(emp, new DateOnly(2026, 10, 5), 1, "Вязание", null, 6, "шт"));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output);
        var ts = await hr.Timesheets.CreateAsync(dep, 2026, 10);
        var row = (await hr.Timesheets.GetByIdAsync(ts)).Rows.Single(r => r.EmployeeId == emp);
        Assert.Equal(("РВ", 8m, false), (row.Cells[saturday].Code, row.Cells[saturday].Hours, row.Cells[saturday].Manual));
        Assert.Equal(("Я", 8m), (row.Cells[new DateOnly(2026, 10, 5)].Code, row.Cells[new DateOnly(2026, 10, 5)].Hours));

        await hr.Timesheets.SetDayAsync(ts, emp, sunday, "В", 0, 0, 0); // мастер знает: в воскресенье отметили за пятницу
        await hr.Timesheets.RefillAsync(ts, (await hr.Timesheets.GetByIdAsync(ts)).RowVersion!);
        row = (await hr.Timesheets.GetByIdAsync(ts)).Rows.Single(r => r.EmployeeId == emp);
        Assert.Equal(("В", "РВ"), (row.Cells[sunday].Code, row.Cells[saturday].Code));
    }

    [SqlFact]
    public async Task Rates_template_is_prefilled_and_import_is_all_or_nothing()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, hiredOn: October.AddMonths(-1));
        var output = new FakeWorkOutputSource(
            new WorkOutputDto(emp, October.AddDays(1), 1, "Вязание", null, 10, "шт"),
            new WorkOutputDto(emp, October.AddDays(2), 2, "Сборка", null, 10, "шт"));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output);
        Assert.Equal(2, (await hr.Payroll.ReadinessAsync(2026, 10)).OperationsWithoutRate);

        var template = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(await hr.Payroll.RatesTemplateAsync()), 100);
        Assert.Equal(PayrollService.RateColumns, template[0]);
        Assert.Equal(["1", "Вязание", "", "", "шт", "", "01.08.2026"], template[1]);
        Assert.Equal(3, template.Count);

        // Ошибки в любой строке — не загружается ничего; номера строк в сообщении.
        var bad = File([["1", "Вязание", "", "", "шт", "55", "01.08.2026"], ["2", "Сборка", "НЕТ-ТАКОГО", "", "шт", "abc", "01.08.2026"]]);
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Payroll.ImportRatesAsync(bad));
        Assert.Equal("hr.rate.import_errors", error.Code);
        Assert.Contains("Строка 3", error.Message);
        Assert.Empty(await hr.Payroll.RatesAsync());

        // Цену вписали в одну строку — загружена одна расценка; вторая ждёт.
        var filled = template.Select(r => r.ToList()).ToList();
        filled[1][5] = "50,5";
        Assert.Equal(1, await hr.Payroll.ImportRatesAsync(File(filled.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList())));
        var rate = Assert.Single(await hr.Payroll.RatesAsync());
        Assert.Equal((1L, 50.5m, new DateOnly(2026, 8, 1)), (rate.OperationId, rate.Price, rate.ValidFrom));
        Assert.Equal(1, (await hr.Payroll.ReadinessAsync(2026, 10)).OperationsWithoutRate);
        Assert.Equal("hr.rate.import_header", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Payroll.ImportRatesAsync(new MemoryStream(SqlTestHost.Spreadsheet.Write([new SheetData("Лист", ["Что-то"], [])]))))).Code);

        static MemoryStream File(IReadOnlyList<IReadOnlyList<string>> rows) =>
            new(SqlTestHost.Spreadsheet.Write([new SheetData("Расценки", PayrollService.RateColumns, rows)]));
    }

    [Fact]
    public void Next_personnel_number_keeps_width_and_ignores_non_numeric()
    {
        Assert.Equal("0001", PersonnelNumbers.Next([]));
        Assert.Equal("0008", PersonnelNumbers.Next(["0007", "0003", "А-15"]));
        Assert.Equal("100", PersonnelNumbers.Next(["99", "7"]));
        Assert.Equal("0001", PersonnelNumbers.Next(["Т-1"]));
    }
}
