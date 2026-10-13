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
public sealed record DevHandoffLineDto(long ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal Grams, decimal WastePercent = 0);

/// <summary>
/// Что уйдёт в производство (D750–D752): предложенная позиция, нормы пряжи на изделие, маршрут, предупреждения. Если модель уже
/// передана — ссылки на позицию и техкарту.
/// </summary>
public sealed record DevHandoffPreviewDto(
    bool CanHandOff, string? Refusal, string SuggestedCode, string SuggestedName, IReadOnlyList<DevOptionDto> Products,
    IReadOnlyList<DevHandoffLineDto> Lines, IReadOnlyList<string> Route, IReadOnlyList<DevNote> Warnings, long? ItemId, long? TechCardId,
    byte[] RowVersion, string? SuggestedSizes = null, string? SuggestedColors = null, bool HasSketchPhoto = false);

/// <summary>Итог передачи: черновик техкарты, позиция, сколько модификаций создано и добавлено ли фото эскиза (D753).</summary>
public sealed record DevHandoffResult(long TechCardId, long ItemId, int Modifications, bool PhotoAdded);

/// <summary>
/// Передача модели в производство (D750–D752, этап 6 ТЗ): одной транзакцией через сервисы владельцев — каталог создаёт позицию
/// «Готовая продукция» (или берётся существующая), производство — черновик техкарты на 1 шт с нормами пряжи и маршрутом;
/// модель переходит в «Передано в производство». Утверждает техкарту производство своим порядком.
/// Право — «Разработка: утверждение» плюс права каталога на позицию и техкарту (их проверяют сервисы владельцев).
/// </summary>
public sealed class DevHandoffService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, CatalogService catalog, TechCardService techCards,
    ModificationService modifications)
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
        var sizes = await db.DevModelSizes.AsNoTracking().Where(z => z.OrganizationId == org && z.ModelId == modelId).OrderBy(z => z.SortOrder)
            .Select(z => z.Name).ToListAsync(ct);
        var sketch = await SketchAsync(org, m.MainSketchFileId, ct);
        return new DevHandoffPreviewDto(refusal is null, refusal, m.Number, m.Name, products, plan.Lines,
            plan.Route.Select(ProductionFlow.StageName).ToList(), plan.Warnings, m.ProductionItemId, m.ProductionTechCardId, m.RowVersion,
            sizes.Count == 0 ? m.SizeRange : string.Join(", ", sizes), m.ColorRange, sketch is not null);
    }

    /// <summary>
    /// Передать модель: <paramref name="existingItemId"/> — привязать к существующей позиции, иначе создать новую с кодом и названием.
    /// Возвращает номер черновика техкарты.
    /// </summary>
    public async Task<DevHandoffResult> HandOffAsync(long modelId, long? existingItemId, string? code, string? name, byte[] rowVersion,
        string? sizes = null, string? colors = null, CancellationToken ct = default)
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
            // D729: рисунок модели — номер и версия; программы его версии. Сами файлы — в модели и рисунке (в техкарте производства вложений нет),
            // путь к ним — в комментарии.
            var pattern = m.PatternId is { } pid
                ? await db.DevPatterns.AsNoTracking().Where(p => p.Id == pid && p.OrganizationId == org).Select(p => p.Number).SingleOrDefaultAsync(ct)
                : null;
            if (m.PatternId is { } pid2 && m.PatternVersion is { } pv)
            {
                programs.AddRange(await db.DevPatternFiles.AsNoTracking()
                    .Where(f => f.OrganizationId == org && f.PatternId == pid2 && f.Version == pv && f.Kind == DevPatternFileKind.Program)
                    .Select(f => f.FileName).Distinct().ToListAsync(ct));
            }

            var comment = $"Из разработки: модель {m.Number} «{m.Name}»" + (pattern is null ? "" : $"; рисунок {pattern}, версия {m.PatternVersion}")
                          + (programs.Count == 0 ? "" : $"; программы: {string.Join(", ", programs.Distinct())}")
                          + (plan.Warnings.Count == 0 ? "" : $"; проверить нормы: {plan.Warnings.Count} замеч.") + $"; файлы: dev/models/{m.Id}?tab=files";
            var cardId = await techCards.CreateAsync(itemId, 1, comment.Length > 500 ? comment[..500] : comment, ct);
            foreach (var line in plan.Lines)
            {
                var card = await techCards.GetAsync(cardId, ct);
                await techCards.SetLineAsync(cardId, line.ItemId, line.Quantity, line.WastePercent, card.RowVersion, ct);
            }

            await techCards.SetRouteAsync(cardId, plan.Route, (await techCards.GetAsync(cardId, ct)).RowVersion, ct);

            // D753: модификации «Размер × Цвет» и фото эскиза — методами каталога; уже существующие сочетания каталог пропускает.
            var axes = new Dictionary<long, string?>();
            if (!string.IsNullOrWhiteSpace(sizes))
            {
                axes[await CharacteristicAsync("Размер", ct)] = sizes;
            }

            if (!string.IsNullOrWhiteSpace(colors))
            {
                axes[await CharacteristicAsync("Цвет", ct)] = colors;
            }

            var created = axes.Count == 0 ? 0 : (await modifications.CreateModificationsAsync(itemId, axes, ct)).Created;
            var photo = false;
            if (await SketchAsync(org, m.MainSketchFileId, ct) is { } sketch
                && !await db.ItemPhotos.AnyAsync(p => p.OrganizationId == org && p.ItemId == itemId, ct))
            {
                await modifications.AddPhotoAsync(itemId, sketch, ct);
                photo = true;
            }
            db.DevModelStages.Add(m.HandOver(itemId, cardId, clock.UtcNow, ctx.UserId));
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevStageChanged, nameof(DevModel), modelId.ToString(),
                DevStages.Name(DevStage.Approved), DevStages.Name(DevStage.Handed),
                $"{m.Number}: позиция №{itemId}, черновик техкарты №{cardId}, норм пряжи — {plan.Lines.Count}, модификаций — {created}"
                + (photo ? ", фото эскиза" : ""), currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return new DevHandoffResult(cardId, itemId, created, photo);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Характеристика каталога по названию (без учёта регистра); нет — создаётся. Архивную не используем — создаём новую.</summary>
    private async Task<long> CharacteristicAsync(string name, CancellationToken ct)
    {
        var existing = (await modifications.CharacteristicsAsync(false, ct))
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return existing?.Id ?? await modifications.CreateCharacteristicAsync(name, ct);
    }

    /// <summary>Закреплённый эскиз, если он годится в фото позиции каталога (JPEG, PNG, WebP до 2 МБ); иначе — null.</summary>
    private async Task<byte[]?> SketchAsync(long org, long? fileId, CancellationToken ct)
    {
        if (fileId is not { } id)
        {
            return null;
        }

        var file = await db.DevFiles.AsNoTracking().Where(f => f.Id == id && f.OrganizationId == org)
            .Select(f => new { f.ContentType, Size = f.Content.Length }).SingleOrDefaultAsync(ct);
        if (file is null || file.Size > ItemPhoto.MaxBytes || file.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            return null;
        }

        return await db.DevFiles.AsNoTracking().Where(f => f.Id == id).Select(f => f.Content).SingleAsync(ct);
    }

    private sealed record Plan(IReadOnlyList<DevHandoffLineDto> Lines, IReadOnlyList<ProductionStage> Route, IReadOnlyList<DevNote> Warnings);

    /// <summary>
    /// Нормы и маршрут (D751, D752): вес детали — из факта принятого образца, иначе последнего образца, иначе план; доли цветов — из того же
    /// факта. Маршрут — этапы производства операций технолога, вязание и контроль качества — всегда.
    /// </summary>
    private async Task<Plan> PlanAsync(long org, long modelId, CancellationToken ct)
    {
        var (norms, warnings) = await DevYarnNorms.ForModelAsync(db, org, modelId, ct);
        var waste = (await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct))?.YarnWastePercent ?? 0;
        var lines = new List<DevHandoffLineDto>();
        foreach (var n in norms)
        {
            if (DevNorms.ToUnit(n.Grams, n.UnitCode, n.Precision) is { } qty)
            {
                lines.Add(new DevHandoffLineDto(n.ItemId, n.Code, n.Name, n.Symbol, qty, n.Grams, waste));
            }
            else
            {
                warnings.Add(DevNote.Of(DevNotes.UnitForProduction, n.Name, n.Symbol));
            }
        }

        // D741: фурнитура и упаковка модели — тоже в техкарту, без отходов.
        var yarnIds = lines.Select(l => l.ItemId).ToList();
        lines.AddRange(await (from a in db.DevModelAccessories.AsNoTracking()
                              join i in db.Items.AsNoTracking() on a.ItemId equals i.Id
                              join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                              where a.OrganizationId == org && a.ModelId == modelId && !yarnIds.Contains(a.ItemId)
                              orderby i.Name
                              select new DevHandoffLineDto(i.Id, i.Code, i.Name, u.Symbol, a.Quantity, 0m, 0m)).ToListAsync(ct));

        var stages = await (from o in db.DevModelOperations.AsNoTracking()
                            join t in db.DevOperationTypes.AsNoTracking() on o.OperationTypeId equals t.Id
                            where o.OrganizationId == org && o.ModelId == modelId && t.ProductionStage != null
                            select t.ProductionStage!.Value).Distinct().ToListAsync(ct);
        var route = stages.Select(s => (ProductionStage)s).Where(Enum.IsDefined)
            .Append(ProductionStage.Knitting).Append(ProductionStage.QualityControl).Distinct().OrderBy(s => s).ToList();
        return new Plan(lines, route, warnings);
    }
}
