using System.Text.Json;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Workspace;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Вид списка номенклатуры пользователя: сортировка и скрытые столбцы (D338).</summary>
public sealed record CatalogViewDto(ItemSort Sort = ItemSort.Default, bool Descending = false, IReadOnlyList<string>? Hidden = null)
{
    /// <summary>Столбцы, которые можно скрыть; код и наименование видны всегда.</summary>
    public static readonly IReadOnlyList<string> Optional = ["article", "type", "unit", "vat", "price"];

    public bool Shows(string column) => Hidden is null || !Hidden.Contains(column);
}

/// <summary>
/// D338: личный вид списка номенклатуры — у каждого пользователя свой, на любом устройстве (личные данные, как панель инструментов).
/// Свой DbContext на операцию: вызывается поверх открытой страницы. Неизвестные столбцы отбрасываются, повреждённые данные — вид по умолчанию.
/// </summary>
public sealed class CatalogViewService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    private const string Kind = "catalog-view";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CatalogViewDto> GetAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).DemandAsync(Permissions.CatalogView, ct);
        var json = await db.UserToolData.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == Kind).Select(d => d.Json).SingleOrDefaultAsync(ct);
        try
        {
            return json is null ? new CatalogViewDto() : Clean(JsonSerializer.Deserialize<CatalogViewDto>(json, Json));
        }
        catch (JsonException)
        {
            return new CatalogViewDto();
        }
    }

    public async Task<CatalogViewDto> SaveAsync(CatalogViewDto view, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).DemandAsync(Permissions.CatalogView, ct);
        var clean = Clean(view);
        var json = JsonSerializer.Serialize(clean, Json);
        var row = await db.UserToolData.SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == Kind, ct);
        if (row is null)
        {
            db.UserToolData.Add(UserToolData.Create(ctx.OrganizationId, ctx.UserId, Kind, json, clock.UtcNow));
        }
        else
        {
            row.Update(json, clock.UtcNow);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Сохранение с другого устройства в ту же секунду: личная настройка — побеждает последнее (как D47), не ошибка.
        }

        return clean;
    }

    private static CatalogViewDto Clean(CatalogViewDto? v) => v is null
        ? new CatalogViewDto()
        : new CatalogViewDto(Enum.IsDefined(v.Sort) ? v.Sort : ItemSort.Default, v.Descending,
            (v.Hidden ?? []).Where(CatalogViewDto.Optional.Contains).Distinct().ToList());
}
