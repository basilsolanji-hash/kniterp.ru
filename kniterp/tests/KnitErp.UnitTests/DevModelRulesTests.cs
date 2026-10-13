using System.Text;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;

namespace KnitErp.UnitTests;

/// <summary>D700–D709: стадии и переходы модели, задание, файлы разработки, роли отдела.</summary>
public sealed class DevModelRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Func<DevStage, bool> AllEnabled = _ => true;

    private static DevModelHeader Header(bool hasPattern = true, DevAudience audience = DevAudience.Women, bool child = false) =>
        new(" Свитер с оленями ", null, 1, audience, child, hasPattern, null, null, new DateOnly(2026, 12, 1), DevPriority.Normal,
            new Dictionary<DevRole, long?> { [DevRole.Designer] = 7, [DevRole.Dessinator] = 8 });

    private static DevModel NewModel(bool hasPattern = true) => DevModel.Create(1, "М-00001", Header(hasPattern), Now, 3).Model;

    [Fact]
    public void New_model_starts_at_idea_with_first_history_entry()
    {
        var (m, first) = DevModel.Create(1, "М-00001", Header(), Now, 3);
        Assert.Equal("Свитер с оленями", m.Name);
        Assert.Equal(DevStage.Idea, m.Stage);
        Assert.Null(first.FromStage);
        Assert.Equal(DevStage.Idea, first.ToStage);
        Assert.Null(first.DaysInPrevious);
        Assert.Equal(7, m.AssigneeOf(DevRole.Designer));
        Assert.Null(m.AssigneeOf(DevRole.Programmer));
    }

    [Fact]
    public void Next_stage_skips_pattern_without_pattern_and_disabled_stages()
    {
        Assert.Equal(DevStage.Pattern, DevStages.Next(DevStage.Brief, hasPattern: true, AllEnabled));
        Assert.Equal(DevStage.YarnAndKnit, DevStages.Next(DevStage.Brief, hasPattern: false, AllEnabled));
        Assert.Equal(DevStage.Sample, DevStages.Next(DevStage.YarnAndKnit, true, s => s != DevStage.Program));
        Assert.Equal(DevStage.Approved, DevStages.Next(DevStage.Costing, true, AllEnabled));
        Assert.Null(DevStages.Next(DevStage.Approved, true, AllEnabled));
        Assert.Null(DevStages.Next(DevStage.Cancelled, true, AllEnabled));
    }

    [Fact]
    public void Approved_and_further_need_approve_right()
    {
        Assert.NotNull(DevStages.CheckMove(DevStage.Costing, DevStage.Approved, true, canApprove: false, AllEnabled));
        Assert.Null(DevStages.CheckMove(DevStage.Costing, DevStage.Approved, true, canApprove: true, AllEnabled));
        // Снять утверждение (вернуть на доработку) тоже может только утверждающий.
        Assert.NotNull(DevStages.CheckMove(DevStage.Approved, DevStage.Rework, true, canApprove: false, AllEnabled));
        Assert.Null(DevStages.CheckMove(DevStage.Approved, DevStage.Rework, true, canApprove: true, AllEnabled));
    }

    [Theory]
    [InlineData(DevStage.Idea, DevStage.Handed)]
    [InlineData(DevStage.Handed, DevStage.Rework)]
    [InlineData(DevStage.Idea, DevStage.Cancelled)]
    [InlineData(DevStage.Cancelled, DevStage.Idea)]
    [InlineData(DevStage.Idea, DevStage.Idea)]
    public void Forbidden_moves_are_refused_even_for_approver(DevStage from, DevStage to) =>
        Assert.NotNull(DevStages.CheckMove(from, to, true, canApprove: true, AllEnabled));

    [Fact]
    public void Pattern_stage_is_closed_for_model_without_pattern_and_disabled_stage_is_closed()
    {
        Assert.NotNull(DevStages.CheckMove(DevStage.Brief, DevStage.Pattern, hasPattern: false, true, AllEnabled));
        Assert.NotNull(DevStages.CheckMove(DevStage.Sample, DevStage.Approval, true, true, s => s != DevStage.Approval));
        // Пропустить стадию вперёд и вернуться назад можно.
        Assert.Null(DevStages.CheckMove(DevStage.Idea, DevStage.Sample, true, false, AllEnabled));
        Assert.Null(DevStages.CheckMove(DevStage.Approval, DevStage.Rework, true, false, AllEnabled));
    }

    [Fact]
    public void Move_writes_history_with_days_on_previous_stage()
    {
        var m = NewModel();
        var e = m.MoveTo(DevStage.Brief, false, AllEnabled, " задание готово ", Now.AddDays(3).AddHours(5), 9);
        Assert.Equal(DevStage.Idea, e.FromStage);
        Assert.Equal(DevStage.Brief, e.ToStage);
        Assert.Equal(3, e.DaysInPrevious);
        Assert.Equal("задание готово", e.Comment);
        Assert.Equal(DevStage.Brief, m.Stage);
        Assert.Equal(Now.AddDays(3).AddHours(5), m.StageSinceUtc);
        var ex = Assert.Throws<BusinessRuleException>(() => m.MoveTo(DevStage.Approved, false, AllEnabled, null, Now, 9));
        Assert.Equal("dev.stage.move", ex.Code);
    }

    [Fact]
    public void Cancel_needs_reason_and_restore_returns_to_previous_stage()
    {
        var m = NewModel();
        m.MoveTo(DevStage.YarnAndKnit, false, AllEnabled, null, Now, 9);
        Assert.Throws<BusinessRuleException>(() => m.Cancel(" ", Now, 9));
        var cancel = m.Cancel("Заказчик отказался", Now.AddDays(1), 9);
        Assert.Equal(DevStage.Cancelled, m.Stage);
        Assert.Equal("Заказчик отказался", m.CancelReason);
        Assert.Equal(DevStage.Cancelled, cancel.ToStage);
        Assert.Equal("dev.model.cancelled", Assert.Throws<BusinessRuleException>(() => m.Update(Header())).Code);
        var restore = m.Restore(false, Now.AddDays(2), 9);
        Assert.Equal(DevStage.YarnAndKnit, m.Stage);
        Assert.Equal(DevStage.YarnAndKnit, restore.ToStage);
        Assert.Null(m.CancelReason);
        Assert.Throws<BusinessRuleException>(() => m.Restore(false, Now, 9));
    }

    [Fact]
    public void Child_safety_only_for_kids_and_pattern_flag_guards_pattern_stage()
    {
        var adult = DevModel.Create(1, "М-1", Header(audience: DevAudience.Women, child: true), Now, 3).Model;
        Assert.False(adult.ChildSafety);
        var kid = DevModel.Create(1, "М-2", Header(audience: DevAudience.Kids, child: true), Now, 3).Model;
        Assert.True(kid.ChildSafety);

        kid.MoveTo(DevStage.Pattern, false, AllEnabled, null, Now, 3);
        Assert.Equal("dev.model.pattern_stage", Assert.Throws<BusinessRuleException>(() => kid.Update(Header(hasPattern: false))).Code);
    }

    [Fact]
    public void Header_changes_are_listed_for_audit()
    {
        var m = NewModel();
        var changes = m.Update(Header() with { Name = "Свитер с ёлками", Priority = DevPriority.Urgent });
        Assert.Contains(changes, c => c.Field == "Название" && c.Before == "Свитер с оленями" && c.After == "Свитер с ёлками");
        Assert.Contains(changes, c => c.Field == "Приоритет" && c.After == "Срочно");
        Assert.Empty(m.Update(Header() with { Name = "Свитер с ёлками", Priority = DevPriority.Urgent }));
    }

    [Fact]
    public void Brief_is_validated_and_money_is_not_written_to_audit()
    {
        var m = NewModel();
        var brief = new DevBrief("Свитер оверсайз", "42–52", "молочный", 1234.567m, 3500m, 300, new DateOnly(2026, 11, 1), "шерсть 50%", null, null);
        var changes = m.UpdateBrief(brief);
        Assert.Equal(1234.57m, m.TargetCost);
        var cost = Assert.Single(changes, c => c.Field == "Целевая себестоимость");
        Assert.Null(cost.Before);
        Assert.Equal("изменена", cost.After);
        Assert.DoesNotContain(changes, c => (c.After ?? "").Contains("1234"));
        Assert.Equal("dev.brief.run", Assert.Throws<BusinessRuleException>(() => m.UpdateBrief(brief with { RunQuantity = 0 })).Code);
        Assert.Equal("dev.brief.money", Assert.Throws<BusinessRuleException>(() => m.UpdateBrief(brief with { TargetPrice = -1 })).Code);
    }

    [Fact]
    public void Lateness_and_stage_overdue()
    {
        var today = new DateOnly(2026, 10, 11);
        Assert.True(DevModel.IsLate(DevStage.Sample, new DateOnly(2026, 10, 10), today));
        Assert.False(DevModel.IsLate(DevStage.Handed, new DateOnly(2026, 10, 10), today));
        Assert.False(DevModel.IsLate(DevStage.Sample, null, today));
        Assert.True(DevModel.IsStageOverdue(DevStage.Pattern, Now.AddDays(-8), 7, Now));
        Assert.False(DevModel.IsStageOverdue(DevStage.Pattern, Now.AddDays(-6), 7, Now));
        Assert.False(DevModel.IsStageOverdue(DevStage.Pattern, Now.AddDays(-60), 0, Now));
    }

    [Fact]
    public void Stage_setting_allows_disabling_only_optional_stages()
    {
        var s = DevStageSetting.Create(1, DevStage.Approval, 10, false);
        Assert.False(s.IsEnabled);
        Assert.Equal("dev.stage.required", Assert.Throws<BusinessRuleException>(() => DevStageSetting.Create(1, DevStage.Sample, 5, false)).Code);
        Assert.Equal("dev.stage.plan_days", Assert.Throws<BusinessRuleException>(() => DevStageSetting.Create(1, DevStage.Sample, 400, true)).Code);
        Assert.Equal("dev.stage.setting", Assert.Throws<BusinessRuleException>(() => DevStageSetting.Create(1, DevStage.Handed, 1, true)).Code);
    }

    [Fact]
    public void File_type_is_checked_by_extension_and_signature()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n...");
        var bmp = new byte[40];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        var dxf = Encoding.ASCII.GetBytes("999\nсгенерировано тестом\n  0\nSECTION\n  2\nHEADER\n");
        byte[] program = [0x53, 0x49, 0x4E, 0x01, 0x02, 0x03];

        Assert.Equal(DevFileTypes.Png, DevFileTypes.Detect(DevFileKind.Pattern, "рисунок.png", png));
        Assert.Equal(DevFileTypes.Bmp, DevFileTypes.Detect(DevFileKind.Pattern, "рисунок.BMP", bmp));
        Assert.Equal(DevFileTypes.Pdf, DevFileTypes.Detect(DevFileKind.Lekalo, "лекала.pdf", pdf));
        Assert.Equal(DevFileTypes.Dxf, DevFileTypes.Detect(DevFileKind.Lekalo, "лекала.dxf", dxf));
        Assert.Equal(DevFileTypes.Binary, DevFileTypes.Detect(DevFileKind.MachineProgram, "jacquard.sin", program));
        Assert.Equal(DevFileTypes.Binary, DevFileTypes.Detect(DevFileKind.MachineProgram, "jacquard.dat", program));

        Assert.Null(DevFileTypes.Detect(DevFileKind.Pattern, "рисунок.png", pdf));        // содержимое не PNG
        Assert.Null(DevFileTypes.Detect(DevFileKind.Pattern, "рисунок.pdf", pdf));        // PDF не рисунок
        Assert.Null(DevFileTypes.Detect(DevFileKind.MachineProgram, "x.sin", Encoding.ASCII.GetBytes("<html><script>")));
        Assert.Null(DevFileTypes.Detect(DevFileKind.MachineProgram, "x.dat", [(byte)'M', (byte)'Z', 0x90]));
        Assert.Null(DevFileTypes.Detect(DevFileKind.Lekalo, "x.dxf", Encoding.ASCII.GetBytes("hello")));
    }

    [Fact]
    public void File_name_is_cleaned_and_size_is_limited()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];
        var f = DevFile.Create(1, 2, DevFileKind.Sketch, @"C:\temp\..\эскиз.png", png, 3, " первый ", Now, 4);
        Assert.Equal("эскиз.png", f.FileName);
        Assert.Equal(3, f.Version);
        Assert.Equal("первый", f.Comment);
        Assert.True(f.IsImage);
        Assert.Equal("dev.file.size", Assert.Throws<BusinessRuleException>(() => DevFile.Create(1, 2, DevFileKind.Sketch, "a.png", [], 1, null, Now, 4)).Code);
        Assert.Equal("dev.file.type", Assert.Throws<BusinessRuleException>(() => DevFile.Create(1, 2, DevFileKind.Sketch, "a.exe", png, 1, null, Now, 4)).Code);
    }

    [Fact]
    public void Department_roles_have_permissions_from_the_spec()
    {
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.DevChief, Permissions.DevApprove));
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.DevChief, Permissions.DevSettings));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.DevTechnologist, Permissions.DevApprove));
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.DevDessinator, Permissions.DevPattern));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.DevDessinator, Permissions.DevCostView));
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.DevSampler, Permissions.DevSample));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.DevSampler, Permissions.DevEdit));
        Assert.Contains(SystemRoles.DevChief, SystemRoles.Ordered);
        Assert.Equal("Разработка", Permissions.GroupOf(Permissions.DevCostView));
        Assert.Contains("Разработка", Permissions.Groups);
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.Owner, Permissions.DevApprove));
    }

    [Fact]
    public void Number_is_formatted_from_counter() =>
        Assert.Equal("М-00042", KnitErp.Application.Development.DevModelService.FormatNumber("dev.model-000042"));
}

