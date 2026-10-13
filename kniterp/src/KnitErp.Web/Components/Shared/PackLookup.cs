using KnitErp.Application.Catalog;

namespace KnitErp.Web.Components.Shared;

/// <summary>D353 (сессия 4, Р4): упаковки позиций, загруженные страницей документа один раз (PackService.AllAsync).</summary>
public static class PackLookup
{
    public static readonly IReadOnlyDictionary<long, IReadOnlyList<ItemPackDto>> Empty = new Dictionary<long, IReadOnlyList<ItemPackDto>>();

    public static IReadOnlyList<ItemPackDto>? Of(IReadOnlyDictionary<long, IReadOnlyList<ItemPackDto>> packs, long? itemId) =>
        itemId is { } id && packs.TryGetValue(id, out var list) ? list : null;

    /// <summary>Упаковка по штрихкоду (скан в поле «Позиция»).</summary>
    public static ItemPackDto? ByBarcode(IReadOnlyDictionary<long, IReadOnlyList<ItemPackDto>> packs, string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : packs.Values.SelectMany(p => p).FirstOrDefault(p => p.Barcode == code.Trim());
}
