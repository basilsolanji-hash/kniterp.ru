using System.Globalization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>D328: тип значений характеристики — что можно ввести у модификации.</summary>
public enum CharacteristicType : byte
{
    Text = 1,

    /// <summary>Число («48», «0.5»): хранится с точкой, без лишних нулей — «48,0» и «48» одна модификация.</summary>
    Number = 2,

    /// <summary>Да или нет («С капюшоном»).</summary>
    Flag = 3,

    /// <summary>Запись своего справочника («Цвет» из справочника цветов): хранится её название.</summary>
    Catalog = 4,
}

/// <summary>
/// Характеристика модификаций (D82), как в МойСклад: «Цвет», «Размер», «Рост». Значения вводятся у модификации («красный», «48»);
/// D328 — с типом: текст, число, да/нет или запись своего справочника (тогда другое значение не примется). Не удаляется — архивируется.
/// </summary>
public sealed class Characteristic
{
    public const string Yes = "Да";
    public const string No = "Нет";

    public const int NameMaxLength = 60;
    public const int MaxCharacteristics = 20;

    private Characteristic()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public CharacteristicType Type { get; private set; } = CharacteristicType.Text;

    /// <summary>Свой справочник для типа «Справочник».</summary>
    public long? CatalogId { get; private set; }

    public static Characteristic Create(long organizationId, string? name, int sortOrder, CharacteristicType type = CharacteristicType.Text,
        long? catalogId = null)
    {
        var c = new Characteristic { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Название характеристики"), SortOrder = sortOrder };
        c.SetType(type, catalogId);
        return c;
    }

    public void SetType(CharacteristicType type, long? catalogId)
    {
        if (!Enum.IsDefined(type))
        {
            throw new BusinessRuleException("catalog.characteristic.type", "Тип характеристики — текст, число, да/нет или справочник.");
        }

        if ((type == CharacteristicType.Catalog) != (catalogId is not null))
        {
            throw new BusinessRuleException("catalog.characteristic.catalog", "Для характеристики «Справочник» выберите свой справочник.");
        }

        Type = type;
        CatalogId = catalogId;
    }

    /// <summary>
    /// Значение по типу: число — с точкой без лишних нулей, да/нет — «Да»/«Нет», справочник — название записи как в справочнике
    /// (catalogNames — действующие записи; для других типов не нужен). Неверное — понятная ошибка с названием характеристики.
    /// </summary>
    public string Normalize(string? value, IReadOnlyCollection<string>? catalogNames = null)
    {
        var text = Variants.CleanValue(value);
        switch (Type)
        {
            case CharacteristicType.Number:
                var raw = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
                return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? number.ToString("0.######", CultureInfo.InvariantCulture)
                    : throw new BusinessRuleException("catalog.characteristic.number", $"«{Name}»: «{text}» — не число.");
            case CharacteristicType.Flag:
                return text.ToLowerInvariant() switch
                {
                    "да" or "yes" or "true" or "1" or "+" => Yes,
                    "нет" or "no" or "false" or "0" or "-" => No,
                    _ => throw new BusinessRuleException("catalog.characteristic.flag", $"«{Name}»: укажите «Да» или «Нет»."),
                };
            case CharacteristicType.Catalog:
                return catalogNames?.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase))
                       ?? throw new BusinessRuleException("catalog.characteristic.not_in_catalog", $"«{Name}»: «{text}» нет в справочнике — добавьте запись или выберите из списка.");
            default:
                return text;
        }
    }

    public void Rename(string? name) => Name = DomainText.Require(name, NameMaxLength, "Название характеристики");

    public void SetArchived(bool archived) => IsArchived = archived;
}

/// <summary>Значение характеристики у модификации: «Цвет» = «красный».</summary>
public sealed class ItemCharacteristicValue
{
    public const int ValueMaxLength = 100;

    private ItemCharacteristicValue()
    {
    }

    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public long CharacteristicId { get; private set; }
    public string Value { get; private set; } = string.Empty;

    public static ItemCharacteristicValue Create(long organizationId, long itemId, long characteristicId, string value) =>
        new() { OrganizationId = organizationId, ItemId = itemId, CharacteristicId = characteristicId, Value = Variants.CleanValue(value) };
}