/// <summary>D790–D798: правила автоматизации разработки.</summary>
public sealed class DevAutomationRulesTests
{
    private static readonly Func<DevStage, bool> AllEnabled = _ => true;

    [Fact]
    public void Planned_launch_sums_enabled_stage_norms_and_skips_pattern_without_pattern()
    {
        var today = new DateOnly(2026, 10, 11);
        // 3+3+7+5+3+5+5+7+2+3 = 43 дня по умолчанию.
        Assert.Equal(today.AddDays(43), DevAutomationRules.PlannedLaunch(today, true, AllEnabled, DevStages.DefaultPlanDays));
        Assert.Equal(today.AddDays(36), DevAutomationRules.PlannedLaunch(today, false, AllEnabled, DevStages.DefaultPlanDays));
        Assert.Equal(today.AddDays(31), DevAutomationRules.PlannedLaunch(today, true, s => s is not (DevStage.Approval or DevStage.Rework),
            DevStages.DefaultPlanDays));
    }

    [Fact]
    public void Default_name_joins_category_collection_and_customer()
    {
        Assert.Equal("Свитер — Осень-зима 2026 — ООО «Пример»", DevAutomationRules.DefaultName("Свитер", "Осень-зима 2026", "ООО «Пример»"));
        Assert.Equal("Шапка", DevAutomationRules.DefaultName("Шапка", null, " "));
        Assert.True(DevAutomationRules.DefaultName("Шапка", new string('я', 300), null).Length <= DevModel.NameMaxLength);
    }

