using KnitErp.Application.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Common;

/// <summary>Дополнительное поле в настройках. CatalogId/Catalog — свой справочник поля типа «Справочник» (D79).</summary>
public sealed record CustomFieldDto(
    long Id, string Name, CustomFieldType Type, int SortOrder, bool IsArchived, int Filled, byte[] RowVersion, long? CatalogId = null,
    string? Catalog = null);

/// <summary>
/// Общая логика дополнительных полей (D77, D79) для любого вида документа или справочника: список, добавление, переименование,
/// порядок, архив. Право проверяет вызывающий сервис; здесь — только своя организация и свой вид (<see cref="CustomFieldTarget"/>).
/// </summary>
internal sealed class CustomFieldStore(IKnitErpDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<CustomFieldDto>> ListAsync(AccessContext ctx, CustomFieldTarget target, bool includeArchived, CancellationToken ct)
    {
        var filled = await db.CustomFieldValues.AsNoTracking().Where(v => v.OrganizationId == ctx.OrganizationId)
            .GroupBy(v => v.FieldId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var catalogs = await db.UserCatalogs.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var rows = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.OrganizationId == ctx.OrganizationId && f.Target == target && (includeArchived || !f.IsArchived))
            .OrderBy(f => f.IsArchived).ThenBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        return rows.Select(f => new CustomFieldDto(f.Id, f.Name, f.Type, f.SortOrder, f.IsArchived, filled.GetValueOrDefault(f.Id), f.RowVersion,
            f.CatalogId, f.CatalogId is { } c ? catalogs.GetValueOrDefault(c) : null)).ToList();
    }

    public async Task<long> CreateAsync(AccessContext ctx, CustomFieldTarget target, string? name, CustomFieldType type, long? catalogId, string reason,
        CancellationToken ct)
    {
        var fields = db.CustomFieldDefinitions.Where(f => f.OrganizationId == ctx.OrganizationId && f.Target == target);
        if (await fields.CountAsync(f => !f.IsArchived, ct) >= CustomFieldDefinition.MaxFields)
        {
            throw new BusinessRuleException("custom_field.too_many", $"Дополнительных полей не больше {CustomFieldDefinition.MaxFields}.");
        }

        if (catalogId is { } cid)
        {
            var catalog = await db.UserCatalogs.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid && c.OrganizationId == ctx.OrganizationId, ct)
                          ?? throw new NotFoundException("Справочник");
            if (catalog.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Справочник «{catalog.Name}» в архиве.");
            }
        }

        var last = await fields.MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var field = CustomFieldDefinition.Create(ctx.OrganizationId, target, name, type, last + 10, type == CustomFieldType.Catalog ? catalogId : null);
        await EnsureFreeAsync(ctx, target, field.Name, null, ct);
        db.CustomFieldDefinitions.Add(field);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, field.Id, null, $"{field.Name} ({CustomFieldDefinition.TypeName(field.Type)})", reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return field.Id;
    }

    public async Task RenameAsync(AccessContext ctx, CustomFieldTarget target, long id, string? name, byte[] rowVersion, string reason, CancellationToken ct)
    {
        var field = await LoadAsync(ctx, target, id, rowVersion, ct);
        var before = field.Name;
        field.Rename(name);
        await EnsureFreeAsync(ctx, target, field.Name, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, id, before, field.Name, reason);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Сдвиг на одну позицию вверх (-1) или вниз (+1) среди действующих полей.</summary>
    public async Task MoveAsync(AccessContext ctx, CustomFieldTarget target, long id, int delta, CancellationToken ct)
    {
        var fields = await db.CustomFieldDefinitions.Where(f => f.OrganizationId == ctx.OrganizationId && f.Target == target && !f.IsArchived)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        var index = fields.FindIndex(f => f.Id == id);
        if (index < 0)
        {
            throw new NotFoundException("Дополнительное поле");
        }

        var to = Math.Clamp(index + Math.Sign(delta), 0, fields.Count - 1);
        (fields[index], fields[to]) = (fields[to], fields[index]);
        for (var i = 0; i < fields.Count; i++)
        {
            fields[i].MoveTo((i + 1) * 10);
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(AccessContext ctx, CustomFieldTarget target, long id, bool archived, byte[] rowVersion, string reason,
        CancellationToken ct)
    {
        var field = await LoadAsync(ctx, target, id, rowVersion, ct);
        if (!archived)
        {
            await EnsureFreeAsync(ctx, target, field.Name, id, ct);
        }

        field.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, id,
            archived ? field.Name : "в архиве", archived ? "в архиве" : field.Name, reason);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Значения полей у записи: пустое удаляет, архивное поле не меняется, справочное — только действующая запись своего справочника.
    /// Возвращает изменения для журнала; сохраняет вызывающий.
    /// </summary>
    public async Task<IReadOnlyList<string>> SetValuesAsync(AccessContext ctx, CustomFieldTarget target, long targetId,
        IReadOnlyDictionary<long, string?> values, CancellationToken ct)
    {
        var fields = await db.CustomFieldDefinitions.Where(f => f.OrganizationId == ctx.OrganizationId && f.Target == target)
            .ToDictionaryAsync(f => f.Id, ct);
        var fieldIds = fields.Keys.ToList();
        var existing = await db.CustomFieldValues.Where(v => v.OrganizationId == ctx.OrganizationId && v.TargetId == targetId && fieldIds.Contains(v.FieldId))
            .ToDictionaryAsync(v => v.FieldId, ct);
        var changes = new List<string>();
        foreach (var (fieldId, input) in values)
        {
            var field = fields.GetValueOrDefault(fieldId) ?? throw new NotFoundException("Дополнительное поле");
            if (field.IsArchived)
            {
                continue;
            }

            var value = field.Normalize(input);
            var current = existing.GetValueOrDefault(fieldId);
            if (value == current?.Value)
            {
                continue;
            }

            // D328: контрагент — своей организации и не в архиве.
            if (value is not null && field.Type == CustomFieldType.Counterparty)
            {
                var counterpartyId = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                var counterparty = await db.Counterparties.AsNoTracking()
                                       .SingleOrDefaultAsync(c => c.Id == counterpartyId && c.OrganizationId == ctx.OrganizationId, ct)
                                   ?? throw new NotFoundException("Контрагент");
                if (counterparty.IsArchived)
                {
                    throw new BusinessRuleException("catalog.archived", $"«{counterparty.Name}» в архиве.");
                }
            }

            if (value is not null && field.Type == CustomFieldType.Catalog)
            {
                var entryId = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                var entry = await db.UserCatalogEntries.AsNoTracking()
                                .SingleOrDefaultAsync(e => e.Id == entryId && e.OrganizationId == ctx.OrganizationId && e.CatalogId == field.CatalogId, ct)
                            ?? throw new NotFoundException("Запись справочника");
                if (entry.IsArchived)
                {
                    throw new BusinessRuleException("catalog.archived", $"«{entry.Name}» в архиве.");
                }
            }

            if (value is null)
            {
                db.CustomFieldValues.Remove(current!);
            }
            else if (current is null)
            {
                db.CustomFieldValues.Add(CustomFieldValue.Create(ctx.OrganizationId, fieldId, targetId, value));
            }
            else
            {
                current.Set(value);
            }

            changes.Add($"{field.Name}: {current?.Value ?? "—"} → {value ?? "—"}");
        }

        return changes;
    }

    /// <summary>Значения полей записи для показа: действующие поля и архивные, если заполнены; справочные — с названием записи.</summary>
    public async Task<IReadOnlyList<CustomValueDto>> ValuesAsync(AccessContext ctx, CustomFieldTarget target, long targetId, CancellationToken ct)
    {
        var fields = await db.CustomFieldDefinitions.AsNoTracking().Where(f => f.OrganizationId == ctx.OrganizationId && f.Target == target)
            .OrderBy(f => f.IsArchived).ThenBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        var fieldIds = fields.Select(f => f.Id).ToList();
        var values = await db.CustomFieldValues.AsNoTracking()
            .Where(v => v.OrganizationId == ctx.OrganizationId && v.TargetId == targetId && fieldIds.Contains(v.FieldId))
            .ToDictionaryAsync(v => v.FieldId, v => v.Value, ct);
        var entryIds = fields.Where(f => f.Type == CustomFieldType.Catalog && values.ContainsKey(f.Id))
            .Select(f => long.Parse(values[f.Id], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var entries = await db.UserCatalogEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var counterpartyIds = fields.Where(f => f.Type == CustomFieldType.Counterparty && values.ContainsKey(f.Id))
            .Select(f => long.Parse(values[f.Id], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var counterparties = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && counterpartyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return fields.Where(f => !f.IsArchived || values.ContainsKey(f.Id)).Select(f =>
        {
            var value = values.GetValueOrDefault(f.Id);
            string? display = value is null ? null : f.Type switch
            {
                CustomFieldType.Catalog when entries.TryGetValue(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture), out var e) => e.Display,
                CustomFieldType.Counterparty when counterparties.TryGetValue(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture), out var name) => name,
                _ => null,
            };
            return new CustomValueDto(f.Id, f.Name, f.Type, f.IsArchived, value, f.CatalogId, display);
        }).ToList();
    }

    private async Task<CustomFieldDefinition> LoadAsync(AccessContext ctx, CustomFieldTarget target, long id, byte[] rowVersion, CancellationToken ct)
    {
        var field = await db.CustomFieldDefinitions.SingleOrDefaultAsync(f => f.Id == id && f.OrganizationId == ctx.OrganizationId && f.Target == target, ct)
                    ?? throw new NotFoundException("Дополнительное поле");
        return field.EnsureVersion(field.RowVersion, rowVersion);
    }

    private async Task EnsureFreeAsync(AccessContext ctx, CustomFieldTarget target, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.CustomFieldDefinitions.AnyAsync(f => f.OrganizationId == ctx.OrganizationId && f.Target == target && !f.IsArchived
                                                         && f.Name == name && f.Id != exceptId, ct))
        {
            throw new BusinessRuleException("custom_field.duplicate", $"Поле «{name}» уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CustomFieldDefinition), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}

/// <summary>Значение доп. поля у записи. Display — название записи для поля «Справочник».</summary>
public sealed record CustomValueDto(long FieldId, string Name, CustomFieldType Type, bool IsArchived, string? Value, long? CatalogId = null,
    string? Display = null);
