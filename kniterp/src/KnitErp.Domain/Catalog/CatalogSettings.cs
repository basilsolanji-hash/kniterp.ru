using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Общие настройки каталога организации (D312, сессия 4): какие виды позиций используются (услуги, комплекты), автоматический код
/// новой позиции (префикс + номер) и тип новой позиции по умолчанию. Строки нет — действуют значения по умолчанию (всё включено,
/// код вводится вручную). Выключенный вид не создаётся вручную и скрыт в списках выбора; уже созданные позиции и загрузки из файла
/// и внешних систем он не трогает.
/// </summary>
public sealed class CatalogSettings
{
    public const int PrefixMaxLength = 10;
    public const byte MinDigits = 3;
    public const byte MaxDigits = 8;

    private CatalogSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public bool ServicesEnabled { get; private set; } = true;
    public bool BundlesEnabled { get; private set; } = true;

    /// <summary>Префикс автокода, например «ТВ-»; null — код вводится вручную.</summary>
    public string? AutoCodePrefix { get; private set; }

    public byte AutoCodeDigits { get; private set; } = 5;
    public int NextNumber { get; private set; } = 1;
    public ItemType DefaultItemType { get; private set; } = ItemType.Finished;
    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool AutoCode => AutoCodePrefix is not null;

    public static CatalogSettings Default(long organizationId) => new() { OrganizationId = organizationId };

    public void Set(bool servicesEnabled, bool bundlesEnabled, string? autoCodePrefix, byte autoCodeDigits, int nextNumber, ItemType defaultItemType,
        DateTime nowUtc)
    {
        var prefix = string.IsNullOrWhiteSpace(autoCodePrefix) ? null : Item.NormalizeCode(autoCodePrefix);
        if (prefix is not null && (prefix.Length > PrefixMaxLength || prefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new BusinessRuleException("catalog.settings.prefix", "Префикс кода — до 10 знаков без пробелов, например «ТВ-».");
        }

        if (autoCodeDigits is < MinDigits or > MaxDigits)
        {
            throw new BusinessRuleException("catalog.settings.digits", "Номер в автокоде — от 3 до 8 цифр.");
        }

        if (nextNumber < 1 || nextNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length > autoCodeDigits)
        {
            throw new BusinessRuleException("catalog.settings.next", "Следующий номер — от 1 и не длиннее заданного числа цифр.");
        }

        // Все проверки — до изменения полей: отказ не оставляет в контексте вкладки наполовину изменённые настройки.
        if ((defaultItemType == ItemType.Service && !servicesEnabled) || (defaultItemType == ItemType.Bundle && !bundlesEnabled)
                                                                     || !ItemTypes.All.Contains(defaultItemType))
        {
            throw new BusinessRuleException("catalog.settings.default_type", "Тип новой позиции по умолчанию должен быть из включённых видов.");
        }

        ServicesEnabled = servicesEnabled;
        BundlesEnabled = bundlesEnabled;
        AutoCodePrefix = prefix;
        AutoCodeDigits = autoCodeDigits;
        NextNumber = nextNumber;
        DefaultItemType = defaultItemType;
        UpdatedAtUtc = nowUtc;
    }

    public bool IsEnabled(ItemType type) => type switch
    {
        ItemType.Service => ServicesEnabled,
        ItemType.Bundle => BundlesEnabled,
        _ => true,
    };

    /// <summary>Выключенный вид нельзя создать вручную или назначить позиции.</summary>
    public void EnsureEnabled(ItemType type)
    {
        if (type == ItemType.Service && !ServicesEnabled)
        {
            throw new BusinessRuleException("catalog.kind_disabled", "Услуги выключены в настройках каталога организации.");
        }

        if (type == ItemType.Bundle && !BundlesEnabled)
        {
            throw new BusinessRuleException("catalog.kind_disabled", "Комплекты выключены в настройках каталога организации.");
        }
    }

    /// <summary>Пример кода для подсказки в форме.</summary>
    public string? PreviewCode() => AutoCodePrefix is null ? null : Format(NextNumber);

    /// <summary>Следующий код автонумерации; номер сдвигается. Занятость кода проверяет вызывающий (код уникален навсегда).</summary>
    public string TakeCode()
    {
        if (AutoCodePrefix is null)
        {
            throw new BusinessRuleException("catalog.code.required", "Укажите код позиции.");
        }

        var code = Format(NextNumber);
        NextNumber++;
        return code;
    }

    private string Format(int number) => AutoCodePrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(AutoCodeDigits, '0');
}