/// <summary>
/// Правила модификаций (D82). Модификация — отдельная позиция номенклатуры со своим кодом, остатком, штрихкодами и ценами,
/// связанная с основной позицией: так остатки, документы и отчёты работают без изменений. Набор значений характеристик
/// уникален среди модификаций одной позиции (ключ варианта, уникальный индекс в БД).
/// </summary>
public static class Variants
{
    public const int MaxPerItem = 500;
    public const int MaxPerBatch = 200;
    public const int KeyMaxLength = 450;

    /// <summary>Значение без лишних пробелов; пустое — ошибка.</summary>
    public static string CleanValue(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (text.Length == 0)
        {
            throw new BusinessRuleException("catalog.variant.empty", "Значение характеристики не может быть пустым.");
        }

        if (text.Length > ItemCharacteristicValue.ValueMaxLength)
        {
            throw new BusinessRuleException("catalog.variant.long", $"Значение характеристики — не длиннее {ItemCharacteristicValue.ValueMaxLength} символов.");
        }

        return text;
    }

    /// <summary>Ключ варианта: «3=красный|7=48» по возрастанию номера характеристики, без учёта регистра.</summary>
    public static string Key(IReadOnlyDictionary<long, string> values)
    {
        if (values.Count == 0)
        {
            throw new BusinessRuleException("catalog.variant.no_values", "У модификации должна быть хотя бы одна характеристика.");
        }

        var key = string.Join('|', values.OrderBy(v => v.Key)
            .Select(v => v.Key.ToString(CultureInfo.InvariantCulture) + "=" + CleanValue(v.Value).ToLowerInvariant()));
        if (key.Length > KeyMaxLength)
        {
            throw new BusinessRuleException("catalog.variant.long", "Слишком длинный набор значений характеристик.");
        }

        return key;
    }

    /// <summary>Все сочетания значений: Цвет [красный, синий] × Размер [46, 48] → 4 набора.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<long, string>> Matrix(IReadOnlyDictionary<long, IReadOnlyList<string>> axes)
    {
        IEnumerable<Dictionary<long, string>> result = [new Dictionary<long, string>()];
        foreach (var (characteristic, values) in axes.Where(a => a.Value.Count > 0).OrderBy(a => a.Key))
        {
            var clean = values.Select(CleanValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result = result.SelectMany(r => clean.Select(v => new Dictionary<long, string>(r) { [characteristic] = v }));
        }

        var list = result.Where(r => r.Count > 0).Cast<IReadOnlyDictionary<long, string>>().ToList();
        if (list.Count > MaxPerBatch)
        {
            throw new BusinessRuleException("catalog.variant.too_many", $"За один раз — не больше {MaxPerBatch} модификаций, получилось {list.Count}.");
        }

        return list;
    }

    /// <summary>Значения через запятую или точку с запятой: «46, 48, 50».</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Фото позиции (D82): JPEG, PNG или WebP до 2 МБ, не больше 8 на позицию; хранится в базе (попадает в резервные копии).
/// Тип определяется по содержимому файла, а не по имени. Первое по порядку — основное.
/// </summary>
public sealed class ItemPhoto
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxPerItem = 8;

    private ItemPhoto()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];
    public int SizeBytes { get; private set; }
    public int SortOrder { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static ItemPhoto Create(long organizationId, long itemId, byte[] content, int sortOrder, long userId, DateTime nowUtc)
    {
        if (content.Length == 0)
        {
            throw new BusinessRuleException("catalog.photo.empty", "Файл пустой.");
        }

        if (content.Length > MaxBytes)
        {
            throw new BusinessRuleException("catalog.photo.too_large", "Фото — не больше 2 МБ. Уменьшите размер снимка.");
        }

        return new ItemPhoto
        {
            OrganizationId = organizationId, ItemId = itemId, Content = content, SizeBytes = content.Length, SortOrder = sortOrder,
            ContentType = Detect(content) ?? throw new BusinessRuleException("catalog.photo.type", "Подходят только фото JPEG, PNG или WebP."),
            CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    public void MoveTo(int sortOrder) => SortOrder = sortOrder;

    /// <summary>Тип по сигнатуре файла; null — не изображение из разрешённых.</summary>
    public static string? Detect(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
