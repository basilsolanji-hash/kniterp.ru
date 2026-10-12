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

/// <summary>
/// GroupId — группа с подгруппами (D79), 0 — позиции без группы. Search — по коду, названию, артикулу и штрихкоду.
/// Kind — «Товары / Услуги» (D300).
/// </summary>
/// <summary>Вычисляемый столбец поиска позиций: UPPER(код|наименование|артикул) с двоичным сравнением (сессия 4).</summary>
public static class ItemSearch
{
    public const string Column = "SearchKey";
}

/// <summary>Отбор списка номенклатуры. Skip/Take — серверная страница (сессия 4): список на 50 000 позиций не читается целиком.</summary>
public sealed record ItemFilter(
    ItemType? Type = null, string? Search = null, bool IncludeArchived = false, int Take = 500, long? GroupId = null, long? TagId = null,
    ItemKind? Kind = null, int Skip = 0);

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

    /// <summary>
    /// Позиции по отбору списка — в том же порядке, что и список (D318, «все по отбору» для массового изменения и этикеток).
    /// </summary>
    public async Task<IReadOnlyList<long>> ListIdsAsync(ItemFilter filter, int max, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var query = await FilterAsync(ctx, filter, ct);
        return await query.OrderBy(i => i.Type).ThenBy(i => i.Name).ThenBy(i => i.Id).Select(i => i.Id).Take(Math.Max(1, max)).ToListAsync(ct);
    }

    /// <summary>
    /// D320: похожие действующие позиции для подсказки при создании — то же наименование (без учёта регистра) или наименование, содержащее
    /// введённое (от 5 знаков); не больше 5. Не запрет: одинаковые названия у разных позиций бывают.
    /// </summary>
    public async Task<IReadOnlyList<ItemDto>> SimilarAsync(string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var n = (name ?? string.Empty).Trim();
        if (n.Length < 3)
        {
            return [];
        }

        var u = n.ToUpperInvariant();
        var exact = "|" + u + "|";
        var query = db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && !i.IsArchived && i.ParentItemId == null);
        query = n.Length >= 5
            ? query.Where(i => EF.Property<string>(i, ItemSearch.Column).Contains(exact) || EF.Property<string>(i, ItemSearch.Column).Contains(u))
            : query.Where(i => EF.Property<string>(i, ItemSearch.Column).Contains(exact));
        return await query.OrderBy(i => i.Name).Take(5)
            .Select(i => new ItemDto(i.Id, i.Code, i.Name, i.Type, i.UnitId, "", i.Description, i.IsArchived, i.RowVersion, i.VatRateId, null, i.Article, i.GroupId,
                null, i.ParentItemId))
            .ToListAsync(ct);
    }

    /// <summary>Отбор списка номенклатуры: организация, архив, тип, вид, поиск, группа с подгруппами, тег.</summary>
    private async Task<IQueryable<Item>> FilterAsync(AccessContext ctx, ItemFilter filter, CancellationToken ct)
    {
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

        if (filter.Kind is { } kind)
        {
            var kindTypes = ItemTypes.OfKind(kind).ToArray();
            query = query.Where(i => kindTypes.Contains(i.Type));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            // Штрихкод — точное совпадение по индексу (организация, код): позиции находятся одним поиском, а не подзапросом на каждую строку.
            var byBarcode = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && b.Code == s)
                .Select(b => b.ItemId).ToListAsync(ct);
            // Код, наименование, артикул — по вычисляемому столбцу поиска (верхний регистр, двоичное сравнение): на 50 000 позиций
            // ~40 мс вместо ~700 мс у LIKE по трём столбцам; регистр не важен (замер сессии 4, CatListPerformanceTests).
            var u = s.ToUpperInvariant();
            query = query.Where(i => EF.Property<string>(i, ItemSearch.Column).Contains(u) || byBarcode.Contains(i.Id));
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

        return query;
    }

    public async Task<ItemListDto> ListItemsAsync(ItemFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var query = await FilterAsync(ctx, filter, ct);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(i => i.Type).ThenBy(i => i.Name).ThenBy(i => i.Id) // Id — устойчивый порядок страниц при одинаковых названиях
            .Skip(Math.Max(0, filter.Skip))
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
        try
        {
            return await CreateItemCoreAsync(cmd, ct);
        }
        catch
        {
            // Отказ после сдвига номера автокода или добавления позиции не должен оставить изменения в контексте вкладки Blazor.
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<long> CreateItemCoreAsync(ItemCommand cmd, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await RequireActiveUnitAsync(ctx, cmd.UnitId, ct);
        // D312: выключенный вид не создаётся; пустой код — следующий номер автокода (если он включён).
        // Ревью: счётчики автокодов — свежие и под блокировкой организации до конца транзакции (иначе устаревшая копия во вкладке
        // или одновременное создание из двух вкладок роняли страницу ошибкой версии).
        await using var tx = await db.BeginTransactionAsync(ct);
        var settings = await ItemCodesService.FreshSettingsAsync(db, ctx.OrganizationId, ct, lockCounters: true);
        // D317: незаданная ставка НДС, ТН ВЭД, страна и маркировка — из значений по умолчанию группы (с учётом родителей).
        var groupDefaults = await GroupDefaultsService.ResolveAsync(db, ctx.OrganizationId, cmd.GroupId, ct);
        cmd = cmd with { VatRateId = cmd.VatRateId ?? groupDefaults.VatRateId };
        settings?.EnsureEnabled(cmd.Type);
        settings?.EnsureGroup(cmd.GroupId);
        settings?.EnsureVat(cmd.VatRateId);
        var code = cmd.Code;
        if (string.IsNullOrWhiteSpace(code) && settings is { AutoCode: true })
        {
            code = await NextAutoCodeAsync(ctx, settings, ct);
        }

        var item = Item.Create(ctx.OrganizationId, code, cmd.Name, cmd.Type, cmd.UnitId, cmd.Description, clock.UtcNow);
        await RequireActiveVatRateAsync(ctx, cmd.VatRateId, ct);
        ServiceRules.EnsureVat(item.Type, cmd.VatRateId);
        item.SetVatRate(cmd.VatRateId);
        if (cmd.GroupId is { } gid)
        {
            var group = await db.ItemGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid && g.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Группа номенклатуры");
            if (group.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Группа «{group.Name}» в архиве.");
            }

            item.SetDetails(item.Details with
            {
                GroupId = gid,
                TnVedCode = item.TnVedCode ?? groupDefaults.TnVedCode,
                OriginCountryCode = item.OriginCountryCode ?? groupDefaults.OriginCountryCode,
                OriginCountryName = item.OriginCountryCode is null ? groupDefaults.OriginCountryName : item.OriginCountryName,
            });
            if (groupDefaults.Tracking is { } tracking && ItemTypes.IsStocked(item.Type))
            {
                item.SetTrade(item.Trade with { IsMarked = tracking != TrackingType.None, TrackingType = tracking });
            }
        }

        await EnsureCodeFreeAsync(ctx, item.Code, null, ct);
        // D315: артикул по шаблону настроек каталога (если включён).
        if (settings is { AutoArticle: true } && await ItemCodesService.NextArticleAsync(db, ctx.OrganizationId, settings, item.Code, ct) is { } article)
        {
            item.SetDetails(item.Details with { Article = article });
        }

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        // D315: внутренний EAN-13 новой позиции (кроме услуги), если включён.
        if (settings is { AutoBarcode: true } && item.Type != ItemType.Service)
        {
            var barcode = await ItemCodesService.NextFreeBarcodeAsync(db, ctx.OrganizationId, settings, null, ct);
            db.ItemBarcodes.Add(ItemBarcode.Create(ctx.OrganizationId, item.Id, BarcodeType.Ean13, barcode));
        }

        Audit(ctx, AuditActions.CatalogCreated, nameof(Item), item.Id, null, $"{item.Code} {item.Name}", ItemTypes.Name(item.Type));
        await db.SaveOrConflictAsync(ct);
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
        ServiceRules.EnsureVat(cmd.Type, cmd.VatRateId);
        var settings = await db.CatalogSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
        settings?.EnsureVat(cmd.VatRateId); // D312: обязательная ставка НДС — и при изменении позиции
        if (cmd.Type != item.Type)
        {
            settings?.EnsureEnabled(cmd.Type);
            await EnsureTypeChangeAsync(ctx, item, cmd.Type, ct);
        }

        if (cmd.UnitId != item.UnitId && await db.ItemPacks.AnyAsync(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == id, ct))
        {
            // D307: коэффициенты упаковок заданы в основной единице — при её смене они потеряли бы смысл.
            throw new BusinessRuleException("catalog.pack.unit_change", "У позиции есть упаковки: их коэффициенты заданы в основной единице. Уберите упаковки, затем меняйте единицу.");
        }

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

    /// <summary>
    /// Смена типа (D300, D302). Услугой или комплектом может стать только позиция, которой не было на складе (нет движений и партий),
    /// без модификаций и не входящая в комплекты; иначе остаток, партии и старые документы указывали бы на то, что на складе не
    /// хранится. Комплект с составом перестаёт быть комплектом только после очистки состава. Те же запреты — триггеры базы.
    /// </summary>
    private async Task EnsureTypeChangeAsync(AccessContext ctx, Item item, ItemType newType, CancellationToken ct)
    {
        if (item.Type == ItemType.Bundle && await db.BundleComponents.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.BundleItemId == item.Id, ct))
        {
            throw new BusinessRuleException("catalog.bundle.has_components", $"У комплекта {item.Code} есть состав — сначала уберите компоненты.");
        }

        if (ItemTypes.IsStocked(newType) || !ItemTypes.IsStocked(item.Type))
        {
            return;
        }

        // Сообщения целиком для каждого типа — чтобы их можно было перевести (D60).
        var service = newType == ItemType.Service;
        if (item.IsModification || await db.Items.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.ParentItemId == item.Id, ct))
        {
            throw service
                ? new BusinessRuleException("catalog.service.variants", "Позицию с модификациями (или модификацию) нельзя сделать услугой.")
                : new BusinessRuleException("catalog.service.variants", "Позицию с модификациями (или модификацию) нельзя сделать комплектом.");
        }

        if (await db.ItemPacks.AnyAsync(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == item.Id, ct))
        {
            throw service
                ? new BusinessRuleException("catalog.pack.exists", $"У позиции {item.Code} есть упаковки — уберите их, затем делайте услугой.")
                : new BusinessRuleException("catalog.pack.exists", $"У позиции {item.Code} есть упаковки — уберите их, затем делайте комплектом.");
        }

        if (await db.BundleComponents.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.ComponentItemId == item.Id, ct))
        {
            throw service
                ? new BusinessRuleException("catalog.bundle.component_in_use", $"Позиция {item.Code} входит в комплекты — её нельзя сделать услугой.")
                : new BusinessRuleException("catalog.bundle.component_in_use", $"Позиция {item.Code} входит в комплекты — её нельзя сделать комплектом.");
        }

        if (await db.StockMovements.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.ItemId == item.Id, ct)
            || await db.StockBatches.AnyAsync(b => b.OrganizationId == ctx.OrganizationId && b.ItemId == item.Id, ct))
        {
            throw service
                ? new BusinessRuleException("catalog.service.has_stock", $"Позиция {item.Code} уже была на складе (есть движения или партии) — её нельзя сделать услугой. Заведите услугу отдельной позицией.")
                : new BusinessRuleException("catalog.service.has_stock", $"Позиция {item.Code} уже была на складе (есть движения или партии) — её нельзя сделать комплектом. Заведите комплект отдельной позицией.");
        }
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

    /// <summary>D312: следующий свободный код автонумерации (занятые вручную номера пропускаются; код уникален навсегда, включая архив).</summary>
    private async Task<string> NextAutoCodeAsync(AccessContext ctx, CatalogSettings settings, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var code = settings.TakeCode();
            if (!await db.Items.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.Code == code, ct))
            {
                return code;
            }
        }

        throw new BusinessRuleException("catalog.code.auto_exhausted", "Не удалось подобрать свободный код: увеличьте следующий номер в настройках каталога.");
    }

    private void Audit(AccessContext ctx, string action, string entityType, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entityType, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