    [Fact]
    public void Auto_advance_by_uploaded_file()
    {
        Assert.Equal(DevStage.Sample, DevAutomationRules.AutoAdvance(DevStage.Program, DevFileKind.MachineProgram, true, AllEnabled));
        Assert.Equal(DevStage.Pattern, DevAutomationRules.AutoAdvance(DevStage.Brief, DevFileKind.Pattern, true, AllEnabled));
        Assert.Equal(DevStage.Pattern, DevAutomationRules.AutoAdvance(DevStage.Idea, DevFileKind.Pattern, true, AllEnabled));
        Assert.Null(DevAutomationRules.AutoAdvance(DevStage.Brief, DevFileKind.Pattern, false, AllEnabled));
        Assert.Null(DevAutomationRules.AutoAdvance(DevStage.Sample, DevFileKind.MachineProgram, true, AllEnabled));
        Assert.Null(DevAutomationRules.AutoAdvance(DevStage.Idea, DevFileKind.Photo, true, AllEnabled));
        Assert.Null(DevAutomationRules.AutoAdvance(DevStage.Brief, DevFileKind.Pattern, true, s => s != DevStage.Pattern));
    }

    [Fact]
    public void Template_fills_only_blank_fields()
    {
        var t = DevCategoryTemplate.Create(1, 1);
        t.Set("42–52", "молочный", "50% шерсть", null, "ручная стирка");
        var filled = t.FillBlanks(new DevBrief("описание", "44–50", null, 100m, null, 10, null, null, null, null));
        Assert.Equal(("44–50", "молочный", "50% шерсть", "ручная стирка", 100m), (filled.SizeRange!, filled.ColorRange!, filled.Composition!, filled.Care!, filled.TargetCost!.Value));
        Assert.False(t.IsEmpty);
        t.Set(" ", null, null, null, null);
        Assert.True(t.IsEmpty);
    }

