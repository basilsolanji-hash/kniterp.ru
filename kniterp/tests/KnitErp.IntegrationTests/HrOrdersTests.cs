using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Этап 2 модуля «Сотрудники»: кадровые приказы Т-1, Т-5, Т-6, Т-8 и штатное расписание Т-3 (D203–D206).</summary>
public sealed class HrOrdersTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly OrderDay = new(2026, 10, 1);

    private async Task<(long Knitting, long Sewing, long Knitter, long Seamstress)> StructureAsync(Application.Organizations.CreatedOrganization org)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        return (await s.Structure.CreateDepartmentAsync("Вязальный цех", null), await s.Structure.CreateDepartmentAsync("Швейный цех", null),
            await s.Structure.CreatePositionAsync("Вязальщица"), await s.Structure.CreatePositionAsync("Швея"));
    }

    private static PersonnelOrderValues Hire(string number, long dep, long pos, decimal rate = 1m) =>
        new(PersonnelOrderKind.Hire, OrderDay, OrderDay, PersonnelNumber: number, LastName: "Козлова", FirstName: "Ирина", MiddleName: "Павловна",
            DepartmentId: dep, PositionId: pos, Rate: rate, Salary: 45000m, TrialMonths: 3, Basis: "Трудовой договор от 01.10.2026 № 7");

    private async Task<long> PostedAsync(HrServices hr, PersonnelOrderValues values)
    {
        var id = await hr.Orders.CreateAsync(values);
        await hr.Orders.PostAsync(id, (await hr.Orders.GetAsync(id)).RowVersion);
        return id;
    }

    [SqlFact]
    public async Task Hire_order_creates_employee_and_history_and_cannot_be_changed_after_posting()
    {
        var org = await host.CreateOrganizationAsync();
        var (knitting, _, knitter, _) = await StructureAsync(org);
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);

        var id = await hr.Orders.CreateAsync(Hire("0100", knitting, knitter));
        var draft = await hr.Orders.GetAsync(id);
        Assert.Equal((PersonnelOrderStatus.Draft, true, true), (draft.Status, draft.CanEdit, draft.CanPost));
        Assert.Matches(@"\d", draft.Number);
        await hr.Orders.PostAsync(id, draft.RowVersion);

        var posted = await hr.Orders.GetAsync(id);
        Assert.Equal(PersonnelOrderStatus.Posted, posted.Status);
        var employee = await hr.Db.Employees.SingleAsync(e => e.Id == posted.Values.EmployeeId);
        Assert.Equal(("0100", knitting, knitter, OrderDay), (employee.PersonnelNumber, employee.DepartmentId, employee.PositionId, employee.HiredOn));
        var history = Assert.Single(await hr.Db.EmploymentHistory.Where(h => h.EmployeeId == employee.Id).ToListAsync());
        Assert.Equal((PersonnelOrderKind.Hire, 1m, 45000m), (history.Kind, history.Rate, history.Salary));

        Assert.Equal("hr.order.not_draft", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Orders.UpdateAsync(id, Hire("0101", knitting, knitter), posted.RowVersion))).Code);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            hr.Db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[hr_orders] SET [Basis] = N'x' WHERE [Id] = {0}", id));
        Assert.Contains("неизменяем", ex.Message + ex.InnerException?.Message);
        var ex2 = await Assert.ThrowsAnyAsync<Exception>(() =>
            hr.Db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[hr_employment_history] WHERE [EmployeeId] = {0}", employee.Id));
        Assert.Contains("неизменяема", ex2.Message + ex2.InnerException?.Message);

        var print = await hr.Orders.PrintAsync(id);
        Assert.Equal(("Козлова Ирина Павловна", "Вязальный цех", "Вязальщица", (byte?)3), (print.EmployeeName, print.ToDepartment, print.ToPosition, print.TrialMonths));
    }

    [SqlFact]
    public async Task Staffing_blocks_hire_and_transfer_over_establishment()
    {
        var org = await host.CreateOrganizationAsync();
        var (knitting, sewing, knitter, seamstress) = await StructureAsync(org);
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        await hr.Staffing.AddAsync(knitting, knitter, 1.5m, 45000m, 5000m, OrderDay);
        await hr.Staffing.AddAsync(sewing, seamstress, 1m, 40000m, 0m, OrderDay);

        var first = await PostedAsync(hr, Hire("0201", knitting, knitter));
        var second = await hr.Orders.CreateAsync(Hire("0202", knitting, knitter));
        Assert.Equal("hr.staffing.over", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await hr.Orders.PostAsync(second, (await hr.Orders.GetAsync(second)).RowVersion))).Code);
        Assert.Equal(PersonnelOrderStatus.Draft, (await hr.Orders.GetAsync(second)).Status);

        // Полставки ещё помещаются.
        await hr.Orders.UpdateAsync(second, Hire("0202", knitting, knitter, 0.5m), (await hr.Orders.GetAsync(second)).RowVersion);
        await hr.Orders.PostAsync(second, (await hr.Orders.GetAsync(second)).RowVersion);

        // В подразделении без строки штата — отказ, даже если штат вообще есть.
        var third = await hr.Orders.CreateAsync(Hire("0203", sewing, knitter));
        Assert.Equal("hr.staffing.no_position", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await hr.Orders.PostAsync(third, (await hr.Orders.GetAsync(third)).RowVersion))).Code);

        var staffing = await hr.Staffing.GetAsync();
        var row = staffing.Rows.Single(r => r.DepartmentId == knitting);
        Assert.Equal((1.5m, 1.5m, 0m, 75000m), (row.Rates, row.Occupied, row.Vacant, row.MonthlyFund));

        // Перевод на занятую должность — отказ; на свободную — проходит, карточка меняется.
        var employeeId = (await hr.Orders.GetAsync(first)).Values.EmployeeId!.Value;
        await PostedAsync(hr, new PersonnelOrderValues(PersonnelOrderKind.Transfer, OrderDay, OrderDay.AddDays(5), employeeId,
            DepartmentId: sewing, PositionId: seamstress, Rate: 1m, Salary: 40000m));
        var moved = await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
        Assert.Equal((sewing, seamstress), (moved.DepartmentId, moved.PositionId));
        var print = await hr.Orders.PrintAsync((await hr.Orders.ListAsync(new PersonnelOrderFilter(PersonnelOrderKind.Transfer))).Single().Id);
        Assert.Equal(("Вязальный цех", "Вязальщица", "Швейный цех", "Швея"), (print.FromDepartment, print.FromPosition, print.ToDepartment, print.ToPosition));

        Assert.Equal("hr.staffing.below_occupied", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            await hr.Staffing.ChangeAsync(row.Id, 0.25m, 45000m, 0m, OrderDay, row.RowVersion))).Code);
    }

    [SqlFact]
    public async Task Leave_and_dismissal_orders_change_status_and_block_account()
    {
        var org = await host.CreateOrganizationAsync();
        var (knitting, _, knitter, _) = await StructureAsync(org);
        long employeeId;
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            employeeId = (await hr.Orders.GetAsync(await PostedAsync(hr, Hire("0301", knitting, knitter)))).Values.EmployeeId!.Value;
            // «Сегодня» тестов — 09.10.2026: отпуск уже идёт.
            await PostedAsync(hr, new PersonnelOrderValues(PersonnelOrderKind.Leave, OrderDay, new DateOnly(2026, 10, 5), employeeId,
                EffectiveTo: new DateOnly(2026, 10, 18), LeaveKind: LeaveKind.Annual, Basis: "Заявление"));
            Assert.Equal(EmploymentStatus.OnLeave, (await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Status);
            var leave = (await hr.Orders.ListAsync(new PersonnelOrderFilter(PersonnelOrderKind.Leave))).Single();
            Assert.Equal(14, (await hr.Orders.PrintAsync(leave.Id)).LeaveDays);
        }

        var user = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: employeeId);
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await PostedAsync(hr, new PersonnelOrderValues(PersonnelOrderKind.Dismissal, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), employeeId,
                DismissalGround: "80", Basis: "Заявление от 06.10.2026"));
            var employee = await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
            Assert.Equal((EmploymentStatus.Dismissed, (DateOnly?)new DateOnly(2026, 10, 20)), (employee.Status, employee.DismissedOn));
            Assert.Equal(MembershipStatus.Blocked,
                (await hr.Db.OrganizationMembers.AsNoTracking().SingleAsync(m => m.UserId == user && m.OrganizationId == org.OrganizationId)).Status);
            var print = await hr.Orders.PrintAsync((await hr.Orders.ListAsync(new PersonnelOrderFilter(PersonnelOrderKind.Dismissal))).Single().Id);
            Assert.Contains("пункт 3 части первой статьи 77", print.Ground!.Wording);
            Assert.True(await hr.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.EmployeeDismissed
                                                              && a.EntityId == employeeId.ToString()));
        }
    }

    [SqlFact]
    public async Task Hr_officer_runs_orders_but_cannot_block_accounts_and_others_cannot_see_orders()
    {
        var org = await host.CreateOrganizationAsync();
        var (knitting, _, knitter, _) = await StructureAsync(org);
        var officer = await host.InviteActiveAsync(org, SystemRoles.HrOfficer);
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);

        long employeeId;
        await using (var hr = host.Hr(officer, org.OrganizationId))
        {
            employeeId = (await hr.Orders.GetAsync(await PostedAsync(hr, Hire("0401", knitting, knitter)))).Values.EmployeeId!.Value;
            await hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Snils: HrTestKit.NextSnils()), null);
        }

        await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: employeeId);
        await using (var hr = host.Hr(officer, org.OrganizationId))
        {
            var dismissal = await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Dismissal, OrderDay, OrderDay, employeeId, DismissalGround: "77-1"));
            Assert.Equal("hr.order.user_active", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await hr.Orders.PostAsync(dismissal, (await hr.Orders.GetAsync(dismissal)).RowVersion))).Code);
            Assert.Equal(EmploymentStatus.Working, (await hr.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Status);
        }

        await using (var hr = host.Hr(storekeeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Orders.ListAsync(new PersonnelOrderFilter()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Staffing.GetAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.Orders.CreateAsync(Hire("0402", knitting, knitter)));
        }
    }

    [SqlFact]
    public async Task Draft_validation_and_foreign_references()
    {
        var a = await host.CreateOrganizationAsync();
        var b = await host.CreateOrganizationAsync();
        var (knitting, _, knitter, _) = await StructureAsync(a);
        var (foreignDep, _, _, _) = await StructureAsync(b);
        await using var hr = host.Hr(a.OwnerUserId, a.OrganizationId);

        await Assert.ThrowsAsync<NotFoundException>(() => hr.Orders.CreateAsync(Hire("0501", foreignDep, knitter)));
        var employeeId = (await hr.Orders.GetAsync(await PostedAsync(hr, Hire("0500", knitting, knitter)))).Values.EmployeeId;
        Assert.Equal("hr.order.ground", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Dismissal, OrderDay, OrderDay, employeeId, DismissalGround: "999")))).Code);
        Assert.Equal("hr.order.trial", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.Orders.CreateAsync(Hire("0502", knitting, knitter) with { TrialMonths = 7 }))).Code);
        Assert.Equal("hr.order.leave_period", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
        {
            var emp = (await hr.Orders.GetAsync(await PostedAsync(hr, Hire("0503", knitting, knitter)))).Values.EmployeeId;
            await hr.Orders.CreateAsync(new PersonnelOrderValues(PersonnelOrderKind.Leave, OrderDay, OrderDay, emp, EffectiveTo: OrderDay.AddDays(-1),
                LeaveKind: LeaveKind.Unpaid));
        })).Code);

        var id = await hr.Orders.CreateAsync(Hire("0504", knitting, knitter));
        await hr.Orders.CancelAsync(id, (await hr.Orders.GetAsync(id)).RowVersion);
        Assert.Equal(PersonnelOrderStatus.Cancelled, (await hr.Orders.GetAsync(id)).Status);

        await using var other = host.Hr(b.OwnerUserId, b.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => other.Orders.GetAsync(id));
    }

    [SqlFact]
    public async Task Employment_history_on_card_hides_salary_and_order_links_without_rights()
    {
        var org = await host.CreateOrganizationAsync();
        var other = await host.CreateOrganizationAsync();
        var (knitting, sewing, knitter, seamstress) = await StructureAsync(org);
        long employeeId;
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            employeeId = (await hr.Orders.GetAsync(await PostedAsync(hr, Hire("0601", knitting, knitter)))).Values.EmployeeId!.Value;
            await PostedAsync(hr, new PersonnelOrderValues(PersonnelOrderKind.Transfer, OrderDay, OrderDay.AddDays(3), employeeId,
                DepartmentId: sewing, PositionId: seamstress, Rate: 1m, Salary: 47000m));
            var history = await hr.Orders.HistoryAsync(employeeId);
            Assert.Equal([PersonnelOrderKind.Hire, PersonnelOrderKind.Transfer], history.Select(h => h.Kind));
            Assert.Equal((45000m, 47000m), (history[0].Salary, history[1].Salary));
            Assert.All(history, h => Assert.NotNull(h.OrderId));
        }

        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using (var hr = host.Hr(storekeeper, org.OrganizationId))
        {
            var history = await hr.Orders.HistoryAsync(employeeId);
            Assert.Equal(2, history.Count);
            Assert.All(history, h => Assert.Null(h.Salary));
            Assert.All(history, h => Assert.Null(h.OrderId));
        }

        var worker = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: employeeId);
        await using (var hr = host.Hr(worker, org.OrganizationId))
        {
            Assert.Equal(47000m, (await hr.Orders.HistoryAsync(employeeId))[1].Salary); // свой оклад сотрудник видит
        }

        await using (var hr = host.Hr(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => hr.Orders.HistoryAsync(employeeId));
        }
    }

    [SqlFact]
    public async Task Existing_organizations_have_hr_officer_role()
    {
        var org = await host.CreateOrganizationAsync();
        await using var db = host.NewDb();
        var role = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.OrganizationId == org.OrganizationId && r.Code == SystemRoles.HrOfficer);
        Assert.Equal("Кадровик", role.Name);
        Assert.Contains(role.Permissions, p => p.PermissionCode == Permissions.HrOrderPost && p.Level == PermissionLevel.Full);
    }
}
