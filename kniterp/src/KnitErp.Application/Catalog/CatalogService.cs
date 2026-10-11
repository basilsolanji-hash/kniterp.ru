using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record UnitDto(long Id, string Code, string Name, string Symbol, byte Precision, int ItemCount, bool IsArchived, byte[] RowVersion);

public sealed record ItemDto(
    long Id, string Code, string Name, ItemType Type, long UnitId, string UnitSymbol, string? Description, bool IsArchived, byte[] RowVersion,
    long? VatRateId = null, string? VatRateName = null, string? Article = null, long? GroupId = null, decimal? SalePrice = null,
    long? ParentItemId = null, int Modifications = 0, long? PhotoId = null)
{
    public string TypeName => ItemTypes.Name(Type);
}

/// <summary>GroupId — группа с подгруппами (D79), 0 — позиции без группы. Search — по коду, названию, артикулу и штрихкоду.</summary>
public sealed record ItemFilter(ItemType? Type = null, string? Search = null, bool IncludeArchived = false, int Take = 500, long? GroupId = null, long? TagId = null);

public sealed record ItemListDto(IReadOnlyList<ItemDto> Items, int Total, bool CanEdit, bool CanArchive);

/// <summary>VatRateId — вид ставки НДС (D60); null — не указана. GroupId — группа новой позиции (D79), при изменении не используется.</summary>
public sealed record ItemCommand(string Code, string Name, ItemType Type, long UnitId, string? Description, long? VatRateId = null, long? GroupId = null);

public sealed record UnitCommand(string Code, string Name, string Symbol, byte Precision);

