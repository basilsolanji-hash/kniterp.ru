using KnitErp.Domain.Sales;

namespace KnitErp.UnitTests;

/// <summary>D110: какие события заказ прошёл и какой этап выбрать — только вперёд по списку этапов.</summary>
public sealed class StageAutomationTests
{
    private static readonly StageRuleTarget[] Rules =
    [
        new(StageTrigger.Confirmed, 1, 10), new(StageTrigger.Invoiced, 2, 20), new(StageTrigger.Paid, 3, 30),
        new(StageTrigger.ProductionStarted, 4, 40), new(StageTrigger.Shipped, 6, 60),
    ];

    [Fact]
    public void Facts_give_reached_events_with_full_payment_to_the_kopeck()
    {
        Assert.Equal([StageTrigger.Confirmed], SalesStageAutomation.Reached(new SalesOrderFacts(true, false, 100m, 0m, false, false)));
        Assert.Contains(StageTrigger.PartiallyPaid, SalesStageAutomation.Reached(new SalesOrderFacts(true, true, 100m, 99.99m, false, false)));
        Assert.Contains(StageTrigger.Paid, SalesStageAutomation.Reached(new SalesOrderFacts(true, true, 100m, 100m, false, false)));
        Assert.Contains(StageTrigger.Paid, SalesStageAutomation.Reached(new SalesOrderFacts(true, true, 100m, 250m, false, false)));
        Assert.Contains(StageTrigger.PartiallyShipped, SalesStageAutomation.Reached(new SalesOrderFacts(true, false, 100m, 0m, true, false)));
        var all = SalesStageAutomation.Reached(new SalesOrderFacts(true, true, 100m, 100m, true, true));
        Assert.Contains(StageTrigger.Shipped, all);
        Assert.DoesNotContain(StageTrigger.PartiallyShipped, all);
    }

    [Fact]
    public void Pick_takes_furthest_reached_stage_and_never_moves_back()
    {
        var reached = new HashSet<StageTrigger> { StageTrigger.Confirmed, StageTrigger.Invoiced, StageTrigger.Paid };
        Assert.Equal(3, SalesStageAutomation.Pick(null, null, Rules, reached)!.StageId);
        Assert.Equal(3, SalesStageAutomation.Pick(20, 2, Rules, reached)!.StageId);
        Assert.Null(SalesStageAutomation.Pick(30, 3, Rules, reached));           // уже там
        Assert.Null(SalesStageAutomation.Pick(40, 4, Rules, reached));           // вручную дальше — не откатываем
        Assert.Null(SalesStageAutomation.Pick(10, 1, Rules, new HashSet<StageTrigger>()));
        Assert.Equal(6, SalesStageAutomation.Pick(40, 4, Rules, new HashSet<StageTrigger>(reached) { StageTrigger.Shipped })!.StageId);
    }
}
