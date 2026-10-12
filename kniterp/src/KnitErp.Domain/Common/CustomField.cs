using System.Globalization;

namespace KnitErp.Domain.Common;

/// <summary>Документ, к которому относится дополнительное поле.</summary>
public enum CustomFieldTarget : byte
{
    SalesOrder = 1,

    /// <summary>Позиция номенклатуры (D79).</summary>
    Item = 2,
}

public enum CustomFieldType : byte
{
    Text = 1,
    Number = 2,
    Date = 3,
    Flag = 4,

    /// <summary>Значение из своего справочника пользователя (D79); хранится номер записи.</summary>
    Catalog = 5,

    /// <summary>D328: многострочный текст (состав, уход за изделием) — до 1000 символов.</summary>
    LongText = 6,

    /// <summary>D328: ссылка — только http(s) (страница товара, фото у поставщика).</summary>
    Link = 7,

    /// <summary>D328: контрагент организации (производитель, поставщик); хранится номер контрагента.</summary>
    Counterparty = 8,
}

/// <summary>
/// Дополнительное поле документа (D77), как в МойСклад: «количество / штук», «время вязания», «итого часов».
/// Тип — текст, число, дата или флажок; значения хранятся строкой в одном формате (число — с точкой, дата — ГГГГ-ММ-ДД).
/// Поле не влияет на учёт. Не удаляется — уходит в архив, значения сохраняются.
/// </summary>
public sealed class CustomFieldDefinition
{
    public const int NameMaxLength = 100;
    public const int ValueMaxLength = 1000;
    public const int MaxFields = 30;

    private CustomFieldDefinition()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public CustomFieldTarget Target { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public CustomFieldType Type { get; private set; }
    public int SortOrder { get; private set; }

    /// <summary>Свой справочник для поля типа «Справочник».</summary>
    public long? CatalogId { get; private set; }

    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CustomFieldDefinition Create(long organizationId, CustomFieldTarget target, string? name, CustomFieldType type, int sortOrder,
        long? catalogId = null)
    {
        if (!Enum.IsDefined(type) || !Enum.IsDefined(target))
        {
            throw new BusinessRuleException("custom_field.type", "Тип поля — текст, многострочный текст, число, дата, флажок, ссылка, справочник или контрагент.");
        }

        // D328: новые типы — пока только у номенклатуры (карточка заказа покупателя их не показывает; сессия № 1 добавит).
        if (target == CustomFieldTarget.SalesOrder && type > CustomFieldType.Catalog)
        {
            throw new BusinessRuleException("custom_field.type", "У заказа покупателя тип поля — текст, число, дата, флажок или справочник.");
        }

        if ((type == CustomFieldType.Catalog) != (catalogId is not null))
        {
            throw new BusinessRuleException("custom_field.catalog", "Для поля «Справочник» выберите свой справочник.");
        }

        return new CustomFieldDefinition
        {
            OrganizationId = organizationId, Target = target, Type = type, SortOrder = sortOrder, CatalogId = catalogId,
            Name = DomainText.Require(name, NameMaxLength, "Название поля"),
        };
    }

    public void Rename(string? name) => Name = DomainText.Require(name, NameMaxLength, "Название поля");

    public void MoveTo(int sortOrder) => SortOrder = sortOrder;

    public void SetArchived(bool archived) => IsArchived = archived;

    /// <summary>Значение в хранимом виде или null (пусто). Неверный ввод — понятная ошибка с названием поля.</summary>
    public string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        switch (Type)
        {
            case CustomFieldType.Number:
                var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
                if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    throw new BusinessRuleException("custom_field.number", $"Поле «{Name}»: укажите число, например 12,5.");
                }

                return number.ToString(CultureInfo.InvariantCulture);
            case CustomFieldType.Date:
                if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date)
                    && !DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    throw new BusinessRuleException("custom_field.date", $"Поле «{Name}»: укажите дату, например 17.10.2026.");
                }

                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case CustomFieldType.Flag:
                return text is "true" or "1" or "on" or "да" ? "true" : null;
            case CustomFieldType.Link:
                // Только абсолютная http(s)-ссылка: «javascript:» и прочие схемы в поле не попадут.
                return text.Length <= ValueMaxLength && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? uri.AbsoluteUri
                    : throw new BusinessRuleException("custom_field.link", $"Поле «{Name}»: укажите ссылку, начинающуюся с https://.");
            case CustomFieldType.Counterparty:
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var counterparty) && counterparty > 0
                    ? counterparty.ToString(CultureInfo.InvariantCulture)
                    : throw new BusinessRuleException("custom_field.counterparty", $"Поле «{Name}»: выберите контрагента.");
            case CustomFieldType.Catalog:
                // Номер записи справочника; что запись из нужного справочника и не в архиве — проверяет сервис.
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var entry) && entry > 0
                    ? entry.ToString(CultureInfo.InvariantCulture)
                    : throw new BusinessRuleException("custom_field.catalog", $"Поле «{Name}»: выберите значение из справочника.");
            default:
                return DomainText.Optional(text, ValueMaxLength, Name);
        }
    }

    public static string TypeName(CustomFieldType type) => type switch
    {
        CustomFieldType.Text => "Текст",
        CustomFieldType.Number => "Число",
        CustomFieldType.Date => "Дата",
        CustomFieldType.Flag => "Флажок",
        CustomFieldType.Catalog => "Справочник",
        CustomFieldType.LongText => "Многострочный текст",
        CustomFieldType.Link => "Ссылка",
        CustomFieldType.Counterparty => "Контрагент",
        _ => type.ToString(),
    };
}

/// <summary>Значение дополнительного поля у конкретного документа.</summary>
public sealed class CustomFieldValue
{
    private CustomFieldValue()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long FieldId { get; private set; }
    public long TargetId { get; private set; }
    public string Value { get; private set; } = string.Empty;

    public static CustomFieldValue Create(long organizationId, long fieldId, long targetId, string value) =>
        new() { OrganizationId = organizationId, FieldId = fieldId, TargetId = targetId, Value = value };

    public void Set(string value) => Value = value;
}
