using KnitErp.Application.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Встроенные значения справочников «Категории изделий» и «Виды рисунков» (D733). Существующим организациям их создала миграция;
/// новой организации — первый вызов разработки. Значения можно переименовать, отправить в архив и дополнить своими.
/// </summary>
internal static class DevLookupDefaults
{
    private static readonly DevLookupKind[] Seeded = [DevLookupKind.Category, DevLookupKind.PatternKind];

    /// <summary>Создать встроенные значения, если у организации нет ни одной записи такого справочника.</summary>
    public static async Task EnsureAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct)
    {
        var present = await db.DevLookups.Where(l => l.OrganizationId == organizationId && Seeded.Contains(l.Kind))
            .Select(l => l.Kind).Distinct().ToListAsync(ct);
        var added = new List<DevLookup>();
        foreach (var kind in Seeded.Except(present))
        {
            added.AddRange(DevLookup.BuiltIn(kind).Select((x, i) => DevLookup.Create(organizationId, kind, x.Name, x.Code, (i + 1) * 10)));
        }

        if (added.Count == 0)
        {
            return;
        }

        db.DevLookups.AddRange(added);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Соседняя вкладка успела создать те же записи (уникальное название) — берём её.
            db.DiscardChanges();
        }
    }

    /// <summary>Записи справочника для выбора: действующие, по порядку; архивная остаётся у объекта, который на неё уже ссылается.</summary>
    public static async Task<IReadOnlyList<DevOptionDto>> OptionsAsync(IKnitErpDbContext db, long organizationId, DevLookupKind kind, CancellationToken ct) =>
        await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == organizationId && l.Kind == kind && !l.IsArchived)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => new DevOptionDto(l.Id, l.Name)).ToListAsync(ct);

    /// <summary>Запись своей организации и нужного справочника; новая ссылка — только на действующую.</summary>
    public static async Task<DevLookup> RequireAsync(IKnitErpDbContext db, long organizationId, DevLookupKind kind, long id, long? currentId, string code,
        string message, CancellationToken ct)
    {
        var l = await db.DevLookups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.Kind == kind, ct);
        if (l is null || (l.IsArchived && id != currentId))
        {
            throw new KnitErp.Domain.Common.BusinessRuleException(code, message);
        }

        return l;
    }
}
