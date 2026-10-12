using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Группа в дереве: Path — «Пряжа / Шерсть», Depth — уровень с нуля, Items — действующих позиций прямо в группе.</summary>
public sealed record ItemGroupDto(long Id, long? ParentId, string Name, string Path, int Depth, int Items, bool IsArchived, byte[] RowVersion);

public sealed record ItemBarcodeDto(long Id, BarcodeType Type, string Code)
{
    public string TypeName => ItemBarcode.TypeName(Type);
}

public sealed record PriceTypeDto(long Id, string Name, bool IncludesVat, bool IsDefault, bool IsArchived, int Prices, byte[] RowVersion);

public sealed record ItemPriceDto(long PriceTypeId, string PriceType, bool IncludesVat, bool IsDefault, decimal? Price);

/// <summary>
/// Карточка позиции (D79). Цены и закупочная цена — только с правом «Цены и суммы» (иначе null / пусто).
/// </summary>
public sealed record ItemCardDto(
    long Id, string Code, string Name, ItemType Type, long UnitId, string UnitSymbol, string? Description, long? VatRateId, bool IsArchived,
    ItemDetails Details, string? GroupPath, IReadOnlyList<ItemBarcodeDto> Barcodes, IReadOnlyList<ItemPriceDto> Prices,
    IReadOnlyList<CustomValueDto> CustomFields, decimal Stock, bool CanEdit, bool CanSeePrices, byte[] RowVersion)
{
    public string TypeName => ItemTypes.Name(Type);

    /// <summary>Хранится на складе: у услуги (D300) нет остатка, партий, веса, объёма и неснижаемого остатка.</summary>
    public bool IsStocked => ItemTypes.IsStocked(Type);
}

public sealed record UserCatalogDto(long Id, string Name, bool IsArchived, int Entries, byte[] RowVersion);

public sealed record UserCatalogEntryDto(long Id, string Name, string? Code, bool IsArchived, byte[] RowVersion)
{
    public string Display => Code is null ? Name : $"{Code} — {Name}";
}

