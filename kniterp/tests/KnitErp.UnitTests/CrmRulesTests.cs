using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;

namespace KnitErp.UnitTests;

/// <summary>CRM (D600–D609): нормализация контактов, правила воронки, вероятности, лида и сделки, права ролей.</summary>
public sealed class CrmRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("+7 900 000-00-01", "+79000000001")]
    [InlineData("8 (900) 000-00-01", "+79000000001")]
    [InlineData("900 000 00 01", "+79000000001")]
    [InlineData("+375 29 000-00-01", "+375290000001")]
    [InlineData("  ", null)]
    public void Phone_is_normalized_to_e164(string input, string? expected) => Assert.Equal(expected, CrmText.NormalizePhone(input));

    [Theory]
    [InlineData("12-34")]
    [InlineData("+7 900 000-00-0x")]
    [InlineData("0000000000000")]
    public void Bad_phone_is_rejected(string input) =>
        Assert.Equal("crm.phone", Assert.Throws<BusinessRuleException>(() => CrmText.NormalizePhone(input)).Code);

    [Fact]
    public void Email_inn_and_masks()
    {
        Assert.Equal("test@example.org", CrmText.NormalizeEmail(" Test@Example.ORG "));
        Assert.Throws<BusinessRuleException>(() => CrmText.NormalizeEmail("test@example"));
        Assert.Throws<BusinessRuleException>(() => CrmText.NormalizeEmail("a b@example.org"));
        Assert.Equal("crm.inn", Assert.Throws<BusinessRuleException>(() => CrmText.NormalizeInn("12345")).Code);
        Assert.Equal("+7 900 ***-**-01", CrmText.MaskPhone("+79000000001"));
        Assert.Equal("t***@example.org", CrmText.MaskEmail("test@example.org"));
    }

    [Fact]
    public void Default_pipeline_has_one_won_and_one_lost_stage()
    {
        Assert.Single(CrmStage.Defaults, d => d.Kind == CrmStageKind.Won);
        Assert.Single(CrmStage.Defaults, d => d.Kind == CrmStageKind.Lost);
        Assert.All(CrmStage.Defaults, d => Assert.Contains(d.Color, CrmStage.Colors));
        var won = CrmStage.Create(1, 1, "Выиграна", "green", 100, CrmStageKind.Won, 10);
        Assert.Equal("crm.stage.final", Assert.Throws<BusinessRuleException>(won.Archive).Code);
        Assert.Throws<BusinessRuleException>(() => CrmStage.Create(1, 1, "Проиграна", "red", 5, CrmStageKind.Lost, 20));
        Assert.Throws<BusinessRuleException>(() => CrmStage.Create(1, 1, "Этап", "pink", 5, CrmStageKind.Open, 30));
        Assert.Throws<BusinessRuleException>(() => CrmStage.Create(1, 1, "Этап", "gray", 101, CrmStageKind.Open, 30));
    }

    [Fact]
    public void Deal_takes_probability_from_stage_and_closes_on_final_stage()
    {
        var first = Stage(1, 10, CrmStageKind.Open);
        var offer = Stage(2, 60, CrmStageKind.Open);
        var won = Stage(3, 100, CrmStageKind.Won);
        var lost = Stage(4, 0, CrmStageKind.Lost);
        var (deal, entry) = CrmDeal.Create(1, "С-000001", new CrmDealValues("Свитера", 5, Amount: 1000m), first, "RUB", 7, Now);
        Assert.Equal((10, CrmDealOutcome.Open, (long?)null, 1L), (deal.Probability, deal.Outcome, entry.FromStageId, entry.ToStageId));
        Assert.Equal(100m, deal.WeightedAmount);

        var move = deal.MoveTo(offer, 7, Now.AddDays(2.5));
        Assert.Equal((60, 2.5m, (long?)1), (deal.Probability, move.DaysInStage, move.FromStageId));
        deal.SetProbability(70);
        Assert.Equal(700m, deal.WeightedAmount);
        Assert.Equal("crm.deal.same_stage", Assert.Throws<BusinessRuleException>(() => deal.MoveTo(offer, 7, Now)).Code);
        Assert.Equal("crm.deal.loss_reason", Assert.Throws<BusinessRuleException>(() => deal.MoveTo(lost, 7, Now)).Code);
        Assert.Equal("crm.deal.other_pipeline", Assert.Throws<BusinessRuleException>(() =>
            deal.MoveTo(CrmStage.Create(1, 99, "Чужой", "gray", 10, CrmStageKind.Open, 1), 7, Now)).Code);

        deal.MoveTo(won, 7, Now.AddDays(3));
        Assert.Equal((CrmDealOutcome.Won, 100), (deal.Outcome, deal.Probability));
        Assert.Equal("crm.deal.closed", Assert.Throws<BusinessRuleException>(() => deal.SetLine(1, 1, 1)).Code);
        deal.MoveTo(first, 7, Now.AddDays(4));
        Assert.Null(deal.ClosedAtUtc);

        Assert.Equal("crm.deal.first_stage", Assert.Throws<BusinessRuleException>(() =>
            CrmDeal.Create(1, "С-000002", new CrmDealValues("Пледы", 5), won, "RUB", 7, Now)).Code);
    }

    [Fact]
    public void Deal_amount_follows_lines()
    {
        var (deal, _) = CrmDeal.Create(1, "С-000001", new CrmDealValues("Свитера", 5, Amount: 999m), Stage(1, 10, CrmStageKind.Open), "RUB", 7, Now);
        deal.SetLine(10, 3, 1500m);
        deal.SetLine(11, 2, 250.5m);
        Assert.Equal(5001m, deal.Amount);
        deal.Update(new CrmDealValues("Свитера", 5, Amount: 1m));
        Assert.Equal(5001m, deal.Amount); // сумма с позициями — их сумма
        Assert.Throws<BusinessRuleException>(() => deal.SetLine(12, 0, 1));
        Assert.False(deal.AddParticipant(7)); // ответственный не наблюдатель
        Assert.True(deal.AddParticipant(8));
        Assert.False(deal.AddParticipant(8));
    }

    [Fact]
    public void Lead_lifecycle_and_merge()
    {
        var lead = CrmLead.Create(1, "Л-000001", new CrmLeadValues("300 свитеров", Phone: "+7 900 000-00-01", ExpectedAmount: 10m), "RUB", 7, Now);
        Assert.Equal((CrmLeadStatus.New, 7L, "RUB"), (lead.Status, lead.ResponsibleUserId, lead.CurrencyCode));
        Assert.Throws<BusinessRuleException>(() => CrmLead.Create(1, "Л-2", new CrmLeadValues(" "), "RUB", 7, Now));
        Assert.Equal("crm.currency", Assert.Throws<BusinessRuleException>(() => CrmLead.Create(1, "Л-2", new CrmLeadValues("Х", CurrencyCode: "XXX"), "RUB", 7, Now)).Code);
        Assert.Equal("crm.amount", Assert.Throws<BusinessRuleException>(() => CrmLead.Create(1, "Л-2", new CrmLeadValues("Х", ExpectedAmount: 1.001m), "RUB", 7, Now)).Code);
        lead.StartWork();
        Assert.Throws<BusinessRuleException>(lead.StartWork);
        lead.MarkQualified(1, 2, 3, Now);
        Assert.Equal("crm.lead.qualified", Assert.Throws<BusinessRuleException>(() => lead.MarkQualified(1, 2, 3, Now)).Code);
        Assert.Throws<BusinessRuleException>(() => lead.Update(new CrmLeadValues("Новое")));

        var a = CrmLead.Create(1, "Л-000002", new CrmLeadValues("Заявка", Phone: "+7 900 000-00-02"), "RUB", 7, Now);
        var b = CrmLead.Create(1, "Л-000003", new CrmLeadValues("Заявка", Email: "x@example.org", Need: "Шарфы"), "RUB", 7, Now);
        Assert.Equal("crm.lead.merge_self", Assert.Throws<BusinessRuleException>(() => a.MergeInto(a, 1, Now)).Code);
        b.MergeInto(a, 9, Now);
        Assert.Equal((CrmLeadStatus.Rejected, "x@example.org", "Шарфы", "+79000000002"), (b.Status, a.Email, a.Need, a.Phone));
        Assert.Throws<BusinessRuleException>(b.Reopen);
        Assert.Contains("+7 900 ***-**-02", a.Describe());
        Assert.DoesNotContain("79000000002", a.Describe());
    }

    [Fact]
    public void Crm_roles_and_permissions()
    {
        Assert.All(new[] { Permissions.CrmView, Permissions.CrmEdit, Permissions.CrmAll, Permissions.CrmSettings, Permissions.CrmExport }, code =>
        {
            Assert.True(Permissions.IsKnown(code));
            Assert.Equal("CRM", Permissions.GroupOf(code));
            Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.Owner, code));
            Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.SalesHead, code));
        });
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.SalesManager, Permissions.CrmAll));
        Assert.Equal(PermissionLevel.Full, RoleMatrixP0.LevelOf(SystemRoles.SalesManager, Permissions.SalesEdit));
        Assert.Equal(PermissionLevel.None, RoleMatrixP0.LevelOf(SystemRoles.Storekeeper, Permissions.CrmView));
        Assert.Contains(SystemRoles.SalesManager, SystemRoles.Ordered);
        Assert.All(RoleMatrixP0.PermissionsOf(SystemRoles.SalesHead), p => Assert.True(Permissions.IsKnown(p.PermissionCode)));
        Assert.False(SystemRoles.IsAdministrative(SystemRoles.SalesHead));
    }

    private static CrmStage Stage(long id, int probability, CrmStageKind kind)
    {
        var stage = CrmStage.Create(1, 1, $"Этап {id}", "gray", probability, kind, (int)id * 10);
        typeof(CrmStage).GetProperty(nameof(CrmStage.Id))!.SetValue(stage, id);
        return stage;
    }
}
