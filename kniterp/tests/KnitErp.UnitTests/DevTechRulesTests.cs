using KnitErp.Domain.Common;
using KnitErp.Domain.Development;

namespace KnitErp.UnitTests;

/// <summary>D710–D716: станки, заправка, размеры и градация, факт образца и доли цветов, операции, показатели заполнения.</summary>
public sealed class DevTechRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static DevMachineValues Stoll(int feeders = 8) => new(" STOLL №3 ", DevMachineBrand.Stoll, "CMS 530", 7, feeders, 130, 2, "ИНВ-17");

    [Fact]
    public void Machine_parameters_and_status_log()
    {
        var m = DevMachine.Create(1, Stoll());
        Assert.Equal("STOLL №3", m.Name);
        Assert.Equal("STOLL CMS 530 · E7 · 8 нитеводителей · 130 см · 2 сист.", m.Parameters);
        Assert.Equal(DevMachineStatus.Working, m.Status);

        var note = m.AddNote("перенёс, плохо работает", DevMachineStatus.Limited, Now, 5);
        Assert.Equal((DevMachineStatus.Limited, DevMachineStatus.Limited), (m.Status, note.StatusAfter!.Value));
        var same = m.AddNote("смазали", DevMachineStatus.Limited, Now, 5);
        Assert.Null(same.StatusAfter); // состояние не изменилось — в журнале только комментарий
        Assert.Equal("Состояние: Не работает", m.AddNote(" ", DevMachineStatus.Down, Now, 5).Text);
        Assert.Throws<BusinessRuleException>(() => m.AddNote(" ", null, Now, 5));

        Assert.Equal("dev.machine.feeders", Assert.Throws<BusinessRuleException>(() => DevMachine.Create(1, Stoll(feeders: 0))).Code);
        Assert.Equal("dev.machine.gauge", Assert.Throws<BusinessRuleException>(() => DevMachine.Create(1, Stoll() with { Gauge = 40 })).Code);
    }

    [Fact]
    public void Threading_is_checked_against_machine_feeders_and_needs_yarn()
    {
        var row = new DevThreadingRow(8, DevFeederSide.Left, 10, null, 4, true, "N", false);
        var t = DevThreading.Create(1, 2, row, feederCount: 8);
        Assert.Equal((8, DevFeederSide.Left, 4, true, "N"), (t.Feeder, t.Side, t.Ends, t.Paraffin, t.Mode!));
        Assert.Equal("dev.threading.feeder", Assert.Throws<BusinessRuleException>(() => DevThreading.Create(1, 2, row with { Feeder = 9 }, 8)).Code);
        Assert.Equal("dev.threading.ends", Assert.Throws<BusinessRuleException>(() => DevThreading.Create(1, 2, row with { Ends = 0 }, 8)).Code);
        Assert.Equal("dev.threading.yarn", Assert.Throws<BusinessRuleException>(() => DevThreading.Create(1, 2, row with { ColorId = null, Label = " " }, 8)).Code);
        var separator = DevThreading.Create(1, 2, row with { ColorId = null, Label = "Разделитель (нейлон)", Ends = 1, Paraffin = false }, 8);
        Assert.Equal("Разделитель (нейлон)", separator.Label);

        DevThreading.EnsureUnique([row, row with { Side = DevFeederSide.Right }]);
        Assert.Equal("dev.threading.duplicate", Assert.Throws<BusinessRuleException>(() => DevThreading.EnsureUnique([row, row with { Ends = 2 }])).Code);
    }

    [Fact]
    public void Detail_plan_and_palette_color_are_validated()
    {
        var d = DevDetail.Create(1, 2, " Перед ", 1, 1);
        d.SetKnitting(3, null, 180, 100.456m, "Если хлопок 20/1 — 4 конца");
        Assert.Equal(("Перед", 180, 100.46m), (d.Name, d.PlanKnitSeconds!.Value, d.PlanWeightGrams!.Value));
        Assert.Equal("dev.detail.time", Assert.Throws<BusinessRuleException>(() => d.SetKnitting(3, null, 0, null, null)).Code);
        Assert.Equal("dev.detail.quantity", Assert.Throws<BusinessRuleException>(() => d.Rename("Рукав", 0)).Code);
        var before = d.ThreadingRevision;
        d.TouchThreading();
        Assert.Equal(before + 1, d.ThreadingRevision);

        var c = DevModelColor.Create(1, 2, "доп.1", 5, "#2ba89a", null, 1);
        Assert.Equal("#2BA89A", c.Swatch);
        Assert.Equal("dev.color.swatch", Assert.Throws<BusinessRuleException>(() => c.Set("доп.1", 5, "red;background:url(x)", null)).Code);
    }

    [Fact]
    public void Color_shares_sum_to_100_and_are_computed_from_weights()
    {
        DevSampleColorShare.EnsureValid([(1, 60m), (2, 40m)]);
        DevSampleColorShare.EnsureValid([(1, 60m), (2, 39.6m)]); // допуск округления 0,5 %
        DevSampleColorShare.EnsureValid([]);
        Assert.Equal("dev.share.sum", Assert.Throws<BusinessRuleException>(() => DevSampleColorShare.EnsureValid([(1, 70m), (2, 40m)])).Code);
        Assert.Equal("dev.share.duplicate", Assert.Throws<BusinessRuleException>(() => DevSampleColorShare.EnsureValid([(1, 50m), (1, 50m)])).Code);

        var shares = DevSampleColorShare.FromWeights([(1, 67m), (2, 33m), (3, 0.5m)]);
        Assert.Equal(100m, shares.Sum(s => s.Percent));
        Assert.Equal(66.66m, shares.Single(s => s.ColorId == 1).Percent); // 66,67 − 0,01 остатка округления — сумма ровно 100
        Assert.Empty(DevSampleColorShare.FromWeights([(1, 0m)]));
    }

    [Fact]
    public void Sample_fact_and_decision_rules()
    {
        var fact = DevSampleDetail.Create(1, 2, 3);
        fact.Record(195, 101.234m, Now, 7);
        Assert.Equal((195, 101.23m), (fact.KnitSeconds!.Value, fact.WeightGrams!.Value));
        Assert.Equal("dev.sample.empty", Assert.Throws<BusinessRuleException>(() => fact.Record(null, null, Now, 7)).Code);

        var s = DevSample.Create(1, 2, 1, null, Now, 7);
        s.MarkKnitted();
        Assert.Equal(DevSampleStatus.Ready, s.Status);
        Assert.Equal("dev.sample.reason", Assert.Throws<BusinessRuleException>(() => s.Decide(DevSampleStatus.Rework, " ")).Code);
        s.Decide(DevSampleStatus.Rework, "длина рукава +2 см");
        s.MarkKnitted();
        Assert.Equal(DevSampleStatus.Rework, s.Status); // решение не затирается
    }

    [Fact]
    public void Grading_and_tolerance()
    {
        // Размеры 1–4, базовый — 2-й (индекс 1), шаг +2 см.
        Assert.Equal(new[] { 58m, 60m, 62m, 64m }, Enumerable.Range(0, 4).Select(i => DevMeasureRules.Grade(60m, 2m, i, 1)));
        Assert.Equal(55.5m, DevMeasureRules.Grade(57m, -1.5m, 2, 1));
        Assert.Equal((1.5m, true), DevMeasureRules.Check(60m, 61.5m, 1m));
        Assert.Equal((-1m, false), DevMeasureRules.Check(60m, 59m, 1m));
        Assert.Equal("dev.measure.value", Assert.Throws<BusinessRuleException>(() => DevMeasureRules.EnsureCm(-1)).Code);
        Assert.Equal("dev.measure.tolerance", Assert.Throws<BusinessRuleException>(() => DevMeasure.Create(1, 2, "Длина", 60, null, 1)).Code);
        Assert.Equal("dev.size.box", Assert.Throws<BusinessRuleException>(() => DevModelSize.Create(1, 2, "1", 1, true, 0)).Code);
    }

    [Fact]
    public void Operation_totals_take_own_norm_before_directory()
    {
        var (minutes, cost) = DevModelOperation.Totals([(null, null, 3m, 10m), (1.5m, 4m, 2m, 9m), (null, null, null, null)]);
        Assert.Equal((4.5m, 14m), (minutes, cost));
        Assert.Equal("dev.operation.stage", Assert.Throws<BusinessRuleException>(() =>
            DevOperationType.Create(1, new DevOperationValues("Вязание", "шт", 1, 3, 6))).Code);
        Assert.Equal("шт", DevOperationType.Create(1, new DevOperationValues("Парить", null, null, 0.1m, 3)).Unit);
    }

    [Fact]
    public void Completeness_counts_sections()
    {
        var c = DevCompleteness.From(true, true, true, true, false, false, false, false, true, false);
        Assert.Equal((5, 10, 50), (c.Done, c.Total, c.Percent));
        Assert.Equal(DevCompleteness.SectionNames, c.Sections.Select(s => s.Section));
    }

    [Fact]
    public void Program_file_is_attached_to_detail_and_machine_before_insert()
    {
        byte[] program = [0x53, 0x49, 0x4E, 1, 2];
        var f = DevFile.Create(1, 2, DevFileKind.MachineProgram, "jacquard.sin", program, 1, null, Now, 3);
        f.AttachProgram(5, 6, "STOLL №3: STOLL CMS 530 · E7");
        Assert.Equal((5L, 6L), (f.DetailId!.Value, f.MachineId!.Value));
        var sketch = DevFile.Create(1, 2, DevFileKind.Sketch, "s.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 1, null, Now, 3);
        Assert.Equal("dev.file.program", Assert.Throws<BusinessRuleException>(() => sketch.AttachProgram(5, 6, "x")).Code);
    }
}
