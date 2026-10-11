using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using static KnitErp.Domain.Production.ProductionOperationKind;
using static KnitErp.Domain.Production.ProductionStage;

namespace KnitErp.UnitTests;

/// <summary>Поток изделий по этапам (D92): не больше предыдущего этапа, переделка и брак на контроле качества, исправления.</summary>
public sealed class ProductionFlowTests
{
    private static ProductionFlow Flow(decimal quantity, decimal released, params ProductionStep[] steps) => new(quantity, steps, released);

    private static string Code(Action action) => Assert.Throws<BusinessRuleException>(action).Code;

    [Fact]
    public void Stage_cannot_exceed_previous_stage_and_knitting_is_limited_by_order()
    {
        var flow = Flow(100, 0, new(Knitting, Done, 60), new(Assembly, Done, 40));
        Assert.Equal((40m, 20m, 40m, 0m), (flow.Available(Knitting), flow.Available(Assembly), flow.Available(Steaming), flow.Available(QualityControl)));
        Assert.Equal("production.stage.exceeds", Code(() => flow.EnsureCanApply(new(Assembly, Done, 21))));
        Assert.Equal("production.stage.exceeds", Code(() => flow.EnsureCanApply(new(Knitting, Done, 41))));
        Assert.Equal("production.stage.exceeds", Code(() => flow.EnsureCanApply(new(Packing, Done, 1))));
        flow.EnsureCanApply(new(Assembly, Done, 20));
        Assert.Equal("production.stage.qc_only", Code(() => flow.EnsureCanApply(new(Assembly, Defect, 1))));
        Assert.Throws<BusinessRuleException>(() => flow.EnsureCanApply(new(Assembly, Done, 0)));
    }

    [Fact]
    public void Quality_control_splits_into_good_rework_and_defect()
    {
        // 50 прошли ВТО; на контроле: 30 годных, 5 брак, 10 на переделку в сборку.
        var flow = Flow(50, 0,
            new(Knitting, Done, 50), new(Assembly, Done, 50), new(Steaming, Done, 50),
            new(QualityControl, Done, 30), new(QualityControl, Defect, 5), new(QualityControl, Rework, 10, Assembly));
        Assert.Equal((50m, 40m, 40m, 30m), (flow.Done(Knitting), flow.Done(Assembly), flow.Done(Steaming), flow.Done(QualityControl)));
        Assert.Equal((5m, 10m), (flow.Defect, flow.Rework));
        // Сборка переделывает 10, ВТО ждёт их; на контроле осталось 5 непроверенных; брак довязывается — вязать можно ещё 5.
        Assert.Equal((5m, 10m, 0m, 5m, 30m), (flow.Available(Knitting), flow.Available(Assembly), flow.Available(Steaming),
            flow.Available(QualityControl), flow.Available(Packing)));
        Assert.Equal("production.stage.rework_target", Code(() => flow.EnsureCanApply(new(QualityControl, Rework, 1, Packing))));
        Assert.Equal("production.stage.rework_target", Code(() => flow.EnsureCanApply(new(QualityControl, Rework, 1))));
        Assert.Equal("production.stage.return_not_allowed", Code(() => flow.EnsureCanApply(new(QualityControl, Done, 1, Assembly))));
        Assert.Equal("production.stage.exceeds", Code(() => flow.EnsureCanApply(new(QualityControl, Defect, 6))));
    }

    [Fact]
    public void Correction_reduces_only_what_has_not_moved_on_and_output_limits_packing()
    {
        var flow = Flow(20, 8,
            new(Knitting, Done, 20), new(Assembly, Done, 15), new(Steaming, Done, 12), new(QualityControl, Done, 10), new(Packing, Done, 10));
        Assert.Equal(2m, flow.ReadyForOutput);
        Assert.Equal("production.stage.correction_sign", Code(() => flow.EnsureCanApply(new(Knitting, Correction, 5))));
        flow.EnsureCanApply(new(Knitting, Correction, -5));
        Assert.Equal("production.stage.correction_exceeds", Code(() => flow.EnsureCanApply(new(Knitting, Correction, -6))));
        // Упаковано 10, выпущено 8: уменьшить упаковку можно только на 2.
        Assert.Equal("production.stage.correction_exceeds", Code(() => flow.EnsureCanApply(new(Packing, Correction, -3))));
        flow.EnsureCanApply(new(Packing, Correction, -2));
        // ВТО: 12 сделано, 10 уже ушли на контроль.
        Assert.Equal("production.stage.correction_exceeds", Code(() => flow.EnsureCanApply(new(Steaming, Correction, -3))));
    }

    [Fact]
    public void Operation_requires_reason_for_correction_and_defect()
    {
        var now = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        Assert.Throws<BusinessRuleException>(() =>
            ProductionOperation.Create(1, 1, new(QualityControl, Defect, 1), null, DateOnly.FromDateTime(now), 1, now));
        Assert.Throws<BusinessRuleException>(() =>
            ProductionOperation.Create(1, 1, new(Knitting, Correction, -1), " ", DateOnly.FromDateTime(now), 1, now));
        var op = ProductionOperation.Create(1, 1, new(QualityControl, Rework, 2, Assembly), null, DateOnly.FromDateTime(now), 1, now);
        Assert.Equal((Assembly, (string?)null), (op.ReturnToStage!.Value, op.Reason));
    }
}
