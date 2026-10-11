using KnitErp.Domain.Common;
using KnitErp.Domain.Production;

namespace KnitErp.UnitTests;

public sealed class ShiftTaskTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);

    [Theory]
    [InlineData(10, 6, 60)]
    [InlineData(3, 1, 33)]
    [InlineData(3, 2, 67)]
    [InlineData(8, 10, 125)]
    [InlineData(5, -1, 0)]
    [InlineData(0, 5, 0)]
    public void Percent_is_rounded_and_never_negative(decimal planned, decimal fact, int percent) =>
        Assert.Equal(percent, ShiftTask.Percent(planned, fact));

    [Fact]
    public void Shortfall_reason_from_list_and_cancelled_task_is_frozen()
    {
        var task = ShiftTask.Create(1, Day, 2, 3, ProductionStage.Knitting, 10, " до обеда ", 4, DateTime.UtcNow);
        Assert.Equal("до обеда", task.Comment);
        Assert.Equal("production.shop.reason", Assert.Throws<BusinessRuleException>(() => task.ReportShortfall("Погода", null, DateTime.UtcNow)).Code);
        task.ReportShortfall("Брак сырья", "  ", DateTime.UtcNow);
        Assert.Equal(("Брак сырья", (string?)null), (task.ShortfallReason, task.ShortfallComment));

        task.Cancel(4, DateTime.UtcNow);
        Assert.True(task.IsCancelled);
        Assert.Equal("production.shift.cancelled", Assert.Throws<BusinessRuleException>(() => task.Cancel(4, DateTime.UtcNow)).Code);
        Assert.Equal("production.shift.cancelled", Assert.Throws<BusinessRuleException>(() => task.ReportShortfall("Брак сырья", null, DateTime.UtcNow)).Code);
    }

    [Fact]
    public void Planned_quantity_and_stage_are_checked()
    {
        Assert.Equal("production.shift.planned",
            Assert.Throws<BusinessRuleException>(() => ShiftTask.Create(1, Day, 2, 3, ProductionStage.Knitting, 0, null, 4, DateTime.UtcNow)).Code);
        Assert.Equal("production.stage.unknown",
            Assert.Throws<BusinessRuleException>(() => ShiftTask.Create(1, Day, 2, 3, (ProductionStage)9, 1, null, 4, DateTime.UtcNow)).Code);
    }
}
