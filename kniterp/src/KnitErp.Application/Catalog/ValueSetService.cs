using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record ValueSetDto(long Id, long CharacteristicId, string Characteristic, string Name, IReadOnlyList<string> Values, bool IsArchived, byte[] RowVersion);

/// <summary>
/// Наборы значений характеристик (D321): смотреть — «Справочники: просмотр», менять — «Справочники: изменение»; изменения — в журнал.
/// «Добавить готовые» создаёт недостающие характеристики («Размер», «Цвет») и образцы наборов.
/// </summary>
public sealed class ValueSetService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<ValueSetDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var rows = await (from v in db.CharacteristicValueSets.AsNoTracking()
                          join c in db.Characteristics.AsNoTracking() on v.CharacteristicId equals c.Id
                          where v.OrganizationId == ctx.OrganizationId && (includeArchived || (!v.IsArchived && !c.IsArchived))
                          orderby c.SortOrder, v.Name
                          select new { v, c.Name }).ToListAsync(ct);
        return rows.Select(r => new ValueSetDto(r.v.Id, r.v.CharacteristicId, r.Name, r.v.Name, r.v.Values, r.v.IsArchived, r.v.RowVersion)).ToList();
    }

    public async Task<long> CreateAsync(long characteristicId, string? name, string? values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await RequireCharacteristicAsync(ctx.OrganizationId, characteristicId, ct);
        if (await db.CharacteristicValueSets.CountAsync(v => v.OrganizationId == ctx.OrganizationId, ct) >= CharacteristicValueSet.MaxPerOrganization)
        {
            throw new BusinessRuleException("catalog.value_set.limit", "Не больше 100 наборов значений — отправьте ненужные в архив и измените их.");
        }

        var set = CharacteristicValueSet.Create(ctx.OrganizationId, characteristicId, name, values);
        db.CharacteristicValueSets.Add(set);
        await db.SaveChangesAsync(ct);
        Audit(ctx, set, null, "Набор значений создан");
        await db.SaveChangesAsync(ct);
        return set.Id;
    }

    public async Task UpdateAsync(long id, string? name, string? values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var set = await FindAsync(ctx.OrganizationId, id, ct);
        set.EnsureVersion(set.RowVersion, rowVersion);
        var before = $"{set.Name}: {set.ValuesText}";
        try
        {
            set.Set(name, values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, set, before, "Набор значений изменён");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var set = await FindAsync(ctx.OrganizationId, id, ct);
        set.EnsureVersion(set.RowVersion, rowVersion);
        set.SetArchived(archived);
        Audit(ctx, set, null, archived ? "Набор значений в архиве" : "Набор значений снова действует");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Готовые наборы (размеры, цвета) — создаются недостающие; существующие с тем же названием не трогаются. Возвращает число созданных.</summary>
    public async Task<int> AddPresetsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var org = ctx.OrganizationId;
        var characteristics = await db.Characteristics.Where(c => c.OrganizationId == org).ToListAsync(ct);
        var existing = await db.CharacteristicValueSets.AsNoTracking().Where(v => v.OrganizationId == org).Select(v => new { v.CharacteristicId, v.Name }).ToListAsync(ct);
        var created = 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var (charName, setName, values) in CharacteristicValueSet.Presets)
        {
            var c = characteristics.FirstOrDefault(x => !x.IsArchived && string.Equals(x.Name, charName, StringComparison.OrdinalIgnoreCase));
            if (c is null)
            {
                if (characteristics.Count(x => !x.IsArchived) >= Characteristic.MaxCharacteristics)
                {
                    continue;
                }

                c = Characteristic.Create(org, charName, characteristics.Count + 1);
                db.Characteristics.Add(c);
                await db.SaveChangesAsync(ct);
                characteristics.Add(c);
            }

            if (existing.Any(e => e.CharacteristicId == c.Id && string.Equals(e.Name, setName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var set = CharacteristicValueSet.Create(org, c.Id, setName, values);
            db.CharacteristicValueSets.Add(set);
            await db.SaveChangesAsync(ct);
            Audit(ctx, set, null, "Готовый набор значений добавлен");
            created++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return created;
    }

    private async Task RequireCharacteristicAsync(long org, long id, CancellationToken ct)
    {
        var c = await db.Characteristics.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new NotFoundException("Характеристика");
        if (c.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Характеристика «{c.Name}» в архиве.");
        }
    }

    private async Task<CharacteristicValueSet> FindAsync(long org, long id, CancellationToken ct) =>
        await db.CharacteristicValueSets.SingleOrDefaultAsync(v => v.Id == id && v.OrganizationId == org, ct) ?? throw new NotFoundException("Набор значений");

    private void Audit(AccessContext ctx, CharacteristicValueSet set, string? before, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(CharacteristicValueSet),
            set.Id.ToString(), before, $"{set.Name}: {set.ValuesText}", reason, currentUser.CorrelationId));
}
