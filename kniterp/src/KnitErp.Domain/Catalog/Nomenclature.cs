using System.Globalization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Группа номенклатуры (D79) — папка, как в МойСклад: «Пряжа → Шерсть → Меринос». Вложенность до <see cref="MaxDepth"/> уровней;
/// у действующих групп одного родителя названия уникальны. Не удаляется — архивируется (если в ней нет действующих позиций и подгрупп).
/// </summary>
public sealed class ItemGroup
{
    public const int NameMaxLength = 150;
    public const int MaxDepth = 5;
    public const int MaxGroups = 1000;

    private ItemGroup()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long? ParentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ItemGroup Create(long organizationId, long? parentId, string? name) =>
        new() { OrganizationId = organizationId, ParentId = parentId, Name = DomainText.Require(name, NameMaxLength, "Название группы") };

    public void Rename(string? name)
    {
        EnsureActive();
        Name = DomainText.Require(name, NameMaxLength, "Название группы");
    }

    /// <summary>Перенос в другую группу (null — в корень). Циклы и глубину проверяет сервис по дереву.</summary>
    public void MoveTo(long? parentId)
    {
        EnsureActive();
        if (parentId == Id)
        {
            throw new BusinessRuleException("catalog.group.cycle", "Группу нельзя вложить саму в себя.");
        }

        ParentId = parentId;
    }

    public void SetArchived(bool archived) => IsArchived = archived;

    private void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Группа в архиве. Сначала верните её из архива.");
        }
    }
}

/// <summary>
/// Дополнительные сведения карточки номенклатуры (D79). OriginCountryCode — цифровой код страны по ОКСМ (643 — Россия),
/// для ввезённых товаров печатается в УПД (графы 10, 10а) вместе с номером декларации (графа 11). TnVedCode — код ТН ВЭД ЕАЭС
/// (графа 1б при вывозе в страны ЕАЭС). Вес и объём — на единицу измерения позиции.
/// </summary>
public sealed record ItemDetails(
    long? GroupId, string? Article, string? OriginCountryCode, string? OriginCountryName, string? CustomsDeclaration, string? TnVedCode,
    decimal? WeightKg, decimal? VolumeM3, decimal? MinStock, decimal? PurchasePrice, string PurchaseCurrency = PriceCurrencies.Default);

/// <summary>
/// D151: валюты цен номенклатуры — из справочника валют стран (D60) и основных валют закупок. По умолчанию рубль. Цены в документах,
/// фидах и на площадках — всегда в валюте организации по курсу ЦБ (D146) на дату документа или выгрузки.
/// </summary>
public static class PriceCurrencies
{
    public const string Default = "RUB";

    public static readonly IReadOnlyList<string> All = ["RUB", "BYN", "KZT", "UZS", "USD", "EUR", "CNY", "TRY"];

    /// <summary>Пусто — рубль; иначе код из списка (регистр не важен), неизвестный — понятный отказ.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Default;
        }

        var c = code.Trim().ToUpperInvariant();
        return All.Contains(c) ? c : throw new BusinessRuleException("catalog.price.currency", $"Валюта цены «{code.Trim()}» не из справочника: {string.Join(", ", All)}.");
    }

    public static string Symbol(string code) => code switch
    {
        "RUB" => "₽",
        "USD" => "$",
        "EUR" => "€",
        "CNY" => "¥",
        _ => code,
    };
}

public static class ItemDetailRules
{
    public const int ArticleMaxLength = 64;
    public const int CountryNameMaxLength = 60;
    public const int DeclarationMaxLength = 50;

    /// <summary>Частые страны происхождения по ОКСМ — подсказка в карточке.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> CommonCountries =
    [
        ("643", "Россия"), ("156", "Китай"), ("792", "Турция"), ("860", "Узбекистан"), ("398", "Казахстан"), ("112", "Беларусь"),
        ("356", "Индия"), ("050", "Бангладеш"), ("704", "Вьетнам"), ("380", "Италия"), ("276", "Германия"), ("586", "Пакистан"),
    ];

    public const string RussiaCode = "643";

