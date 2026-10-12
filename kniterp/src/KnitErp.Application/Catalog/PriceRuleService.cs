using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record PriceRuleDto(long PriceTypeId, string PriceType, decimal Factor, PriceRounding Rounding, bool IsActive, bool Exists, byte[] RowVersion);

/// <summary>
/// Автоцены по правилу (D322): смотреть — «Цены и суммы», менять и пересчитывать — «Цены: переоценка и история» (catalog.price.edit).
/// Пересчёт пишет историю цен с источником «автоцена по правилу» и журнал.
/// </summary>
public sealed class PriceRuleService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<PriceRuleDto>> ListAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && !t.IsArchived).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);
        var rules = await db.PriceRules.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(r => r.PriceTypeId, ct);
        return types.Select(t => rules.TryGetValue(t.Id, out var r)
            ? new PriceRuleDto(t.Id, t.Name, r.Factor, r.Rounding, r.IsActive, true, r.RowVersion)
            : new PriceRuleDto(t.Id, t.Name, 2, PriceRounding.Ruble, false, false, [])).ToList();
    }

    /// <summary>Сохранить правило; включённое — сразу пересчитать цены всех позиций с закупочной ценой. Возвращает число изменённых цен.</summary>
    public async Task<int> SaveAsync(long priceTypeId, decimal factor, PriceRounding rounding, bool isActive, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceEdit, ct);
        var org = ctx.OrganizationId;
        var type = await db.PriceTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == priceTypeId && t.OrganizationId == org, ct) ?? throw new NotFoundException("Вид цены");
        var rule = await db.PriceRules.SingleOrDefaultAsync(r => r.PriceTypeId == priceTypeId && r.OrganizationId == org, ct);
        if (rule is null)
        {
            rule = PriceRule.Create(org, priceTypeId);
            db.PriceRules.Add(rule);
        }
        else
        {
            rule.EnsureVersion(rule.RowVersion, rowVersion);
        }

        try
        {
            rule.Set(factor, rounding, isActive, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogChanged, nameof(PriceRule), priceTypeId.ToString(), null,
            (isActive ? "включено: " : "выключено: ") + Revaluation.Describe(RevaluationMode.FromPurchase, factor, rounding), $"Автоцена «{type.Name}»",
            currentUser.CorrelationId));
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        var changed = isActive ? await AutoPrices.ApplyAsync(db, org, null, ctx.UserId, clock.UtcNow, ct) : 0;
        await tx.CommitAsync(ct);
        return changed;
    }
}

/// <summary>Пересчёт цен по включённым правилам (D322) — общий для карточки, Excel, прайса поставщика и кнопки «Пересчитать».</summary>
public static class AutoPrices
{
    /// <summary>itemIds null — все действующие позиции с закупочной ценой. Возвращает число изменённых цен; SaveChanges — здесь.</summary>
    public static async Task<int> ApplyAsync(IKnitErpDbContext db, long org, IReadOnlyCollection<long>? itemIds, long? userId, DateTime nowUtc, CancellationToken ct)
    {
        var rules = await db.PriceRules.AsNoTracking().Where(r => r.OrganizationId == org && r.IsActive).ToListAsync(ct);
        if (rules.Count == 0 || itemIds is { Count: 0 })
        {
            return 0;
        }

        var query = db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived && i.PurchasePrice > 0);
        if (itemIds is not null)
        {
            query = query.Where(i => itemIds.Contains(i.Id));
        }

        var items = await query.Select(i => new { i.Id, i.PurchasePrice }).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var typeIds = rules.Select(r => r.PriceTypeId).ToList();
        var changed = 0;
        foreach (var chunk in ids.Chunk(1000))
        {
            var prices = await db.ItemPrices.Where(p => p.OrganizationId == org && chunk.Contains(p.ItemId) && typeIds.Contains(p.PriceTypeId))
                .ToDictionaryAsync(p => (p.ItemId, p.PriceTypeId), ct);
            foreach (var item in items.Where(i => chunk.Contains(i.Id)))
            {
                foreach (var rule in rules)
                {
                    if (rule.PriceFor(item.PurchasePrice) is not { } price)
                    {
                        continue;
                    }

                    if (prices.TryGetValue((item.Id, rule.PriceTypeId), out var current))
                    {
                        if (current.Price == price)
                        {
                            continue;
                        }

                        db.ItemPriceHistory.Add(ItemPriceHistory.Create(org, item.Id, rule.PriceTypeId, current.Price, price, nowUtc, userId, PriceChangeSource.Rule));
                        current.Set(price);
                    }
                    else
                    {
                        db.ItemPrices.Add(ItemPrice.Create(org, item.Id, rule.PriceTypeId, price));
                        db.ItemPriceHistory.Add(ItemPriceHistory.Create(org, item.Id, rule.PriceTypeId, null, price, nowUtc, userId, PriceChangeSource.Rule));
                    }

                    changed++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return changed;
    }
}
