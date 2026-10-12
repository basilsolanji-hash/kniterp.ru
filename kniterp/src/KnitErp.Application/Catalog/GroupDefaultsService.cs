using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Свои значения группы и итоговые с учётом родителей; RowVersion пустой — своих значений ещё нет.</summary>
public sealed record GroupDefaultsDto(long GroupId, GroupDefaultValues Own, GroupDefaultValues Effective, byte[] RowVersion);

/// <summary>
/// Значения по умолчанию групп (D317): смотреть — «Справочники: просмотр», менять — «Справочники: изменение»; изменение — в журнал.
/// Единица и ставка НДС проверяются: своей организации и действующие.
/// </summary>
public sealed class GroupDefaultsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<GroupDefaultsDto> GetAsync(long groupId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        await RequireGroupAsync(ctx.OrganizationId, groupId, ct);
        var own = await db.ItemGroupDefaults.AsNoTracking().SingleOrDefaultAsync(d => d.GroupId == groupId && d.OrganizationId == ctx.OrganizationId, ct);
        return new GroupDefaultsDto(groupId, own?.Values ?? GroupDefaultValues.Empty, await ResolveAsync(db, ctx.OrganizationId, groupId, ct), own?.RowVersion ?? []);
    }

    public async Task SaveAsync(long groupId, GroupDefaultValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var org = ctx.OrganizationId;
        var group = await RequireGroupAsync(org, groupId, ct);
        if (values.UnitId is { } u && !await db.Units.AnyAsync(x => x.Id == u && x.OrganizationId == org && !x.IsArchived, ct))
        {
            throw new NotFoundException("Единица измерения");
        }

        if (values.VatRateId is { } v && !await db.VatRates.AnyAsync(x => x.Id == v && x.OrganizationId == org, ct))
        {
            throw new NotFoundException("Ставка НДС");
        }

        var row = await db.ItemGroupDefaults.SingleOrDefaultAsync(d => d.GroupId == groupId && d.OrganizationId == org, ct);
        if (row is null)
        {
            row = ItemGroupDefaults.Create(org, groupId);
            db.ItemGroupDefaults.Add(row);
        }
        else
        {
            row.EnsureVersion(row.RowVersion, rowVersion);
        }

        var before = Describe(row.Values);
        try
        {
            row.Set(values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogChanged, nameof(ItemGroup), groupId.ToString(), before,
            Describe(row.Values), $"Значения по умолчанию группы «{group.Name}»", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Итоговые значения группы: своя, затем родители до корня (ближайшее заданное побеждает).</summary>
    internal static async Task<GroupDefaultValues> ResolveAsync(IKnitErpDbContext db, long org, long? groupId, CancellationToken ct)
    {
        if (groupId is null)
        {
            return GroupDefaultValues.Empty;
        }

        var parents = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
        var defaults = await db.ItemGroupDefaults.AsNoTracking().Where(d => d.OrganizationId == org).ToDictionaryAsync(d => d.GroupId, d => d.Values, ct);
        if (defaults.Count == 0)
        {
            return GroupDefaultValues.Empty;
        }

        var chain = new List<GroupDefaultValues>();
        for (long? id = groupId; id is { } g && chain.Count <= ItemGroup.MaxDepth && parents.ContainsKey(g); id = parents[g])
        {
            if (defaults.TryGetValue(g, out var v))
            {
                chain.Add(v);
            }
        }

        return GroupDefaultValues.Resolve(chain);
    }

    private async Task<ItemGroup> RequireGroupAsync(long org, long groupId, CancellationToken ct) =>
        await db.ItemGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == org, ct) ?? throw new NotFoundException("Группа номенклатуры");

    private static string Describe(GroupDefaultValues v) =>
        v.IsEmpty ? "не заданы" : string.Join("; ", new[]
        {
            v.Type is { } t ? "тип " + ItemTypes.Name(t) : null, v.UnitId is { } u ? "единица #" + u : null, v.VatRateId is { } r ? "НДС #" + r : null,
            v.TnVedCode is { } tn ? "ТН ВЭД " + tn : null, v.OriginCountryCode is { } c ? "страна " + c : null,
            v.Tracking is { } m ? "маркировка " + Marking.Name(m) : null,
        }.Where(x => x is not null));
}
