using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Настройки разработки (D702, D709): нормативы и включённость стадий, справочники (коллекции, переплетения, классы машин,
/// причины доработок, точки измерения). Смотреть — «Разработка: просмотр», менять — «Разработка: справочники и стадии».
/// </summary>
public sealed class DevSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<DevStageSettingDto>> StagesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        return plan.Stages.OrderBy(s => DevStages.IndexOf(s.Key))
            .Select(s => new DevStageSettingDto(s.Key, s.Value.PlanDays, s.Value.Enabled, DevStages.Optional.Contains(s.Key))).ToList();
    }

    public async Task SetStageAsync(DevStage stage, int planDays, bool isEnabled, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        try
        {
            var setting = await db.DevStageSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId && s.Stage == stage, ct);
            string before;
            if (setting is null)
            {
                before = $"{DevStages.DefaultPlanDays(stage)} дн., включена";
                // Сначала проверка, потом Add: отклонённая настройка не должна остаться в DbContext вкладки.
                setting = DevStageSetting.Create(ctx.OrganizationId, stage, planDays, isEnabled);
                db.DevStageSettings.Add(setting);
            }
            else
            {
                before = Describe(setting.PlanDays, setting.IsEnabled);
                setting.Set(planDays, isEnabled);
            }

            Audit(ctx, AuditActions.DevSettingsChanged, null, before, Describe(planDays, isEnabled), $"Стадия «{DevStages.Name(stage)}»");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public async Task<IReadOnlyList<DevLookupDto>> LookupsAsync(DevLookupKind kind, bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        return await db.DevLookups.AsNoTracking()
            .Where(l => l.OrganizationId == ctx.OrganizationId && l.Kind == kind && (includeArchived || !l.IsArchived))
            .OrderBy(l => l.IsArchived).ThenBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new DevLookupDto(l.Id, l.Kind, l.Name, l.Number, l.IsArchived, l.RowVersion)).ToListAsync(ct);
    }

    public async Task<long> AddLookupAsync(DevLookupKind kind, string? name, int? number, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        var order = await db.DevLookups.Where(l => l.OrganizationId == ctx.OrganizationId && l.Kind == kind).MaxAsync(l => (int?)l.SortOrder, ct) ?? 0;
        var item = DevLookup.Create(ctx.OrganizationId, kind, name, number, order + 1);
        await EnsureUniqueAsync(ctx, kind, item.Name, null, ct);
        db.DevLookups.Add(item);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, AuditActions.DevSettingsChanged, item.Id, null, item.Name, DevLookup.KindName(kind));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return item.Id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public async Task RenameLookupAsync(long id, string? name, int? number, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, item) = await LoadAsync(id, rowVersion, ct);
        try
        {
            var before = item.Name;
            item.Rename(name, number);
            await EnsureUniqueAsync(ctx, item.Kind, item.Name, item.Id, ct);
            Audit(ctx, AuditActions.DevSettingsChanged, id, before, item.Name, DevLookup.KindName(item.Kind));
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public async Task SetLookupArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, item) = await LoadAsync(id, rowVersion, ct);
        item.SetArchived(archived);
        Audit(ctx, AuditActions.DevSettingsChanged, id, archived ? "действует" : "в архиве", archived ? "в архиве" : "действует",
            $"{DevLookup.KindName(item.Kind)}: {item.Name}");
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<(AccessContext, DevLookup)> LoadAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        var item = await db.DevLookups.SingleOrDefaultAsync(l => l.Id == id && l.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Запись справочника");
        item.EnsureVersion(item.RowVersion, rowVersion);
        return (ctx, item);
    }

    private async Task EnsureUniqueAsync(AccessContext ctx, DevLookupKind kind, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.DevLookups.AnyAsync(l => l.OrganizationId == ctx.OrganizationId && l.Kind == kind && l.Name == name && l.Id != exceptId, ct))
        {
            throw new BusinessRuleException("dev.lookup.duplicate", $"«{name}» уже есть в справочнике.");
        }
    }

    private static string Describe(int days, bool enabled) => enabled ? $"{days} дн., включена" : $"{days} дн., отключена";

    private void Audit(AccessContext ctx, string action, long? id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, "DevSettings", id?.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
