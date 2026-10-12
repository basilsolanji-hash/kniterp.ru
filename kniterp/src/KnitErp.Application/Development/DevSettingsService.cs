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

    /// <summary>Настройки автоматизации и шаблоны задания по категориям (D790–D795).</summary>
    public async Task<DevAutomationDto> AutomationAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct) ?? DevSettings.Defaults(org);
        var users = await (from mem in db.OrganizationMembers.AsNoTracking()
                           join u in db.Users.AsNoTracking() on mem.UserId equals u.Id
                           where mem.OrganizationId == org && mem.Status == KnitErp.Domain.Access.MembershipStatus.Active
                           orderby u.DisplayName
                           select new DevOptionDto(u.Id, u.DisplayName)).ToListAsync(ct);
        var names = users.ToDictionary(u => u.Id, u => u.Name);
        var templates = await db.DevCategoryTemplates.AsNoTracking().Where(t => t.OrganizationId == org).ToListAsync(ct);
        return new DevAutomationDto(
            DevModel.Roles.Select(r => new DevAssigneeDto(r, settings.DefaultOf(r), settings.DefaultOf(r) is { } u ? names.GetValueOrDefault(u) : null)).ToList(),
            settings.AutoLaunchDate, settings.AutoAdvance,
            Enum.GetValues<DevCategory>().Select(c => templates.FirstOrDefault(t => t.Category == c) is { } t
                ? new DevCategoryTemplateDto(c, t.SizeRange, t.ColorRange, t.Composition, t.Labeling, t.Care)
                : new DevCategoryTemplateDto(c, null, null, null, null, null)).ToList(),
            users, ctx.Permissions.Has(Permissions.DevSettings));
    }

    public async Task SaveAutomationAsync(IReadOnlyDictionary<DevRole, long?> defaults, bool autoLaunchDate, bool autoAdvance, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        var org = ctx.OrganizationId;
        var check = defaults.Values.OfType<long>().Distinct().ToList();
        if (check.Count > 0 && await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org && check.Contains(m.UserId)
                && m.Status == KnitErp.Domain.Access.MembershipStatus.Active, ct) != check.Count)
        {
            throw new BusinessRuleException("dev.model.assignee", "Исполнитель должен быть активным пользователем организации.");
        }

        try
        {
            var settings = await db.DevSettings.SingleOrDefaultAsync(x => x.OrganizationId == org, ct);
            var isNew = settings is null;
            settings ??= DevSettings.Defaults(org);
            settings.Set(defaults, autoLaunchDate, autoAdvance);
            if (isNew)
            {
                db.DevSettings.Add(settings);
            }

            Audit(ctx, AuditActions.DevSettingsChanged, null, null,
                $"исполнители по умолчанию: {defaults.Values.Count(v => v is not null)}, дата запуска — {(autoLaunchDate ? "по нормативам" : "вручную")}, "
                + $"автопереход — {(autoAdvance ? "включён" : "выключен")}", "Автоматизация разработки");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Настройки цеха и печати (D719): допуск факта к плану, разделы показателя заполнения, формат этикеток.</summary>
    public async Task<DevShopSettingsDto> ShopSettingsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var s = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct) ?? DevSettings.Defaults(ctx.OrganizationId);
        return new DevShopSettingsDto(s.FactTolerancePercent,
            DevCompleteness.SectionNames.Select((n, i) => new DevSectionDto(n, (s.CompletenessSections & (1 << i)) != 0)).ToList(),
            s.LabelWidthMm, s.LabelHeightMm, s.LabelsOnA4, ctx.Permissions.Has(Permissions.DevSettings));
    }

    public async Task SaveShopSettingsAsync(int factTolerancePercent, IReadOnlyCollection<string> sections, int labelWidthMm, int labelHeightMm, bool labelsOnA4,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        var org = ctx.OrganizationId;
        var mask = DevCompleteness.SectionNames.Select((n, i) => sections.Contains(n) ? 1 << i : 0).Sum();
        try
        {
            var settings = await db.DevSettings.SingleOrDefaultAsync(x => x.OrganizationId == org, ct);
            var isNew = settings is null;
            settings ??= DevSettings.Defaults(org);
            var before = $"допуск {settings.FactTolerancePercent} %, разделов {BitCount(settings.CompletenessSections)}, этикетка "
                         + (settings.LabelsOnA4 ? "A4" : $"{settings.LabelWidthMm}×{settings.LabelHeightMm} мм");
            settings.SetShop(factTolerancePercent, mask, labelWidthMm, labelHeightMm, labelsOnA4);
            if (isNew)
            {
                db.DevSettings.Add(settings);
            }

            Audit(ctx, AuditActions.DevSettingsChanged, null, before,
                $"допуск {factTolerancePercent} %, разделов {BitCount(mask)}, этикетка " + (labelsOnA4 ? "A4" : $"{labelWidthMm}×{labelHeightMm} мм"), "Цех и печать");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    public async Task SaveTemplateAsync(DevCategoryTemplateDto template, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        try
        {
            var t = await db.DevCategoryTemplates.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId && x.Category == template.Category, ct);
            var isNew = t is null;
            t ??= DevCategoryTemplate.Create(ctx.OrganizationId, template.Category);
            t.Set(template.SizeRange, template.ColorRange, template.Composition, template.Labeling, template.Care);
            if (isNew)
            {
                db.DevCategoryTemplates.Add(t);
            }

            Audit(ctx, AuditActions.DevSettingsChanged, null, null, t.IsEmpty ? "очищен" : "сохранён", $"Шаблон задания: {DevModel.CategoryName(template.Category)}");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private static string Describe(int days, bool enabled) => enabled ? $"{days} дн., включена" : $"{days} дн., отключена";

    private void Audit(AccessContext ctx, string action, long? id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, "DevSettings", id?.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
