using KnitErp.Application.Catalog;
using KnitErp.Domain.Catalog;

namespace KnitErp.Web.Api;

/// <summary>
/// API v1 каталога (D307, сессия № 4) — отдельным файлом-расширением, существующие методы ApiV1 не переписываются:
/// номенклатура с видом, маркировкой, упаковками и составом комплекта; доступность позиции (у комплекта — по компонентам).
/// </summary>
public static class ApiCatalog
{
    public const int MaxLimit = 500;

    public static void MapCatalogApi(this RouteGroupBuilder api)
    {
        api.MapGet("/catalog/items", async (CatalogService catalog, CatalogQueryService query, string? search, ItemType? type, ItemKind? kind, long? groupId,
                long? tagId, bool? includeArchived, int? offset, int? limit, CancellationToken ct) =>
            {
                var o = Math.Max(0, offset ?? 0);
                var l = Math.Clamp(limit ?? 100, 1, MaxLimit);
                var list = await catalog.ListItemsAsync(new ItemFilter(type, search, includeArchived ?? false, l, groupId, tagId, kind, Skip: o), ct);
                return ApiResult.Ok(new Page<CatalogItemDto>(await query.EnrichAsync(list.Items, ct), o, l,
                    o + list.Items.Count < list.Total ? o + list.Items.Count : null));
            })
            .WithSummary("Номенклатура для обмена: вид, предмет расчёта, маркировка, GTIN, упаковки, состав комплекта");

        api.MapGet("/items/{id:long}/availability", async (CatalogQueryService query, long id, CancellationToken ct) =>
                ApiResult.Ok(await query.AvailabilityAsync(id, ct)))
            .WithSummary("Доступность позиции по складам: остаток − резерв; у комплекта — сколько наборов можно собрать");

        api.MapGet("/items/{id:long}/packs", async (PackService packs, long id, CancellationToken ct) => ApiResult.Ok(await packs.ListAsync(id, ct)))
            .WithSummary("Упаковки позиции: единица, коэффициент к основной единице, штрихкод");

        api.MapGet("/barcodes/{code}", async (PackService packs, string code, CancellationToken ct) =>
                await packs.ScanAsync(code, ct) is { } hit ? ApiResult.Ok(hit) : new ApiResult(StatusCodes.Status404NotFound, null))
            .WithSummary("Позиция по штрихкоду (позиции или упаковки) и количество в основных единицах");
    }
}
