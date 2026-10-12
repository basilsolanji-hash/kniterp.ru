using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnitErp.Application.Exchange.MoySklad;

// Модели JSON API МойСклад 1.2 (https://api.moysklad.ru/api/remap/1.2/) — только поля, нужные для загрузки справочников (D310).
// Цены в МойСклад — в копейках (value), количества и вес — числа. Ссылки на другие объекты — meta.href, id берётся из его конца;
// по ссылкам из ответа knitERP никогда не ходит (защита от подмены адреса).

public sealed record MsMeta(string? Href, string? Type, int? Size, int? Limit, int? Offset);

public sealed record MsRef(MsMeta? Meta, string? Id = null, string? Name = null)
{
    /// <summary>Id объекта по ссылке: последний сегмент meta.href без параметров.</summary>
    public string? RefId => Id ?? MoySkladIds.FromHref(Meta?.Href);
}

public sealed record MsPage<T>(MsMeta? Meta, IReadOnlyList<T>? Rows);

public sealed record MsPrice(decimal? Value, MsRef? PriceType);

public sealed record MsPack(string? Id, MsRef? Uom, decimal? Quantity, IReadOnlyList<Dictionary<string, string>>? Barcodes);

public sealed record MsCharacteristic(string? Name, string? Value);

public sealed record MsComponent(MsRef? Assortment, decimal? Quantity);

public sealed record MsComponents(MsMeta? Meta, IReadOnlyList<MsComponent>? Rows);

/// <summary>Строка товара, услуги, комплекта, модификации, группы, единицы или страны — общая модель, лишние поля null.</summary>
public sealed record MsRow
{
    public string Id { get; init; } = string.Empty;
    public MsMeta? Meta { get; init; }
    public string? Updated { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Code { get; init; }
    public string? ExternalCode { get; init; }
    public bool Archived { get; init; }
    public string? PathName { get; init; }
    public string? Article { get; init; }
    public int? Vat { get; init; }
    public bool? VatEnabled { get; init; }
    public int? EffectiveVat { get; init; }
    public MsRef? ProductFolder { get; init; }
    public MsRef? Uom { get; init; }
    public MsRef? Country { get; init; }
    public MsRef? Product { get; init; }
    public IReadOnlyList<MsPrice>? SalePrices { get; init; }
    public MsPrice? BuyPrice { get; init; }
    public MsPrice? MinPrice { get; init; }
    public IReadOnlyList<Dictionary<string, string>>? Barcodes { get; init; }
    public decimal? Weight { get; init; }
    public decimal? Volume { get; init; }
    public string? Tnved { get; init; }
    public string? TrackingType { get; init; }
    public string? PaymentItemType { get; init; }
    public IReadOnlyList<MsPack>? Packs { get; init; }
    public IReadOnlyList<MsCharacteristic>? Characteristics { get; init; }
    public MsComponents? Components { get; init; }

    /// <summary>D332: доп. поля объекта МойСклад (type: string, text, long, double, boolean, time, link, customentity, counterparty…).</summary>
    public IReadOnlyList<MsAttribute>? Attributes { get; init; }

    /// <summary>D333: остаток в отчёте report/stock/all (по всем складам).</summary>
    public decimal? Stock { get; init; }

    /// <summary>D336: остатки по складам в отчёте report/stock/bystore.</summary>
    public IReadOnlyList<MsStoreStock>? StockByStore { get; init; }
}

public sealed record MsPriceType(string Id, string? Name, string? ExternalCode);

/// <summary>D336: остаток позиции на одном складе МойСклад (meta.href склада, название, количество).</summary>
public sealed record MsStoreStock(MsMeta? Meta, string? Name, decimal? Stock);

/// <summary>Доп. поле в строке объекта: значение — строка, число, логическое или объект ({ "name": … } у справочника и контрагента).</summary>
public sealed record MsAttribute(string? Id, string? Name, string? Type, JsonElement Value);

/// <summary>
/// D327: новые цены одного объекта МойСклад — все цены продажи (текущие из МойСклад с заменой связанных видов: так неважно, обнуляет ли
/// МойСклад непереданные виды) и закупочная (null — не менять). Значения — в копейках, как в API.
/// </summary>
public sealed record MsPriceUpdate(string Id, IReadOnlyList<MsPriceValue> SalePrices, decimal? BuyPrice);

public sealed record MsPriceValue(string PriceTypeId, decimal Value);

public static class MoySkladIds
{
    public static string? FromHref(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var path = href.Split('?', 2)[0].TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[(slash + 1)..];
    }

