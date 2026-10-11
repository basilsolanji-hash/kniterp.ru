using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>Блоки карточки позиции, которые организация может скрыть (D312): только отображение, данные и права не меняются.</summary>
[Flags]
public enum CardBlocks
{
    None = 0,
    Trade = 1,
    Packs = 2,
    Suppliers = 4,
    Photos = 8,
    Modifications = 16,
    All = Trade | Packs | Suppliers | Photos | Modifications,
}

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

    /// <summary>Группа обязательна у новой позиции и при сохранении сведений.</summary>
    public bool RequireGroup { get; private set; }

    /// <summary>Ставка НДС обязательна у новой и изменяемой позиции.</summary>
    public bool RequireVat { get; private set; }

    public CardBlocks HiddenBlocks { get; private set; }

    /// <summary>D315: новой позиции (кроме услуги) сразу выдаётся внутренний EAN-13.</summary>
    public bool AutoBarcode { get; private set; }

    public byte BarcodePrefix { get; private set; } = InternalEan13.MinPrefix;
    public long NextBarcodeNumber { get; private set; } = 1;

    /// <summary>D315: пустой артикул новой позиции заполняется по шаблону.</summary>
    public bool AutoArticle { get; private set; }

    public string? ArticleTemplateText { get; private set; }
    public byte ArticleDigits { get; private set; } = 5;
    public int NextArticleNumber { get; private set; } = 1;
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

    /// <summary>Обязательные поля карточки (D312, часть 2). Скрытие блоков — отдельно, проверок не требует.</summary>
    public void SetCard(bool requireGroup, bool requireVat, CardBlocks hiddenBlocks, DateTime nowUtc)
    {
        if ((hiddenBlocks & ~CardBlocks.All) != 0)
        {
            throw new BusinessRuleException("catalog.settings.blocks", "Неизвестный блок карточки.");
        }

        RequireGroup = requireGroup;
        RequireVat = requireVat;
        HiddenBlocks = hiddenBlocks;
        UpdatedAtUtc = nowUtc;
    }

    public void EnsureGroup(long? groupId)
    {
        if (RequireGroup && groupId is null)
        {
            throw new BusinessRuleException("catalog.required.group", "Группа обязательна в настройках каталога: выберите группу позиции.");
        }
    }

    public void EnsureVat(long? vatRateId)
    {
        if (RequireVat && vatRateId is null)
        {
            throw new BusinessRuleException("catalog.required.vat", "Ставка НДС обязательна в настройках каталога: выберите ставку.");
        }
    }

    /// <summary>Автоматические штрихкоды и артикулы (D315). Все проверки — до изменения полей.</summary>
    public void SetCodes(bool autoBarcode, byte barcodePrefix, long nextBarcodeNumber, bool autoArticle, string? articleTemplate, byte articleDigits,
        int nextArticleNumber, DateTime nowUtc)
    {
        if (barcodePrefix is < InternalEan13.MinPrefix or > InternalEan13.MaxPrefix)
        {
            throw new BusinessRuleException("catalog.settings.barcode_prefix", "Префикс внутреннего штрихкода — от 20 до 29.");
        }

        if (nextBarcodeNumber is < 1 or > InternalEan13.MaxNumber)
        {
            throw new BusinessRuleException("catalog.settings.barcode_next", "Следующий номер штрихкода — от 1 до 9 999 999 999.");
        }

        string? template = null;
        if (autoArticle || !string.IsNullOrWhiteSpace(articleTemplate))
        {
            template = ArticleTemplate.Validate(articleTemplate);
        }

        if (articleDigits is < MinDigits or > MaxDigits)
        {
            throw new BusinessRuleException("catalog.settings.digits", "Номер в автокоде — от 3 до 8 цифр.");
        }

        if (nextArticleNumber < 1 || nextArticleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length > articleDigits)
        {
            throw new BusinessRuleException("catalog.settings.next", "Следующий номер — от 1 и не длиннее заданного числа цифр.");
        }

        AutoBarcode = autoBarcode;
        BarcodePrefix = barcodePrefix;
        NextBarcodeNumber = nextBarcodeNumber;
        AutoArticle = autoArticle;
        ArticleTemplateText = template;
        ArticleDigits = articleDigits;
        NextArticleNumber = nextArticleNumber;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Следующий внутренний EAN-13; номер сдвигается. Занятость проверяет вызывающий.</summary>
    public string TakeBarcode() => InternalEan13.Build(BarcodePrefix, NextBarcodeNumber++);

    public string? PreviewBarcode() => NextBarcodeNumber <= InternalEan13.MaxNumber ? InternalEan13.Build(BarcodePrefix, NextBarcodeNumber) : null;

    /// <summary>Артикул по шаблону для позиции с кодом; номер сдвигается, только если шаблон его содержит.</summary>
    public string? TakeArticle(string itemCode)
    {
        if (!AutoArticle || ArticleTemplateText is not { } template)
        {
            return null;
        }

        var article = ArticleTemplate.Apply(template, itemCode, NextArticleNumber, ArticleDigits);
        if (ArticleTemplate.UsesNumber(template))
        {
            NextArticleNumber++;
        }

        return article;
    }

    public string? PreviewArticle(string itemCode = "ТВ-0001") =>
        ArticleTemplateText is { } t ? ArticleTemplate.Apply(t, itemCode, NextArticleNumber, ArticleDigits) : null;

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
