using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;

namespace KnitErp.UnitTests;

/// <summary>Кадровые приказы и штатное расписание (D203–D205).</summary>
public sealed class HrOrderRulesTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static readonly DateTime Now = new(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);

    private static PersonnelOrderValues Hire() =>
        new(PersonnelOrderKind.Hire, Day, Day, PersonnelNumber: " 0100 ", LastName: "Козлова", FirstName: "Ирина", DepartmentId: 1, PositionId: 2);

    [Fact]
    public void Hire_draft_defaults_to_full_rate_and_posting_freezes_it()
    {
        var order = PersonnelOrder.Create(1, "1", Hire(), 5, Now);
        Assert.Equal(("0100", 1m, PersonnelOrderStatus.Draft), (order.PersonnelNumber, order.Rate, order.Status));
        order.Post(5, Now);
        Assert.Equal("hr.order.not_draft", Assert.Throws<BusinessRuleException>(() => order.Edit(Hire())).Code);
        Assert.Equal("hr.order.not_draft", Assert.Throws<BusinessRuleException>(order.Cancel).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.6)]
    [InlineData(0.333)]
    public void Rate_is_limited(double rate) =>
        Assert.Equal("hr.order.rate", Assert.Throws<BusinessRuleException>(() =>
            PersonnelOrder.Create(1, "1", Hire() with { Rate = (decimal)rate }, 5, Now)).Code);

    [Fact]
    public void Kind_specific_fields_are_required()
    {
        Assert.Equal("hr.order.employee", Assert.Throws<BusinessRuleException>(() =>
            PersonnelOrder.Create(1, "1", new PersonnelOrderValues(PersonnelOrderKind.Transfer, Day, Day, DepartmentId: 1, PositionId: 1), 5, Now)).Code);
        Assert.Equal("hr.order.leave_kind", Assert.Throws<BusinessRuleException>(() =>
            PersonnelOrder.Create(1, "1", new PersonnelOrderValues(PersonnelOrderKind.Leave, Day, Day, 3, EffectiveTo: Day), 5, Now)).Code);
        Assert.Equal("hr.order.ground", Assert.Throws<BusinessRuleException>(() =>
            PersonnelOrder.Create(1, "1", new PersonnelOrderValues(PersonnelOrderKind.Dismissal, Day, Day, 3), 5, Now)).Code);
        Assert.Equal("hr.order.effective_date", Assert.Throws<BusinessRuleException>(() =>
            PersonnelOrder.Create(1, "1", Hire() with { EffectiveFrom = Day.AddYears(2) }, 5, Now)).Code);
    }

    [Fact]
    public void Leave_days_are_calendar_days_inclusive()
    {
        var order = PersonnelOrder.Create(1, "1", new PersonnelOrderValues(PersonnelOrderKind.Leave, Day, Day, 3, EffectiveTo: Day.AddDays(13),
            LeaveKind: LeaveKind.Annual), 5, Now);
        Assert.Equal(14, order.LeaveDays);
        Assert.Equal("ОТ", LeaveKinds.TimesheetCode(LeaveKind.Annual));
        Assert.Equal("ДО", LeaveKinds.TimesheetCode(LeaveKind.Unpaid));
    }

    [Fact]
    public void Kind_cannot_change_on_edit()
    {
        var order = PersonnelOrder.Create(1, "1", Hire(), 5, Now);
        Assert.Equal("hr.order.kind_change", Assert.Throws<BusinessRuleException>(() =>
            order.Edit(new PersonnelOrderValues(PersonnelOrderKind.Dismissal, Day, Day, 3, DismissalGround: "80"))).Code);
    }

    [Fact]
    public void Dismissal_grounds_reference_labour_code()
    {
        Assert.All(DismissalGrounds.All, g => Assert.Contains("Трудового кодекса Российской Федерации", g.Wording));
        Assert.Equal(DismissalGrounds.All.Count, DismissalGrounds.All.Select(g => g.Code).Distinct().Count());
        Assert.Contains("пункт 3 части первой статьи 77", DismissalGrounds.Find("80")!.Wording);
    }

    [Fact]
    public void Staffing_fits_and_rejects()
    {
        var line = StaffingPosition.Create(1, 1, 1, 2m, 40000m, 0m, Day);
        Assert.True(line.Fits(1.5m, 0.5m));
        Assert.False(line.Fits(1.5m, 1m));
        Assert.Equal("hr.staffing.rates", Assert.Throws<BusinessRuleException>(() => line.Change(0, 1, 0, Day)).Code);
        line.Archive();
        Assert.Equal("hr.staffing.archived", Assert.Throws<BusinessRuleException>(() => line.Change(1, 1, 0, Day)).Code);
    }

    [Fact]
    public void Hr_officer_role_covers_hr_and_nothing_else()
    {
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.HrOfficer, Permissions.HrOrderPost));
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.HrOfficer, Permissions.PersonalDataEdit));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.HrOfficer, Permissions.UserManage));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.HrOfficer, Permissions.PriceView));
        Assert.All(RoleMatrixP0.PermissionsOf(SystemRoles.HrOfficer), p => Assert.True(Permissions.IsKnown(p.PermissionCode)));
        Assert.Equal("Кадровик", SystemRoles.NameOf(SystemRoles.HrOfficer));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.Auditor, Permissions.HrOrderPost));
    }
}
