using KnitErp.Domain.Common;
using KnitErp.Domain.Production;

namespace KnitErp.UnitTests;

public sealed class ProductionRouteTests
{
    private static ProductionStep Done(ProductionStage stage, decimal q) => new(stage, ProductionOperationKind.Done, q, null);

    [Fact]
    public void Route_requires_knitting_and_quality_control_and_keeps_standard_order()
    {
        Assert.Equal(ProductionRoute.FullMask, ProductionRoute.Full.Mask);
        Assert.Equal("production.route.invalid", Assert.Throws<BusinessRuleException>(() =>
            ProductionRoute.Of([ProductionStage.Assembly, ProductionStage.QualityControl])).Code);
        Assert.Equal("production.route.invalid", Assert.Throws<BusinessRuleException>(() =>
            ProductionRoute.Of([ProductionStage.Knitting, ProductionStage.Packing])).Code);
        Assert.Throws<BusinessRuleException>(() => ProductionRoute.From(0b10_1001));

        // Порядок задаёт стандарт, а не порядок перечисления.
        var scarf = ProductionRoute.Of([ProductionStage.Packing, ProductionStage.QualityControl, ProductionStage.Steaming, ProductionStage.Knitting]);
        Assert.Equal([ProductionStage.Knitting, ProductionStage.Steaming, ProductionStage.QualityControl, ProductionStage.Packing], scarf.Stages);
        Assert.Equal((ProductionStage?)ProductionStage.Knitting, scarf.Previous(ProductionStage.Steaming));
        Assert.Equal((ProductionStage?)ProductionStage.QualityControl, scarf.Next(ProductionStage.Steaming));
        Assert.Null(scarf.Previous(ProductionStage.Knitting));
        Assert.Null(scarf.Next(ProductionStage.Packing));
        Assert.False(scarf.Contains(ProductionStage.Assembly));
        Assert.Equal("Вязание → ВТО → Контроль качества → Упаковка", scarf.Describe());
    }

    [Fact]
    public void Flow_skips_stages_outside_route()
    {
        // Шарф: без сборки.
        var route = ProductionRoute.Of([ProductionStage.Knitting, ProductionStage.Steaming, ProductionStage.QualityControl, ProductionStage.Packing]);
        var flow = new ProductionFlow(10, [Done(ProductionStage.Knitting, 10)], 0, route);
        Assert.Equal((0m, 10m), (flow.Available(ProductionStage.Assembly), flow.Available(ProductionStage.Steaming)));
        Assert.Equal(10m, flow.UsedDownstream(ProductionStage.Knitting) + 10); // на ВТО пока ничего не ушло
        Assert.Equal("production.stage.not_in_route", Assert.Throws<BusinessRuleException>(() =>
            flow.EnsureCanApply(Done(ProductionStage.Assembly, 1))).Code);
        Assert.Equal("production.stage.rework_target", Assert.Throws<BusinessRuleException>(() =>
            new ProductionFlow(10, [Done(ProductionStage.Knitting, 10), Done(ProductionStage.Steaming, 10)], 0, route)
                .EnsureCanApply(new ProductionStep(ProductionStage.QualityControl, ProductionOperationKind.Rework, 1, ProductionStage.Assembly))).Code);

        flow = new ProductionFlow(10, [Done(ProductionStage.Knitting, 10), Done(ProductionStage.Steaming, 6)], 0, route);
        Assert.Equal(6m, flow.UsedDownstream(ProductionStage.Knitting));
        Assert.Equal(6m, flow.Available(ProductionStage.QualityControl));
    }

    [Fact]
    public void Without_packing_quality_control_is_the_last_stage_and_feeds_output()
    {
        var route = ProductionRoute.Of([ProductionStage.Knitting, ProductionStage.QualityControl]);
        var flow = new ProductionFlow(5, [Done(ProductionStage.Knitting, 5), Done(ProductionStage.QualityControl, 4)], 1, route);
        Assert.Equal(ProductionStage.QualityControl, route.Last);
        Assert.Equal(5m - 4m, flow.Available(ProductionStage.QualityControl));
        Assert.Equal(3m, flow.ReadyForOutput);
        Assert.Equal(1m, flow.UsedDownstream(ProductionStage.QualityControl));
        Assert.Equal(0m, flow.Available(ProductionStage.Packing));
    }

    [Fact]
    public void Full_route_keeps_previous_behaviour()
    {
        var flow = new ProductionFlow(10, [Done(ProductionStage.Knitting, 10), Done(ProductionStage.Assembly, 7)], 0);
        Assert.Equal((7m, 0m), (flow.Available(ProductionStage.Steaming), flow.Available(ProductionStage.QualityControl)));
        Assert.Equal(7m, flow.UsedDownstream(ProductionStage.Knitting));
    }

    [Fact]
    public void Flow_from_summed_steps_equals_flow_from_all_rows()
    {
        // Настоящая история заказа: отметки, исправления, брак и переделки вразнобой; суммы по (этап, вид, возврат) дают тот же поток.
        var rows = new List<ProductionStep>
        {
            Done(ProductionStage.Knitting, 6), Done(ProductionStage.Knitting, 6), new(ProductionStage.Knitting, ProductionOperationKind.Correction, -1),
            Done(ProductionStage.Assembly, 5), Done(ProductionStage.Assembly, 4), Done(ProductionStage.Steaming, 9),
            Done(ProductionStage.QualityControl, 4), new(ProductionStage.QualityControl, ProductionOperationKind.Defect, 1),
            new(ProductionStage.QualityControl, ProductionOperationKind.Rework, 1, ProductionStage.Assembly),
            new(ProductionStage.QualityControl, ProductionOperationKind.Rework, 2, ProductionStage.Knitting),
            Done(ProductionStage.QualityControl, 1), Done(ProductionStage.Packing, 3), Done(ProductionStage.Packing, 1),
        };
        var summed = rows.GroupBy(r => (r.Stage, r.Kind, r.ReturnTo)).Select(g => new ProductionStep(g.Key.Stage, g.Key.Kind, g.Sum(r => r.Quantity), g.Key.ReturnTo));
        foreach (var route in new[] { ProductionRoute.Full, ProductionRoute.Of([ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming, ProductionStage.QualityControl]) })
        {
            var a = new ProductionFlow(10, rows, 2, route);
            var b = new ProductionFlow(10, summed, 2, route);
            foreach (var stage in ProductionFlow.Stages)
            {
                Assert.Equal((a.Done(stage), a.Available(stage), a.UsedDownstream(stage)), (b.Done(stage), b.Available(stage), b.UsedDownstream(stage)));
            }

            Assert.Equal((a.Defect, a.Rework, a.ReadyForOutput), (b.Defect, b.Rework, b.ReadyForOutput));
        }
    }
}