    /// <summary>Проверка и приведение: коды — только цифры нужной длины, числа — не отрицательные, до 6 знаков (цена — до 4).</summary>
    public static ItemDetails Normalize(ItemDetails d)
    {
        var country = Digits(d.OriginCountryCode);
        if (country is not null && country.Length != 3)
        {
            throw new BusinessRuleException("catalog.item.country", "Код страны по ОКСМ — 3 цифры, например 643 (Россия), 156 (Китай).");
        }

        var countryName = DomainText.Optional(d.OriginCountryName, CountryNameMaxLength, "Страна происхождения");
        if (country is not null && countryName is null)
        {
            countryName = CommonCountries.FirstOrDefault(c => c.Code == country).Name;
        }

        if (country is null && countryName is not null)
        {
            country = CommonCountries.FirstOrDefault(c => string.Equals(c.Name, countryName, StringComparison.OrdinalIgnoreCase)).Code
                      ?? throw new BusinessRuleException("catalog.item.country", "Укажите цифровой код страны по ОКСМ, например 156 для Китая.");
        }

        var tnved = Digits(d.TnVedCode);
        if (tnved is not null && tnved.Length is not (4 or 6 or 8 or 10))
        {
            throw new BusinessRuleException("catalog.item.tnved", "Код ТН ВЭД ЕАЭС — 10 цифр (допускается 4, 6 или 8 для группы).");
        }

        return d with
        {
            Article = DomainText.Optional(d.Article, ArticleMaxLength, "Артикул"),
            OriginCountryCode = country,
            OriginCountryName = countryName,
            CustomsDeclaration = DomainText.Optional(d.CustomsDeclaration, DeclarationMaxLength, "Номер декларации"),
            TnVedCode = tnved,
            WeightKg = NonNegative(d.WeightKg, 6, "Вес"),
            VolumeM3 = NonNegative(d.VolumeM3, 6, "Объём"),
            MinStock = NonNegative(d.MinStock, 6, "Неснижаемый остаток"),
            PurchasePrice = NonNegative(d.PurchasePrice, 4, "Закупочная цена"),
        };
    }

    private static string? Digits(string? value)
    {
        var v = value?.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (!v.All(char.IsAsciiDigit))
        {
            throw new BusinessRuleException("catalog.item.digits", $"«{v}» — нужны только цифры.");
        }

        return v;
    }

    private static decimal? NonNegative(decimal? value, int scale, string field)
    {
        if (value is not { } v)
        {
            return null;
        }

        if (v < 0 || decimal.Round(v, scale) != v || v > 999_999_999_999m)
        {
            throw new BusinessRuleException("catalog.item.number", $"«{field}»: не меньше нуля, до {scale} знаков после запятой.");
        }

        return v;
    }
}

public enum BarcodeType : byte
{
    Ean13 = 1,
    Ean8 = 2,
    Code128 = 3,
    Gtin = 4,
}

/// <summary>
/// Штрихкод позиции (D79). EAN-13 и EAN-8 проверяются по контрольной цифре; Code 128 — печатные латинские символы и цифры;
/// GTIN-14 — 14 цифр с контрольной. Штрихкод уникален в организации: сканер должен находить одну позицию.
/// </summary>
public sealed class ItemBarcode
{
    public const int CodeMaxLength = 64;
    public const int MaxPerItem = 20;

    private ItemBarcode()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public BarcodeType Type { get; private set; }
    public string Code { get; private set; } = string.Empty;

    public static ItemBarcode Create(long organizationId, long itemId, BarcodeType type, string? code) =>
        new() { OrganizationId = organizationId, ItemId = itemId, Type = type, Code = Normalize(type, code) };

    /// <summary>Тип по виду кода: 13 цифр — EAN-13, 8 — EAN-8, 14 — GTIN, иначе Code 128.</summary>
    public static BarcodeType Detect(string code) => code.All(char.IsAsciiDigit)
        ? code.Length switch { 13 => BarcodeType.Ean13, 8 => BarcodeType.Ean8, 14 => BarcodeType.Gtin, _ => BarcodeType.Code128 }
        : BarcodeType.Code128;

