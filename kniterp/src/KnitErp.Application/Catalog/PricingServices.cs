using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record PriceHistoryDto(DateTime ChangedAtUtc, string PriceType, decimal? OldPrice, decimal? NewPrice, string? User, PriceChangeSource Source);

/// <summary>
/// Торговые сведения позиции (D305): предмет расчёта, маркировка, GTIN, минимальная цена (null без права «Цены и суммы»)
/// и последние изменения цен (пусто без этого права).
/// </summary>
public sealed record ItemTradeDto(long ItemId, ItemType Type, ItemTradeDetails Trade, IReadOnlyList<PriceHistoryDto> History, bool CanEdit, bool CanSeePrices,
    byte[] RowVersion);

/// <summary>Предмет расчёта, маркировка «Честного ЗНАКа», минимальная цена и история цен позиции (D305).</summary>
public sealed class ItemTradeService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int HistoryRows = 50;

    public async Task<ItemTradeDto> GetAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var history = prices
            ? await (from h in db.ItemPriceHistory.AsNoTracking()
                     join t in db.PriceTypes.AsNoTracking() on h.PriceTypeId equals t.Id
                     join u in db.Users.AsNoTracking() on h.UserId equals (long?)u.Id into users
                     from u in users.DefaultIfEmpty()
                     where h.OrganizationId == ctx.OrganizationId && h.ItemId == itemId
                     orderby h.ChangedAtUtc descending, h.Id descending
                     select new PriceHistoryDto(h.ChangedAtUtc, t.Name, h.OldPrice, h.NewPrice, u == null ? null : u.DisplayName, h.Source))
                .Take(HistoryRows).ToListAsync(ct)
            : [];
        var trade = prices ? item.Trade : item.Trade with { MinPrice = null };
        return new ItemTradeDto(item.Id, item.Type, trade, history, ctx.Permissions.Has(Permissions.CatalogEdit) && !item.IsArchived, prices, item.RowVersion);
    }

    /// <summary>Минимальную цену меняет только тот, кто видит цены: без права «Цены и суммы» она остаётся прежней.</summary>
    public async Task SaveAsync(long itemId, ItemTradeDetails details, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        item.EnsureVersion(item.RowVersion, rowVersion);
        if (!ctx.Permissions.Has(Permissions.PriceView))
        {
            details = details with { MinPrice = item.MinPrice };
        }

        var changes = item.SetTrade(details);
        if (item.Gtin is { } gtin
            && await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && i.Gtin == gtin && i.Id != itemId)
                .Select(i => i.Code).FirstOrDefaultAsync(ct) is { } owner)
        {
            throw new BusinessRuleException("catalog.gtin.duplicate", $"GTIN {gtin} уже у позиции {owner}.");
        }

        foreach (var c in changes)
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), itemId.ToString(),
                c.Before, c.After, c.Field, currentUser.CorrelationId));
        }

        await db.SaveOrConflictAsync(ct);
    }
}

/// <summary>
/// Массовая переоценка (D306): отбор (группа с подгруппами, вид, поиск) и правило («± N %» к текущей цене вида или
/// «закупочная цена × коэффициент») с округлением. Сначала предпросмотр, затем применение — расчёт повторяется на сервере.
/// </summary>
public sealed record RevaluationRequest(
    long PriceTypeId, RevaluationMode Mode, decimal Value, PriceRounding Rounding = PriceRounding.Kopeck, long? GroupId = null, ItemKind? Kind = null,
    string? Search = null);

/// <summary>Строка предпросмотра: NewPrice null — пропущена (нет текущей или закупочной цены); BelowMinimum — новая цена ниже минимальной.</summary>
public sealed record RevaluationRowDto(long ItemId, string Code, string Name, decimal? OldPrice, decimal? NewPrice, bool BelowMinimum)
{
    public bool Changes => NewPrice is { } n && n != OldPrice;
}

public sealed record RevaluationPreviewDto(string Rule, string PriceType, int Total, int ToChange, int Skipped, int BelowMinimum, IReadOnlyList<RevaluationRowDto> Rows);

public sealed record RevaluationJournalDto(long Id, DateTime CreatedAtUtc, string PriceType, string Rule, int Items, string User);

