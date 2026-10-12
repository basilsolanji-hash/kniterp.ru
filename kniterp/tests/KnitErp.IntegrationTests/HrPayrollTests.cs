using System.Text;
using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Этап 4 модуля «Сотрудники»: сдельная оплата, доплаты по ст. 152–154 ТК РФ, расчётный листок, выгрузка в 1С (D211–D213).</summary>
public sealed class HrPayrollTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly Dec1 = new(2026, 12, 1);

    private static async Task<long> HireAsync(HrServices hr, string number, string lastName, long dep, long pos, decimal? salary)
    {
        var id = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Hire, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1),
            PersonnelNumber: number, LastName: lastName, FirstName: "Анна", DepartmentId: dep, PositionId: pos, Salary: salary));
        await hr.Orders.PostAsync(id, (await hr.Orders.GetAsync(id)).RowVersion);
        return (await hr.Orders.GetAsync(id)).Values.EmployeeId!.Value;
    }

    [SqlFact]
    public async Task Piece_rates_salary_and_premiums_make_payslip_and_approved_run_is_frozen()
    {
        var org = await host.CreateOrganizationAsync();
        long dep, pos;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            dep = await s.Structure.CreateDepartmentAsync("Вязальный цех", null);
            pos = await s.Structure.CreatePositionAsync("Вязальщица");
        }

        long master, knitter;
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            master = await HireAsync(hr, "0001", "Окладова", dep, pos, 36600m);
            knitter = await HireAsync(hr, "0002", "Сдельная", dep, pos, null);
            await hr.Payroll.AddRateAsync(new PieceRateCommand(10, "Вязка свитера", null, "шт", 120m, Dec1));
            await hr.Payroll.AddRateAsync(new PieceRateCommand(10, "Вязка свитера", null, "шт", 150m, new DateOnly(2026, 12, 15)));
            await hr.Payroll.AddRateAsync(new PieceRateCommand(10, "Вязка свитера (модель 77)", 77, "шт", 200m, Dec1));
            Assert.Equal("hr.rate.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                hr.Payroll.AddRateAsync(new PieceRateCommand(10, "Вязка свитера", null, "шт", 130m, Dec1)))).Code);

            var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
            await hr.Timesheets.SetDayAsync(ts, master, new DateOnly(2026, 12, 2), "Я", 8, 0, 3); // 3 сверхурочных: 2 × 1,5 + 1 × 2
            await hr.Timesheets.SetDayAsync(ts, master, new DateOnly(2026, 12, 3), "Я", 8, 2, 0); // 2 ночных
        }

        var output = new FakeWorkOutputSource(
            new WorkOutputDto(knitter, new DateOnly(2026, 12, 5), 10, "Вязка свитера", 5, 30, "шт"),
            new WorkOutputDto(knitter, new DateOnly(2026, 12, 20), 10, "Вязка свитера", 5, 20, "шт"),
            new WorkOutputDto(knitter, new DateOnly(2026, 12, 10), 10, "Вязка свитера", 77, 5, "шт"),
            new WorkOutputDto(knitter, new DateOnly(2026, 12, 11), 99, "Ручная штопка", null, 4, "шт"),
            new WorkOutputDto(knitter, new DateOnly(2026, 11, 30), 10, "Вязка свитера", 5, 100, "шт")); // другой месяц — не попадает

        var worker = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: master);
        var accountant = org.OwnerUserId; // у Бухгалтера заработок — по отдельному праву (D212), считает Владелец
        long runId;
        await using (var hr = host.Hr(accountant, org.OrganizationId, output))
        {
            var calc = await hr.Payroll.CalculateAsync(2026, 12);
            runId = calc.RunId;
            Assert.Contains(calc.Warnings, w => w.Contains("Ручная штопка"));
            var dto = await hr.Payroll.GetAsync(2026, 12);
            // Декабрь 2026: 23 рабочих дня, 31.12 — накануне праздника (7 ч) → норма 183 ч; часовая ставка 36 600 / 183 = 200.
            var m = dto.Employees.Single(e => e.EmployeeId == master);
            Assert.Equal((36600m, 80m + 1000m, 37680m), (m.Salary, m.Premiums, m.Total));
            var k = dto.Employees.Single(e => e.EmployeeId == knitter);
            Assert.Equal((30 * 120m + 20 * 150m + 5 * 200m, 0m), (k.Piece, k.Salary));

            Assert.Equal("hr.payroll.timesheets_open", (await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Payroll.ApproveAsync(runId, dto.RowVersion!))).Code);
        }

        await using (var hr = host.Hr(worker, org.OrganizationId, output))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => hr.Payroll.MyPayslipAsync(2026, 12)); // черновик сотруднику не виден
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output))
        {
            var grid = await hr.Timesheets.GetAsync(dep, 2026, 12);
            await hr.Timesheets.CloseAsync(grid.Id!.Value, grid.RowVersion!);
            await hr.Payroll.CalculateAsync(2026, 12);
            await hr.Payroll.ApproveAsync(runId, (await hr.Payroll.GetAsync(2026, 12)).RowVersion!);
            Assert.Equal("hr.payroll.approved", (await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Payroll.CalculateAsync(2026, 12))).Code);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                hr.Db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[hr_payroll_lines] SET [Amount] = 1 WHERE [RunId] = {0}", runId));
            Assert.Contains("неизменяем", ex.Message + ex.InnerException?.Message);

            var (name, content) = await hr.PayrollExport.ExportAsync(runId);
            var xml = Encoding.UTF8.GetString(content);
            Assert.Equal("knitERP-ZUP-2026-12.xml", name);
            Assert.Contains("СдельнаяОплата", xml);
            Assert.Contains("ДоплатаЗаРаботуВНочноеВремя", xml);
            Assert.DoesNotContain("СНИЛС", xml);
        }

        await using (var hr = host.Hr(worker, org.OrganizationId, output))
        {
            var slip = await hr.Payroll.MyPayslipAsync(2026, 12);
            Assert.Equal((37680m, PayrollStatus.Approved), (slip.Total, slip.Status));
            Assert.Contains(slip.Lines, l => l.Kind == PayrollLineKind.Overtime && l.Amount == 1000m);
            await Assert.ThrowsAsync<NotFoundException>(() => hr.Payroll.PayslipAsync(2026, 12, knitter));
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Payroll.GetAsync(2026, 12));
        }

        await using (var hr = host.Hr(accountant, org.OrganizationId, output))
        {
            await hr.Payroll.PayslipAsync(2026, 12, knitter);
            Assert.True(await hr.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.PayslipViewed
                                                              && a.ActorUserId == accountant && a.EntityId == knitter.ToString()));
        }
    }

    [SqlFact]
    public async Task Pairs_from_production_are_paid_and_operations_without_rate_are_listed()
    {
        // Как отдаёт производство (D92): операция — этап 1–5, единица — обозначение единицы изделия («пар», «шт.»).
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 9, 1));
        var output = new FakeWorkOutputSource(
            new WorkOutputDto(employeeId, new DateOnly(2026, 10, 2), 1, "Вязание", 501, 40, "пар"),
            new WorkOutputDto(employeeId, new DateOnly(2026, 10, 3), 2, "Сборка", 501, 12, "Шт."));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output);
        await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание носков", 501, "Пар", 35m, new DateOnly(2026, 10, 1)));

        var operations = await hr.Payroll.OperationsAsync(); // «сегодня» тестов — 09.10.2026
        Assert.True(operations.Single(o => o.OperationId == 1).HasRate);
        var missing = operations.Single(o => o.OperationId == 2);
        Assert.Equal((false, "шт", "Сборка"), (missing.HasRate, missing.Unit, missing.OperationName));

        await hr.Timesheets.CreateAsync(dep, 2026, 10);
        var calc = await hr.Payroll.CalculateAsync(2026, 10);
        Assert.Contains(calc.Warnings, w => w.Contains("Сборка"));
        Assert.Equal(40 * 35m, (await hr.Payroll.GetAsync(2026, 10)).Employees.Single().Piece);
    }

    [SqlFact]
    public async Task Without_production_source_piece_part_is_zero_and_other_roles_are_denied()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, _) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 11, 1));
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.Timesheets.CreateAsync(dep, 2026, 12);
            var calc = await hr.Payroll.CalculateAsync(2026, 12);
            Assert.Equal(0m, calc.Total); // без оклада и без выработки — начислять нечего
        }

        await using (var hr = host.Hr(storekeeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Payroll.GetAsync(2026, 12));
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Payroll.CalculateAsync(2026, 12));
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Payroll.RatesAsync());
        }
    }
}