    public static string Normalize(BarcodeType type, string? code)
    {
        var c = (code ?? string.Empty).Trim();
        if (type is BarcodeType.Ean13 or BarcodeType.Ean8 or BarcodeType.Gtin)
        {
            c = c.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        var ok = type switch
        {
            BarcodeType.Ean13 => c.Length == 13 && GtinValid(c),
            BarcodeType.Ean8 => c.Length == 8 && GtinValid(c),
            BarcodeType.Gtin => c.Length == 14 && GtinValid(c),
            BarcodeType.Code128 => c.Length is > 0 and <= CodeMaxLength && c.All(ch => ch is >= ' ' and <= '~'),
            _ => false,
        };
        if (!ok)
        {
            throw new BusinessRuleException("catalog.barcode.invalid", type switch
            {
                BarcodeType.Ean13 => $"Штрихкод EAN-13 «{c}» неверный: 13 цифр с верной контрольной цифрой.",
                BarcodeType.Ean8 => $"Штрихкод EAN-8 «{c}» неверный: 8 цифр с верной контрольной цифрой.",
                BarcodeType.Gtin => $"GTIN «{c}» неверный: 14 цифр с верной контрольной цифрой.",
                _ => $"Штрихкод Code 128: латинские буквы, цифры и знаки, до {CodeMaxLength} символов.",
            });
        }

        return c;
    }

    public static string TypeName(BarcodeType type) => type switch
    {
        BarcodeType.Ean13 => "EAN-13",
        BarcodeType.Ean8 => "EAN-8",
        BarcodeType.Gtin => "GTIN-14",
        _ => "Code 128",
    };

    /// <summary>Контрольная цифра GTIN (EAN-8/13, GTIN-14): веса 3 и 1 справа налево, без последней цифры.</summary>
    private static bool GtinValid(string digits)
    {
        if (!digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (int i = digits.Length - 2, w = 3; i >= 0; i--, w = 4 - w)
        {
            sum += (digits[i] - '0') * w;
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}

/// <summary>
/// Вид цены (D79): «Цена продажи», «Оптовая», «Маркетплейс». Один — основной: его цена подставляется в заказ покупателя.
/// IncludesVat — цены этого вида с НДС (как обычно в прайсе).
/// </summary>
public sealed class PriceType
{
    public const int NameMaxLength = 100;
    public const int MaxTypes = 20;
    public const string DefaultName = "Цена продажи";

    private PriceType()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IncludesVat { get; private set; } = true;
    public bool IsDefault { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static PriceType Create(long organizationId, string? name, bool includesVat, bool isDefault, int sortOrder) =>
        new()
        {
            OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Название вида цены"), IncludesVat = includesVat,
            IsDefault = isDefault, SortOrder = sortOrder,
        };

    public void Set(string? name, bool includesVat)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Вид цены в архиве.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название вида цены");
        IncludesVat = includesVat;
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    public void SetArchived(bool archived)
    {
        if (archived && IsDefault)
        {
            throw new BusinessRuleException("catalog.price_type.default", "Основной вид цены нельзя убрать в архив.");
        }

        IsArchived = archived;
    }
}

/// <summary>Цена позиции по виду цены (D79). Нет записи — цена не задана.</summary>
public sealed class ItemPrice
{
    private ItemPrice()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public long PriceTypeId { get; private set; }
    public decimal Price { get; private set; }

    /// <summary>D151: валюта цены (по умолчанию рубль). В документы цена попадает в валюте организации по курсу на дату документа.</summary>
    public string CurrencyCode { get; private set; } = PriceCurrencies.Default;

    public static ItemPrice Create(long organizationId, long itemId, long priceTypeId, decimal price, string? currency = null) =>
        new() { OrganizationId = organizationId, ItemId = itemId, PriceTypeId = priceTypeId, Price = Check(price), CurrencyCode = PriceCurrencies.Normalize(currency) };

    /// <summary>Новая цена; валюта — прежняя, если не указана.</summary>
    public void Set(decimal price, string? currency = null)
    {
        Price = Check(price);
        CurrencyCode = currency is null ? CurrencyCode : PriceCurrencies.Normalize(currency);
    }

    public static decimal Check(decimal price)
    {
        if (price < 0 || decimal.Round(price, 4) != price || price > 999_999_999_999m)
        {
            throw new BusinessRuleException("catalog.price", "Цена — не меньше нуля, до четырёх знаков после запятой.");
        }

        return price;
    }
}

/// <summary>
/// Свой справочник пользователя (D79): «Цвета», «Составы пряжи», «Классы машин». Значения — записи справочника; справочник
/// подключается к доп. полю номенклатуры (тип «Справочник»). Не удаляется — архивируется.
/// </summary>
public sealed class UserCatalog
{
    public const int NameMaxLength = 100;
    public const int MaxCatalogs = 50;

    private UserCatalog()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static UserCatalog Create(long organizationId, string? name) =>
        new() { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Название справочника") };

    public void Rename(string? name) => Name = DomainText.Require(name, NameMaxLength, "Название справочника");

    public void SetArchived(bool archived) => IsArchived = archived;
}

/// <summary>Запись своего справочника: название и необязательный код (например, номер цвета «0012»).</summary>
public sealed class UserCatalogEntry
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 40;
    public const int MaxEntries = 5000;

    private UserCatalogEntry()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long CatalogId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Code { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static UserCatalogEntry Create(long organizationId, long catalogId, string? name, string? code)
    {
        var e = new UserCatalogEntry { OrganizationId = organizationId, CatalogId = catalogId };
        e.Set(name, code);
        return e;
    }

    public void Set(string? name, string? code)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Запись в архиве. Сначала верните её из архива.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название");
        Code = DomainText.Optional(code, CodeMaxLength, "Код");
    }

    public void SetArchived(bool archived) => IsArchived = archived;

    public string Display => Code is null ? Name : $"{Code} — {Name}";
}

public static class Decimals
{
    /// <summary>Число из ввода пользователя: пробелы — разделители тысяч, запятая или точка — дробная часть.</summary>
    public static decimal? Parse(string? text, string field)
    {
        var t = text?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.').Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new BusinessRuleException("field.number", $"«{field}»: укажите число, например 12,5.");
    }
}
