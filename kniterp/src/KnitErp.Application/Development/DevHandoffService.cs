using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Production;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Норма пряжи для черновика техкарты: позиция, количество в её единице и откуда взят вес.</summary>
public sealed record DevHandoffLineDto(long ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal Grams);

/// <summary>
/// Что уйдёт в производство (D750–D752): предложенная позиция, нормы пряжи на изделие, маршрут, предупреждения. Если модель уже
/// передана — ссылки на позицию и техкарту.
/// </summary>
public sealed record DevHandoffPreviewDto(
    bool CanHandOff, string? Refusal, string SuggestedCode, string SuggestedName, IReadOnlyList<DevOptionDto> Products,
    IReadOnlyList<DevHandoffLineDto> Lines, IReadOnlyList<string> Route, IReadOnlyList<string> Warnings, long? ItemId, long? TechCardId,
    byte[] RowVersion);

/// <summary>
/// Передача модели в производство (D750–D752, этап 6 ТЗ): одной транзакцией через сервисы владельцев — каталог создаёт позицию
/// «Готовая продукция» (или берётся существующая), производство — черновик техкарты на 1 шт с нормами пряжи и маршрутом;
/// модель переходит в «Передано в производство». Утверждает техкарту производство своим порядком.
/// Право — «Разработка: утверждение» плюс права каталога на позицию и техкарту (их проверяют сервисы владельцев).
/// </summary>
public sealed class DevHandoffService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, CatalogService catalog, TechCardService techCards)
{
    /// <summary>Код ОКЕИ «штука» — единица готового изделия.</summary>
    private const string PieceUnitCode = "796";

