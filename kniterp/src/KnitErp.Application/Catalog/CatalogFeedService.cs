using System.Globalization;
using System.Text;
using System.Xml;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record CatalogFeedDto(
    long Id, string Name, string Prefix, string PriceType, string? Warehouse, string? Group, bool OnlyAvailable, bool IsActive, string CreatedBy,
    DateTime CreatedAtUtc, DateTime? LastRequestedAtUtc, byte[] RowVersion);

public sealed record CatalogFeedCommand(string? Name, long PriceTypeId, long? WarehouseId, long? GroupId, bool OnlyAvailable);

/// <summary>Ключ показывается один раз — после закрытия его не увидеть, только выпустить новый фид.</summary>
public sealed record CatalogFeedIssued(long Id, string Key);

public sealed record FeedOptionDto(long Id, string Name);

public sealed record CatalogFeedOptionsDto(IReadOnlyList<FeedOptionDto> PriceTypes, IReadOnlyList<FeedOptionDto> Warehouses, IReadOnlyList<FeedOptionDto> Groups);

/// <summary>Готовый фид: XML и сколько предложений в нём.</summary>
public sealed record CatalogFeedFile(long FeedId, byte[] Xml, int Offers);

/// <summary>
/// Фиды каталога в формате YML (D314): управление — Владелец или Администратор (изменение реквизитов организации) с правом видеть цены;
/// чтение — по секретной ссылке без входа (площадка забирает файл сама). В фиде: группы (категории), позиции с ценой выбранного вида —
/// товары, модификации (group_id основной позиции, характеристики — param) и комплекты (доступно — по компонентам); услуги и позиции без
/// цены не попадают. Наличие — остаток минус резерв подтверждённых заказов по выбранному складу или по всем.
/// </summary>
public sealed class CatalogFeedService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxOffers = 100_000;

    public async Task<IReadOnlyList<CatalogFeedDto>> ListAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        return await (from f in db.CatalogFeeds.AsNoTracking()
                      join t in db.PriceTypes.AsNoTracking() on f.PriceTypeId equals t.Id
                      join u in db.Users.AsNoTracking() on f.CreatedByUserId equals u.Id
                      where f.OrganizationId == ctx.OrganizationId
                      orderby f.IsActive descending, f.Name
                      select new CatalogFeedDto(f.Id, f.Name, f.Prefix, t.Name,
                          db.Warehouses.Where(w => w.Id == f.WarehouseId).Select(w => w.Name).FirstOrDefault(),
                          db.ItemGroups.Where(g => g.Id == f.GroupId).Select(g => g.Name).FirstOrDefault(),
                          f.OnlyAvailable, f.IsActive, u.DisplayName, f.CreatedAtUtc, f.LastRequestedAtUtc, f.RowVersion)).ToListAsync(ct);
    }

    public async Task<CatalogFeedOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        return new CatalogFeedOptionsDto(
            await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived).OrderBy(t => t.Name)
                .Select(t => new FeedOptionDto(t.Id, t.Name)).ToListAsync(ct),
            await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == org && !w.IsArchived).OrderBy(w => w.Name)
                .Select(w => new FeedOptionDto(w.Id, w.Name)).ToListAsync(ct),
            await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org && !g.IsArchived).OrderBy(g => g.Name)
                .Select(g => new FeedOptionDto(g.Id, g.Name)).ToListAsync(ct));
    }

    public async Task<CatalogFeedIssued> CreateAsync(CatalogFeedCommand cmd, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        if (!await db.PriceTypes.AnyAsync(t => t.Id == cmd.PriceTypeId && t.OrganizationId == org && !t.IsArchived, ct))
        {
            throw new NotFoundException("Вид цены");
        }

        if (cmd.WarehouseId is { } w && !await db.Warehouses.AnyAsync(x => x.Id == w && x.OrganizationId == org && !x.IsArchived, ct))
        {
            throw new NotFoundException("Склад");
        }

        if (cmd.GroupId is { } g && !await db.ItemGroups.AnyAsync(x => x.Id == g && x.OrganizationId == org && !x.IsArchived, ct))
        {
            throw new NotFoundException("Группа номенклатуры");
        }

        if (await db.CatalogFeeds.CountAsync(f => f.OrganizationId == org, ct) >= CatalogFeed.MaxPerOrganization)
        {
            throw new BusinessRuleException("catalog.feed.limit", "Не больше 10 фидов на организацию — выключенный фид можно включить снова.");
        }

        var (feed, key) = CatalogFeed.Create(org, cmd.Name, cmd.PriceTypeId, cmd.WarehouseId, cmd.GroupId, cmd.OnlyAvailable, ctx.UserId, clock.UtcNow);
        db.CatalogFeeds.Add(feed);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, feed.Id, null, $"{feed.Name}, ключ {feed.Prefix}…", "Фид каталога создан");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CatalogFeedIssued(feed.Id, key);
    }

    public async Task SetActiveAsync(long id, bool active, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var feed = await db.CatalogFeeds.SingleOrDefaultAsync(f => f.Id == id && f.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Фид каталога");
        feed.EnsureVersion(feed.RowVersion, rowVersion);
        feed.SetActive(active);
        Audit(ctx, feed.Id, null, active ? "включён" : "выключен", $"Фид каталога «{feed.Name}»");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Файл фида по ключу из ссылки — без пользователя. Неизвестный или выключенный ключ — null (ответ 404, без подсказки, что именно не так).
    /// </summary>
    public async Task<CatalogFeedFile?> BuildAsync(string? key, string fallbackUrl, CancellationToken ct = default)
    {
        if (!CatalogFeed.LooksLikeKey(key))
        {
            return null;
        }

        var hash = CatalogFeed.Hash(key!);
        var feed = await db.CatalogFeeds.SingleOrDefaultAsync(f => f.KeyHash == hash && f.IsActive, ct);
        if (feed is null)
        {
            return null;
        }

        var org = feed.OrganizationId;
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == org, ct);

        // Группы (категории) и отбор по группе с подгруппами.
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org && !g.IsArchived)
            .Select(g => new { g.Id, g.ParentId, g.Name }).ToListAsync(ct);
        HashSet<long>? groupFilter = null;
        if (feed.GroupId is { } root)
        {
            var children = groups.ToLookup(g => g.ParentId);
            var ids = new List<long> { root };
            for (var i = 0; i < ids.Count; i++)
            {
                ids.AddRange(children[ids[i]].Select(g => g.Id));
            }

            groupFilter = [.. ids];
        }

        // Позиции с ценой выбранного вида; услуги — не товар площадки.
        var priced = await (from i in db.Items.AsNoTracking()
                            join p in db.ItemPrices.AsNoTracking() on i.Id equals p.ItemId
                            where i.OrganizationId == org && !i.IsArchived && i.Type != ItemType.Service && p.PriceTypeId == feed.PriceTypeId && p.Price > 0
                            select new
                            {
                                i.Id, i.Code, i.Name, i.Type, i.Description, i.Article, i.GroupId, i.ParentItemId, i.OriginCountryName, i.WeightKg, i.Gtin,
                                p.Price,
                            }).Take(MaxOffers + 1).ToListAsync(ct);
        var parentsWithVariants = priced.Where(i => i.ParentItemId is not null).Select(i => i.ParentItemId!.Value).ToHashSet();
        var parentGroups = await db.Items.AsNoTracking().Where(i => parentsWithVariants.Contains(i.Id)).Select(i => new { i.Id, i.GroupId })
            .ToDictionaryAsync(x => x.Id, x => x.GroupId, ct);
        var rows = priced
            .Where(i => !parentsWithVariants.Contains(i.Id)) // продаются модификации, а не основная позиция
            .Select(i => new { Item = i, GroupId = i.ParentItemId is { } pid ? parentGroups.GetValueOrDefault(pid) ?? i.GroupId : i.GroupId })
            .Where(x => groupFilter is null || x.GroupId is { } gid && groupFilter.Contains(gid))
            .ToList();

        // Наличие: остаток − резерв по складу фида или по всем действующим складам.
        List<long> warehouseIds = feed.WarehouseId is { } wid
            ? [wid]
            : await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == org && !w.IsArchived).Select(w => w.Id).ToListAsync(ct);
        var stock = await db.StockMovements.AsNoTracking().Where(m => m.OrganizationId == org && warehouseIds.Contains(m.WarehouseId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var reserved = await Reservations.ByWarehouseItemAsync(db, org, warehouseIds, null, null, ct);
        var available = new Dictionary<long, decimal>(stock);
        foreach (var ((_, itemId), quantity) in reserved)
        {
            available[itemId] = available.GetValueOrDefault(itemId) - quantity;
        }

        var bundleIds = rows.Where(r => r.Item.Type == ItemType.Bundle).Select(r => r.Item.Id).ToList();
        var specs = bundleIds.Count == 0
            ? []
            : await (from c in db.BundleComponents.AsNoTracking()
                     join i in db.Items.AsNoTracking() on c.BundleItemId equals i.Id
                     join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                     where c.OrganizationId == org && bundleIds.Contains(c.BundleItemId)
                     select new BundleComponentSpec(c.BundleItemId, c.ComponentItemId, c.Quantity, u.Precision)).ToListAsync(ct);
        decimal Available(long id, ItemType type) =>
            type == ItemType.Bundle ? BundleExpansion.Available(id, specs, available) : Math.Max(0, available.GetValueOrDefault(id));

        var offers = rows.Select(r => new { r.Item, r.GroupId, Count = Available(r.Item.Id, r.Item.Type) })
            .Where(o => !feed.OnlyAvailable || o.Count > 0).Take(MaxOffers).ToList();
        var offerIds = offers.Select(o => o.Item.Id).ToHashSet();

        // Штрихкоды и характеристики модификаций — одним запросом каждое.
        var barcodes = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org).Select(b => new { b.ItemId, b.Code }).ToListAsync(ct))
            .Where(b => offerIds.Contains(b.ItemId)).ToLookup(b => b.ItemId, b => b.Code);
        var parameters = (await (from v in db.ItemCharacteristicValues.AsNoTracking()
                                 join c in db.Characteristics.AsNoTracking() on v.CharacteristicId equals c.Id
                                 where v.OrganizationId == org
                                 select new { v.ItemId, c.Name, v.Value }).ToListAsync(ct))
            .ToLookup(p => p.ItemId);

        if (feed.Touch(clock.UtcNow))
        {
            await db.SaveChangesAsync(ct);
        }

        var currency = organization.CountryCode switch
        {
            Countries.Kazakhstan => "KZT",
            Countries.Uzbekistan => "UZS",
            Countries.Belarus => "BYN",
            _ => "RUR",
        };
        var usedGroups = CategoryClosure(groups.Select(g => (g.Id, g.ParentId)).ToList(), offers.Select(o => o.GroupId));

        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, Async = true };
        await using (var x = XmlWriter.Create(buffer, settings))
        {
            await x.WriteStartDocumentAsync();
            x.WriteStartElement("yml_catalog");
            x.WriteAttributeString("date", clock.UtcNow.ToString("yyyy-MM-ddTHH:mm+00:00", CultureInfo.InvariantCulture));
            x.WriteStartElement("shop");
            x.WriteElementString("name", Cut(organization.ShortName, 20));
            x.WriteElementString("company", organization.FullName);
            x.WriteElementString("url", organization.WebsiteUrl ?? fallbackUrl);
            x.WriteElementString("platform", "knitERP");
            x.WriteStartElement("currencies");
            x.WriteStartElement("currency");
            x.WriteAttributeString("id", currency);
            x.WriteAttributeString("rate", "1");
            x.WriteEndElement();
            x.WriteEndElement();

            x.WriteStartElement("categories");
            foreach (var g in groups.Where(g => usedGroups.Contains(g.Id)).OrderBy(g => g.Id))
            {
                x.WriteStartElement("category");
                x.WriteAttributeString("id", g.Id.ToString(CultureInfo.InvariantCulture));
                if (g.ParentId is { } parent && usedGroups.Contains(parent))
                {
                    x.WriteAttributeString("parentId", parent.ToString(CultureInfo.InvariantCulture));
                }

                x.WriteString(g.Name);
                x.WriteEndElement();
            }

            x.WriteEndElement();

            x.WriteStartElement("offers");
            foreach (var o in offers)
            {
                var i = o.Item;
                x.WriteStartElement("offer");
                x.WriteAttributeString("id", i.Id.ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("available", o.Count > 0 ? "true" : "false");
                if (i.ParentItemId is { } pid)
                {
                    x.WriteAttributeString("group_id", pid.ToString(CultureInfo.InvariantCulture));
                }

                x.WriteElementString("name", i.Name);
                x.WriteElementString("price", i.Price.ToString("0.##", CultureInfo.InvariantCulture));
                x.WriteElementString("currencyId", currency);
                if (o.GroupId is { } gid && usedGroups.Contains(gid))
                {
                    x.WriteElementString("categoryId", gid.ToString(CultureInfo.InvariantCulture));
                }

                x.WriteElementString("vendorCode", i.Article ?? i.Code);
                x.WriteElementString("shop-sku", i.Code);
                foreach (var code in barcodes[i.Id].Concat(i.Gtin is { } gtin ? [gtin] : []).Distinct().Take(10))
                {
                    x.WriteElementString("barcode", code);
                }

                if (i.Description is { Length: > 0 } description)
                {
                    x.WriteElementString("description", description);
                }

                if (i.OriginCountryName is { Length: > 0 } country)
                {
                    x.WriteElementString("country_of_origin", country);
                }

                if (i.WeightKg is > 0 and var weight)
                {
                    x.WriteElementString("weight", weight.ToString("0.###", CultureInfo.InvariantCulture));
                }

                x.WriteElementString("count", decimal.Floor(o.Count).ToString(CultureInfo.InvariantCulture));
                foreach (var p in parameters[i.Id])
                {
                    x.WriteStartElement("param");
                    x.WriteAttributeString("name", p.Name);
                    x.WriteString(p.Value);
                    x.WriteEndElement();
                }

                x.WriteEndElement();
            }

            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
            await x.WriteEndDocumentAsync();
        }

        return new CatalogFeedFile(feed.Id, buffer.ToArray(), offers.Count);
    }

    /// <summary>Группы предложений вместе с их родителями — дерево категорий фида без пустых веток.</summary>
    private static HashSet<long> CategoryClosure(IReadOnlyList<(long Id, long? ParentId)> groups, IEnumerable<long?> used)
    {
        var parents = groups.ToDictionary(g => g.Id, g => g.ParentId);
        var result = new HashSet<long>();
        foreach (var start in used)
        {
            for (var id = start; id is { } g && parents.ContainsKey(g) && result.Add(g); id = parents[g])
            {
            }
        }

        return result;
    }

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct); // фид открывает цены

        if (currentUser.ApiScope is not null)
        {
            throw new BusinessRuleException("catalog.feed.via_api", "Фиды каталога настраиваются только в интерфейсе, не через API.");
        }

        return ctx;
    }

    private void Audit(AccessContext ctx, long id, string? before, string after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(CatalogFeed),
            id.ToString(CultureInfo.InvariantCulture), before, after, reason, currentUser.CorrelationId));
}