/// <summary>
/// Номенклатура как в МойСклад (D79): группы-папки, карточка позиции (артикул, страна происхождения, декларация, ТН ВЭД, вес, объём,
/// неснижаемый остаток, закупочная цена), штрихкоды, виды цен и цены, доп. поля и свои справочники пользователя.
/// Смотреть — «Номенклатура: просмотр», менять — «Номенклатура: изменение»; цены — дополнительно «Цены и суммы».
/// </summary>
public sealed class NomenclatureService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private const string ItemFieldReason = "Дополнительное поле номенклатуры";

    private CustomFieldStore Fields => new(db, currentUser, clock);

    // ---------- Группы ----------

    public async Task<IReadOnlyList<ItemGroupDto>> GroupsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        return await GroupTreeAsync(ctx.OrganizationId, includeArchived, ct);
    }

    internal async Task<IReadOnlyList<ItemGroupDto>> GroupTreeAsync(long organizationId, bool includeArchived, CancellationToken ct)
    {
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == organizationId && (includeArchived || !g.IsArchived))
            .ToListAsync(ct);
        var counts = await db.Items.AsNoTracking().Where(i => i.OrganizationId == organizationId && !i.IsArchived && i.GroupId != null)
            .GroupBy(i => i.GroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var children = groups.ToLookup(g => g.ParentId);
        var result = new List<ItemGroupDto>();
        void Walk(long? parent, string prefix, int depth)
        {
            foreach (var g in children[parent].OrderBy(g => g.IsArchived).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var path = prefix.Length == 0 ? g.Name : $"{prefix} / {g.Name}";
                result.Add(new ItemGroupDto(g.Id, g.ParentId, g.Name, path, depth, counts.GetValueOrDefault(g.Id), g.IsArchived, g.RowVersion));
                Walk(g.Id, path, depth + 1);
            }
        }

        Walk(null, string.Empty, 0);
        return result;
    }

    public async Task<long> CreateGroupAsync(long? parentId, string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        if (await db.ItemGroups.CountAsync(g => g.OrganizationId == ctx.OrganizationId && !g.IsArchived, ct) >= ItemGroup.MaxGroups)
        {
            throw new BusinessRuleException("catalog.group.too_many", $"Групп не больше {ItemGroup.MaxGroups}.");
        }

        await EnsureParentAsync(ctx, null, parentId, ct);
        var group = ItemGroup.Create(ctx.OrganizationId, parentId, name);
        await EnsureGroupFreeAsync(ctx, parentId, group.Name, null, ct);
        db.ItemGroups.Add(group);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(ItemGroup), group.Id, null, group.Name, "Группа номенклатуры");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return group.Id;
    }

    public async Task RenameGroupAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, group) = await LoadGroupAsync(id, rowVersion, ct);
        var before = group.Name;
        group.Rename(name);
        await EnsureGroupFreeAsync(ctx, group.ParentId, group.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(ItemGroup), id, before, group.Name, "Группа номенклатуры");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Перенос группы в другую (null — в корень): не в саму себя и не в свою подгруппу, глубина не больше пяти.</summary>
    public async Task MoveGroupAsync(long id, long? parentId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, group) = await LoadGroupAsync(id, rowVersion, ct);
        await EnsureParentAsync(ctx, id, parentId, ct);
        group.MoveTo(parentId);
        await EnsureGroupFreeAsync(ctx, parentId, group.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(ItemGroup), id, null, parentId?.ToString() ?? "корень", $"{group.Name}: перенос");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>В архив — только пустая группа: без действующих позиций и подгрупп.</summary>
    public async Task SetGroupArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, group) = await LoadGroupAsync(id, rowVersion, ct);
        if (archived)
        {
            if (await db.Items.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.GroupId == id && !i.IsArchived, ct)
                || await db.ItemGroups.AnyAsync(g => g.OrganizationId == ctx.OrganizationId && g.ParentId == id && !g.IsArchived, ct))
            {
                throw new BusinessRuleException("catalog.group.not_empty", $"В группе «{group.Name}» есть позиции или подгруппы — сначала перенесите их.");
            }
        }
        else
        {
            if (group.ParentId is { } p && await db.ItemGroups.AnyAsync(g => g.Id == p && g.IsArchived, ct))
            {
                throw new BusinessRuleException("catalog.archived", "Родительская группа в архиве — сначала верните её.");
            }

            await EnsureGroupFreeAsync(ctx, group.ParentId, group.Name, id, ct);
        }

        group.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(ItemGroup), id,
            archived ? group.Name : "в архиве", archived ? "в архиве" : group.Name, "Группа номенклатуры");
        await db.SaveOrConflictAsync(ct);
    }

    // ---------- Карточка позиции ----------

    public async Task<ItemCardDto> GetItemAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        var unit = await db.Units.AsNoTracking().Where(u => u.Id == item.UnitId).Select(u => u.Symbol).SingleAsync(ct);
        var path = item.GroupId is { } gid ? (await GroupTreeAsync(ctx.OrganizationId, true, ct)).FirstOrDefault(g => g.Id == gid)?.Path : null;
        var barcodes = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && b.ItemId == id).OrderBy(b => b.Id)
            .Select(b => new ItemBarcodeDto(b.Id, b.Type, b.Code)).ToListAsync(ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView) ? await PricesAsync(ctx.OrganizationId, id, ct) : [];
        // Ревью D339: остаток — только по складам, которые пользователь видит (как в подборе позиций), а не по всей организации.
        var visible = KnitErp.Application.Warehousing.WarehouseScope.Visible(ctx, Permissions.WarehouseReportView, Permissions.WarehouseDocumentCreate, Permissions.SalesView);
        var stock = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.ItemId == id && (visible == null || visible.Contains(m.WarehouseId)))
            .SumAsync(m => (decimal?)m.Quantity, ct) ?? 0m;
        var details = ctx.Permissions.Has(Permissions.PriceView) ? item.Details : item.Details with { PurchasePrice = null };
        return new ItemCardDto(item.Id, item.Code, item.Name, item.Type, item.UnitId, unit, item.Description, item.VatRateId, item.IsArchived, details,
            path, barcodes, prices, await Fields.ValuesAsync(ctx, CustomFieldTarget.Item, id, ct), stock,
            ctx.Permissions.Has(Permissions.CatalogEdit), ctx.Permissions.Has(Permissions.PriceView), item.RowVersion);
    }

    /// <summary>
    /// Сведения карточки и доп. поля. Закупочную цену меняет только тот, кто видит цены: без права она остаётся прежней.
    /// </summary>
    public async Task SaveItemDetailsAsync(long id, ItemDetails details, IReadOnlyDictionary<long, string?> customFields, byte[] rowVersion,
        CancellationToken ct = default)
    {
        try
        {
            await SaveItemDetailsCoreAsync(id, details, customFields, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges(); // D304: отклонённая правка не остаётся в DbContext вкладки (сведения меняются до проверки доп. полей)
            throw;
        }
    }

    private async Task SaveItemDetailsCoreAsync(long id, ItemDetails details, IReadOnlyDictionary<long, string?> customFields, byte[] rowVersion,
        CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        item.EnsureVersion(item.RowVersion, rowVersion);
        if (!ctx.Permissions.Has(Permissions.PriceView))
        {
            details = details with { PurchasePrice = item.PurchasePrice };
        }

        // D312: обязательная группа — её нельзя убрать у позиции (модификация берёт группу основной позиции).
        if (item.ParentItemId is null)
        {
            (await db.CatalogSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct))?.EnsureGroup(details.GroupId);
        }

        if (details.GroupId is { } gid && gid != item.GroupId)
        {
            var group = await db.ItemGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid && g.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Группа номенклатуры");
            if (group.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Группа «{group.Name}» в архиве.");
            }
        }

        var purchaseBefore = item.PurchasePrice;
        foreach (var c in item.SetDetails(details))
        {
            Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, c.Before, c.After, c.Field);
        }

        var fieldChanges = await Fields.SetValuesAsync(ctx, CustomFieldTarget.Item, id, customFields, ct);
        if (fieldChanges.Count > 0)
        {
            Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, null, string.Join("; ", fieldChanges), "Дополнительные поля");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        if (item.PurchasePrice != purchaseBefore)
        {
            await AutoPrices.ApplyAsync(db, ctx.OrganizationId, [id], ctx.UserId, clock.UtcNow, ct); // D322: автоцены от новой закупочной
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Штрихкод: тип по виду кода, если не указан; уникален в организации.</summary>
    public async Task<long> AddBarcodeAsync(long itemId, string? code, BarcodeType? type = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве.");
        }

        var trimmedCode = (code ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (item.Type == ItemType.Service && (type ?? ItemBarcode.Detect(trimmedCode)) == BarcodeType.Gtin)
        {
            // D300: GTIN-14 — код для маркировки товара; у услуги его не бывает.
            throw new BusinessRuleException("catalog.service.gtin", "У услуги нет кода маркировки GTIN — добавьте EAN-13 или Code 128.");
        }

        if (await db.ItemBarcodes.CountAsync(b => b.ItemId == itemId, ct) >= ItemBarcode.MaxPerItem)
        {
            throw new BusinessRuleException("catalog.barcode.too_many", $"У позиции не больше {ItemBarcode.MaxPerItem} штрихкодов.");
        }

        var trimmed = (code ?? string.Empty).Trim();
        var barcode = ItemBarcode.Create(ctx.OrganizationId, itemId, type ?? ItemBarcode.Detect(trimmed.Replace(" ", string.Empty, StringComparison.Ordinal)),
            trimmed);
        var owner = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && b.Code == barcode.Code)
                        .Join(db.Items.AsNoTracking(), b => b.ItemId, i => i.Id, (b, i) => i.Code + " " + i.Name).FirstOrDefaultAsync(ct)
                    // D307: штрихкод упаковки тоже занят — сканер должен находить одно.
                    ?? await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.Barcode == barcode.Code)
                        .Join(db.Items.AsNoTracking(), p => p.ItemId, i => i.Id, (p, i) => i.Code + " " + i.Name).FirstOrDefaultAsync(ct);
        if (owner is not null)
        {
            throw new BusinessRuleException("catalog.barcode.duplicate", $"Штрихкод {barcode.Code} уже у позиции {owner}.");
        }

        db.ItemBarcodes.Add(barcode);
        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), itemId, null, $"{ItemBarcode.TypeName(barcode.Type)} {barcode.Code}", "Штрихкод добавлен");
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.DiscardChanges();
            // Ревью D339: тот же штрихкод добавили одновременно в другой вкладке — понятная причина, а не сбой; вкладка остаётся рабочей.
            if (await db.ItemBarcodes.AsNoTracking().AnyAsync(b => b.OrganizationId == ctx.OrganizationId && b.Code == barcode.Code, ct))
            {
                throw new BusinessRuleException("catalog.barcode.duplicate", $"Штрихкод {barcode.Code} только что добавлен к другой позиции.");
            }

            throw;
        }

        return barcode.Id;
    }

    /// <summary>Штрихкод — признак позиции, не документ: удаляется, удаление пишется в журнал.</summary>
    public async Task RemoveBarcodeAsync(long barcodeId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var barcode = await db.ItemBarcodes.SingleOrDefaultAsync(b => b.Id == barcodeId && b.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Штрихкод");
        db.ItemBarcodes.Remove(barcode);
        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), barcode.ItemId, barcode.Code, null, "Штрихкод удалён");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Позиция по штрихкоду — для сканера; null — не найдено.</summary>
    public async Task<long?> FindByBarcodeAsync(string? code, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var c = (code ?? string.Empty).Trim();
        return c.Length == 0 ? null : await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId && b.Code == c)
            .Select(b => (long?)b.ItemId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Цены позиции по видам цен: значение — задать, null — убрать. Нужны права на номенклатуру и на цены.
    /// D344 (ответ заказчика): при одновременной правке сохраняется последнее изменение, без конфликта. В историю цен пишется
    /// настоящая прежняя цена из базы; одновременная вставка цены того же вида (уникальный индекс) — повтор поверх неё.
    /// </summary>
    public async Task SetPricesAsync(long itemId, IReadOnlyDictionary<long, decimal?> prices, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SetPricesCoreAsync(itemId, prices, ct);
                return;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                db.DiscardChanges();
            }
            catch
            {
                db.DiscardChanges(); // D304: цены меняются по одной — при отказе на середине ничего не остаётся в DbContext вкладки
                throw;
            }
        }
    }

    private async Task SetPricesCoreAsync(long itemId, IReadOnlyDictionary<long, decimal?> prices, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        if (item.IsArchived)
        {
            // Ревью D339: цены архивной позиции не меняются (как и прочие её сведения) — сначала верните её из архива.
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве.");
        }

        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(t => t.Id, ct);
        var current = await db.ItemPrices.Where(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == itemId).ToDictionaryAsync(p => p.PriceTypeId, ct);
        // D341: у вкладки один DbContext — отслеживаемая цена могла устареть (её поменял другой пользователь): прежняя цена — из базы.
        var fresh = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == itemId)
            .ToDictionaryAsync(p => p.PriceTypeId, p => p.Price, ct);
        foreach (var (typeId, tracked) in current.ToList())
        {
            if (!fresh.TryGetValue(typeId, out var actual) || actual != tracked.Price)
            {
                await db.ReloadAsync(tracked, ct); // удалённая в базе строка отсоединяется
                if (!fresh.ContainsKey(typeId))
                {
                    current.Remove(typeId);
                }
            }
        }

        foreach (var (typeId, price) in prices)
        {
            var type = types.GetValueOrDefault(typeId) ?? throw new NotFoundException("Вид цены");
            var existing = current.GetValueOrDefault(typeId);
            var before = existing?.Price; // до изменения: existing.Set меняет цену на месте
            if (before == price)
            {
                continue;
            }

            if (type.IsArchived && price is not null)
            {
                throw new BusinessRuleException("catalog.archived", $"Вид цены «{type.Name}» в архиве.");
            }

            if (price is null)
            {
                if (existing is not null)
                {
                    db.ItemPrices.Remove(existing);
                }
            }
            else if (existing is null)
            {
                db.ItemPrices.Add(ItemPrice.Create(ctx.OrganizationId, itemId, typeId, price.Value));
            }
            else
            {
                existing.Set(price.Value);
            }

            db.ItemPriceHistory.Add(ItemPriceHistory.Create(ctx.OrganizationId, itemId, typeId, before, price, clock.UtcNow, ctx.UserId,
                PriceChangeSource.Manual)); // D305: история цен
            Audit(ctx, AuditActions.CatalogChanged, nameof(Item), itemId, before?.ToString("0.00##"), price?.ToString("0.00##"),
                $"{item.Code}: {type.Name}");
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<ItemPriceDto>> PricesAsync(long organizationId, long itemId, CancellationToken ct)
    {
        var prices = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.ItemId == itemId)
            .ToDictionaryAsync(p => p.PriceTypeId, p => p.Price, ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == organizationId && (!t.IsArchived || prices.Keys.Contains(t.Id)))
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        return types.Select(t => new ItemPriceDto(t.Id, t.Name, t.IncludesVat, t.IsDefault, prices.TryGetValue(t.Id, out var p) ? p : null)).ToList();
    }

    // ---------- Виды цен ----------

    public async Task<IReadOnlyList<PriceTypeDto>> PriceTypesAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var counts = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId)
            .GroupBy(p => p.PriceTypeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && (includeArchived || !t.IsArchived))
            .OrderBy(t => t.IsArchived).ThenByDescending(t => t.IsDefault).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        return types.Select(t => new PriceTypeDto(t.Id, t.Name, t.IncludesVat, t.IsDefault, t.IsArchived, counts.GetValueOrDefault(t.Id), t.RowVersion))
            .ToList();
    }

    public async Task<long> CreatePriceTypeAsync(string? name, bool includesVat, CancellationToken ct = default)
    {
        var ctx = await DemandPriceEditAsync(ct);
        var types = db.PriceTypes.Where(t => t.OrganizationId == ctx.OrganizationId);
        if (await types.CountAsync(t => !t.IsArchived, ct) >= PriceType.MaxTypes)
        {
            throw new BusinessRuleException("catalog.price_type.too_many", $"Видов цен не больше {PriceType.MaxTypes}.");
        }

        var last = await types.MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
        var type = PriceType.Create(ctx.OrganizationId, name, includesVat, !await types.AnyAsync(t => t.IsDefault, ct), last + 10);
        await EnsurePriceTypeFreeAsync(ctx, type.Name, null, ct);
        db.PriceTypes.Add(type);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(PriceType), type.Id, null, type.Name, "Вид цены");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return type.Id;
    }

    public async Task UpdatePriceTypeAsync(long id, string? name, bool includesVat, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, type) = await LoadPriceTypeAsync(id, rowVersion, ct);
        var before = $"{type.Name}{(type.IncludesVat ? ", с НДС" : ", без НДС")}";
        type.Set(name, includesVat);
        await EnsurePriceTypeFreeAsync(ctx, type.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(PriceType), id, before, $"{type.Name}{(type.IncludesVat ? ", с НДС" : ", без НДС")}", "Вид цены");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task MakeDefaultPriceTypeAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, type) = await LoadPriceTypeAsync(id, rowVersion, ct);
        if (type.IsArchived || type.IsDefault)
        {
            return;
        }

        var current = await db.PriceTypes.Where(t => t.OrganizationId == ctx.OrganizationId && t.IsDefault).ToListAsync(ct);
        current.ForEach(t => t.SetDefault(false));
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        type.SetDefault(true);
        Audit(ctx, AuditActions.CatalogChanged, nameof(PriceType), id, current.FirstOrDefault()?.Name, type.Name, "Основной вид цены");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetPriceTypeArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, type) = await LoadPriceTypeAsync(id, rowVersion, ct);
        if (!archived)
        {
            await EnsurePriceTypeFreeAsync(ctx, type.Name, id, ct);
        }

        type.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(PriceType), id,
            archived ? type.Name : "в архиве", archived ? "в архиве" : type.Name, "Вид цены");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Основной вид цены новой организации.</summary>
    internal static void SeedDefaultPriceType(IKnitErpDbContext db, long organizationId) =>
        db.PriceTypes.Add(PriceType.Create(organizationId, PriceType.DefaultName, includesVat: true, isDefault: true, sortOrder: 10));

    // ---------- Доп. поля номенклатуры ----------

    public async Task<IReadOnlyList<CustomFieldDto>> ItemFieldsAsync(bool includeArchived = false, CancellationToken ct = default) =>
        await Fields.ListAsync(await guard.DemandAsync(Permissions.CatalogView, ct), CustomFieldTarget.Item, includeArchived, ct);

    public async Task<long> CreateItemFieldAsync(string? name, CustomFieldType type, long? catalogId = null, CancellationToken ct = default) =>
        await Fields.CreateAsync(await guard.DemandAsync(Permissions.CatalogEdit, ct), CustomFieldTarget.Item, name, type, catalogId, ItemFieldReason, ct);

    public async Task RenameItemFieldAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default) =>
        await Fields.RenameAsync(await guard.DemandAsync(Permissions.CatalogEdit, ct), CustomFieldTarget.Item, id, name, rowVersion, ItemFieldReason, ct);

    public async Task MoveItemFieldAsync(long id, int delta, CancellationToken ct = default) =>
        await Fields.MoveAsync(await guard.DemandAsync(Permissions.CatalogEdit, ct), CustomFieldTarget.Item, id, delta, ct);

    public async Task SetItemFieldArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default) =>
        await Fields.SetArchivedAsync(await guard.DemandAsync(Permissions.CatalogEdit, ct), CustomFieldTarget.Item, id, archived, rowVersion,
            ItemFieldReason, ct);

    // ---------- Свои справочники ----------

    public async Task<IReadOnlyList<UserCatalogDto>> CatalogsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var counts = await db.UserCatalogEntries.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived)
            .GroupBy(e => e.CatalogId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await db.UserCatalogs.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && (includeArchived || !c.IsArchived))
            .OrderBy(c => c.IsArchived).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(c => new UserCatalogDto(c.Id, c.Name, c.IsArchived, counts.GetValueOrDefault(c.Id), c.RowVersion)).ToList();
    }

    public async Task<long> CreateCatalogAsync(string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        if (await db.UserCatalogs.CountAsync(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived, ct) >= UserCatalog.MaxCatalogs)
        {
            throw new BusinessRuleException("catalog.user.too_many", $"Своих справочников не больше {UserCatalog.MaxCatalogs}.");
        }

        var catalog = UserCatalog.Create(ctx.OrganizationId, name);
        await EnsureCatalogFreeAsync(ctx, catalog.Name, null, ct);
        db.UserCatalogs.Add(catalog);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(UserCatalog), catalog.Id, null, catalog.Name, "Свой справочник");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return catalog.Id;
    }

    public async Task RenameCatalogAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, catalog) = await LoadCatalogAsync(id, rowVersion, ct);
        var before = catalog.Name;
        catalog.Rename(name);
        await EnsureCatalogFreeAsync(ctx, catalog.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(UserCatalog), id, before, catalog.Name, "Свой справочник");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetCatalogArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, catalog) = await LoadCatalogAsync(id, rowVersion, ct);
        if (archived && await db.CustomFieldDefinitions.AnyAsync(f => f.OrganizationId == ctx.OrganizationId && f.CatalogId == id && !f.IsArchived, ct))
        {
            throw new BusinessRuleException("catalog.user.used", $"Справочник «{catalog.Name}» подключён к доп. полю — сначала уберите поле в архив.");
        }

        // Ревью D328: и к характеристике модификаций (тип «Справочник») — иначе новые значения ей не из чего выбрать.
        if (archived && await db.Characteristics.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.CatalogId == id && !c.IsArchived, ct))
        {
            throw new BusinessRuleException("catalog.user.used_by_characteristic", $"Справочник «{catalog.Name}» подключён к характеристике модификаций — сначала смените её тип или уберите в архив.");
        }

        if (!archived)
        {
            await EnsureCatalogFreeAsync(ctx, catalog.Name, id, ct);
        }

        catalog.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(UserCatalog), id,
            archived ? catalog.Name : "в архиве", archived ? "в архиве" : catalog.Name, "Свой справочник");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<IReadOnlyList<UserCatalogEntryDto>> EntriesAsync(long catalogId, bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        if (!await db.UserCatalogs.AnyAsync(c => c.Id == catalogId && c.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Справочник");
        }

        return await db.UserCatalogEntries.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.CatalogId == catalogId && (includeArchived || !e.IsArchived))
            .OrderBy(e => e.IsArchived).ThenBy(e => e.Code).ThenBy(e => e.Name)
            .Select(e => new UserCatalogEntryDto(e.Id, e.Name, e.Code, e.IsArchived, e.RowVersion)).ToListAsync(ct);
    }

    /// <summary>Записи всех действующих своих справочников — для выбора значений доп. полей в карточке.</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<UserCatalogEntryDto>>> EntriesByCatalogAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var entries = await db.UserCatalogEntries.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived)
            .OrderBy(e => e.Code).ThenBy(e => e.Name)
            .Select(e => new { e.CatalogId, Dto = new UserCatalogEntryDto(e.Id, e.Name, e.Code, e.IsArchived, e.RowVersion) }).ToListAsync(ct);
        return entries.GroupBy(e => e.CatalogId).ToDictionary(g => g.Key, g => (IReadOnlyList<UserCatalogEntryDto>)g.Select(x => x.Dto).ToList());
    }

    public async Task<long> AddEntryAsync(long catalogId, string? name, string? code, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var catalog = await db.UserCatalogs.AsNoTracking().SingleOrDefaultAsync(c => c.Id == catalogId && c.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Справочник");
        if (catalog.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Справочник «{catalog.Name}» в архиве.");
        }

        if (await db.UserCatalogEntries.CountAsync(e => e.CatalogId == catalogId && !e.IsArchived, ct) >= UserCatalogEntry.MaxEntries)
        {
            throw new BusinessRuleException("catalog.user.entries_too_many", $"Записей в справочнике не больше {UserCatalogEntry.MaxEntries}.");
        }

        var entry = UserCatalogEntry.Create(ctx.OrganizationId, catalogId, name, code);
        await EnsureEntryFreeAsync(catalogId, entry.Name, null, ct);
        db.UserCatalogEntries.Add(entry);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(UserCatalogEntry), entry.Id, null, entry.Display, catalog.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return entry.Id;
    }

    public async Task UpdateEntryAsync(long id, string? name, string? code, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, entry) = await LoadEntryAsync(id, rowVersion, ct);
        var before = entry.Display;
        var oldName = entry.Name;
        try
        {
            entry.Set(name, code);
            await EnsureEntryFreeAsync(entry.CatalogId, entry.Name, id, ct);
            Audit(ctx, AuditActions.CatalogChanged, nameof(UserCatalogEntry), id, before, entry.Display, "Запись своего справочника");
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveOrConflictAsync(ct);
            if (!string.Equals(oldName, entry.Name, StringComparison.Ordinal))
            {
                await RenameCharacteristicValuesAsync(ctx.OrganizationId, entry.CatalogId, oldName, entry.Name, ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Ревью D328: у модификаций хранится название записи справочника — переименование переносится в их значения и ключи,
    /// иначе любая правка такой модификации отклонялась бы («нет в справочнике»).
    /// </summary>
    private async Task RenameCharacteristicValuesAsync(long org, long catalogId, string oldName, string newName, CancellationToken ct)
    {
        var characteristicIds = await db.Characteristics.Where(c => c.OrganizationId == org && c.CatalogId == catalogId).Select(c => c.Id).ToListAsync(ct);
        if (characteristicIds.Count == 0)
        {
            return;
        }

        var affected = (await db.ItemCharacteristicValues.Where(v => v.OrganizationId == org && characteristicIds.Contains(v.CharacteristicId)).ToListAsync(ct))
            .Where(v => string.Equals(v.Value, oldName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (affected.Count == 0)
        {
            return;
        }

        var itemIds = affected.Select(v => v.ItemId).Distinct().ToList();
        foreach (var v in affected)
        {
            v.Rename(newName);
        }

        var all = (await db.ItemCharacteristicValues.Where(v => itemIds.Contains(v.ItemId)).ToListAsync(ct)).ToLookup(v => v.ItemId);
        foreach (var item in await db.Items.Where(i => i.OrganizationId == org && itemIds.Contains(i.Id)).ToListAsync(ct))
        {
            item.SetVariantKey(Variants.Key(all[item.Id].ToDictionary(v => v.CharacteristicId, v => v.Value)));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetEntryArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, entry) = await LoadEntryAsync(id, rowVersion, ct);
        if (!archived)
        {
            await EnsureEntryFreeAsync(entry.CatalogId, entry.Name, id, ct);
        }

        entry.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(UserCatalogEntry), id,
            archived ? entry.Display : "в архиве", archived ? "в архиве" : entry.Display, "Запись своего справочника");
        await db.SaveOrConflictAsync(ct);
    }

    // ---------- Служебное ----------

    private async Task<AccessContext> DemandPriceEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    /// <summary>Родитель — своя действующая группа, не сама группа и не её подгруппа; глубина не больше <see cref="ItemGroup.MaxDepth"/>.</summary>
    private async Task EnsureParentAsync(AccessContext ctx, long? groupId, long? parentId, CancellationToken ct)
    {
        if (parentId is not { } pid)
        {
            return;
        }

        var all = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(g => g.Id, ct);
        if (!all.TryGetValue(pid, out var parent))
        {
            throw new NotFoundException("Группа номенклатуры");
        }

        if (parent.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Группа «{parent.Name}» в архиве.");
        }

        var depth = 1;
        for (long? cur = pid; cur is { } c; cur = all[c].ParentId, depth++)
        {
            if (c == groupId)
            {
                throw new BusinessRuleException("catalog.group.cycle", "Группу нельзя перенести в её же подгруппу.");
            }
        }

        // Глубина самой группы с её подгруппами.
        var below = 0;
        if (groupId is { } gid)
        {
            var children = all.Values.ToLookup(g => g.ParentId);
            int Height(long id) => children[id].Select(ch => 1 + Height(ch.Id)).DefaultIfEmpty(0).Max();
            below = Height(gid);
        }

        if (depth + below > ItemGroup.MaxDepth)
        {
            throw new BusinessRuleException("catalog.group.depth", $"Вложенность групп — не больше {ItemGroup.MaxDepth} уровней.");
        }
    }

    private async Task<(AccessContext, ItemGroup)> LoadGroupAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var group = await db.ItemGroups.SingleOrDefaultAsync(g => g.Id == id && g.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Группа номенклатуры");
        return (ctx, group.EnsureVersion(group.RowVersion, rowVersion));
    }

    private async Task<(AccessContext, PriceType)> LoadPriceTypeAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await DemandPriceEditAsync(ct);
        var type = await db.PriceTypes.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Вид цены");
        return (ctx, type.EnsureVersion(type.RowVersion, rowVersion));
    }

    private async Task<(AccessContext, UserCatalog)> LoadCatalogAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var catalog = await db.UserCatalogs.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Справочник");
        return (ctx, catalog.EnsureVersion(catalog.RowVersion, rowVersion));
    }

    private async Task<(AccessContext, UserCatalogEntry)> LoadEntryAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var entry = await db.UserCatalogEntries.SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Запись справочника");
        return (ctx, entry.EnsureVersion(entry.RowVersion, rowVersion));
    }

    private async Task EnsureGroupFreeAsync(AccessContext ctx, long? parentId, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.ItemGroups.AnyAsync(g => g.OrganizationId == ctx.OrganizationId && !g.IsArchived && g.ParentId == parentId && g.Name == name
                                             && g.Id != exceptId, ct))
        {
            throw new BusinessRuleException("catalog.group.duplicate", $"Группа «{name}» здесь уже есть.");
        }
    }

    private async Task EnsurePriceTypeFreeAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.PriceTypes.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && !t.IsArchived && t.Name == name && t.Id != exceptId, ct))
        {
            throw new BusinessRuleException("catalog.price_type.duplicate", $"Вид цены «{name}» уже есть.");
        }
    }

    private async Task EnsureCatalogFreeAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.UserCatalogs.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.Name == name && c.Id != exceptId, ct))
        {
            throw new BusinessRuleException("catalog.user.duplicate", $"Справочник «{name}» уже есть.");
        }
    }

    private async Task EnsureEntryFreeAsync(long catalogId, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.UserCatalogEntries.AnyAsync(e => e.CatalogId == catalogId && !e.IsArchived && e.Name == name && e.Id != exceptId, ct))
        {
            throw new BusinessRuleException("catalog.user.entry_duplicate", $"«{name}» в справочнике уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