    [Fact]
    public void Hints_tell_what_is_missing()
    {
        Assert.Contains(DevAutomationRules.AllHints[0], DevAutomationRules.Hints(DevStage.Pattern, true, true, true, false, hasResponsible: false));
        Assert.Contains(DevAutomationRules.AllHints[4], DevAutomationRules.Hints(DevStage.Program, true, true, true, hasProgram: false, true));
        Assert.Empty(DevAutomationRules.Hints(DevStage.Program, true, true, true, true, true));
        Assert.Empty(DevAutomationRules.Hints(DevStage.Handed, false, false, false, false, false));
        Assert.All(DevAutomationRules.Hints(DevStage.Idea, false, false, false, false, false), h => Assert.Contains(h, DevAutomationRules.AllHints));
    }

    [Fact]
    public void File_quota_limits_count_and_total_size_per_model()
    {
        DevFile.EnsureQuota(0, 0, DevFile.MaxBytes);
        DevFile.EnsureQuota(DevFile.MaxFilesPerModel - 1, DevFile.MaxBytesPerModel - 10, 10);
        Assert.Equal("dev.file.quota", Assert.Throws<BusinessRuleException>(() => DevFile.EnsureQuota(DevFile.MaxFilesPerModel, 0, 1)).Code);
        Assert.Equal("dev.file.quota", Assert.Throws<BusinessRuleException>(() => DevFile.EnsureQuota(1, DevFile.MaxBytesPerModel - 10, 11)).Code);
    }