public sealed class RevaluationService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxItems = 50_000;
    public const int PreviewRows = 500;
    public const int BatchSize = 500;

    public async Task<RevaluationPreviewDto> PreviewAsync(RevaluationRequest request, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var (type, rows) = await CalculateAsync(ctx, request, ct);
        return new RevaluationPreviewDto(Revaluation.Describe(request.Mode, request.Value, request.Rounding), type.Name, rows.Count,
            rows.Count(r => r.Changes), rows.Count(r => r.NewPrice is null), rows.Count(r => r.BelowMinimum),
            rows.OrderBy(r => r.NewPrice is null).ThenBy(r => r.Code).Take(PreviewRows).ToList());
    }

    /// <summary>Применение: новые цены, история с номером переоценки и запись журнала — одной транзакцией. Возвращает число изменённых цен.</summary>
    public async Task<int> ApplyAsync(RevaluationRequest request, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.revaluation.{ctx.OrganizationId}", ct);
        var (type, rows) = await CalculateAsync(ctx, request, ct);
        var changes = rows.Where(r => r.Changes).ToList();
        if (changes.Count == 0)
        {
            throw new BusinessRuleException("catalog.revaluation.nothing", "Переоценка ничего не меняет — проверьте отбор и правило.");
        }

        var rule = Revaluation.Describe(request.Mode, request.Value, request.Rounding);
        var journal = PriceRevaluation.Create(ctx.OrganizationId, type.Id, request.Mode, request.Value, request.Rounding, rule, changes.Count, ctx.UserId,
            clock.UtcNow);
        db.PriceRevaluations.Add(journal);
        await db.SaveChangesAsync(ct);
        foreach (var batch in changes.Chunk(BatchSize))
        {
            var ids = batch.Select(r => r.ItemId).ToList();
            var current = await db.ItemPrices.Where(p => p.OrganizationId == ctx.OrganizationId && p.PriceTypeId == type.Id && ids.Contains(p.ItemId))
                .ToDictionaryAsync(p => p.ItemId, ct);
            foreach (var row in batch)
            {
                if (current.TryGetValue(row.ItemId, out var price))
                {
                    price.Set(row.NewPrice!.Value);
                }
                else
                {
                    db.ItemPrices.Add(ItemPrice.Create(ctx.OrganizationId, row.ItemId, type.Id, row.NewPrice!.Value));
                }

                db.ItemPriceHistory.Add(ItemPriceHistory.Create(ctx.OrganizationId, row.ItemId, type.Id, row.OldPrice, row.NewPrice, clock.UtcNow, ctx.UserId,
                    PriceChangeSource.Revaluation, journal.Id));
            }

            await db.SaveChangesAsync(ct);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(PriceRevaluation),
            journal.Id.ToString(), null, $"{type.Name}: {rule}; позиций {changes.Count}", "Переоценка", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changes.Count;
    }

    public async Task<IReadOnlyList<RevaluationJournalDto>> JournalAsync(int take = 50, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        return await (from r in db.PriceRevaluations.AsNoTracking()
                      join t in db.PriceTypes.AsNoTracking() on r.PriceTypeId equals t.Id
                      join u in db.Users.AsNoTracking() on r.UserId equals u.Id
                      where r.OrganizationId == ctx.OrganizationId
                      orderby r.CreatedAtUtc descending, r.Id descending
                      select new RevaluationJournalDto(r.Id, r.CreatedAtUtc, t.Name, r.Rule, r.Items, u.DisplayName))
            .Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<(PriceType Type, List<RevaluationRowDto> Rows)> CalculateAsync(AccessContext ctx, RevaluationRequest request, CancellationToken ct)
    {
        Revaluation.Validate(request.Mode, request.Value);
        var type = await db.PriceTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.PriceTypeId && t.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Вид цены");
        if (type.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Вид цены «{type.Name}» в архиве.");
        }

        var query = db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && !i.IsArchived);
        if (request.Kind is { } kind)
        {
            var types = ItemTypes.OfKind(kind).ToArray();
            query = query.Where(i => types.Contains(i.Type));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            query = query.Where(i => i.Name.Contains(s) || i.Code.Contains(s) || (i.Article != null && i.Article.Contains(s)));
        }

        if (request.GroupId is { } gid)
        {
            var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId).Select(g => new { g.Id, g.ParentId }).ToListAsync(ct);
            if (groups.All(g => g.Id != gid))
            {
                throw new NotFoundException("Группа номенклатуры");
            }

            var children = groups.ToLookup(g => g.ParentId);
            var ids = new List<long> { gid };
            for (var i = 0; i < ids.Count; i++)
            {
                ids.AddRange(children[ids[i]].Select(g => g.Id));
            }

            query = query.Where(i => i.GroupId != null && ids.Contains(i.GroupId.Value));
        }

        var total = await query.CountAsync(ct);
        if (total > MaxItems)
        {
            throw new BusinessRuleException("catalog.revaluation.too_many", $"В отборе больше {MaxItems} позиций — уточните группу или поиск.");
        }

        var items = await query
            .GroupJoin(db.ItemPrices.AsNoTracking().Where(p => p.PriceTypeId == type.Id), i => i.Id, p => p.ItemId, (i, p) => new { i, p })
            .SelectMany(x => x.p.DefaultIfEmpty(), (x, p) => new { x.i.Id, x.i.Code, x.i.Name, x.i.PurchasePrice, x.i.MinPrice, Price = p == null ? (decimal?)null : p.Price })
            .ToListAsync(ct);
        var rows = items.Select(i =>
        {
            var newPrice = Revaluation.NewPrice(request.Mode, request.Value, request.Rounding, i.Price, i.PurchasePrice);
            return new RevaluationRowDto(i.Id, i.Code, i.Name, i.Price, newPrice, newPrice is { } n && TradeRules.BelowMinimum(n, i.MinPrice));
        }).ToList();
        return (type, rows);
    }
}
