using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Tagging;

public sealed record TagDto(long Id, string Name, TagColor Color, bool IsFavorite, bool IsArchived, int Count, byte[] RowVersion)
{
    public string Css => TagColors.Css(Color);
}

/// <summary>Отмеченный объект: что это, как называется и куда перейти.</summary>
public sealed record TaggedObjectDto(TaggedEntity Entity, long EntityId, string Title, string? Subtitle, string Href);

/// <summary>
/// Теги как в Finder (D103): цветные метки организации на позициях, контрагентах, заказах, складских документах, партиях и
/// инвентаризациях; поиск всего отмеченного одним тегом. Каждый вызов — со своим контекстом базы (как быстрый поиск): виджет тегов
/// стоит на любой карточке и не мешает операциям самой страницы. Ставить и снимать тег можно на объекте, который пользователь
/// видит; тег без прав на объект ничего не открывает — список отмеченного фильтруется правами и областью складов.
/// </summary>
public sealed class TagService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    public const int MaxTags = 500;
    public const int MaxObjects = 1000;

    private static readonly string[] StockViews = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    public async Task<IReadOnlyList<TagDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        return await db.Tags.AsNoTracking()
            .Where(t => t.OrganizationId == ctx.OrganizationId && (includeArchived || !t.IsArchived))
            .OrderBy(t => t.IsArchived).ThenBy(t => t.Name).Take(MaxTags)
            .Select(t => new TagDto(t.Id, t.Name, t.Color, t.IsFavorite, t.IsArchived,
                db.TagLinks.Count(l => l.TagId == t.Id), t.RowVersion))
            .ToListAsync(ct);
    }

    public async Task<long> CreateAsync(string? name, TagColor color, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        return await CreateCoreAsync(db, ctx, name, color, ct);
    }

    public async Task UpdateAsync(long id, string? name, TagColor color, bool favorite, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var tag = await LoadAsync(db, ctx, id, ct);
        tag.EnsureVersion(tag.RowVersion, rowVersion);
        var before = $"{tag.Name}, {TagColors.Name(tag.Color)}";
        var normalized = Tag.Normalize(name);
        if (await db.Tags.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.Name == normalized && t.Id != id, ct))
        {
            throw new BusinessRuleException("tag.duplicate", $"Тег «{normalized}» уже есть.");
        }

        tag.Update(name, color, favorite);
        Audit(db, ctx, AuditActions.TagChanged, id, before, $"{tag.Name}, {TagColors.Name(tag.Color)}", null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>В архив: тег пропадает из меню и с объектов; отметки сохраняются — восстановленный тег вернёт их.</summary>
    public async Task ArchiveAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var tag = await LoadAsync(db, ctx, id, ct);
        tag.EnsureVersion(tag.RowVersion, rowVersion);
        if (archived)
        {
            tag.Archive();
        }
        else
        {
            tag.Restore();
        }

        Audit(db, ctx, AuditActions.TagArchived, id, archived ? "Действует" : "В архиве", archived ? "В архиве" : "Действует", tag.Name);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Теги объекта (без архивных).</summary>
    public async Task<IReadOnlyList<TagDto>> ForEntityAsync(TaggedEntity entity, long entityId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        return await (from l in db.TagLinks.AsNoTracking()
                      join t in db.Tags.AsNoTracking() on l.TagId equals t.Id
                      where l.OrganizationId == ctx.OrganizationId && l.Entity == entity && l.EntityId == entityId && !t.IsArchived
                      orderby t.Name
                      select new TagDto(t.Id, t.Name, t.Color, t.IsFavorite, false, 0, t.RowVersion)).ToListAsync(ct);
    }

    /// <summary>Поставить тег; по названию — существующий или новый (как ввод метки в Finder).</summary>
    public async Task AssignAsync(TaggedEntity entity, long entityId, long? tagId, string? newName = null, TagColor newColor = TagColor.None,
        CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        await EnsureEntityAsync(db, ctx, entity, entityId, ct);
        long id;
        if (tagId is { } existing)
        {
            var tag = await LoadAsync(db, ctx, existing, ct);
            if (tag.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Тег «{tag.Name}» в архиве.");
            }

            id = existing;
        }
        else
        {
            var normalized = Tag.Normalize(newName);
            id = await db.Tags.Where(t => t.OrganizationId == ctx.OrganizationId && t.Name == normalized && !t.IsArchived)
                     .Select(t => (long?)t.Id).SingleOrDefaultAsync(ct)
                 ?? await CreateCoreAsync(db, ctx, newName, newColor, ct);
        }

        if (await db.TagLinks.AnyAsync(l => l.TagId == id && l.Entity == entity && l.EntityId == entityId, ct))
        {
            return;
        }

        db.TagLinks.Add(TagLink.Create(ctx.OrganizationId, id, entity, entityId, ctx.UserId, clock.UtcNow));
        Audit(db, ctx, AuditActions.TagAssigned, id, null, $"{TagColors.EntityName(entity)} #{entityId}", null);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(TaggedEntity entity, long entityId, long tagId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        await EnsureEntityAsync(db, ctx, entity, entityId, ct);
        var link = await db.TagLinks.SingleOrDefaultAsync(l => l.OrganizationId == ctx.OrganizationId && l.TagId == tagId && l.Entity == entity
                                                               && l.EntityId == entityId, ct);
        if (link is null)
        {
            return;
        }

        db.TagLinks.Remove(link);
        Audit(db, ctx, AuditActions.TagRemoved, tagId, $"{TagColors.EntityName(entity)} #{entityId}", null, null);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Всё отмеченное тегом — только объекты, которые пользователь вправе видеть.</summary>
    public async Task<(TagDto Tag, IReadOnlyList<TaggedObjectDto> Objects)> ObjectsAsync(long tagId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Тег");
        var links = await db.TagLinks.AsNoTracking().Where(l => l.TagId == tagId).OrderByDescending(l => l.Id).Take(MaxObjects * 2)
            .Select(l => new { l.Entity, l.EntityId }).ToListAsync(ct);
        var result = new List<TaggedObjectDto>();
        foreach (var group in links.GroupBy(l => l.Entity))
        {
            result.AddRange(await DescribeAsync(db, ctx, group.Key, group.Select(l => l.EntityId).ToList(), ct));
        }

        var count = links.Count;
        return (new TagDto(tag.Id, tag.Name, tag.Color, tag.IsFavorite, tag.IsArchived, count, tag.RowVersion),
            result.OrderBy(o => o.Entity).ThenBy(o => o.Title).Take(MaxObjects).ToList());
    }

    /// <summary>Теги для быстрого поиска: «#сроч» или «сроч» находит «Срочно».</summary>
    public async Task<IReadOnlyList<TagDto>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var text = Tag.Normalize(query);
        if (text.Length == 0)
        {
            return [];
        }

        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        return await db.Tags.AsNoTracking()
            .Where(t => t.OrganizationId == ctx.OrganizationId && !t.IsArchived && t.Name.Contains(text))
            .OrderBy(t => t.Name).Take(8)
            .Select(t => new TagDto(t.Id, t.Name, t.Color, t.IsFavorite, false, db.TagLinks.Count(l => l.TagId == t.Id), t.RowVersion))
            .ToListAsync(ct);
    }

    private async Task<long> CreateCoreAsync(IKnitErpDbContext db, AccessContext ctx, string? name, TagColor color, CancellationToken ct)
    {
        var normalized = Tag.Normalize(name);
        if (await db.Tags.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.Name == normalized, ct))
        {
            throw new BusinessRuleException("tag.duplicate", $"Тег «{normalized}» уже есть (возможно, в архиве).");
        }

        if (await db.Tags.CountAsync(t => t.OrganizationId == ctx.OrganizationId, ct) >= MaxTags)
        {
            throw new BusinessRuleException("tag.limit", $"Тегов в организации — не больше {MaxTags}.");
        }

        var tag = Tag.Create(ctx.OrganizationId, name, color, ctx.UserId, clock.UtcNow);
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        Audit(db, ctx, AuditActions.TagCreated, tag.Id, null, $"{tag.Name}, {TagColors.Name(tag.Color)}", null);
        await db.SaveChangesAsync(ct);
        return tag.Id;
    }

    /// <summary>Объект своей организации и доступный пользователю; иначе «не найдено» — тег не раскрывает чужое.</summary>
    private static async Task EnsureEntityAsync(IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, long id, CancellationToken ct)
    {
        if ((await DescribeAsync(db, ctx, entity, [id], ct)).Count == 0)
        {
            throw new NotFoundException("Объект");
        }
    }

    private static async Task<IReadOnlyList<TaggedObjectDto>> DescribeAsync(
        IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var has = ctx.Permissions.Has;
        switch (entity)
        {
            case TaggedEntity.Item when has(Permissions.CatalogView):
                return (await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id))
                        .Select(i => new { i.Id, i.Code, i.Name }).ToListAsync(ct))
                    .Select(i => new TaggedObjectDto(entity, i.Id, $"{i.Code} — {i.Name}", null, $"catalog/items/{i.Id}")).ToList();
            case TaggedEntity.Counterparty when has(Permissions.CatalogView):
                return (await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && ids.Contains(c.Id))
                        .Select(c => new { c.Id, c.Name, c.Inn }).ToListAsync(ct))
                    .Select(c => new TaggedObjectDto(entity, c.Id, c.Name, c.Inn is null ? null : "ИНН " + c.Inn,
                        $"counterparties?search={Uri.EscapeDataString(c.Name)}")).ToList();
            case TaggedEntity.SalesOrder when has(Permissions.SalesView):
                return (await (from o in db.SalesOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.CustomerId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"sales-orders/{o.Id}")).ToList();
            case TaggedEntity.PurchaseOrder when has(Permissions.PurchaseView):
                return (await (from o in db.PurchaseOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.SupplierId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"purchase-orders/{o.Id}")).ToList();
            case TaggedEntity.StockDocument when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.StockDocuments.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id)
                                    && (visible == null || visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value))))
                        .Select(d => new { d.Id, d.Number, d.Kind, d.DocumentDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"{StockDocument.KindName(d.Kind)} {d.Number}", d.DocumentDate.ToString("dd.MM.yyyy"),
                        $"stock-documents/{d.Id}")).ToList();
            }

            case TaggedEntity.InventoryCount when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.InventoryCounts.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id) && (visible == null || visible.Contains(d.WarehouseId)))
                        .Select(d => new { d.Id, d.Number, d.CountDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"Инвентаризация {d.Number}", d.CountDate.ToString("dd.MM.yyyy"), $"inventory/{d.Id}"))
                    .ToList();
            }

            case TaggedEntity.StockBatch when StockViews.Any(has):
                return (await (from b in db.StockBatches.AsNoTracking()
                               join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                               where b.OrganizationId == org && ids.Contains(b.Id)
                               select new { b.Id, b.Number, b.Color, b.DyeLot, i.Code }).ToListAsync(ct))
                    .Select(b => new TaggedObjectDto(entity, b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), b.Code,
                        $"stock-batches?search={Uri.EscapeDataString(b.Number)}")).ToList();
            default:
                return [];
        }
    }

    private static async Task<Tag> LoadAsync(IKnitErpDbContext db, AccessContext ctx, long id, CancellationToken ct) =>
        await db.Tags.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Тег");

    private AccessGuard Guard(IKnitErpDbContext db) => new(db, currentUser, clock);

    private void Audit(IKnitErpDbContext db, AccessContext ctx, string action, long tagId, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Tag), tagId.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
