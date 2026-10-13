using KnitErp.Application.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Встроенные значения справочников разработки (D733, D734): категории, виды рисунков, переплетения, классы машин, причины доработок,
/// точки измерения, производители и типы оборудования, детали, пряжа, составы, отделка, размерные шкалы. Организация получает их при
/// первом обращении к разработке, а новые значения каталога — при следующем, без дублей: встроенная запись узнаётся по коду, своя —
/// по названию. Значения можно переименовать, отправить в архив и дополнить своими.
/// </summary>
internal static class DevLookupDefaults
{
    private static readonly DevLookupKind[] Seeded = Enum.GetValues<DevLookupKind>().Where(k => DevLookup.BuiltIn(k).Count > 0).ToArray();

    /// <summary>Добавить организации недостающие встроенные значения.</summary>
    public static async Task EnsureAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct)
    {
        var existing = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == organizationId && Seeded.Contains(l.Kind))
            .Select(l => new { l.Kind, l.Number, l.Name, l.SortOrder }).ToListAsync(ct);
        var added = new List<DevLookup>();
        foreach (var kind in Seeded)
        {
            var mine = existing.Where(e => e.Kind == kind).ToList();
            var codes = DevLookupCatalog.HasCodes(kind) ? mine.Where(e => e.Number is not null).Select(e => e.Number!.Value).ToHashSet() : [];
            var names = mine.Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sort = mine.Count == 0 ? 0 : mine.Max(e => e.SortOrder);
            foreach (var (code, name) in DevLookup.BuiltIn(kind))
            {
                if ((code is { } c && codes.Contains(c)) || names.Contains(name))
                {
                    continue;
                }

                // Встроенные — в порядке каталога; добавленные позже — после записей организации.
                sort = mine.Count == 0 ? (added.Count(a => a.Kind == kind) + 1) * 10 : sort + 10;
                added.Add(DevLookup.Create(organizationId, kind, name, DevLookupCatalog.HasCodes(kind) ? code : null, sort));
                names.Add(name);
            }
        }

        if (added.Count > 0)
        {
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
    }

    /// <summary>Записи справочника для выбора: действующие, по порядку; архивная остаётся у объекта, который на неё уже ссылается.</summary>
    public static async Task<IReadOnlyList<DevOptionDto>> OptionsAsync(IKnitErpDbContext db, long organizationId, DevLookupKind kind, CancellationToken ct) =>
        await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == organizationId && l.Kind == kind && !l.IsArchived)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => new DevOptionDto(l.Id, l.Name)).ToListAsync(ct);

    /// <summary>Названия действующих записей — подсказки полей ввода (деталь, пряжа, состав, отделка, шкала).</summary>
    public static async Task<IReadOnlyList<string>> NamesAsync(IKnitErpDbContext db, long organizationId, DevLookupKind kind, CancellationToken ct) =>
        (await OptionsAsync(db, organizationId, kind, ct)).Select(o => o.Name).ToList();

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