    public async Task<DevHandoffPreviewDto> PreviewAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var plan = await PlanAsync(org, modelId, ct);
        string? refusal = m.Stage != DevStage.Approved
            ? (m.Stage == DevStage.Handed ? "Модель уже передана в производство." : "Передать в производство можно только утверждённую модель.")
            : !ctx.Permissions.Has(Permissions.DevApprove)
                ? "Передаёт в производство главный технолог (право «Разработка: утверждение»)."
                : !ctx.Permissions.Has(Permissions.CatalogEdit)
                    ? "Нужно право на изменение номенклатуры и техкарт — позицию и техкарту создаёт каталог."
                    : plan.Lines.Count == 0
                        ? "Нет ни одной нормы пряжи: выберите пряжу в палитре и внесите вес деталей (план или факт образца)."
                        : null;
        // Список позиций — только тем, кому каталог виден (без права — передача всё равно запрещена, список не нужен).
        var products = !ctx.Permissions.Has(Permissions.CatalogView) ? [] : await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && i.Type == ItemType.Finished)
            .OrderBy(i => i.Name).Select(i => new DevOptionDto(i.Id, i.Code + " — " + i.Name)).Take(500).ToListAsync(ct);
        return new DevHandoffPreviewDto(refusal is null, refusal, m.Number, m.Name, products, plan.Lines,
            plan.Route.Select(ProductionFlow.StageName).ToList(), plan.Warnings, m.ProductionItemId, m.ProductionTechCardId, m.RowVersion);
    }

    /// <summary>
    /// Передать модель: <paramref name="existingItemId"/> — привязать к существующей позиции, иначе создать новую с кодом и названием.
    /// Возвращает номер черновика техкарты.
    /// </summary>
    public async Task<long> HandOffAsync(long modelId, long? existingItemId, string? code, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevApprove, ct);
        var org = ctx.OrganizationId;
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var m = await db.DevModels.SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
            m.EnsureVersion(m.RowVersion, rowVersion);
            if (m.Stage != DevStage.Approved)
            {
                throw new BusinessRuleException("dev.handoff.stage", "Передать в производство можно только утверждённую модель.");
            }

            var plan = await PlanAsync(org, modelId, ct);
            if (plan.Lines.Count == 0)
            {
                throw new BusinessRuleException("dev.handoff.norms",
                    "Нет ни одной нормы пряжи: выберите пряжу в палитре и внесите вес деталей (план или факт образца).");
            }

            long itemId;
            if (existingItemId is { } existing)
            {
                itemId = await db.Items.AsNoTracking().Where(i => i.Id == existing && i.OrganizationId == org && !i.IsArchived)
                    .Select(i => (long?)i.Id).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Позиция");
            }
            else
            {
                var unitId = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org && u.Code == PieceUnitCode && !u.IsArchived)
                    .Select(u => (long?)u.Id).FirstOrDefaultAsync(ct)
                    ?? throw new BusinessRuleException("dev.handoff.unit", "В справочнике единиц нет «шт» (код 796) — добавьте её в номенклатуре.");
                itemId = await catalog.CreateItemAsync(new ItemCommand(
                    string.IsNullOrWhiteSpace(code) ? m.Number : code.Trim(), string.IsNullOrWhiteSpace(name) ? m.Name : name.Trim(), ItemType.Finished, unitId,
                    $"Из разработки: модель {m.Number}" + (m.Composition is null ? "" : $", состав {m.Composition}")), ct);
            }

            var programs = await db.DevFiles.AsNoTracking().Where(f => f.OrganizationId == org && f.ModelId == modelId && f.Kind == DevFileKind.MachineProgram)
                .Select(f => f.FileName).Distinct().ToListAsync(ct);
            var comment = $"Из разработки: модель {m.Number} «{m.Name}»" + (programs.Count == 0 ? "" : $"; программы: {string.Join(", ", programs)}")
                          + (plan.Warnings.Count == 0 ? "" : $"; проверить нормы: {plan.Warnings.Count} замеч.");
            var cardId = await techCards.CreateAsync(itemId, 1, comment.Length > 500 ? comment[..500] : comment, ct);
            foreach (var line in plan.Lines)
            {
                var card = await techCards.GetAsync(cardId, ct);
                await techCards.SetLineAsync(cardId, line.ItemId, line.Quantity, 0, card.RowVersion, ct);
            }

            await techCards.SetRouteAsync(cardId, plan.Route, (await techCards.GetAsync(cardId, ct)).RowVersion, ct);
            db.DevModelStages.Add(m.HandOver(itemId, cardId, clock.UtcNow, ctx.UserId));
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevStageChanged, nameof(DevModel), modelId.ToString(),
                DevStages.Name(DevStage.Approved), DevStages.Name(DevStage.Handed),
                $"{m.Number}: позиция №{itemId}, черновик техкарты №{cardId}, норм пряжи — {plan.Lines.Count}", currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return cardId;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private sealed record Plan(IReadOnlyList<DevHandoffLineDto> Lines, IReadOnlyList<ProductionStage> Route, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Нормы и маршрут (D751, D752): вес детали — из факта принятого образца, иначе последнего образца, иначе план; доли цветов — из того же
    /// факта. Маршрут — этапы производства операций технолога, вязание и контроль качества — всегда.
    /// </summary>
    private async Task<Plan> PlanAsync(long org, long modelId, CancellationToken ct)
    {
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .ToListAsync(ct);
        var colors = await (from c in db.DevModelColors.AsNoTracking()
                            join i in db.Items.AsNoTracking() on c.ItemId equals i.Id
                            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                            where c.OrganizationId == org && c.ModelId == modelId
                            select new { c.Id, c.Label, ItemId = i.Id, i.Code, i.Name, Unit = u.Code, u.Symbol, u.Precision }).ToListAsync(ct);
        var facts = await (from f in db.DevSampleDetails.AsNoTracking()
                           join s in db.DevSamples.AsNoTracking() on f.SampleId equals s.Id
                           where f.OrganizationId == org && s.ModelId == modelId && s.Status != DevSampleStatus.Rejected
                           select new { f.Id, f.DetailId, f.WeightGrams, s.No, Accepted = s.Status == DevSampleStatus.Accepted }).ToListAsync(ct);
        var best = facts.Where(f => f.WeightGrams is not null).GroupBy(f => f.DetailId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Accepted).ThenByDescending(f => f.No).First());
        var factIds = best.Values.Select(f => f.Id).ToList();
        var shares = await db.DevSampleColorShares.AsNoTracking().Where(s => s.OrganizationId == org && factIds.Contains(s.SampleDetailId)).ToListAsync(ct);
        var input = details.Select(d => best.TryGetValue(d.Id, out var f)
            ? new DevNormDetail(d.Name, d.Quantity, f.WeightGrams, shares.Where(s => s.SampleDetailId == f.Id).ToDictionary(s => s.ColorId, s => s.Percent))
            : new DevNormDetail(d.Name, d.Quantity, d.PlanWeightGrams, new Dictionary<long, decimal>())).ToList();
        var warnings = new List<string>();
        if (details.Count == 0)
        {
            warnings.Add("У модели нет деталей.");
        }

        var result = DevNorms.Compute(input, colors.Select(c => c.Id).ToList());
        warnings.AddRange(result.Warnings);
        var lines = new List<DevHandoffLineDto>();
        foreach (var g in colors.Where(c => result.GramsByColor.ContainsKey(c.Id)).GroupBy(c => c.ItemId))
        {
            var c = g.First();
            var grams = g.Sum(x => result.GramsByColor[x.Id]);
            if (DevNorms.ToUnit(grams, c.Unit, c.Precision) is { } qty)
            {
                lines.Add(new DevHandoffLineDto(c.ItemId, c.Code, c.Name, c.Symbol, qty, grams));
            }
            else
            {
                warnings.Add($"«{c.Name}»: единица «{c.Symbol}» — не кг и не г, норму введёт производство.");
            }
        }

        var stages = await (from o in db.DevModelOperations.AsNoTracking()
                            join t in db.DevOperationTypes.AsNoTracking() on o.OperationTypeId equals t.Id
                            where o.OrganizationId == org && o.ModelId == modelId && t.ProductionStage != null
                            select t.ProductionStage!.Value).Distinct().ToListAsync(ct);
        var route = stages.Select(s => (ProductionStage)s).Where(Enum.IsDefined)
            .Append(ProductionStage.Knitting).Append(ProductionStage.QualityControl).Distinct().OrderBy(s => s).ToList();
        return new Plan(lines, route, warnings);
    }
}
