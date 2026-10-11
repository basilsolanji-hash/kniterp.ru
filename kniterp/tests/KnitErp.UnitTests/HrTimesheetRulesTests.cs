using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;

namespace KnitErp.UnitTests;

/// <summary>Графики работы и коды табеля (D207–D209).</summary>
public sealed class HrTimesheetRulesTests
{
    private static readonly HashSet<DateOnly> Holidays = [new(2026, 11, 4)];

    [Fact]
    public void Five_day_week_shortens_pre_holiday_day_and_skips_weekends_and_holidays()
    {
        Assert.Equal(new DayPlan(true, 7m, 0m), WorkSchedule.DefaultPlan(new DateOnly(2026, 11, 3), Holidays));
        Assert.Equal(new DayPlan(false, 0m, 0m), WorkSchedule.DefaultPlan(new DateOnly(2026, 11, 4), Holidays));
        Assert.Equal(new DayPlan(false, 0m, 0m), WorkSchedule.DefaultPlan(new DateOnly(2026, 11, 7), Holidays));
        Assert.Equal(new DayPlan(true, 8m, 0m), WorkSchedule.DefaultPlan(new DateOnly(2026, 11, 5), Holidays));
    }

    [Fact]
    public void Shift_cycle_counts_from_start_in_both_directions()
    {
        var s = WorkSchedule.Create(1, "2/2", WorkScheduleKind.Shifts, 12, 4, 2, 2, new DateOnly(2026, 11, 1), true);
        Assert.True(s.PlanFor(new DateOnly(2026, 11, 2), Holidays).Work);
        Assert.False(s.PlanFor(new DateOnly(2026, 11, 3), Holidays).Work);
        Assert.False(s.PlanFor(new DateOnly(2026, 11, 4), Holidays).Work);
        Assert.True(s.PlanFor(new DateOnly(2026, 11, 5), Holidays).Work);
        Assert.True(s.PlanFor(new DateOnly(2026, 10, 29), Holidays).Work); // до начала цикла — тот же ритм
        Assert.False(s.PlanFor(new DateOnly(2026, 10, 31), Holidays).Work);
        var fromHoliday = WorkSchedule.Create(1, "3/3", WorkScheduleKind.Shifts, 8, 0, 3, 3, new DateOnly(2026, 11, 4), false);
        Assert.True(fromHoliday.PlanFor(new DateOnly(2026, 11, 4), Holidays).Work); // праздник смене не мешает
        Assert.Equal(4m, s.PlanFor(new DateOnly(2026, 11, 1), Holidays).NightHours);
    }

    [Fact]
    public void Schedule_validation()
    {
        Assert.Equal("hr.schedule.hours", Assert.Throws<BusinessRuleException>(() =>
            WorkSchedule.Create(1, "x", WorkScheduleKind.FiveDay, 8, 9, 5, 2, null, false)).Code);
        Assert.Equal("hr.schedule.cycle", Assert.Throws<BusinessRuleException>(() =>
            WorkSchedule.Create(1, "x", WorkScheduleKind.Shifts, 12, 0, 2, 2, null, false)).Code);
    }

    [Fact]
    public void Day_rules_and_description()
    {
        var day = TimesheetDay.Create(1, 1, 1, new DateOnly(2026, 11, 2), "я", 8, 2, 1, false);
        Assert.Equal("Я 8, ночных 2, сверхурочных 1", day.Describe());
        Assert.Equal("hr.timesheet.hours", Assert.Throws<BusinessRuleException>(() => day.Set("Я", 20, 0, 5, true)).Code);
        Assert.Equal("hr.timesheet.absence_hours", Assert.Throws<BusinessRuleException>(() => day.Set("ОТ", 8, 0, 0, true)).Code);
        Assert.Equal("Я 8, ночных 2, сверхурочных 1", day.Set("Б", 0, 0, 0, true));
    }

    [Fact]
    public void Timesheet_closing_and_correction()
    {
        var ts = Timesheet.Create(1, 2, 2026, 11, 3, DateTime.UtcNow);
        Assert.Equal("hr.timesheet.correct_open", Assert.Throws<BusinessRuleException>(() => ts.Correction(3, DateTime.UtcNow)).Code);
        ts.Close(3, DateTime.UtcNow);
        Assert.Equal("hr.timesheet.closed", Assert.Throws<BusinessRuleException>(ts.EnsureOpen).Code);
        var fix = ts.Correction(3, DateTime.UtcNow);
        Assert.Equal((1, TimesheetStatus.Open), ((int)fix.Version, fix.Status));
        Assert.Equal("hr.timesheet.day", Assert.Throws<BusinessRuleException>(() => fix.EnsureDay(new DateOnly(2026, 12, 1))).Code);
    }

    [Fact]
    public void Codes_are_unique_and_leave_codes_exist()
    {
        Assert.Equal(TimesheetCodes.All.Count, TimesheetCodes.All.Select(c => c.Letter).Distinct().Count());
        Assert.All(LeaveKinds.All, k => Assert.NotNull(TimesheetCodes.Find(LeaveKinds.TimesheetCode(k))));
        Assert.True(TimesheetCodes.IsWorked("РВ"));
        Assert.False(TimesheetCodes.IsWorked("В"));
    }
}
