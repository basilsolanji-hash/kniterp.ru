using KnitErp.Application.Development;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D733: id встроенной записи справочника разработки (категории изделий, виды рисунков) организации — создаёт их при первом вызове.</summary>
internal static class DevTestLookups
{
    public static long Id(SqlTestHost host, long organizationId, DevLookupKind kind, int code)
    {
        using var db = host.NewDb();
        DevLookupDefaults.EnsureAsync(db, organizationId, CancellationToken.None).GetAwaiter().GetResult();
        return db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == organizationId && l.Kind == kind && l.Number == code).Select(l => l.Id).Single();
    }

    public static long Category(SqlTestHost host, long organizationId, DevCategory category) => Id(host, organizationId, DevLookupKind.Category, (int)category);

    public static long PatternKind(SqlTestHost host, long organizationId, DevPatternKind kind) => Id(host, organizationId, DevLookupKind.PatternKind, (int)kind);
}