    /// <summary>Тип объекта по ссылке: …/entity/product/{id} → product.</summary>
    public static string? TypeFromHref(string? href)
    {
        var parts = href?.Split('?', 2)[0].TrimEnd('/').Split('/');
        return parts is { Length: >= 2 } ? parts[^2] : null;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>Ошибка обращения к МойСклад: Status 401/403 — токен неверный или без прав; 429 — превышен лимит после повторов.</summary>
public sealed class MoySkladException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}

/// <summary>
/// Клиент JSON API МойСклад 1.2 (D310). Реализация — в Infrastructure (HttpClient: фиксированный адрес, gzip, лимит 45 запросов
/// за 3 секунды и 5 параллельных, повторы при 429/5xx, ограничение размера ответа); в тестах — подделка с записанными JSON.
/// entity — относительный путь из констант <see cref="MoySkladEntities"/>, произвольный URL не принимается.
/// </summary>
public interface IMoySkladClient
{
    /// <summary>Проверка токена: название первого юрлица аккаунта (GET entity/organization).</summary>
    Task<string?> CheckAsync(string token, CancellationToken ct = default);

    Task<MsPage<MsRow>> ListAsync(string token, string entity, int offset, int limit, string? expand = null, CancellationToken ct = default);

    Task<IReadOnlyList<MsPriceType>> PriceTypesAsync(string token, CancellationToken ct = default);

    /// <summary>D337: что клиент отправляет в заголовках (для проверки связи) — без токена.</summary>
    string HeadersInfo => string.Empty;

    /// <summary>D327: массовое изменение цен (POST entity/… массивом, до 1000 объектов). Возвращает изменённые объекты (id, updated).</summary>
    /// <summary>D333: страница отчёта об остатках по всем складам (все позиции, и с нулевым остатком).</summary>
    Task<MsPage<MsRow>> StockAsync(string token, int offset, int limit, CancellationToken ct = default);

    /// <summary>D336: страница отчёта «Остатки по складам».</summary>
    Task<MsPage<MsRow>> StockByStoreAsync(string token, int offset, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<MsRow>> UpdatePricesAsync(string token, string entity, IReadOnlyList<MsPriceUpdate> updates, CancellationToken ct = default);
}

public static class MoySkladEntities
{
    public const string BaseUrl = "https://api.moysklad.ru/api/remap/1.2/";
    public const string Organization = "entity/organization";
    public const string Uom = "entity/uom";
    public const string Country = "entity/country";
    public const string Folder = "entity/productfolder";
    public const string Product = "entity/product";
    public const string Service = "entity/service";
    public const string Bundle = "entity/bundle";
    public const string Variant = "entity/variant";
    public const string PriceTypes = "context/companysettings/pricetype";

    /// <summary>D333: отчёт «Остатки» по всем складам — только чтение, для сверки.</summary>
    public const string StockAll = "report/stock/all";

    /// <summary>D336: отчёт «Остатки по складам» — только чтение, для сверки по складам.</summary>
    public const string StockByStore = "report/stock/bystore";

    /// <summary>Сущности, в которые knitERP может записывать (только цены, D327).</summary>
    public static readonly IReadOnlySet<string> Writable = new HashSet<string>(StringComparer.Ordinal) { Product, Service, Bundle, Variant };

    public const int MaxBatch = 1000;

    /// <summary>Id объекта МойСклад — UUID (буквы, цифры, дефис): в адрес meta.href ничего иного не подставляется.</summary>
    public static bool IsSafeId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { Organization, Uom, Country, Folder, Product, Service, Bundle, Variant, PriceTypes, StockAll, StockByStore };
}
