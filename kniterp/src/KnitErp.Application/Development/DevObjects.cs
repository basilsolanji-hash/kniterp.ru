using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Application.Tagging;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Объекты «Разработки» в тегах и обсуждениях (D117, D719): деталь — обсуждение техкарты у машины («нитеводитель 6 заедает»),
/// станок — комментарии по машине. Видят те же, кто видит техкарту: разработка или цех. Без цен и себестоимости.
/// </summary>
public static class DevObjects
{
    public static void Register()
    {
        ObjectDirectory.Register(TaggedEntity.DevDetail, DescribeDetailsAsync, Permissions.DevEdit, Permissions.DevPattern);
        ObjectDirectory.Register(TaggedEntity.DevMachine, DescribeMachinesAsync, Permissions.DevSettings);
    }

    private static bool CanSee(AccessContext ctx) =>
        ctx.Permissions.Has(Permissions.DevView) || ctx.Permissions.Has(Permissions.ProductionStageMark);

    private static async Task<IReadOnlyList<TaggedObjectDto>> DescribeDetailsAsync(IKnitErpDbContext db, AccessContext ctx, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (!CanSee(ctx))
        {
            return [];
        }

        var org = ctx.OrganizationId;
        return (await (from d in db.DevDetails.AsNoTracking()
                       join m in db.DevModels.AsNoTracking() on d.ModelId equals m.Id
                       where d.OrganizationId == org && m.OrganizationId == org && ids.Contains(d.Id)
                       select new { d.Id, d.Name, m.Number, Model = m.Name }).ToListAsync(ct))
            .Select(d => new TaggedObjectDto(TaggedEntity.DevDetail, d.Id, $"{d.Number} · {d.Name}", d.Model, $"dev/tech/{d.Id}")).ToList();
    }

    private static async Task<IReadOnlyList<TaggedObjectDto>> DescribeMachinesAsync(IKnitErpDbContext db, AccessContext ctx, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (!CanSee(ctx))
        {
            return [];
        }

        var org = ctx.OrganizationId;
        return (await db.DevMachines.AsNoTracking().Where(m => m.OrganizationId == org && ids.Contains(m.Id))
                .Select(m => new { m.Id, m.Name, m.Model, m.Gauge }).ToListAsync(ct))
            .Select(m => new TaggedObjectDto(TaggedEntity.DevMachine, m.Id, m.Name, m.Model is null ? $"E{m.Gauge}" : $"{m.Model} · E{m.Gauge}", "dev/machines")).ToList();
    }
}
