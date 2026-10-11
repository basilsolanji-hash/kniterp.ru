using KnitErp.Application.Access;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;

namespace KnitErp.IntegrationTests;

/// <summary>«Смена сегодня» на главном экране (D111, D221): план по графику, неявки по табелю и отпуску, права и подразделения.</summary>
public sealed class HrShiftStaffingTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly Wednesday = new(2026, 12, 2);

    [SqlFact]
    public async Task Shift_counts_planned_absent_and_respects_timesheet_rights_and_departments()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, pos, first) = await host.HireAsync(org, "0001", "Аникина", new DateOnly(2026, 1, 15));
        long sick, truant, otherDep;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            sick = await s.Employees.HireAsync(new Application.Structure.EmployeeCommand("0002", "Болеева", "Ирина", null, dep, pos, new DateOnly(2026, 1, 15)));
            truant = await s.Employees.HireAsync(new Application.Structure.EmployeeCommand("0003", "Прогулова", "Ольга", null, dep, pos, new DateOnly(2026, 1, 15)));
            var dep2 = await s.Structure.CreateDepartmentAsync("Швейный цех смены", null);
            otherDep = await s.Employees.HireAsync(new Application.Structure.EmployeeCommand("0004", "Швецова", "Анна", null, dep2, pos, new DateOnly(2026, 1, 15)));
        }

        Assert.Null(await TodayAsync(org.OwnerUserId, org.OrganizationId)); // ни табелей, ни графиков — блок не показывается

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var ts = await hr.Timesheets.CreateAsync(dep, 2026, 12);
            await hr.Timesheets.SetDayAsync(ts, sick, Wednesday, "Б", 0, 0, 0);
            await hr.Timesheets.SetDayAsync(ts, truant, Wednesday, "ПР", 0, 0, 0);
            await hr.Timesheets.SetDayAsync(ts, first, new DateOnly(2026, 12, 5), "Я", 8, 0, 0); // суббота — сверх графика, не сегодня
        }

        // Владелец видит все подразделения: 4 по плану (пятидневка), не вышли двое — без причины неявки в именах.
        var all = await TodayAsync(org.OwnerUserId, org.OrganizationId);
        Assert.NotNull(all);
        Assert.Equal((2, 4, 2), (all.OnShift, all.Planned, all.Absent));
        Assert.Equal(["Болеева И.", "Прогулова О."], all.AbsentNames);
        Assert.Equal("hr/timesheet", all.Href);

        // Суббота: вышла сверх графика одна — на смене 1 из 1.
        var saturday = await TodayAsync(org.OwnerUserId, org.OrganizationId, new DateOnly(2026, 12, 5));
        Assert.Equal((1, 1, 0), (saturday!.OnShift, saturday.Planned, saturday.Absent));

        // Руководитель подразделения — только свой цех; сотрудник без прав на табель — блока нет.
        var head = await host.InviteActiveAsync(org, SystemRoles.DepartmentHead, departmentId: dep);
        var own = await TodayAsync(head, org.OrganizationId);
        Assert.Equal((1, 3, 2), (own!.OnShift, own.Planned, own.Absent));
        var worker = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: otherDep);
        Assert.Null(await TodayAsync(worker, org.OrganizationId));
    }

    private async Task<Application.Workspace.ShiftStaffingDto?> TodayAsync(long userId, long organizationId, DateOnly? today = null)
    {
        await using var db = host.NewDb();
        var ctx = await new AccessGuard(db, new TestUser { UserId = userId, OrganizationId = organizationId }, host.Clock).CurrentAsync();
        return await new ShiftStaffingSource().TodayAsync(db, ctx, today ?? Wednesday, CancellationToken.None);
    }
}
