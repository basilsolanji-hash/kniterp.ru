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
    public async Task Tag_and_discussion_constraints_reserve_ranges_for_all_modules()
    {
        // D202: 1–10 — общие объекты, 11–19 — производство, 20–29 — «Сотрудники»; модули не переписывают ограничение друг друга.
        var org = await host.CreateOrganizationAsync();
        await using var db = host.NewDb();
        foreach (var target in new[] { 11, 19, 25 })
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [kniterp].[discussion_comments] ([OrganizationId], [Target], [TargetId], [AuthorUserId], [Text], [CreatedAtUtc]) VALUES ({0}, {1}, 1, {2}, N'т', SYSUTCDATETIME())",
                org.OrganizationId, target, org.OwnerUserId);
        }

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO [kniterp].[discussion_comments] ([OrganizationId], [Target], [TargetId], [AuthorUserId], [Text], [CreatedAtUtc]) VALUES ({0}, 30, 1, {1}, N'т', SYSUTCDATETIME())",
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
