using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmStageDto(
    long Id, long PipelineId, string Name, string Color, int Probability, CrmStageKind Kind, bool IsArchived, int Deals, byte[] RowVersion)
{
    public bool IsFinal => Kind != CrmStageKind.Open;
}

public sealed record CrmPipelineDto(long Id, string Name, bool IsArchived, IReadOnlyList<CrmStageDto> Stages, byte[] RowVersion);

public sealed record CrmReferenceDto(long Id, CrmReferenceKind Kind, string Name, bool IsArchived, byte[] RowVersion);

public sealed record CrmSettingsDto(
    IReadOnlyList<CrmPipelineDto> Pipelines, IReadOnlyList<CrmReferenceDto> Sources, IReadOnlyList<CrmReferenceDto> LossReasons, bool CanEdit);

/// <summary>
/// Настройки CRM (D602, D603): воронки и их этапы, источники обращений, причины отказа. Смотреть — crm.view, менять —
/// crm.settings. Готовая воронка и справочники появляются у организации при первом обращении к CRM (D609): так модуль
/// не правит создание организации в базе. Ничего не удаляется — архивируется; каждое изменение — в журнал.
/// </summary>
public sealed class CrmSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<CrmSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        await EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var pipelines = await PipelinesAsync(db, ctx.OrganizationId, includeArchived: true, ct);
        var refs = await db.CrmReferences.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId)
            .OrderBy(r => r.IsArchived).ThenBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new CrmReferenceDto(r.Id, r.Kind, r.Name, r.IsArchived, r.RowVersion)).ToListAsync(ct);
        return new CrmSettingsDto(pipelines, refs.Where(r => r.Kind == CrmReferenceKind.Source).ToList(),
            refs.Where(r => r.Kind == CrmReferenceKind.LossReason).ToList(), ctx.Permissions.Has(Permissions.CrmSettings));
    }

    public async Task<long> CreatePipelineAsync(string? name, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.settings.{ctx.OrganizationId}", ct);
        if (await db.CrmPipelines.CountAsync(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived, ct) >= CrmPipeline.MaxPipelines)
        {
            throw new BusinessRuleException("crm.pipeline.too_many", $"Воронок не больше {CrmPipeline.MaxPipelines}.");
        }

        var last = await db.CrmPipelines.Where(p => p.OrganizationId == ctx.OrganizationId).MaxAsync(p => (int?)p.SortOrder, ct) ?? 0;
        var pipeline = CrmPipeline.Create(ctx.OrganizationId, name, last + 10);
        await EnsurePipelineNameFreeAsync(ctx, pipeline.Name, null, ct);
        db.CrmPipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        AddDefaultStages(db, pipeline);
        Audit(ctx, nameof(CrmPipeline), pipeline.Id, null, pipeline.Name, "Воронка создана");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pipeline.Id;
    }

    public async Task RenamePipelineAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var pipeline = await LoadPipelineAsync(ctx, id, rowVersion, ct);
        var before = pipeline.Name;
        pipeline.Rename(name);
        await EnsurePipelineNameFreeAsync(ctx, pipeline.Name, id, ct);
        Audit(ctx, nameof(CrmPipeline), id, before, pipeline.Name, "Воронка переименована");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Воронка в архив: сделки в ней остаются, новые не заводятся. Последнюю действующую воронку в архив не отправить.</summary>
    public async Task ArchivePipelineAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var pipeline = await LoadPipelineAsync(ctx, id, rowVersion, ct);
        if (!await db.CrmPipelines.AnyAsync(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived && p.Id != id, ct))
        {
            throw new BusinessRuleException("crm.pipeline.last", "Это последняя действующая воронка — её нельзя отправить в архив.");
        }

        pipeline.Archive();
        Audit(ctx, nameof(CrmPipeline), id, pipeline.Name, "в архиве", "Воронка");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestorePipelineAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var pipeline = await LoadPipelineAsync(ctx, id, rowVersion, ct);
        await EnsurePipelineNameFreeAsync(ctx, pipeline.Name, id, ct);
        pipeline.Restore();
        Audit(ctx, nameof(CrmPipeline), id, "в архиве", pipeline.Name, "Воронка");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Новый рабочий этап — в конец рабочих, перед «выигрышем» и «проигрышем».</summary>
    public async Task<long> CreateStageAsync(long pipelineId, string? name, string? color, int probability, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var pipeline = await db.CrmPipelines.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pipelineId && p.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Воронка");
        pipeline.EnsureActive();
        var stages = await db.CrmStages.Where(s => s.PipelineId == pipelineId).ToListAsync(ct);
        if (stages.Count(s => !s.IsArchived) >= CrmStage.MaxStages)
        {
            throw new BusinessRuleException("crm.stage.too_many", $"Этапов в воронке не больше {CrmStage.MaxStages}.");
        }

        var stage = CrmStage.Create(ctx.OrganizationId, pipelineId, name, color, probability, CrmStageKind.Open,
            stages.Where(s => !s.IsFinal).Select(s => s.SortOrder).DefaultIfEmpty(0).Max() + 10);
        EnsureStageNameFree(stages, stage.Name, null);
        db.CrmStages.Add(stage);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, nameof(CrmStage), stage.Id, null, $"{stage.Name} ({stage.Probability} %)", $"Этап воронки «{pipeline.Name}»");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return stage.Id;
    }

    public async Task UpdateStageAsync(long id, string? name, string? color, int probability, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var stage = await LoadStageAsync(ctx, id, rowVersion, ct);
        var before = $"{stage.Name} ({stage.Color}, {stage.Probability} %)";
        try
        {
            stage.Set(name, color, probability);
            EnsureStageNameFree(await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == stage.PipelineId).ToListAsync(ct), stage.Name, id);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, nameof(CrmStage), id, before, $"{stage.Name} ({stage.Color}, {stage.Probability} %)", "Этап воронки");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Сдвиг рабочего этапа на одну позицию (-1 — левее, +1 — правее). Финальные этапы всегда в конце.</summary>
    public async Task MoveStageAsync(long id, int delta, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var stage = await db.CrmStages.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Этап воронки");
        var open = await db.CrmStages.Where(s => s.PipelineId == stage.PipelineId && !s.IsArchived && s.Kind == CrmStageKind.Open)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);
        var index = open.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            throw new BusinessRuleException("crm.stage.final", "Этапы «выигрыш» и «проигрыш» всегда в конце воронки.");
        }

        var target = Math.Clamp(index + Math.Sign(delta), 0, open.Count - 1);
        (open[index], open[target]) = (open[target], open[index]);
        for (var i = 0; i < open.Count; i++)
        {
            open[i].MoveTo((i + 1) * 10);
        }

        Audit(ctx, nameof(CrmStage), id, null, string.Join(" → ", open.Select(s => s.Name)), "Порядок этапов");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Рабочий этап в архив: сделки на нём остаются, новые на него не переводятся. Последний рабочий этап нужен.</summary>
    public async Task ArchiveStageAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var stage = await LoadStageAsync(ctx, id, rowVersion, ct);
        if (!stage.IsFinal && !await db.CrmStages.AnyAsync(s => s.PipelineId == stage.PipelineId && s.Id != id && !s.IsArchived && s.Kind == CrmStageKind.Open, ct))
        {
            throw new BusinessRuleException("crm.stage.last_open", "Это последний рабочий этап воронки — его нельзя отправить в архив.");
        }

        stage.Archive();
        Audit(ctx, nameof(CrmStage), id, stage.Name, "в архиве", "Этап воронки");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreStageAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var stage = await LoadStageAsync(ctx, id, rowVersion, ct);
        EnsureStageNameFree(await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == stage.PipelineId).ToListAsync(ct), stage.Name, id);
        stage.Restore();
        Audit(ctx, nameof(CrmStage), id, "в архиве", stage.Name, "Этап воронки");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<long> CreateReferenceAsync(CrmReferenceKind kind, string? name, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var last = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == kind).MaxAsync(r => (int?)r.SortOrder, ct) ?? 0;
        var item = CrmReference.Create(ctx.OrganizationId, kind, name, last + 10);
        await EnsureReferenceFreeAsync(ctx, kind, item.Name, null, ct);
        db.CrmReferences.Add(item);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, nameof(CrmReference), item.Id, null, item.Name, CrmReference.KindName(kind));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return item.Id;
    }

    public async Task RenameReferenceAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var item = await LoadReferenceAsync(ctx, id, rowVersion, ct);
        var before = item.Name;
        item.Rename(name);
        await EnsureReferenceFreeAsync(ctx, item.Kind, item.Name, id, ct);
        Audit(ctx, nameof(CrmReference), id, before, item.Name, CrmReference.KindName(item.Kind));
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetReferenceArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var item = await LoadReferenceAsync(ctx, id, rowVersion, ct);
        if (archived)
        {
            item.Archive();
        }
        else
        {
            await EnsureReferenceFreeAsync(ctx, item.Kind, item.Name, id, ct);
            item.Restore();
        }

        Audit(ctx, nameof(CrmReference), id, archived ? item.Name : "в архиве", archived ? "в архиве" : item.Name, CrmReference.KindName(item.Kind));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Готовая воронка «Продажи» и справочники источников и причин отказа (D609) — один раз, при первом обращении организации к CRM.
    /// Повторный вызов ничего не делает; одновременные — по очереди (блокировка организации).
    /// </summary>
    public static async Task EnsureDefaultsAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct)
    {
        if (await db.CrmPipelines.AnyAsync(p => p.OrganizationId == organizationId, ct))
        {
            return;
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.settings.{organizationId}", ct);
        if (await db.CrmPipelines.AnyAsync(p => p.OrganizationId == organizationId, ct))
        {
            return;
        }

        var pipeline = CrmPipeline.Create(organizationId, CrmPipeline.DefaultName, 10);
        db.CrmPipelines.Add(pipeline);
        var order = 0;
        foreach (var name in CrmReference.DefaultSources)
        {
            db.CrmReferences.Add(CrmReference.Create(organizationId, CrmReferenceKind.Source, name, order += 10));
        }

        order = 0;
        foreach (var name in CrmReference.DefaultLossReasons)
        {
            db.CrmReferences.Add(CrmReference.Create(organizationId, CrmReferenceKind.LossReason, name, order += 10));
        }

        await db.SaveChangesAsync(ct);
        AddDefaultStages(db, pipeline);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Воронки с этапами в порядке показа: рабочие по порядку, затем «выигрыш» и «проигрыш»; архивные — в конце.</summary>
    internal static async Task<IReadOnlyList<CrmPipelineDto>> PipelinesAsync(IKnitErpDbContext db, long organizationId, bool includeArchived, CancellationToken ct)
    {
        var pipelines = await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == organizationId && (includeArchived || !p.IsArchived))
            .OrderBy(p => p.IsArchived).ThenBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct);
        var stages = await db.CrmStages.AsNoTracking().Where(s => s.OrganizationId == organizationId && (includeArchived || !s.IsArchived)).ToListAsync(ct);
        var counts = await db.CrmDeals.AsNoTracking().Where(d => d.OrganizationId == organizationId)
            .GroupBy(d => d.StageId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return pipelines.Select(p => new CrmPipelineDto(p.Id, p.Name, p.IsArchived,
                Ordered(stages.Where(s => s.PipelineId == p.Id))
                    .Select(s => new CrmStageDto(s.Id, s.PipelineId, s.Name, s.Color, s.Probability, s.Kind, s.IsArchived, counts.GetValueOrDefault(s.Id), s.RowVersion))
                    .ToList(), p.RowVersion))
            .ToList();
    }

    internal static IEnumerable<CrmStage> Ordered(IEnumerable<CrmStage> stages) =>
        stages.OrderBy(s => s.IsArchived).ThenBy(s => s.Kind).ThenBy(s => s.SortOrder).ThenBy(s => s.Id);

    private static void AddDefaultStages(IKnitErpDbContext db, CrmPipeline pipeline)
    {
        var order = 0;
        foreach (var (name, color, probability, kind) in CrmStage.Defaults)
        {
            db.CrmStages.Add(CrmStage.Create(pipeline.OrganizationId, pipeline.Id, name, color, probability, kind, order += 10));
        }
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        await EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        return ctx;
    }

    private async Task<CrmPipeline> LoadPipelineAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var p = await db.CrmPipelines.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Воронка");
        return p.EnsureVersion(p.RowVersion, rowVersion);
    }

    private async Task<CrmStage> LoadStageAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var s = await db.CrmStages.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Этап воронки");
        return s.EnsureVersion(s.RowVersion, rowVersion);
    }

    private async Task<CrmReference> LoadReferenceAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var r = await db.CrmReferences.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Запись справочника");
        return r.EnsureVersion(r.RowVersion, rowVersion);
    }

    private async Task EnsurePipelineNameFreeAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.CrmPipelines.AnyAsync(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived && p.Name == name && p.Id != exceptId, ct))
        {
            db.DiscardChanges();
            throw new BusinessRuleException("crm.pipeline.duplicate", $"Воронка «{name}» уже есть.");
        }
    }

    private void EnsureStageNameFree(IEnumerable<CrmStage> stages, string name, long? exceptId)
    {
        if (stages.Any(s => !s.IsArchived && s.Id != exceptId && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            db.DiscardChanges();
            throw new BusinessRuleException("crm.stage.duplicate", $"Этап «{name}» в этой воронке уже есть.");
        }
    }

    private async Task EnsureReferenceFreeAsync(AccessContext ctx, CrmReferenceKind kind, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.CrmReferences.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && r.Kind == kind && !r.IsArchived && r.Name == name && r.Id != exceptId, ct))
        {
            db.DiscardChanges();
            throw new BusinessRuleException("crm.reference.duplicate", $"«{name}» уже есть в справочнике.");
        }
    }

    private void Audit(AccessContext ctx, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