/// <summary>
/// Единицы измерения и номенклатура. Просмотр — catalog.item.view, изменение — catalog.item.edit,
/// архив — catalog.item.archive (матрица P0). Справочник общий для организации.
/// </summary>
public sealed class CatalogService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<UnitDto>> ListUnitsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var counts = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && !i.IsArchived)
            .GroupBy(i => i.UnitId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var units = await db.Units.AsNoTracking()
            .Where(u => u.OrganizationId == ctx.OrganizationId && (includeArchived || !u.IsArchived))
            .OrderBy(u => u.Name).ToListAsync(ct);
        return units.Select(u => new UnitDto(u.Id, u.Code, u.Name, u.Symbol, u.Precision, counts.GetValueOrDefault(u.Id), u.IsArchived, u.RowVersion)).ToList();
    }

    public async Task<long> CreateUnitAsync(UnitCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var unit = UnitOfMeasure.Create(ctx.OrganizationId, cmd.Code, cmd.Name, cmd.Symbol, cmd.Precision);
        if (await db.Units.AnyAsync(u => u.OrganizationId == ctx.OrganizationId && u.Code == unit.Code, ct))
        {
            throw new BusinessRuleException("catalog.unit.duplicate", $"Единица с кодом {unit.Code} уже есть.");
        }

        db.Units.Add(unit);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(UnitOfMeasure), unit.Id, null, $"{unit.Name} ({unit.Symbol})", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return unit.Id;
    }

    public async Task UpdateUnitAsync(long id, UnitCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var unit = await FindUnitAsync(ctx, id, ct);
        unit.EnsureVersion(unit.RowVersion, rowVersion);
        foreach (var c in unit.Update(cmd.Name, cmd.Symbol, cmd.Precision))
        {
            Audit(ctx, AuditActions.CatalogChanged, nameof(UnitOfMeasure), id, c.Before, c.After, c.Field);
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task ArchiveUnitAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var unit = await FindUnitAsync(ctx, id, ct);
        unit.EnsureVersion(unit.RowVersion, rowVersion);
        unit.Archive(await db.Items.CountAsync(i => i.OrganizationId == ctx.OrganizationId && i.UnitId == id && !i.IsArchived, ct));
        Audit(ctx, AuditActions.CatalogArchived, nameof(UnitOfMeasure), id, unit.Name, "в архиве", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<ItemListDto> ListItemsAsync(ItemFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var query = db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId);
        if (filter.TagId is { } tagId)
        {
            // D104: отбор по тегу — в запросе, чтобы находились и объекты за пределами первых строк списка.
            query = query.Where(i => db.TagLinks.Any(l => l.TagId == tagId && l.Entity == KnitErp.Domain.Tagging.TaggedEntity.Item && l.EntityId == i.Id));
        }

        if (!filter.IncludeArchived)
        {
            query = query.Where(i => !i.IsArchived);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(i => i.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(i => i.Name.Contains(s) || i.Code.Contains(s) || (i.Article != null && i.Article.Contains(s))
                                     || db.ItemBarcodes.Any(b => b.ItemId == i.Id && b.Code == s));
        }

        if (filter.GroupId == 0)
        {
            query = query.Where(i => i.GroupId == null);
        }
        else if (filter.GroupId is { } gid)
        {
            var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId)
                .Select(g => new { g.Id, g.ParentId }).ToListAsync(ct);
            var children = groups.ToLookup(g => g.ParentId);
            var ids = new List<long> { gid };
            for (var i = 0; i < ids.Count; i++)
            {
                ids.AddRange(children[ids[i]].Select(g => g.Id));
            }

            query = query.Where(i => i.GroupId != null && ids.Contains(i.GroupId.Value));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(i => i.Type).ThenBy(i => i.Name)
            .Take(Math.Clamp(filter.Take, 1, 2000))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i, u.Symbol })
            .GroupJoin(db.VatRates.AsNoTracking(), x => x.i.VatRateId, r => (long?)r.Id, (x, rates) => new { x.i, x.Symbol, rates })
            .SelectMany(x => x.rates.DefaultIfEmpty(), (x, r) => new ItemDto(
                x.i.Id, x.i.Code, x.i.Name, x.i.Type, x.i.UnitId, x.Symbol, x.i.Description, x.i.IsArchived, x.i.RowVersion,
                x.i.VatRateId, r == null ? null : r.Name, x.i.Article, x.i.GroupId, null, x.i.ParentItemId))
            .ToListAsync(ct);
        if (items.Count > 0)
        {
            // Модификации и основное фото (D82): число модификаций у основной позиции, первое фото — миниатюра в списке.
            var ids = items.Select(i => i.Id).ToList();
            var variants = await db.Items.AsNoTracking()
                .Where(i => i.OrganizationId == ctx.OrganizationId && i.ParentItemId != null && ids.Contains(i.ParentItemId.Value) && !i.IsArchived)
                .GroupBy(i => i.ParentItemId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var photos = await db.ItemPhotos.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && ids.Contains(p.ItemId))
                .GroupBy(p => p.ItemId).Select(g => new { g.Key, Id = g.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.Id).First() })
                .ToDictionaryAsync(x => x.Key, x => x.Id, ct);
            items = items.Select(i => i with { Modifications = variants.GetValueOrDefault(i.Id), PhotoId = photos.TryGetValue(i.Id, out var ph) ? ph : null })
                .ToList();
        }

        if (ctx.Permissions.Has(Permissions.PriceView) && items.Count > 0)
        {
            // Цена основного вида (D79) — только с правом видеть цены.
            var ids = items.Select(i => i.Id).ToList();
            var prices = await db.ItemPrices.AsNoTracking()
                .Where(p => p.OrganizationId == ctx.OrganizationId && ids.Contains(p.ItemId))
                .Join(db.PriceTypes.AsNoTracking().Where(t => t.IsDefault), p => p.PriceTypeId, t => t.Id, (p, t) => new { p.ItemId, p.Price })
                .ToDictionaryAsync(x => x.ItemId, x => x.Price, ct);
            items = items.Select(i => prices.TryGetValue(i.Id, out var price) ? i with { SalePrice = price } : i).ToList();
        }

        return new ItemListDto(items, total, ctx.Permissions.Has(Permissions.CatalogEdit), ctx.Permissions.Has(Permissions.CatalogArchive));
    }

    public async Task<long> CreateItemAsync(ItemCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await RequireActiveUnitAsync(ctx, cmd.UnitId, ct);
        var item = Item.Create(ctx.OrganizationId, cmd.Code, cmd.Name, cmd.Type, cmd.UnitId, cmd.Description, clock.UtcNow);
        await RequireActiveVatRateAsync(ctx, cmd.VatRateId, ct);
        item.SetVatRate(cmd.VatRateId);
        if (cmd.GroupId is { } gid)
        {
            var group = await db.ItemGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid && g.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Группа номенклатуры");
            if (group.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Группа «{group.Name}» в архиве.");
            }

            item.SetDetails(item.Details with { GroupId = gid });
        }

        await EnsureCodeFreeAsync(ctx, item.Code, null, ct);
        db.Items.Add(item);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(Item), item.Id, null, $"{item.Code} {item.Name}", ItemTypes.Name(item.Type));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return item.Id;
    }

    public async Task UpdateItemAsync(long id, ItemCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await FindItemAsync(ctx, id, ct);
        item.EnsureVersion(item.RowVersion, rowVersion);
        await RequireActiveUnitAsync(ctx, cmd.UnitId, ct);
        var oldUnit = item.UnitId;
        var oldVat = item.VatRateId;
        var changes = item.Update(cmd.Code, cmd.Name, cmd.Type, cmd.UnitId, cmd.Description);
        if (cmd.VatRateId != oldVat)
        {
            await RequireActiveVatRateAsync(ctx, cmd.VatRateId, ct);
            var vatChange = item.SetVatRate(cmd.VatRateId)!;
            var vatNames = await db.VatRates.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && (r.Id == oldVat || r.Id == cmd.VatRateId))
                .ToDictionaryAsync(r => r.Id, r => r.Name, ct);
            changes = [.. changes, vatChange with
            {
                Field = "Ставка НДС",
                Before = oldVat is { } b ? vatNames.GetValueOrDefault(b) : "не указана",
                After = cmd.VatRateId is { } a ? vatNames.GetValueOrDefault(a) : "не указана",
            }];
        }

        await EnsureCodeFreeAsync(ctx, item.Code, id, ct);
        var unitNames = await db.Units.AsNoTracking().Where(u => u.OrganizationId == ctx.OrganizationId && (u.Id == oldUnit || u.Id == cmd.UnitId))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.Symbol, ct);
        foreach (var c in changes)
        {
            var isUnit = c.Field == nameof(Item.UnitId);
            Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id,
                isUnit ? unitNames.GetValueOrDefault(c.Before ?? "") : c.Before,
                isUnit ? unitNames.GetValueOrDefault(c.After ?? "") : c.After,
                isUnit ? "Единица" : c.Field);
        }

        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Ставка НДС своей организации и не в архиве (чужая — «не найдено»).</summary>
    private async Task RequireActiveVatRateAsync(AccessContext ctx, long? vatRateId, CancellationToken ct)
    {
        if (vatRateId is not { } id)
        {
            return;
        }

        var rate = await db.VatRates.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Ставка НДС");
        if (rate.IsArchived)
        {
            throw new BusinessRuleException("vat.archived", $"Ставка «{rate.Name}» в архиве.");
        }
    }

    public async Task ArchiveItemAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var item = await FindItemAsync(ctx, id, ct);
        item.EnsureVersion(item.RowVersion, rowVersion);
        item.Archive(clock.UtcNow);
        Audit(ctx, AuditActions.CatalogArchived, nameof(Item), id, $"{item.Code} {item.Name}", "в архиве", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreItemAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var item = await FindItemAsync(ctx, id, ct);
        item.EnsureVersion(item.RowVersion, rowVersion);
        await RequireActiveUnitAsync(ctx, item.UnitId, ct);
        item.Restore();
        Audit(ctx, AuditActions.CatalogRestored, nameof(Item), id, "в архиве", $"{item.Code} {item.Name}", null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Стандартные единицы для новой организации. Вызывается при её создании, без пользователя-автора.</summary>
    internal static void SeedDefaultUnits(IKnitErpDbContext db, long organizationId)
    {
        foreach (var (code, name, symbol, precision) in UnitOfMeasure.Defaults)
        {
            db.Units.Add(UnitOfMeasure.Create(organizationId, code, name, symbol, precision));
        }
    }

    private async Task<UnitOfMeasure> FindUnitAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Units.SingleOrDefaultAsync(u => u.Id == id && u.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Единица измерения");

    private async Task<Item> FindItemAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Items.SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Номенклатура");

    private async Task RequireActiveUnitAsync(AccessContext ctx, long unitId, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().SingleOrDefaultAsync(u => u.Id == unitId && u.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Единица измерения");
        if (unit.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Единица «{unit.Name}» в архиве.");
        }
    }

    private async Task EnsureCodeFreeAsync(AccessContext ctx, string code, long? exceptId, CancellationToken ct)
    {
        var existing = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.Code == code && i.Id != exceptId)
            .Select(i => new { i.Name, i.IsArchived }).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            throw new BusinessRuleException("catalog.item.code_taken",
                existing.IsArchived
                    ? $"Код {code} занят архивной позицией «{existing.Name}». Верните её из архива или выберите другой код."
                    : $"Код {code} уже занят: «{existing.Name}».");
        }
    }

    private void Audit(AccessContext ctx, string action, string entityType, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entityType, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
