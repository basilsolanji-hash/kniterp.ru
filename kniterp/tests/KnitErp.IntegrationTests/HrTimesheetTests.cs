using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Этап 3 модуля «Сотрудники»: графики работы и табель Т-12/Т-13 (D207–D209).</summary>
public sealed class HrTimesheetTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Five_day_timesheet_follows_calendar_and_leave_orders()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 1, 15));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        // Отпуск 2–6 ноября по приказу Т-6.
        var leave = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Leave, new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 2),
            employeeId, EffectiveTo: new DateOnly(2026, 11, 6), LeaveKind: LeaveKind.Annual));
        await hr.Orders.PostAsync(leave, (await hr.Orders.GetAsync(leave)).RowVersion);

        await hr.Timesheets.CreateAsync(dep, 2026, 11);
        var grid = await hr.Timesheets.GetAsync(dep, 2026, 11);
        var row = Assert.Single(grid.Rows);
        Assert.Equal(30, row.Cells.Count);
        Assert.Equal("ОТ", row.Cells[new DateOnly(2026, 11, 2)].Code);
        Assert.Equal("ОТ", row.Cells[new DateOnly(2026, 11, 6)].Code);
        // 3.11 — накануне праздника 4 ноября: в отпуске, поэтому ОТ; 4.11 — праздник: ОТ (отпуск в календарных днях).
        Assert.Equal("ОТ", row.Cells[new DateOnly(2026, 11, 4)].Code);
        Assert.Equal(("В", 0m), (row.Cells[new DateOnly(2026, 11, 7)].Code, row.Cells[new DateOnly(2026, 11, 7)].Hours));
        Assert.Equal(("Я", 8m), (row.Cells[new DateOnly(2026, 11, 9)].Code, row.Cells[new DateOnly(2026, 11, 9)].Hours));
        // Ноябрь 2026: 20 рабочих дней по пятидневке, из них 5 в отпуске (2, 3, 5, 6 — рабочие; 4 — праздник).
        Assert.Equal(16, row.Totals.DaysWorked);
        Assert.Equal(128m, row.Totals.Hours);
        Assert.Equal(5, row.Totals.AbsenceDays["ОТ"]);
        Assert.Contains(new DateOnly(2026, 11, 4), grid.Holidays);
    }

    [SqlFact]
    public async Task Shift_schedule_two_by_two_with_night_hours_and_holiday_work()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 1, 15));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        var schedule = await hr.Timesheets.SaveScheduleAsync(null, new WorkScheduleCommand("Сменный 2/2 по 12 ч", WorkScheduleKind.Shifts, 12m, 4m, 2, 2,
            new DateOnly(2026, 11, 1), Summarized: true), null);
        await hr.Timesheets.AssignScheduleAsync(employeeId, schedule, new DateOnly(2026, 11, 1));
        Assert.Equal("hr.schedule.assigned", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Timesheets.AssignScheduleAsync(employeeId, schedule, new DateOnly(2026, 11, 1)))).Code);

        await hr.Timesheets.CreateAsync(dep, 2026, 11);
        var row = Assert.Single((await hr.Timesheets.GetAsync(dep, 2026, 11)).Rows);
        // Цикл с 1.11: 1, 2 — смены; 3, 4 — выходные; 5, 6 — смены…
        Assert.Equal(("Я", 12m, 4m), (row.Cells[new DateOnly(2026, 11, 1)].Code, row.Cells[new DateOnly(2026, 11, 1)].Hours, row.Cells[new DateOnly(2026, 11, 1)].NightHours));
        Assert.Equal("В", row.Cells[new DateOnly(2026, 11, 4)].Code);
        // 30 дней с 1.11 по циклу 2/2 — 16 смен.
        Assert.Equal(16, row.Totals.DaysWorked);
        Assert.Equal(192m, row.Totals.Hours);
        Assert.Equal(64m, row.Totals.NightHours);

        // Смена, попавшая на 4 ноября при другом начале цикла, — «РВ».
        var holidaySchedule = await hr.Timesheets.SaveScheduleAsync(null, new WorkScheduleCommand("3/3 по 8 ч", WorkScheduleKind.Shifts, 8m, 0m, 3, 3,
            new DateOnly(2026, 11, 4), false), null);
        await hr.Timesheets.AssignScheduleAsync(employeeId, holidaySchedule, new DateOnly(2026, 11, 4));
        var ts = await hr.Timesheets.GetAsync(dep, 2026, 11);
        await hr.Timesheets.RefillAsync(ts.Id!.Value, ts.RowVersion!);
        row = Assert.Single((await hr.Timesheets.GetAsync(dep, 2026, 11)).Rows);
        Assert.Equal("РВ", row.Cells[new DateOnly(2026, 11, 4)].Code);
        Assert.Equal(8m, row.Totals.HolidayHours);
    }

    [SqlFact]
    public async Task Manual_edit_is_audited_kept_on_refill_and_closed_timesheet_needs_correction()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 1, 15));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        var id = await hr.Timesheets.CreateAsync(dep, 2026, 11);
        var sick = new DateOnly(2026, 11, 10);
        await hr.Timesheets.SetDayAsync(id, employeeId, sick, "Б", 0, 0, 0);
        await hr.Timesheets.SetDayAsync(id, employeeId, new DateOnly(2026, 11, 11), "Я", 8, 0, 2);
        Assert.Equal("hr.timesheet.absence_hours", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Timesheets.SetDayAsync(id, employeeId, sick, "Б", 8, 0, 0))).Code);
        Assert.Equal("hr.timesheet.code", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Timesheets.SetDayAsync(id, employeeId, sick, "ЖЖ", 0, 0, 0))).Code);

        var ts = await hr.Timesheets.GetAsync(dep, 2026, 11);
        await hr.Timesheets.RefillAsync(id, ts.RowVersion!);
        ts = await hr.Timesheets.GetAsync(dep, 2026, 11);
        var row = Assert.Single(ts.Rows);
        Assert.Equal(("Б", true), (row.Cells[sick].Code, row.Cells[sick].Manual));
        Assert.Equal(2m, row.Totals.OvertimeHours);
        var audit = await hr.Db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.TimesheetDayChanged).ToListAsync();
        Assert.Contains(audit, a => a.Before == "Я 8" && a.After == "Б 0" && a.Reason!.EndsWith("10.11.2026"));

        await hr.Timesheets.CloseAsync(id, ts.RowVersion!);
        Assert.Equal("hr.timesheet.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Timesheets.SetDayAsync(id, employeeId, sick, "Я", 8, 0, 0))).Code);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            hr.Db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[hr_timesheet_days] SET [Hours] = 1 WHERE [TimesheetId] = {0}", id));
        Assert.Contains("неизменяем", ex.Message + ex.InnerException?.Message);

        var correction = await hr.Timesheets.CorrectAsync(id);
        await hr.Timesheets.SetDayAsync(correction, employeeId, sick, "Я", 8, 0, 0);
        var latest = await hr.Timesheets.GetAsync(dep, 2026, 11);
        Assert.Equal((correction, (byte)1, TimesheetStatus.Open), (latest.Id!.Value, latest.Version, latest.Status!.Value));
        Assert.Equal("Я", latest.Rows.Single().Cells[sick].Code);
        Assert.Equal("Б", (await hr.Timesheets.GetAsync(dep, 2026, 11, id)).Rows.Single().Cells[sick].Code);
        Assert.Equal("hr.timesheet.not_latest", (await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Timesheets.CorrectAsync(id))).Code);

        var (name, content) = await hr.Timesheets.ExportAsync(dep, 2026, 11);
        Assert.Equal("timesheet-2026-11.xlsx", name);
        Assert.True(content.Length > 1000);
    }

    [SqlFact]
    public async Task Department_head_sees_only_own_department_and_employee_sees_own_timesheet()
    {
        var org = await host.CreateOrganizationAsync();
        var (mine, _, employeeId) = await host.HireAsync(org, "0001", hiredOn: new DateOnly(2026, 1, 15));
        var (other, _, _) = await host.HireAsync(org, "0002", "Сидорова", new DateOnly(2026, 1, 15));
        var head = await host.InviteActiveAsync(org, SystemRoles.DepartmentHead, departmentId: mine);
        var worker = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: employeeId);
        var officer = await host.InviteActiveAsync(org, SystemRoles.HrOfficer);

        long id;
        await using (var hr = host.Hr(head, org.OrganizationId))
        {
            id = await hr.Timesheets.CreateAsync(mine, 2026, 11);
            await Assert.ThrowsAsync<NotFoundException>(() => hr.Timesheets.CreateAsync(other, 2026, 11));
            await Assert.ThrowsAsync<NotFoundException>(() => hr.Timesheets.GetAsync(other, 2026, 11));
            Assert.Equal("hr.schedule.scope", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                hr.Timesheets.SaveScheduleAsync(null, new WorkScheduleCommand("x", WorkScheduleKind.FiveDay, 8, 0, 5, 2, null, false), null))).Code);
        }

        await using (var hr = host.Hr(officer, org.OrganizationId))
        {
            var grid = await hr.Timesheets.GetAsync(mine, 2026, 11);
            Assert.True(grid.CanEdit);
            Assert.False(grid.CanClose);
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Timesheets.CloseAsync(id, grid.RowVersion!));
        }

        await using (var hr = host.Hr(head, org.OrganizationId))
        {
            await hr.Timesheets.CloseAsync(id, (await hr.Timesheets.GetAsync(mine, 2026, 11)).RowVersion!);
        }

        await using (var hr = host.Hr(worker, org.OrganizationId))
        {
            var my = await hr.Timesheets.MyAsync(2026, 11);
            Assert.True(my.Closed);
            Assert.Equal(20, my.Totals.DaysWorked); // ноябрь 2026: 21 будний день минус праздник 4 ноября
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Timesheets.GetAsync(mine, 2026, 11));
        }
    }

    [SqlFact]
    public async Task Timesheet_is_created_once_per_month_and_respects_employment_dates()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, _) = await host.HireAsync(org, "0001", hiredOn: new DateOnly(2026, 11, 16));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        await hr.Timesheets.CreateAsync(dep, 2026, 11);
        Assert.Equal("hr.timesheet.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() => hr.Timesheets.CreateAsync(dep, 2026, 11))).Code);
        var row = Assert.Single((await hr.Timesheets.GetAsync(dep, 2026, 11)).Rows);
        Assert.Equal(15, row.Cells.Count);
        Assert.DoesNotContain(new DateOnly(2026, 11, 15), row.Cells.Keys);

        await using var other = host.Hr((await host.CreateOrganizationAsync()).OwnerUserId, (await hr.Db.Organizations.Select(o => o.Id).MaxAsync()));
        await Assert.ThrowsAnyAsync<Exception>(() => other.Timesheets.GetAsync(dep, 2026, 11));
    }
}