/// <summary>Закрытые риски модуля (D210): маска ПДн в журнале обсуждений и автоматический статус отпуска по приказу.</summary>
public sealed class HrRiskTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Discussion_audit_excerpt_hides_long_numbers()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        await s.Discussions.AddAsync(Domain.Tagging.TaggedEntity.Employee, employeeId, "Её СНИЛС 112-233-445 95, паспорт 4500 000001", null);
        var entry = await s.Db.AuditEntries.SingleAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.CommentAdded);
        Assert.DoesNotContain("112-233", entry.After);
        Assert.DoesNotContain("000001", entry.After);
        Assert.Contains(PersonalDataMask.Hidden, entry.After);
    }

    [SqlFact]
    public async Task Import_cannot_hire_over_staffing_and_staffing_audit_has_no_salaries()
    {
        var org = await host.CreateOrganizationAsync();
        long dep, pos;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            dep = await s.Structure.CreateDepartmentAsync("Цех импорта", null);
            pos = await s.Structure.CreatePositionAsync("Вязальщица импорта");
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.Staffing.AddAsync(dep, pos, 1m, 41234m, 0m, new DateOnly(2026, 10, 1));
        }

        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["И-1", "Первая", "Анна", "", "Цех импорта", "Вязальщица импорта", "01.10.2026"],
            ["И-2", "Вторая", "Анна", "", "Цех импорта", "Вязальщица импорта", "01.10.2026"],
        ];
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var file = new MemoryStream(SqlTestHost.Spreadsheet.Write(
                [new Application.Common.SheetData("Сотрудники", Application.Structure.EmployeeExchangeService.Columns, rows)]));
            var plan = await s.EmployeeExchange.PreviewAsync(file);
            Assert.Equal("hr.staffing.over", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.EmployeeExchange.ApplyAsync(plan.Rows))).Code);
        }

        await using var db = host.NewDb();
        Assert.False(await db.Employees.AnyAsync(e => e.OrganizationId == org.OrganizationId)); // загрузка откатилась целиком
        var audit = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.StaffingChanged).ToListAsync();
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.DoesNotContain("41234", (a.Before ?? "") + a.After + a.Reason));
    }

    [SqlFact]
    public async Task Payroll_cannot_be_approved_after_timesheet_correction()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, _) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 11, 1));
        var saved = host.Clock.UtcNow;
        try
        {
            await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
            var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
            await hr.Timesheets.CloseAsync(ts, (await hr.Timesheets.GetAsync(dep, 2026, 12)).RowVersion!);
            await hr.Payroll.CalculateAsync(2026, 12);
            host.Clock.UtcNow = saved.AddMinutes(5);
            await hr.Timesheets.CorrectAsync(ts); // исправительный табель после расчёта
            Assert.Equal("hr.payroll.stale", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await hr.Payroll.ApproveAsync((await hr.Payroll.GetAsync(2026, 12)).Id!.Value, (await hr.Payroll.GetAsync(2026, 12)).RowVersion!))).Code);
        }
        finally
        {
            host.Clock.UtcNow = saved;
        }
    }

    [SqlFact]
    public async Task Output_corrected_after_calculation_blocks_approval_and_is_flagged_after_approval()
    {
        // D217: производство исправило выработку после расчёта — утвердить нельзя; после утверждения — только предупреждение.
        var org = await host.CreateOrganizationAsync();
        var (dep, _, emp) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 11, 1));
        var rows = new List<WorkOutputDto> { new(emp, new DateOnly(2026, 12, 3), 1, "Вязание", null, 10, "шт") };
        var output = new ListWorkOutputSource(rows);
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output);
        await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 50m, new DateOnly(2026, 12, 1)));
        var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
        await hr.Timesheets.CloseAsync(ts, (await hr.Timesheets.GetAsync(dep, 2026, 12)).RowVersion!);
        await hr.Payroll.CalculateAsync(2026, 12);
        Assert.False((await hr.Payroll.GetAsync(2026, 12)).OutputChanged);

        rows[0] = rows[0] with { Quantity = 12 };
        var dto = await hr.Payroll.GetAsync(2026, 12);
        Assert.True(dto.OutputChanged);
        Assert.Equal("hr.payroll.output_changed", (await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Payroll.ApproveAsync(dto.Id!.Value, dto.RowVersion!))).Code);

        rows.Reverse(); // порядок строк источника не важен
        await hr.Payroll.CalculateAsync(2026, 12);
        dto = await hr.Payroll.GetAsync(2026, 12);
        Assert.False(dto.OutputChanged);
        Assert.Equal(600m, dto.Total);
        await hr.Payroll.ApproveAsync(dto.Id!.Value, dto.RowVersion!);

        rows.Add(new WorkOutputDto(emp, new DateOnly(2026, 12, 4), 1, "Вязание", null, 3, "шт"));
        dto = await hr.Payroll.GetAsync(2026, 12);
        Assert.True(dto.OutputChanged); // утверждённые суммы не меняются, страница предупреждает
        Assert.Equal(600m, dto.Total);
        Assert.NotEmpty((await hr.PayrollExport.ExportAsync(dto.Id!.Value)).Content);
    }

    [SqlFact]
    public async Task Output_increase_after_approval_is_paid_once_in_next_month_and_decrease_only_warns()
    {
        // D219: исправленная выработка утверждённого ноября — доплата в декабре один раз; уменьшение — предупреждение, не удержание.
        var org = await host.CreateOrganizationAsync();
        var (dep, _, emp) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 10, 1));
        var rows = new List<WorkOutputDto> { new(emp, new DateOnly(2026, 11, 3), 1, "Вязание", null, 10, "шт") };
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, new ListWorkOutputSource(rows));
        await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 50m, new DateOnly(2026, 11, 1)));
        foreach (var month in new[] { 11, 12 })
        {
            var ts = await hr.Timesheets.CreateAsync(dep, 2026, month);
            await hr.Timesheets.CloseAsync(ts, (await hr.Timesheets.GetAsync(dep, 2026, month)).RowVersion!);
        }

        await hr.Payroll.CalculateAsync(2026, 11);
        var nov = await hr.Payroll.GetAsync(2026, 11);
        await hr.Payroll.ApproveAsync(nov.Id!.Value, nov.RowVersion!);

        rows.Add(new WorkOutputDto(emp, new DateOnly(2026, 11, 20), 1, "Вязание", null, 4, "шт")); // производство дописало ноябрь
        var dec = await hr.Payroll.CalculateAsync(2026, 12);
        Assert.Equal(200m, dec.Total);
        var slip = await hr.Payroll.PayslipAsync(2026, 12, emp);
        var line = Assert.Single(slip.Lines);
        Assert.Equal((PayrollLineKind.PieceCorrection, 200m), (line.Kind, line.Amount));
        Assert.Contains("11.2026", line.Description);
        Assert.Contains("СдельнаяОплата", System.Text.Encoding.UTF8.GetString((await hr.PayrollExport.ExportAsync(nov.Id!.Value)).Content));

        Assert.Equal(200m, (await hr.Payroll.CalculateAsync(2027, 1)).Total); // январский черновик тоже видит недоплату за ноябрь
        var decDto = await hr.Payroll.GetAsync(2026, 12);
        Assert.Equal(200m, decDto.Employees.Single().Piece);
        await hr.Payroll.ApproveAsync(decDto.Id!.Value, decDto.RowVersion!);
        var janDraft = await hr.Payroll.GetAsync(2027, 1);
        Assert.Equal("hr.payroll.stale_corrections", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Payroll.ApproveAsync(janDraft.Id!.Value, janDraft.RowVersion!))).Code); // иначе доплата за ноябрь удвоится

        // Январь: ноябрь уже доплачен в декабре — повторно не начисляется; затем выработку ноября уменьшили — только предупреждение.
        var jan = await hr.Payroll.CalculateAsync(2027, 1);
        Assert.Equal(0m, jan.Total);
        rows.RemoveAt(1);
        rows[0] = rows[0] with { Quantity = 8 };
        jan = await hr.Payroll.CalculateAsync(2027, 1);
        Assert.Equal(0m, jan.Total);
        Assert.Contains(jan.Warnings, w => w.Contains("переплата 300.00") || w.Contains("переплата 300,00"));
    }

    [SqlFact]
    public async Task Correction_ignores_employees_not_employed_then_and_rates_added_after_calculation()
    {
        // D219 (ревью): перерасчёт как исходный расчёт — только работавшие в месяце и расценки, известные к его расчёту.
        var org = await host.CreateOrganizationAsync();
        var (dep, pos, emp) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 10, 1));
        long late;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            late = await s.Employees.HireAsync(new Application.Structure.EmployeeCommand("0099", "Поздняя", "Ольга", null, dep, pos, new DateOnly(2026, 12, 1)));
        }

        var rows = new List<WorkOutputDto>
        {
            new(emp, new DateOnly(2026, 11, 3), 1, "Вязание", null, 10, "шт"),
            new(late, new DateOnly(2026, 11, 4), 1, "Вязание", null, 50, "шт"), // ошибочная отметка: принята только в декабре
        };
        var saved = host.Clock.UtcNow;
        try
        {
            await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, new ListWorkOutputSource(rows));
            await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 50m, new DateOnly(2026, 11, 1)));
            var ts = await hr.Timesheets.CreateAsync(dep, 2026, 11);
            await hr.Timesheets.CloseAsync(ts, (await hr.Timesheets.GetAsync(dep, 2026, 11)).RowVersion!);
            await hr.Payroll.CalculateAsync(2026, 11);
            var nov = await hr.Payroll.GetAsync(2026, 11);
            Assert.Equal(500m, nov.Total);
            await hr.Payroll.ApproveAsync(nov.Id!.Value, nov.RowVersion!);

            host.Clock.UtcNow = saved.AddDays(1);
            await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 80m, new DateOnly(2026, 11, 2))); // задним числом
            rows.Add(new WorkOutputDto(emp, new DateOnly(2026, 11, 25), 1, "Вязание", null, 2, "шт"));
            var dec = await hr.Payroll.CalculateAsync(2026, 12);
            var slip = await hr.Payroll.PayslipAsync(2026, 12, emp);
            Assert.Equal(100m, Assert.Single(slip.Lines, l => l.Kind == PayrollLineKind.PieceCorrection).Amount); // 2 × 50, не × 80 и без «поздней»
            Assert.DoesNotContain(dec.Warnings, w => w.Contains("Поздняя") && w.Contains("переплата"));
            Assert.DoesNotContain((await hr.Payroll.PayslipAsync(2026, 12, late)).Lines, l => l.Kind == PayrollLineKind.PieceCorrection);
        }
        finally
        {
            host.Clock.UtcNow = saved;
        }
    }

    [SqlFact]
    public async Task Piece_worker_premiums_from_piece_rate_are_recalculated_in_proportion()
    {
        // D220: у сдельщика без оклада и тарифа часовая ставка = сдельный заработок / часы — доплаты меняются вместе с выработкой.
        var org = await host.CreateOrganizationAsync();
        var (dep, _, emp) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 10, 1));
        var rows = new List<WorkOutputDto> { new(emp, new DateOnly(2026, 11, 3), 1, "Вязание", null, 10, "шт") };
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, new ListWorkOutputSource(rows));
        await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 50m, new DateOnly(2026, 11, 1)));
        var ts = await hr.Timesheets.CreateAsync(dep, 2026, 11);
        await hr.Timesheets.SetDayAsync(ts, emp, new DateOnly(2026, 11, 3), "Я", 8, 2, 1); // 2 ночных, 1 сверхурочный
        await hr.Timesheets.CloseAsync(ts, (await hr.Timesheets.GetAsync(dep, 2026, 11)).RowVersion!);
        var ts12 = await hr.Timesheets.CreateAsync(dep, 2026, 12);
        await hr.Timesheets.CloseAsync(ts12, (await hr.Timesheets.GetAsync(dep, 2026, 12)).RowVersion!);

        await hr.Payroll.CalculateAsync(2026, 11);
        var nov = await hr.Payroll.GetAsync(2026, 11);
        await hr.Payroll.ApproveAsync(nov.Id!.Value, nov.RowVersion!);
        var novSlip = await hr.Payroll.PayslipAsync(2026, 11, emp);
        var night = novSlip.Lines.Single(l => l.Kind == PayrollLineKind.Night).Amount;
        var overtime = novSlip.Lines.Single(l => l.Kind == PayrollLineKind.Overtime).Amount;
        await using (var db = host.NewDb())
        {
            Assert.All(await db.PayrollLines.Where(l => l.RunId == nov.Id && l.Kind != PayrollLineKind.Piece).ToListAsync(), l => Assert.True(l.FromPiece));
        }

        rows.Add(new WorkOutputDto(emp, new DateOnly(2026, 11, 20), 1, "Вязание", null, 4, "шт")); // сдельная 500 → 700
        await hr.Payroll.CalculateAsync(2026, 12);
        var dec = await hr.Payroll.PayslipAsync(2026, 12, emp);
        Assert.Equal(200m, dec.Lines.Single(l => l.Kind == PayrollLineKind.PieceCorrection).Amount);
        Assert.Equal(Money.Round(night * 700m / 500m) - night, dec.Lines.Single(l => l.Kind == PayrollLineKind.Night).Amount);
        Assert.Equal(Money.Round(overtime * 700m / 500m) - overtime, dec.Lines.Single(l => l.Kind == PayrollLineKind.Overtime).Amount);
        Assert.All(dec.Lines, l => Assert.Contains("11.2026", l.Description));
        var zup = System.Text.Encoding.UTF8.GetString((await hr.PayrollExport.ExportAsync(nov.Id!.Value)).Content);
        Assert.Contains("ДоплатаЗаРаботуВНочноеВремя", zup);

        // Пересчёт декабрьского черновика не удваивает: перерасчёт считается заново, а не добавляется.
        await hr.Payroll.CalculateAsync(2026, 12);
        Assert.Equal(3, (await hr.Payroll.PayslipAsync(2026, 12, emp)).Lines.Count);
    }

    /// <summary>Источник выработки, который тест меняет между вызовами — как производство, исправившее отметки.</summary>
    private sealed class ListWorkOutputSource(List<WorkOutputDto> rows) : IWorkOutputSource
    {
        public Task<IReadOnlyList<WorkOutputDto>> OutputAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkOutputDto>>(rows.Where(r => r.Date >= from && r.Date <= to).ToList());
    }

    [SqlFact]
    public async Task Tag_and_discussion_constraints_reserve_ranges_for_all_modules()
    {
        // D202, D117: 1–10 — общие объекты, 11–19 — производство, 20–29 — «Сотрудники», 30–39 — CRM, 40–49 — «Разработка», до 99 — модули;
        // модули не переписывают ограничение друг друга.
        var org = await host.CreateOrganizationAsync();
        await using var db = host.NewDb();
        foreach (var target in new[] { 11, 19, 25, 31, 42 })
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[discussion_comments] ([OrganizationId], [Target], [TargetId], [AuthorUserId], [Text], [CreatedAtUtc]) VALUES ({0}, {1}, 1, {2}, N'т', SYSUTCDATETIME())",
                org.OrganizationId, target, org.OwnerUserId);
        }

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [kniterp].[discussion_comments] ([OrganizationId], [Target], [TargetId], [AuthorUserId], [Text], [CreatedAtUtc]) VALUES ({0}, 100, 1, {1}, N'т', SYSUTCDATETIME())",
            org.OrganizationId, org.OwnerUserId));
        Assert.Contains("ck_discussion_comments_target", ex.Message + ex.InnerException?.Message);
    }

    [SqlFact]
    public async Task Leave_status_follows_posted_leave_orders()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        var saved = host.Clock.UtcNow;
        try
        {
            await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
            {
                var id = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Leave, new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 20),
                    employeeId, EffectiveTo: new DateOnly(2026, 10, 25), LeaveKind: LeaveKind.Annual));
                await hr.Orders.PostAsync(id, (await hr.Orders.GetAsync(id)).RowVersion);
                Assert.Equal(Domain.Structure.EmploymentStatus.Working, (await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Status);
            }

            host.Clock.UtcNow = new DateTime(2026, 10, 21, 6, 0, 0, DateTimeKind.Utc);
            await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
            {
                Assert.True(await hr.LeaveSync.RunAsync() >= 1);
                Assert.Equal(Domain.Structure.EmploymentStatus.OnLeave, (await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Status);
            }

            host.Clock.UtcNow = new DateTime(2026, 10, 27, 6, 0, 0, DateTimeKind.Utc);
            await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
            {
                await hr.LeaveSync.RunAsync();
                Assert.Equal(Domain.Structure.EmploymentStatus.Working, (await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Status);
                Assert.True(await hr.Db.AuditEntries.AnyAsync(a => a.EntityId == employeeId.ToString() && a.ActorUserId == null
                                                                  && a.Reason!.StartsWith("Автоматически по приказу")));
            }
        }
        finally
        {
            host.Clock.UtcNow = saved;
        }
    }
}
