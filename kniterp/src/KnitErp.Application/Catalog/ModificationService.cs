using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Характеристика; D328 — тип, свой справочник и варианты для выбора (записи справочника или «Да»/«Нет»).</summary>
public sealed record CharacteristicDto(long Id, string Name, bool IsArchived, int Used, byte[] RowVersion,
    CharacteristicType Type = CharacteristicType.Text, long? CatalogId = null, IReadOnlyList<string>? Options = null);

public sealed record VariantValueDto(long CharacteristicId, string Characteristic, string Value);

public sealed record ModificationDto(long Id, string Code, string Name, IReadOnlyList<VariantValueDto> Values, decimal Stock, bool IsArchived);

public sealed record ItemRefDto(long Id, string Code, string Name);

/// <summary>
/// Модификации позиции (D82). У основной позиции — список модификаций; у модификации — основная позиция и свои значения.
/// RowVersion — версия самой позиции (для смены значений модификации).
/// </summary>
public sealed record ItemVariantsDto(ItemRefDto? Parent, IReadOnlyList<VariantValueDto> Values, IReadOnlyList<ModificationDto> Modifications, byte[] RowVersion);

public sealed record ItemPhotoDto(long Id, string ContentType, int SizeBytes, int SortOrder);

public sealed record CreateModificationsResult(int Created, int Skipped);

