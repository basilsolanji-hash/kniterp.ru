using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record SalesDiscountRuleDto(
    long Id, DiscountKind Kind, string Name, decimal Percent, decimal? MinOrderAmount, decimal? MinQuantity, long? ItemGroupId, string? ItemGroup,
    long? TagId, string? Tag, DateOnly ValidFrom, DateOnly? ValidTo, bool IsArchived, bool ValidToday, byte[] RowVersion)
{
    public string KindName => SalesDiscountRule.KindName(Kind);
}

public sealed record SalesDiscountOptionsDto(IReadOnlyList<KnitErp.Application.Structure.LookupDto> Groups, IReadOnlyList<KnitErp.Application.Structure.LookupDto> Tags);

public sealed record SalesDiscountSettingsDto(DiscountCombineMode Mode, IReadOnlyList<DiscountKind> Order, byte[]? RowVersion);

/// <summary>
/// Скидки и акции (D1106): правила по сумме заказа, по количеству и акции на группу позиций; порядок применения и суммирование.
/// Смотреть — «Продажи: просмотр» и «Цены и суммы»; менять — «Продажи: скидки и акции» и «Цены и суммы». Правила не удаляются — архив.
/// </summary>
public sealed class SalesDiscountService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRules = 1000;

    public async Task<IReadOnlyList<SalesDiscountRuleDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var rules = await db.SalesDiscountRules.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && (includeArchived || !r.IsArchived))
            .OrderBy(r => r.IsArchived).ThenBy(r => r.Kind).ThenByDescending(r => r.ValidFrom).ToListAsync(ct);
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var tags = await db.Tags.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var today = await SalesDiscounts.TodayAsync(db, ctx.OrganizationId, clock, ct);
        return rules.Select(r => new SalesDiscountRuleDto(r.Id, r.Kind, r.Name, r.Percent, r.MinOrderAmount, r.MinQuantity, r.ItemGroupId,
            r.ItemGroupId is { } g ? groups.GetValueOrDefault(g) : null, r.TagId, r.TagId is { } t ? tags.GetValueOrDefault(t) : null,
            r.ValidFrom, r.ValidTo, r.IsArchived, r.IsValidOn(today), r.RowVersion)).ToList();
    }

    /// <summary>Группы позиций (с путём «Свитеры / Зима») и теги — для формы правила.</summary>
    public async Task<SalesDiscountOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId && !g.IsArchived)
            .Select(g => new { g.Id, g.ParentId, g.Name }).ToListAsync(ct);
        var byId = groups.ToDictionary(g => g.Id);
        string Path(long id)
        {
            var parts = new List<string>();
            for (long? g = id; g is { } gid && byId.TryGetValue(gid, out var x) && parts.Count < 6; g = x.ParentId)
            {
                parts.Insert(0, x.Name);
            }

            return string.Join(" / ", parts);
        }

        var tags = await db.Tags.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && !t.IsArchived).OrderBy(t => t.Name)
            .Select(t => new KnitErp.Application.Structure.LookupDto(t.Id, t.Name)).ToListAsync(ct);
        return new SalesDiscountOptionsDto(groups.Select(g => new KnitErp.Application.Structure.LookupDto(g.Id, Path(g.Id))).OrderBy(g => g.Name).ToList(), tags);
    }

    public async Task<long> CreateAsync(SalesDiscountRuleTerms terms, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        await ValidateAsync(ctx, terms, ct);
        if (await db.SalesDiscountRules.CountAsync(r => r.OrganizationId == ctx.OrganizationId, ct) >= MaxRules)
        {
            throw new BusinessRuleException("sales.discount.too_many", $"Правил скидок не больше {MaxRules}.");
        }

        var rule = SalesDiscountRule.Create(ctx.OrganizationId, terms);
        db.SalesDiscountRules.Add(rule);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesDiscountChanged, rule.Id, null, Describe(rule), rule.Name);
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }

    public async Task UpdateAsync(long id, SalesDiscountRuleTerms terms, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var rule = await LoadAsync(ctx, id, ct);
        rule.EnsureVersion(rule.RowVersion, rowVersion);
        await ValidateAsync(ctx, terms, ct);
        var before = Describe(rule);
        rule.Update(terms);
        Audit(ctx, AuditActions.SalesDiscountChanged, id, before, Describe(rule), rule.Name);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var rule = await LoadAsync(ctx, id, ct);
        rule.EnsureVersion(rule.RowVersion, rowVersion);
        rule.SetArchived(archived);
        Audit(ctx, AuditActions.SalesDiscountChanged, id, archived ? "действует" : "в архиве", archived ? "в архиве" : "действует", rule.Name);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<SalesDiscountSettingsDto> SettingsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var s = await db.SalesDiscountSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        return new SalesDiscountSettingsDto(s?.Mode ?? DiscountCombineMode.Sequential, s?.Order ?? SalesDiscountSettings.DefaultOrder, s?.RowVersion);
    }

    public async Task SetSettingsAsync(DiscountCombineMode mode, IReadOnlyList<DiscountKind> order, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var s = await db.SalesDiscountSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        var before = s is null ? "по умолчанию" : $"{s.Mode}: {s.KindOrder}";
        if (s is null)
        {
            // Создать при первом сохранении: проверки — до добавления в контекст.
            var created = SalesDiscountSettings.Create(ctx.OrganizationId);
            created.Set(mode, order);
            db.SalesDiscountSettings.Add(created);
            s = created;
        }
        else
        {
            s.Set(mode, order);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.SalesDiscountChanged, nameof(SalesDiscountSettings),
            ctx.OrganizationId.ToString(), before, $"{s.Mode}: {s.KindOrder}", "порядок применения скидок", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    private async Task ValidateAsync(AccessContext ctx, SalesDiscountRuleTerms t, CancellationToken ct)
    {
        if (t.ItemGroupId is { } g && !await db.ItemGroups.AnyAsync(x => x.Id == g && x.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Группа");
        }

        if (t.TagId is { } tag && !await db.Tags.AnyAsync(x => x.Id == tag && x.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Тег");
        }
    }

    private static string Describe(SalesDiscountRule r) =>
        $"{SalesDiscountRule.KindName(r.Kind)} {r.Percent:0.##}%; порог {r.MinOrderAmount?.ToString("0.00") ?? r.MinQuantity?.ToString("0.###") ?? "—"}; "
        + $"группа {r.ItemGroupId?.ToString() ?? "—"}; тег {r.TagId?.ToString() ?? "—"}; {r.ValidFrom:dd.MM.yyyy}–{r.ValidTo?.ToString("dd.MM.yyyy") ?? "бессрочно"}";

    private async Task<SalesDiscountRule> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.SalesDiscountRules.SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Правило скидки");

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesDiscountEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(SalesDiscountRule), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}

/// <summary>
/// Применение скидок к строкам заказа (D1106): по каждой строке — части по видам (соглашение, по количеству, акция, по сумме заказа; в каждом
/// виде — наибольшая подходящая), затем сочетание по настройкам организации и ручная скидка последней. Итог и расшифровка — в строке.
/// </summary>
public static class SalesDiscounts
{
    public static async Task<IReadOnlyDictionary<long, (decimal Total, string? Sources)>> ComputeAsync(
        IKnitErpDbContext db, long org, SalesOrder order, CancellationToken ct)
    {
        if (order.Lines.Count == 0)
        {
            return new Dictionary<long, (decimal, string?)>();
        }

        var settings = await db.SalesDiscountSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == org, ct);
        var mode = settings?.Mode ?? DiscountCombineMode.Sequential;
        var kindOrder = settings?.Order ?? SalesDiscountSettings.DefaultOrder;
        var date = order.OrderDate;
        var tags = await db.TagLinks.AsNoTracking()
            .Where(l => l.OrganizationId == org && l.Entity == TaggedEntity.Counterparty && l.EntityId == order.CustomerId).Select(l => l.TagId).ToListAsync(ct);
        var rules = (await db.SalesDiscountRules.AsNoTracking()
                .Where(r => r.OrganizationId == org && !r.IsArchived && r.ValidFrom <= date && (r.ValidTo == null || r.ValidTo >= date)).ToListAsync(ct))
            .Where(r => r.TagId is null || tags.Contains(r.TagId.Value)).ToList();
        SalesAgreement? agreement = null;
        if (order.AgreementId is { } aid)
        {
            agreement = await db.SalesAgreements.AsNoTracking().SingleOrDefaultAsync(a => a.Id == aid && a.OrganizationId == org, ct);
            if (agreement is not null && !agreement.IsValidOn(date))
            {
                agreement = null;
            }
        }

        var itemIds = order.Lines.Select(l => l.ItemId).ToList();
        var itemGroups = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.GroupId, ct);
        var parents = rules.Any(r => r.ItemGroupId is not null)
            ? await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct)
            : [];
        var gross = order.Lines.Sum(l => Money.Round(l.Quantity * l.Price));

        var result = new Dictionary<long, (decimal, string?)>();
        foreach (var line in order.Lines)
        {
            var group = itemGroups.GetValueOrDefault(line.ItemId);
            var parts = new List<DiscountPart>();
            if (agreement is { DiscountPercent: > 0 })
            {
                parts.Add(new DiscountPart(DiscountKind.Agreement, $"Соглашение {agreement.Number}", agreement.DiscountPercent));
            }

            Best(DiscountKind.Quantity, r => line.Quantity >= r.MinQuantity && (r.ItemGroupId is null || InGroup(group, r.ItemGroupId.Value)));
            Best(DiscountKind.Promotion, r => r.ItemGroupId is { } g && InGroup(group, g));
            Best(DiscountKind.OrderAmount, r => gross >= r.MinOrderAmount);
            var (total, applied) = DiscountMath.Combine(parts, line.ManualDiscountPercent, mode, kindOrder);
            result[line.ItemId] = (total, applied.Count == 0 ? null : DiscountMath.Describe(applied));

            void Best(DiscountKind kind, Func<SalesDiscountRule, bool> fits)
            {
                if (rules.Where(r => r.Kind == kind && fits(r)).MaxBy(r => r.Percent) is { } rule)
                {
                    parts.Add(new DiscountPart(kind, rule.Name, rule.Percent));
                }
            }
        }

        return result;

        bool InGroup(long? itemGroup, long target)
        {
            // Группа позиции или её предок (группы — до 5 уровней).
            for (var g = itemGroup; g is not null; g = parents.GetValueOrDefault(g.Value))
            {
                if (g == target)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Пересчитать скидки черновика по правилам — до подтверждения автоматически при любом изменении строк и шапки.</summary>
    public static async Task ApplyAsync(IKnitErpDbContext db, long org, SalesOrder order, CancellationToken ct)
    {
        if (order.Status != SalesOrderStatus.Draft)
        {
            return;
        }

        foreach (var (itemId, d) in await ComputeAsync(db, org, order, ct))
        {
            order.SetLineDiscount(itemId, d.Total, d.Sources);
        }
    }

    public static async Task<DateOnly> TodayAsync(IKnitErpDbContext db, long org, IClock clock, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }
}
