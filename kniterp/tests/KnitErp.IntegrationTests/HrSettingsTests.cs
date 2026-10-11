using System.Text;
using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Настройки кадров и заработка организации (D216): решения владельца продукта по вопросам 3–10.</summary>
public sealed class HrSettingsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static async Task<long> HireAsync(HrServices hr, long dep, long pos, string number, decimal? salary)
    {
        var id = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Hire, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1),
            PersonnelNumber: number, LastName: "Настройкина", FirstName: "Анна", DepartmentId: dep, PositionId: pos, Salary: salary));
        await hr.Orders.PostAsync(id, (await hr.Orders.GetAsync(id)).RowVersion);
        return (await hr.Orders.GetAsync(id)).Values.EmployeeId!.Value;
    }

    [SqlFact]
    public async Task Payroll_follows_organization_settings_and_zup_codes()
    {
        var org = await host.CreateOrganizationAsync();
        long dep, pos;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            dep = await s.Structure.CreateDepartmentAsync("Цех настроек", null);
            pos = await s.Structure.CreatePositionAsync("Вязальщица настроек");
        }

        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        Assert.Equal("hr.settings.minimum", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Settings.SavePayrollAsync(new PayrollSettingsValues(NightPercent: 15m), null))).Code);
        await hr.Settings.SavePayrollAsync(new PayrollSettingsValues(NightPercent: 40m, OvertimeFirst: 2m, OvertimeNext: 2.5m, HolidayMultiplier: 2m,
            SalaryBase: SalaryBase.Days, ZupVersion: "3.1.30", ZupNightCode: "НочныеКнитЕРП"), null);

        var master = await HireAsync(hr, dep, pos, "0001", 36600m);
        var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
        await hr.Timesheets.SetDayAsync(ts, master, new DateOnly(2026, 12, 3), "Я", 8, 2, 0);        // 2 ночных
        await hr.Timesheets.SetDayAsync(ts, master, new DateOnly(2026, 12, 2), "Я", 8, 0, 3);        // 3 сверхурочных
        await hr.Timesheets.SetDayAsync(ts, master, new DateOnly(2026, 12, 4), "Б", 0, 0, 0);        // день болезни
        await hr.Payroll.CalculateAsync(2026, 12);
        var slip = await hr.Payroll.PayslipAsync(2026, 12, master);
        // По дням: 23 дня нормы, отработано 22 → 36 600 × 22 / 23; часовая ставка по часам: 36 600 / 183 = 200.
        Assert.Equal(Money.Round(36600m * 22 / 23), slip.Lines.Single(l => l.Kind == PayrollLineKind.Salary).Amount);
        Assert.Equal(2 * 200m * 0.4m, slip.Lines.Single(l => l.Kind == PayrollLineKind.Night).Amount);
        Assert.Equal(2 * 200m * 2m + 1 * 200m * 2.5m, slip.Lines.Single(l => l.Kind == PayrollLineKind.Overtime).Amount);

        var (name, xlsx) = await hr.Payroll.ExportAsync(2026, 12);
        Assert.Equal("payroll-2026-12.xlsx", name);
        var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 100);
        Assert.Contains(sheet, r => r.Contains("Доплата за работу в ночное время (ст. 154 ТК РФ)"));

        var grid = await hr.Timesheets.GetAsync(dep, 2026, 12);
        await hr.Timesheets.CloseAsync(grid.Id!.Value, grid.RowVersion!);
        var run = await hr.Payroll.CalculateAsync(2026, 12);
        await hr.Payroll.ApproveAsync(run.RunId, (await hr.Payroll.GetAsync(2026, 12)).RowVersion!);
        var xml = Encoding.UTF8.GetString((await hr.PayrollExport.ExportAsync(run.RunId)).Content);
        Assert.Contains("НочныеКнитЕРП", xml);
        Assert.Contains("ОплатаПоОкладу", xml);
        Assert.Contains("1С: ЗУП 3.1.30", xml);
    }

    [SqlFact]
    public async Task Piecework_premiums_use_tariff_from_settings()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 11, 1));
        var output = new FakeWorkOutputSource(new WorkOutputDto(employeeId, new DateOnly(2026, 12, 3), 1, "Вязание", null, 10, "шт"));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId, output);
        await hr.Settings.SavePayrollAsync(new PayrollSettingsValues(PieceHourlyRate: 150m), null);
        await hr.Payroll.AddRateAsync(new PieceRateCommand(1, "Вязание", null, "шт", 100m, new DateOnly(2026, 12, 1)));
        var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
        await hr.Timesheets.SetDayAsync(ts, employeeId, new DateOnly(2026, 12, 3), "Я", 8, 3, 0);
        await hr.Payroll.CalculateAsync(2026, 12);
        var slip = await hr.Payroll.PayslipAsync(2026, 12, employeeId);
        Assert.Equal(1000m, slip.Lines.Single(l => l.Kind == PayrollLineKind.Piece).Amount);
        Assert.Equal(3 * 150m * 0.2m, slip.Lines.Single(l => l.Kind == PayrollLineKind.Night).Amount); // по тарифу, а не 1000 / часы
    }

    [SqlFact]
    public async Task Hr_rules_leave_by_order_consent_block_and_officer_closes_timesheet()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 1, 15));
        var officer = await host.InviteActiveAsync(org, SystemRoles.HrOfficer);
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.Settings.SaveRulesAsync(new HrRulesValues(HrOfficerClosesTimesheet: true, LeaveOnlyByOrder: true, BlockPersonalDataWithoutConsent: true), null);
            Assert.Equal("hr.personal_data.no_consent", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Phone: "+7 900 000 0000"), null))).Code);
            var v = await hr.PersonalData.GiveConsentAsync(employeeId, new DateOnly(2026, 10, 1), null, null);
            await hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Phone: "+7 900 000 0000"), v);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var row = await s.Employees.GetAsync(employeeId);
            Assert.Equal("hr.leave.order_only", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Employees.SetOnLeaveAsync(employeeId, true, row.RowVersion))).Code);
        }

        await using (var hr = host.Hr(officer, org.OrganizationId))
        {
            var id = await hr.Timesheets.CreateAsync(dep, 2026, 11);
            var grid = await hr.Timesheets.GetAsync(dep, 2026, 11);
            Assert.True(grid.CanClose); // право роли «Кадровик» включено настройкой
            await hr.Timesheets.CloseAsync(id, grid.RowVersion!);
        }

        await using (var db = host.NewDb())
        {
            var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.OrganizationId == org.OrganizationId && r.Code == SystemRoles.HrOfficer);
            Assert.Contains(role.Permissions, p => p.PermissionCode == Permissions.TimesheetClose);
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var current = await hr.Settings.GetAsync();
            await hr.Settings.SaveRulesAsync(new HrRulesValues(false, false, false), current.RowVersion);
        }

        await using var check = host.NewDb();
        var after = await check.Roles.Include(r => r.Permissions).SingleAsync(r => r.OrganizationId == org.OrganizationId && r.Code == SystemRoles.HrOfficer);
        Assert.DoesNotContain(after.Permissions, p => p.PermissionCode == Permissions.TimesheetClose);
    }

    [SqlFact]
    public async Task Settings_rights_payroll_for_calculators_rules_for_organization_editors()
    {
        var org = await host.CreateOrganizationAsync();
        var admin = await host.InviteActiveAsync(org, SystemRoles.Administrator);
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using (var hr = host.Hr(admin, org.OrganizationId))
        {
            var dto = await hr.Settings.GetAsync();
            Assert.Equal((false, true), (dto.CanEditPayroll, dto.CanEditRules));
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Settings.SavePayrollAsync(new PayrollSettingsValues(NightPercent: 30m), null));
            await hr.Settings.SaveRulesAsync(new HrRulesValues(LeaveOnlyByOrder: true), null);
        }

        await using (var hr = host.Hr(storekeeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Settings.GetAsync());
        }
    }
}