/// <summary>
/// Модификации и фото номенклатуры (D82), как в МойСклад. Характеристики («Цвет», «Размер») — справочник организации;
/// модификации создаются из сочетаний значений, каждая — отдельная позиция со своим остатком. Фото — до 8 на позицию.
/// Смотреть — «Номенклатура: просмотр», менять — «Номенклатура: изменение».
/// </summary>
public sealed class ModificationService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    // ---------- Характеристики ----------

    public async Task<IReadOnlyList<CharacteristicDto>> CharacteristicsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var used = await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == ctx.OrganizationId)
            .GroupBy(v => v.CharacteristicId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var list = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && (includeArchived || !c.IsArchived))
            .OrderBy(c => c.IsArchived).ThenBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        var names = await CatalogNamesAsync(ctx.OrganizationId, list, ct);
        return list.Select(c => new CharacteristicDto(c.Id, c.Name, c.IsArchived, used.GetValueOrDefault(c.Id), c.RowVersion, c.Type, c.CatalogId,
            c.Type switch
            {
                CharacteristicType.Flag => [Characteristic.Yes, Characteristic.No],
                CharacteristicType.Catalog => names.GetValueOrDefault(c.CatalogId!.Value) ?? [],
                _ => null,
            })).ToList();
    }

    public async Task<long> CreateCharacteristicAsync(string? name, CancellationToken ct = default) =>
        await CreateCharacteristicAsync(name, CharacteristicType.Text, null, ct);

    /// <summary>D328: характеристика с типом значений (число, да/нет, запись своего справочника).</summary>
    public async Task<long> CreateCharacteristicAsync(string? name, CharacteristicType type, long? catalogId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var existing = await db.Characteristics.Where(c => c.OrganizationId == ctx.OrganizationId).ToListAsync(ct);
        if (existing.Count(c => !c.IsArchived) >= Characteristic.MaxCharacteristics)
        {
            throw new BusinessRuleException("catalog.characteristic.too_many", $"Характеристик — не больше {Characteristic.MaxCharacteristics}.");
        }

        await RequireCatalogAsync(ctx.OrganizationId, catalogId, ct);
        var characteristic = Characteristic.Create(ctx.OrganizationId, name, existing.Count == 0 ? 1 : existing.Max(c => c.SortOrder) + 1, type, catalogId);
        EnsureUnique(existing, characteristic.Name, null);
        db.Characteristics.Add(characteristic);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(Characteristic), characteristic.Id, null, characteristic.Name, "Характеристика модификаций");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return characteristic.Id;
    }

    public async Task RenameCharacteristicAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, characteristic) = await LoadCharacteristicAsync(id, rowVersion, ct);
        var before = characteristic.Name;
        characteristic.Rename(name);
        EnsureUnique(await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId).ToListAsync(ct), characteristic.Name, id);
        Audit(ctx, AuditActions.CatalogChanged, nameof(Characteristic), id, before, characteristic.Name, "Характеристика модификаций");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// D328: сменить тип значений. Уже введённые у модификаций значения должны подходить новому типу в точности (иначе — список
    /// неподходящих: исправьте их в модификациях или выберите другой тип): так ключи модификаций не меняются.
    /// </summary>
    public async Task SetCharacteristicTypeAsync(long id, CharacteristicType type, long? catalogId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, characteristic) = await LoadCharacteristicAsync(id, rowVersion, ct);
        try
        {
            await RequireCatalogAsync(ctx.OrganizationId, catalogId, ct);
            var before = $"{CharacteristicTypeName(characteristic.Type)}";
            characteristic.SetType(type, catalogId);
            var names = (await CatalogNamesAsync(ctx.OrganizationId, [characteristic], ct)).GetValueOrDefault(catalogId ?? 0);
            var bad = new List<string>();
            foreach (var value in await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == ctx.OrganizationId && v.CharacteristicId == id)
                         .Select(v => v.Value).Distinct().ToListAsync(ct))
            {
                try
                {
                    if (characteristic.Normalize(value, names) != value)
                    {
                        bad.Add(value);
                    }
                }
                catch (BusinessRuleException)
                {
                    bad.Add(value);
                }
            }

            if (bad.Count > 0)
            {
                throw new BusinessRuleException("catalog.characteristic.values_mismatch",
                    $"У модификаций уже есть значения «{characteristic.Name}», не подходящие новому типу: {string.Join(", ", bad.Take(5))}{(bad.Count > 5 ? " …" : "")}. Исправьте их или выберите другой тип.");
            }

            Audit(ctx, AuditActions.CatalogChanged, nameof(Characteristic), id, before, CharacteristicTypeName(type), $"Тип характеристики «{characteristic.Name}»");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public static string CharacteristicTypeName(CharacteristicType type) => type switch
    {
        CharacteristicType.Number => "Число",
        CharacteristicType.Flag => "Да/нет",
        CharacteristicType.Catalog => "Справочник",
        _ => "Текст",
    };

    private async Task RequireCatalogAsync(long org, long? catalogId, CancellationToken ct)
    {
        if (catalogId is { } cid && !await db.UserCatalogs.AnyAsync(c => c.Id == cid && c.OrganizationId == org && !c.IsArchived, ct))
        {
            throw new NotFoundException("Справочник");
        }
    }

    /// <summary>Названия действующих записей своих справочников характеристик типа «Справочник».</summary>
    private async Task<Dictionary<long, IReadOnlyList<string>>> CatalogNamesAsync(long org, IEnumerable<Characteristic> characteristics, CancellationToken ct)
    {
        var ids = characteristics.Where(c => c.CatalogId is not null).Select(c => c.CatalogId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return (await db.UserCatalogEntries.AsNoTracking().Where(e => e.OrganizationId == org && ids.Contains(e.CatalogId) && !e.IsArchived)
                .OrderBy(e => e.Name).Select(e => new { e.CatalogId, e.Name }).ToListAsync(ct))
            .GroupBy(e => e.CatalogId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.Name).ToList());
    }

    /// <summary>Характеристика в архив: у существующих модификаций значения остаются, для новых её не выбрать.</summary>
    public async Task SetCharacteristicArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, characteristic) = await LoadCharacteristicAsync(id, rowVersion, ct);
        if (!archived)
        {
            EnsureUnique(await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId).ToListAsync(ct), characteristic.Name, id);
        }

        characteristic.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(Characteristic), id,
            null, archived ? "в архиве" : "действует", characteristic.Name);
        await db.SaveOrConflictAsync(ct);
    }

    // ---------- Модификации ----------

    public async Task<ItemVariantsDto> VariantsAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        var names = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId)
            .ToDictionaryAsync(c => c.Id, c => (c.Name, c.SortOrder), ct);
        if (item.ParentItemId is { } parentId)
        {
            var parent = await db.Items.AsNoTracking().Where(i => i.Id == parentId).Select(i => new ItemRefDto(i.Id, i.Code, i.Name)).SingleAsync(ct);
            var own = await ValuesAsync(ctx, [itemId], names, ct);
            return new ItemVariantsDto(parent, own.GetValueOrDefault(itemId) ?? [], [], item.RowVersion);
        }

        var modifications = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && i.ParentItemId == itemId)
            .OrderBy(i => i.IsArchived).ThenBy(i => i.Code).Select(i => new { i.Id, i.Code, i.Name, i.IsArchived }).ToListAsync(ct);
        var ids = modifications.Select(m => m.Id).ToList();
        var values = await ValuesAsync(ctx, ids, names, ct);
        var stock = await db.StockMovements.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId && ids.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return new ItemVariantsDto(null, [], modifications
            .Select(m => new ModificationDto(m.Id, m.Code, m.Name, values.GetValueOrDefault(m.Id) ?? [], stock.GetValueOrDefault(m.Id), m.IsArchived)).ToList(),
            item.RowVersion);
    }

    /// <summary>
    /// Модификации из сочетаний значений: axes — характеристика → значения через запятую («46, 48, 50»). Уже существующие
    /// сочетания пропускаются. Код — «код основной-N», название — «название, значение, значение»; цены копируются с основной.
    /// </summary>
    public async Task<CreateModificationsResult> CreateModificationsAsync(long parentId, IReadOnlyDictionary<long, string?> axes, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var parent = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == parentId && i.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Номенклатура");
        var characteristics = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(c => c.Id, ct);
        var catalogNames = await CatalogNamesAsync(ctx.OrganizationId, characteristics.Values, ct);
        var lists = new Dictionary<long, IReadOnlyList<string>>();
        foreach (var (id, text) in axes)
        {
            var c = characteristics.GetValueOrDefault(id) ?? throw new NotFoundException("Характеристика");
            // D321: «42-56/2» раскрывается в диапазон, как в наборах значений.
            var values = string.IsNullOrWhiteSpace(text) ? [] : CharacteristicValueSet.Expand(text);
            if (values.Count > 0 && c.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Характеристика «{c.Name}» в архиве.");
            }

            // D328: значения по типу характеристики («48,0» → «48», «да» → «Да», запись справочника — как в справочнике).
            lists[id] = values.Select(v => c.Normalize(v, catalogNames.GetValueOrDefault(c.CatalogId ?? 0))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var matrix = Variants.Matrix(lists);
        if (matrix.Count == 0)
        {
            throw new BusinessRuleException("catalog.variant.no_values", "Укажите значения хотя бы одной характеристики, например размеры «46, 48, 50».");
        }

        var existingKeys = (await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && i.ParentItemId == parentId)
            .Select(i => i.VariantKey!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var fresh = matrix.Where(v => !existingKeys.Contains(Variants.Key(v))).ToList();
        if (existingKeys.Count + fresh.Count > Variants.MaxPerItem)
        {
            throw new BusinessRuleException("catalog.variant.too_many", $"У позиции — не больше {Variants.MaxPerItem} модификаций.");
        }

        var codes = (await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId).Select(i => i.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var prices = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == parentId).ToListAsync(ct);
        var number = existingKeys.Count;
        var created = new List<(Item Item, IReadOnlyDictionary<long, string> Values)>();
        foreach (var values in fresh)
        {
            string code;
            do
            {
                number++;
                var suffix = "-" + number;
                code = parent.Code[..Math.Min(parent.Code.Length, Item.CodeMaxLength - suffix.Length)] + suffix;
            }
            while (!codes.Add(code));

            var label = string.Join(", ", values.OrderBy(v => characteristics[v.Key].SortOrder).ThenBy(v => v.Key).Select(v => Variants.CleanValue(v.Value)));
            var name = $"{parent.Name}, {label}";
            var item = Item.CreateModification(parent, code, name[..Math.Min(name.Length, Item.NameMaxLength)], Variants.Key(values), clock.UtcNow);
            db.Items.Add(item);
            created.Add((item, values));
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        foreach (var (item, values) in created)
        {
            foreach (var (characteristicId, value) in values)
            {
                db.ItemCharacteristicValues.Add(ItemCharacteristicValue.Create(ctx.OrganizationId, item.Id, characteristicId, value));
            }

            foreach (var price in prices)
            {
                db.ItemPrices.Add(ItemPrice.Create(ctx.OrganizationId, item.Id, price.PriceTypeId, price.Price));
                db.ItemPriceHistory.Add(ItemPriceHistory.Create(ctx.OrganizationId, item.Id, price.PriceTypeId, null, price.Price, clock.UtcNow, ctx.UserId,
                    PriceChangeSource.Modification)); // D305
            }

            Audit(ctx, AuditActions.CatalogCreated, nameof(Item), item.Id, null, $"{item.Code} {item.Name}", $"Модификация {parent.Code}");
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CreateModificationsResult(created.Count, matrix.Count - fresh.Count);
    }

    /// <summary>Новые значения характеристик модификации; сочетание не должно повторять другую модификацию той же позиции.</summary>
    public async Task SetValuesAsync(long itemId, IReadOnlyDictionary<long, string?> values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        item.EnsureVersion(item.RowVersion, rowVersion);
        var characteristics = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(c => c.Id, ct);
        var catalogNames = await CatalogNamesAsync(ctx.OrganizationId, characteristics.Values, ct);
        var clean = new Dictionary<long, string>();
        foreach (var (id, value) in values.Where(v => !string.IsNullOrWhiteSpace(v.Value)))
        {
            var c = characteristics.GetValueOrDefault(id) ?? throw new NotFoundException("Характеристика");
            clean[id] = c.Normalize(value, catalogNames.GetValueOrDefault(c.CatalogId ?? 0)); // D328
        }

        var key = Variants.Key(clean);
        if (await db.Items.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.ParentItemId == item.ParentItemId && i.VariantKey == key
                                         && i.Id != itemId, ct))
        {
            throw new BusinessRuleException("catalog.variant.duplicate", "Модификация с такими значениями у этой позиции уже есть.");
        }

        var before = item.VariantKey;
        item.SetVariantKey(key);
        var current = await db.ItemCharacteristicValues.Where(v => v.ItemId == itemId).ToListAsync(ct);
        db.ItemCharacteristicValues.RemoveRange(current);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        foreach (var (id, value) in clean)
        {
            db.ItemCharacteristicValues.Add(ItemCharacteristicValue.Create(ctx.OrganizationId, itemId, id, value));
        }

        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), itemId, before, key, "Значения характеристик");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ---------- Фото ----------

    public async Task<IReadOnlyList<ItemPhotoDto>> PhotosAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        return await db.ItemPhotos.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == itemId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => new ItemPhotoDto(p.Id, p.ContentType, p.SizeBytes, p.SortOrder)).ToListAsync(ct);
    }

    public async Task<long> AddPhotoAsync(long itemId, byte[] content, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Номенклатура");
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве.");
        }

        var orders = await db.ItemPhotos.AsNoTracking().Where(p => p.ItemId == itemId).Select(p => p.SortOrder).ToListAsync(ct);
        if (orders.Count >= ItemPhoto.MaxPerItem)
        {
            throw new BusinessRuleException("catalog.photo.too_many", $"У позиции — не больше {ItemPhoto.MaxPerItem} фото.");
        }

        var photo = ItemPhoto.Create(ctx.OrganizationId, itemId, content, orders.Count == 0 ? 0 : orders.Max() + 1, ctx.UserId, clock.UtcNow);
        db.ItemPhotos.Add(photo);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), itemId, null, $"{photo.ContentType}, {photo.SizeBytes / 1024} КБ", "Фото добавлено");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return photo.Id;
    }

    /// <summary>Фото — признак позиции, не документ: удаляется, удаление пишется в журнал.</summary>
    public async Task RemovePhotoAsync(long photoId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var photo = await db.ItemPhotos.SingleOrDefaultAsync(p => p.Id == photoId && p.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Фото");
        db.ItemPhotos.Remove(photo);
        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), photo.ItemId, $"{photo.ContentType}, {photo.SizeBytes / 1024} КБ", null, "Фото удалено");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Сделать основным: фото встаёт первым, остальные — следом в прежнем порядке.</summary>
    public async Task MakeMainPhotoAsync(long photoId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var itemId = await db.ItemPhotos.AsNoTracking().Where(p => p.Id == photoId && p.OrganizationId == ctx.OrganizationId)
                         .Select(p => (long?)p.ItemId).SingleOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Фото");

        // Порядок меняется без загрузки самих картинок: только номера.
        var orders = await db.ItemPhotos.AsNoTracking().Where(p => p.ItemId == itemId).OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => p.Id).ToListAsync(ct);
        orders.Remove(photoId);
        orders.Insert(0, photoId);
        await using var tx = await db.BeginTransactionAsync(ct);
        for (var i = 0; i < orders.Count; i++)
        {
            var id = orders[i];
            var order = i;
            await db.ItemPhotos.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.SortOrder, order), ct);
        }

        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), itemId, null, photoId.ToString(), "Основное фото");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Содержимое фото для показа; чужая организация — «не найдено».</summary>
    public async Task<(string ContentType, byte[] Content)> GetPhotoAsync(long photoId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var photo = await db.ItemPhotos.AsNoTracking().Where(p => p.Id == photoId && p.OrganizationId == ctx.OrganizationId)
                        .Select(p => new { p.ContentType, p.Content }).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Фото");
        return (photo.ContentType, photo.Content);
    }

    private async Task<Dictionary<long, IReadOnlyList<VariantValueDto>>> ValuesAsync(
        AccessContext ctx, IReadOnlyCollection<long> itemIds, IReadOnlyDictionary<long, (string Name, int SortOrder)> names, CancellationToken ct) =>
        (await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == ctx.OrganizationId && itemIds.Contains(v.ItemId)).ToListAsync(ct))
        .GroupBy(v => v.ItemId)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<VariantValueDto>)g
            .OrderBy(v => names.TryGetValue(v.CharacteristicId, out var n) ? n.SortOrder : int.MaxValue).ThenBy(v => v.CharacteristicId)
            .Select(v => new VariantValueDto(v.CharacteristicId, names.TryGetValue(v.CharacteristicId, out var n) ? n.Name : "?", v.Value)).ToList());

    private async Task<(AccessContext, Characteristic)> LoadCharacteristicAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var characteristic = await db.Characteristics.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
                             ?? throw new NotFoundException("Характеристика");
        return (ctx, characteristic.EnsureVersion(characteristic.RowVersion, rowVersion));
    }

    private static void EnsureUnique(IEnumerable<Characteristic> all, string name, long? exceptId)
    {
        if (all.Any(c => !c.IsArchived && c.Id != exceptId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException("catalog.characteristic.duplicate", $"Характеристика «{name}» уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
