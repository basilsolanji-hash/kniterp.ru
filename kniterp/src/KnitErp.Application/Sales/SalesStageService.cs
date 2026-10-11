using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>D110: правило «событие → этап»; StageId = null — событие этап не меняет.</summary>
public sealed record StageRuleDto(StageTrigger Trigger, string Event, long? StageId);

public sealed record SalesStageDto(long Id, string Name, string Color, int SortOrder, bool IsArchived, int Orders, byte[] RowVersion);

/// <summary>
/// Этапы заказов покупателей (D75): список, добавление, переименование и цвет, порядок, архив. Смотреть — «Продажи: просмотр»,
/// менять — «Продажи: заказы и оплаты». Этап в заказе меняет <see cref="SalesService.SetOrderStageAsync"/>.
/// </summary>
public sealed class SalesStageService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<SalesStageDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var counts = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId && o.StageId != null)
            .GroupBy(o => o.StageId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var stages = await db.SalesOrderStages.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && (includeArchived || !s.IsArchived))
            .OrderBy(s => s.IsArchived).ThenBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);
        return stages.Select(s => new SalesStageDto(s.Id, s.Name, s.Color, s.SortOrder, s.IsArchived, counts.GetValueOrDefault(s.Id), s.RowVersion))
            .ToList();
    }

    public async Task<long> CreateAsync(string? name, string? color, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var count = await db.SalesOrderStages.CountAsync(s => s.OrganizationId == ctx.OrganizationId && !s.IsArchived, ct);
        if (count >= SalesOrderStage.MaxStages)
        {
            throw new BusinessRuleException("sales.stage.too_many", $"Этапов не больше {SalesOrderStage.MaxStages}.");
        }

        var last = await db.SalesOrderStages.Where(s => s.OrganizationId == ctx.OrganizationId).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0;
        var stage = SalesOrderStage.Create(ctx.OrganizationId, name, color, last + 10);
        await EnsureFreeAsync(ctx, stage.Name, null, ct);
        db.SalesOrderStages.Add(stage);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, stage.Id, null, $"{stage.Name} ({stage.Color})", "Этап заказа покупателя");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return stage.Id;
    }

    public async Task UpdateAsync(long id, string? name, string? color, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, stage) = await LoadAsync(id, rowVersion, ct);
        var before = $"{stage.Name} ({stage.Color})";
        stage.Set(name, color);
        await EnsureFreeAsync(ctx, stage.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, id, before, $"{stage.Name} ({stage.Color})", "Этап заказа покупателя");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Сдвиг на одну позицию вверх (-1) или вниз (+1) среди действующих этапов.</summary>
    public async Task MoveAsync(long id, int delta, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var stages = await db.SalesOrderStages.Where(s => s.OrganizationId == ctx.OrganizationId && !s.IsArchived)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);
        var index = stages.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            throw new NotFoundException("Этап заказа");
        }

        var target = Math.Clamp(index + Math.Sign(delta), 0, stages.Count - 1);
        (stages[index], stages[target]) = (stages[target], stages[index]);
        for (var i = 0; i < stages.Count; i++)
        {
            stages[i].MoveTo((i + 1) * 10);
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, stage) = await LoadAsync(id, rowVersion, ct);
        stage.Archive();
        Audit(ctx, AuditActions.CatalogArchived, id, stage.Name, "в архиве", "Этап заказа покупателя");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, stage) = await LoadAsync(id, rowVersion, ct);
        await EnsureFreeAsync(ctx, stage.Name, id, ct);
        stage.Restore();
        Audit(ctx, AuditActions.CatalogRestored, id, "в архиве", stage.Name, "Этап заказа покупателя");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>D110: правила автоматической смены этапа — по всем событиям, в порядке процесса.</summary>
    public async Task<IReadOnlyList<StageRuleDto>> RulesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var rules = await db.SalesStageRules.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(r => r.Trigger, ct);
        return SalesStageAutomation.All.Select(t => new StageRuleDto(t, SalesStageAutomation.Name(t), rules.GetValueOrDefault(t)?.StageId)).ToList();
    }

    /// <summary>D110: задать этап для события (null — не менять этап по этому событию).</summary>
    public async Task SetRuleAsync(StageTrigger trigger, long? stageId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        string? stageName = null;
        if (stageId is { } sid)
        {
            var stage = await db.SalesOrderStages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid && x.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Этап заказа");
            if (stage.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Этап «{stage.Name}» в архиве.");
            }

            stageName = stage.Name;
        }

        var rule = await db.SalesStageRules.SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Trigger == trigger, ct);
        var before = rule?.StageId is { } old
            ? await db.SalesOrderStages.AsNoTracking().Where(x => x.Id == old).Select(x => x.Name).SingleOrDefaultAsync(ct)
            : null;
        if (rule is null)
        {
            db.SalesStageRules.Add(SalesStageRule.Create(ctx.OrganizationId, trigger, stageId));
        }
        else
        {
            rule.SetStage(stageId);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(SalesStageRule),
            ((byte)trigger).ToString(), before ?? "не менять", stageName ?? "не менять", $"Автосмена этапа: {SalesStageAutomation.Name(trigger)}",
            currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Этапы по умолчанию для новой организации. Вызывается при её создании.</summary>
    internal static void SeedDefaults(IKnitErpDbContext db, long organizationId)
    {
        var order = 0;
        foreach (var (name, color) in SalesOrderStage.Defaults)
        {
            db.SalesOrderStages.Add(SalesOrderStage.Create(organizationId, name, color, order += 10));
        }
    }

    private async Task<(AccessContext, SalesOrderStage)> LoadAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var stage = await db.SalesOrderStages.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Этап заказа");
        return (ctx, stage.EnsureVersion(stage.RowVersion, rowVersion));
    }

    private async Task EnsureFreeAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.SalesOrderStages.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && !s.IsArchived && s.Name == name && s.Id != exceptId, ct))
        {
            throw new BusinessRuleException("sales.stage.duplicate", $"Этап «{name}» уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(SalesOrderStage), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