    [Fact]
    public void Sample_custody_transitions_and_overdue()
    {
        Assert.True(DevSample.IsReturnOverdue(DevSampleCustody.AtCustomer, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));
        Assert.False(DevSample.IsReturnOverdue(DevSampleCustody.AtCustomer, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 2)));
        Assert.False(DevSample.IsReturnOverdue(DevSampleCustody.AtCustomer, null, new DateOnly(2027, 1, 1)));
        Assert.False(DevSample.IsReturnOverdue(DevSampleCustody.Returned, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));

        var s = DevSample.Create(1, 1, 1, null, DateTime.UtcNow, 1);
        Assert.Equal("dev.sample.custody.knitting", Assert.Throws<BusinessRuleException>(() => s.HandToCustomer(null, "ООО", new DateOnly(2026, 10, 1), null, null)).Code);
        s.MarkKnitted();
        Assert.Equal("dev.sample.custody.not_out", Assert.Throws<BusinessRuleException>(() => s.CloseCustody(DevSampleCustody.Returned, new DateOnly(2026, 10, 1), null)).Code);
        s.HandToCustomer(5, "  ООО «Пример»  ", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), null);
        Assert.Equal(("ООО «Пример»", DevSampleCustody.AtCustomer, 5L), (s.CustodyRecipient, s.Custody, s.CustodyCounterpartyId!.Value));
        Assert.Equal("dev.sample.custody.result", Assert.Throws<BusinessRuleException>(() => s.CloseCustody(DevSampleCustody.AtFactory, new DateOnly(2026, 10, 2), null)).Code);
        s.CloseCustody(DevSampleCustody.Kept, new DateOnly(2026, 10, 2), null);
        Assert.Equal("dev.sample.custody.out", Assert.Throws<BusinessRuleException>(() => s.HandToCustomer(null, "ООО", new DateOnly(2026, 10, 3), null, null)).Code);
    }

    private static DevPatternValues PatternValues(int? rw = 24, int? width = 120, string? align = null) =>
        new("Олени", 1, rw, 30, 7m, 1, width, align, null, null, null);

    [Fact]
    public void Pattern_statuses_versions_and_editing()
    {
        var p = DevPattern.Create(1, "Р-00001", PatternValues(), DateTime.UtcNow, 1);
        Assert.Equal((DevPatternStatus.Draft, 1), (p.Status, p.CurrentVersion));
        Assert.Equal("dev.pattern.status", Assert.Throws<BusinessRuleException>(() => p.MoveTo(DevPatternStatus.Draft)).Code);
        p.MoveTo(DevPatternStatus.Review);
        p.MoveTo(DevPatternStatus.Approved);
        Assert.Equal("dev.pattern.locked", Assert.Throws<BusinessRuleException>(() => p.Set(PatternValues())).Code);
        p.MarkReadyForProgramming(DateTime.UtcNow);
        Assert.Equal(2, p.NextVersion());
        Assert.Equal((DevPatternStatus.Draft, (DateTime?)null), (p.Status, p.ReadyForProgrammingAtUtc));
        p.MoveTo(DevPatternStatus.Archived);
        Assert.Equal("dev.pattern.locked", Assert.Throws<BusinessRuleException>(() => p.NextVersion()).Code);
        p.MoveTo(DevPatternStatus.Draft);
        Assert.Equal("dev.pattern.rapport", Assert.Throws<BusinessRuleException>(() => p.Set(PatternValues(rw: 0))).Code);
        Assert.Equal("Р-00012", DevPattern.FormatNumber("dev.pattern-000012"));
    }

    [Fact]
    public void Pattern_approval_checks_colors_yarn_feeders_and_rapport()
    {
        var p = DevPattern.Create(1, "Р-00001", PatternValues(), DateTime.UtcNow, 1);
        long[] colors = [1, 2, 3];
        IReadOnlyCollection<long> all = new HashSet<long>(colors);
        string Code(Action a) => Assert.Throws<BusinessRuleException>(a).Code;
        Assert.Equal("dev.pattern.approve.colors", Code(() => DevPatternRules.EnsureApprovable(p, [], [("Основной", all)], 8)));
        Assert.Equal("dev.pattern.approve.colorway", Code(() => DevPatternRules.EnsureApprovable(p, colors, [], 8)));
        Assert.Equal("dev.pattern.approve.yarn", Code(() => DevPatternRules.EnsureApprovable(p, colors, [("Основной", all), ("Красный", new HashSet<long> { 1, 2 })], 8)));
        Assert.Equal("dev.pattern.approve.machine", Code(() => DevPatternRules.EnsureApprovable(p, colors, [("Основной", all)], null)));
        Assert.Equal("dev.pattern.approve.feeders", Code(() => DevPatternRules.EnsureApprovable(p, colors, [("Основной", all)], 2)));
        DevPatternRules.EnsureApprovable(p, colors, [("Основной", all)], 8); // 120 кратно 24
        var odd = DevPattern.Create(1, "Р-2", PatternValues(width: 125), DateTime.UtcNow, 1);
        Assert.Equal("dev.pattern.approve.align", Code(() => DevPatternRules.EnsureApprovable(odd, colors, [("Основной", all)], 8)));
        DevPatternRules.EnsureApprovable(DevPattern.Create(1, "Р-3", PatternValues(width: 125, align: "по центру переда"), DateTime.UtcNow, 1), colors,
            [("Основной", all)], 8);
        Assert.Equal("dev.pattern.approve.rapport", Code(() => DevPatternRules.EnsureApprovable(DevPattern.Create(1, "Р-4", PatternValues(rw: null), DateTime.UtcNow, 1),
            colors, [("Основной", all)], 8)));
    }

    [Fact]
    public void Approved_pattern_version_stays_locked_until_new_version()
    {
        var p = DevPattern.Create(1, "Р-1", PatternValues(), DateTime.UtcNow, 1);
        p.MoveTo(DevPatternStatus.Approved);
        p.MoveTo(DevPatternStatus.Draft); // «вернуть в черновик» не открывает правку утверждённой версии
        Assert.False(p.IsEditable);
        Assert.Equal("dev.pattern.locked", Assert.Throws<BusinessRuleException>(() => p.Set(PatternValues())).Code);
        p.MoveTo(DevPatternStatus.Archived);
        p.MoveTo(DevPatternStatus.Draft);
        Assert.False(p.IsEditable);
        p.NextVersion();
        Assert.True(p.IsEditable);
        p.Set(PatternValues());
    }

    [Fact]
    public void Grid_key_is_validated_and_can_be_cleared()
    {
        var c = DevPatternColor.Create(1, 1, 1, "Фон", null, null, "#FFFFFF");
        Assert.Equal("field.too_long", Assert.Throws<BusinessRuleException>(() => c.SetShare(50, "Background1")).Code);
        c.SetShare(0, null, clearKey: true);
        Assert.Null(c.GridKey);
        c.SetShare(40, "#000000");
        Assert.Equal(("#000000", 40m), (c.GridKey, c.SharePercent!.Value));
    }

    [Fact]
    public void Text_grid_is_bounded_by_size_and_row_length()
    {
        Assert.False(DevPatternGrid.LooksLikeTextGrid(new byte[DevPatternGrid.MaxTextBytes + 1].Select(_ => (byte)'1').ToArray()));
        var longRow = System.Text.Encoding.ASCII.GetBytes(string.Join(",", Enumerable.Repeat("a", DevPatternGrid.MaxSide + 1)));
        Assert.False(DevPatternGrid.LooksLikeTextGrid(longRow));
        Assert.Equal("dev.pattern.grid.size", Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(longRow, "text/csv")).Code);
        Assert.True(DevPatternGrid.LooksLikeTextGrid(System.Text.Encoding.ASCII.GetBytes(string.Join(",", Enumerable.Repeat("a", DevPatternGrid.MaxSide)))));
    }
}
