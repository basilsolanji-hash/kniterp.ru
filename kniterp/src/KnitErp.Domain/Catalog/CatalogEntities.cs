using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Единица измерения. Точность — сколько знаков после запятой допустимо в количестве
/// (шт — 0, кг — 3). Количества хранятся в decimal(18,6), поэтому точность не больше 6.
/// </summary>
public sealed class UnitOfMeasure
{
    public const int CodeMaxLength = 10;
    public const int NameMaxLength = 50;
    public const int SymbolMaxLength = 10;
    public const byte MaxPrecision = 6;

    private UnitOfMeasure()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Код по ОКЕИ (796 — штука, 166 — килограмм); для своих единиц — любой короткий код.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string Symbol { get; private set; } = string.Empty;
    public byte Precision { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Стандартный набор для новой организации. Тот же список — в миграции AddCatalogAndWarehouses.</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Symbol, byte Precision)> Defaults =
    [
        ("796", "Штука", "шт", 0),
        ("166", "Килограмм", "кг", 3),
        ("163", "Грамм", "г", 1),
        ("006", "Метр", "м", 2),
        ("055", "Квадратный метр", "м²", 3),
        ("715", "Пара", "пар", 0),
        ("778", "Упаковка", "упак", 0),
        // D300: единицы для услуг; для действующих организаций их добавляет миграция CatServices.
        ("876", "Условная единица", "усл. ед", 0),
        ("356", "Час", "ч", 2),
    ];

    public static UnitOfMeasure Create(long organizationId, string code, string name, string symbol, byte precision)
    {
        var unit = new UnitOfMeasure { OrganizationId = organizationId };
        unit.Code = DomainText.Require(code, CodeMaxLength, "Код единицы");
        unit.Set(name, symbol, precision);
        return unit;
    }

    public IReadOnlyList<FieldChange> Update(string name, string symbol, byte precision)
    {
        EnsureActive();
        var before = (Name, Symbol, Precision);
        Set(name, symbol, precision);
        var changes = new List<FieldChange>();
        if (before.Name != Name)
        {
            changes.Add(new FieldChange("Наименование", before.Name, Name));
        }

        if (before.Symbol != Symbol)
        {
            changes.Add(new FieldChange("Обозначение", before.Symbol, Symbol));
        }

        if (before.Precision != Precision)
        {
            changes.Add(new FieldChange("Знаков после запятой", before.Precision.ToString(), Precision.ToString()));
        }

        return changes;
    }

    public void Archive(int activeItems)
    {
        EnsureActive();
        if (activeItems > 0)
        {
            throw new BusinessRuleException("catalog.unit.in_use", $"Единицу используют позиции номенклатуры ({activeItems}).");
        }

        IsArchived = true;
    }

    private void Set(string name, string symbol, byte precision)
    {
        Name = DomainText.Require(name, NameMaxLength, "Наименование единицы");
        Symbol = DomainText.Require(symbol, SymbolMaxLength, "Обозначение единицы");
        if (precision > MaxPrecision)
        {
            throw new BusinessRuleException("catalog.unit.precision", $"Знаков после запятой — от 0 до {MaxPrecision}.");
        }

        Precision = precision;
    }

    private void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Запись в архиве.");
        }
    }
}

/// <summary>
/// Тип номенклатуры. Допущение D32: фиксированный список; числа хранятся в базе и закреплены CHECK.
/// Service (D300) — услуга: продаётся и покупается, но не хранится на складе.
/// Bundle (D302) — комплект: продаётся одной строкой, остаток и списание — по компонентам.
/// </summary>
public enum ItemType : byte
{
    RawMaterial = 1,
    Material = 2,
    Accessory = 3,
    SemiFinished = 4,
    Finished = 5,
    Packaging = 6,
    Other = 9,
    Service = 10,
    Bundle = 11,
}

/// <summary>Вид позиции для отбора «Товары / Услуги / Комплекты» (D300, D302).</summary>
public enum ItemKind : byte
{
    Goods = 1,
    Service = 2,
    Bundle = 3,
}

public static class ItemTypes
{
    public static readonly IReadOnlyList<ItemType> All =
    [
        ItemType.RawMaterial, ItemType.Material, ItemType.Accessory, ItemType.SemiFinished, ItemType.Finished, ItemType.Packaging, ItemType.Other,
        ItemType.Service, ItemType.Bundle,
    ];

    /// <summary>Типы, которые хранятся на складе: у них есть остаток, движения и партии (D300).</summary>
    public static readonly IReadOnlyList<ItemType> Stocked = All.Where(IsStocked).ToArray();

    /// <summary>Типы, которых не бывает на складе (массив — для отбора в запросах к базе).</summary>
    public static readonly ItemType[] NotStocked = All.Where(t => !IsStocked(t)).ToArray();

    public static bool IsStocked(ItemType type) => type is not (ItemType.Service or ItemType.Bundle);

    public static ItemKind KindOf(ItemType type) => type switch
    {
        ItemType.Service => ItemKind.Service,
        ItemType.Bundle => ItemKind.Bundle,
        _ => ItemKind.Goods,
    };

    public static IReadOnlyList<ItemType> OfKind(ItemKind kind) => All.Where(t => KindOf(t) == kind).ToArray();

    public static string KindName(ItemKind kind) => kind switch
    {
        ItemKind.Service => "Услуги",
        ItemKind.Bundle => "Комплекты",
        _ => "Товары",
    };

    /// <summary>
    /// Признак предмета расчёта для чека (54-ФЗ, тег 1212) по типу позиции: услуга — «услуга», остальное — «товар» (D300).
    /// </summary>
    public static PaymentSubject DefaultPaymentSubject(ItemType type) => type == ItemType.Service ? PaymentSubject.Service : PaymentSubject.Goods;

    public static string Name(ItemType type) => type switch
    {
        ItemType.RawMaterial => "Пряжа и сырьё",
        ItemType.Material => "Материалы",
        ItemType.Accessory => "Фурнитура",
        ItemType.SemiFinished => "Полуфабрикаты",
        ItemType.Finished => "Готовая продукция",
        ItemType.Packaging => "Упаковка",
        ItemType.Other => "Прочее",
        ItemType.Service => "Услуги",
        ItemType.Bundle => "Комплекты",
        _ => type.ToString(),
    };

    public static bool IsDefined(ItemType type) => All.Contains(type);
}

/// <summary>Признак предмета расчёта (54-ФЗ, тег 1212): справочное поле для будущей кассы. Числа — коды тега.</summary>
public enum PaymentSubject : byte
{
    Goods = 1,
    ExcisableGoods = 2,
    Service = 4,
}

/// <summary>Правила услуги (D300): не хранится на складе, нет веса, объёма и неснижаемого остатка, ставка НДС обязательна.</summary>
public static class ServiceRules
{
    /// <summary>Единицы по умолчанию для услуг: «усл. ед» (ОКЕИ 876) и «ч» (356).</summary>
    public const string ServiceUnitCode = "876";
    public const string HourUnitCode = "356";

    public static void EnsureVat(ItemType type, long? vatRateId)
    {
        if (type == ItemType.Service && vatRateId is null)
        {
            throw new BusinessRuleException("catalog.service.vat_required", "У услуги ставка НДС обязательна — выберите её (или «Без НДС»).");
        }
    }

    /// <summary>Позиция, которая не хранится на складе, в складском документе — ошибка.</summary>
    public static BusinessRuleException NotStocked(IEnumerable<string> codes) =>
        new("item.service_not_stocked",
            $"Услуги не хранятся на складе и не проводятся складскими документами: {string.Join(", ", codes.Take(10))}.");

    /// <summary>Комплект (D302) в складском документе — ошибка: на складе лежат его компоненты.</summary>
    public static BusinessRuleException BundleNotStocked(IEnumerable<string> codes) =>
        new("item.bundle_not_stocked",
            $"Комплект не хранится на складе — в складском документе укажите его компоненты: {string.Join(", ", codes.Take(10))}.");

    /// <summary>D354 (сессия 4, Р1): в акте услуг — только услуги; товар и комплект оформляются отгрузкой или поступлением.</summary>
    public static BusinessRuleException ActGoods(IEnumerable<string> codes)
    {
        var list = string.Join(", ", codes.Take(10));
        return new BusinessRuleException("stock.service_act.goods", $"В акте услуг — только услуги; товары оформляются отгрузкой или поступлением: {list}.");
    }
}

/// <summary>
/// Позиция номенклатуры: пряжа, фурнитура, изделие. Код уникален в организации навсегда — архивный код
/// не выдаётся повторно, чтобы старые документы не указывали на другую вещь. Не удаляется — архивируется.
/// </summary>
public sealed class Item
{
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 300;
    public const int DescriptionMaxLength = 1000;

    private Item()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public ItemType Type { get; private set; }
    public long UnitId { get; private set; }
    public string? Description { get; private set; }

    /// <summary>Вид ставки НДС (D60); процент — по дате документа из справочника ставок. Пусто — не указана.</summary>
    public long? VatRateId { get; private set; }

    /// <summary>Группа (папка) номенклатуры (D79); null — без группы.</summary>
    public long? GroupId { get; private set; }

    public string? Article { get; private set; }
    public string? OriginCountryCode { get; private set; }
    public string? OriginCountryName { get; private set; }
    public string? CustomsDeclaration { get; private set; }
    public string? TnVedCode { get; private set; }
    public decimal? WeightKg { get; private set; }
    public decimal? VolumeM3 { get; private set; }

    /// <summary>Неснижаемый остаток: ниже — позиция в списке «Нужно закупить».</summary>
    public decimal? MinStock { get; private set; }

    /// <summary>Закупочная цена по умолчанию — подставляется в заказ поставщику.</summary>
    public decimal? PurchasePrice { get; private set; }

    /// <summary>D151: валюта закупочной цены (по умолчанию рубль); в заказ поставщику — по курсу на дату заказа.</summary>
    public string PurchaseCurrency { get; private set; } = PriceCurrencies.Default;

    /// <summary>Основная позиция, если это модификация (D82): «Свитер» → «Свитер, красный, 48».</summary>
    public long? ParentItemId { get; private set; }

    /// <summary>Ключ набора значений характеристик модификации (<see cref="Variants.Key"/>); уникален у одной основной позиции.</summary>
    public string? VariantKey { get; private set; }

    public bool IsModification => ParentItemId is not null;

    /// <summary>Признак предмета расчёта для чека (54-ФЗ, D305): у услуги всегда «услуга».</summary>
    public PaymentSubject PaymentSubject { get; private set; } = PaymentSubject.Goods;

    /// <summary>Маркируемый товар «Честного ЗНАКа» (D305): вид продукции и GTIN-14; коды маркировки — отдельное ТЗ.</summary>
    public bool IsMarked { get; private set; }

    public TrackingType TrackingType { get; private set; }
    public string? Gtin { get; private set; }

    /// <summary>Минимальная цена продажи (D305): цена ниже — предупреждение в заказе.</summary>
    public decimal? MinPrice { get; private set; }

    public ItemTradeDetails Trade => new(PaymentSubject, IsMarked, TrackingType, Gtin, MinPrice);

    /// <summary>Предмет расчёта, маркировка и минимальная цена (D305). Возвращает изменения для журнала.</summary>
    public IReadOnlyList<FieldChange> SetTrade(ItemTradeDetails details)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        var d = TradeRules.Normalize(Type, details);
        (string Field, string? Before, string? After)[] pairs =
        [
            ("Предмет расчёта", TradeRules.PaymentSubjectName(PaymentSubject), TradeRules.PaymentSubjectName(d.PaymentSubject)),
            ("Маркировка", IsMarked ? Marking.Name(TrackingType) : null, d.IsMarked ? Marking.Name(d.TrackingType) : null),
            ("GTIN", Gtin, d.Gtin), ("Минимальная цена", TradeRules.Format(MinPrice), TradeRules.Format(d.MinPrice)),
        ];
        PaymentSubject = d.PaymentSubject;
        IsMarked = d.IsMarked;
        TrackingType = d.TrackingType;
        Gtin = d.Gtin;
        MinPrice = d.MinPrice;
        return pairs.Where(p => p.Before != p.After).Select(p => new FieldChange(p.Field, p.Before, p.After)).ToList();
    }

    public bool IsArchived { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Сведения карточки (D79); ссылку на группу проверяет сервис. Возвращает изменения для журнала.</summary>
    public IReadOnlyList<FieldChange> SetDetails(ItemDetails details)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        var d = ItemDetailRules.Normalize(details);
        if (Type == ItemType.Service && (d.WeightKg is not null || d.VolumeM3 is not null || d.MinStock is not null))
        {
            // D300: услуга не хранится на складе — у неё нет веса, объёма и неснижаемого остатка.
            throw new BusinessRuleException("catalog.service.no_stock_fields", "У услуги нет веса, объёма и неснижаемого остатка — оставьте поля пустыми.");
        }

        if (Type == ItemType.Bundle && d.MinStock is not null)
        {
            // D302: остаток комплекта считается по компонентам — неснижаемый остаток задаётся у компонентов.
            throw new BusinessRuleException("catalog.bundle.no_min_stock", "У комплекта нет неснижаемого остатка — задайте его у компонентов.");
        }

        static string? N(decimal? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (string Field, string? Before, string? After)[] pairs =
        [
            ("Группа", GroupId?.ToString(), d.GroupId?.ToString()), ("Артикул", Article, d.Article),
            ("Страна происхождения", OriginCountryCode, d.OriginCountryCode), ("Номер декларации", CustomsDeclaration, d.CustomsDeclaration),
            ("Код ТН ВЭД", TnVedCode, d.TnVedCode), ("Вес, кг", N(WeightKg), N(d.WeightKg)), ("Объём, м³", N(VolumeM3), N(d.VolumeM3)),
            ("Неснижаемый остаток", N(MinStock), N(d.MinStock)), ("Закупочная цена", N(PurchasePrice), N(d.PurchasePrice)),
            ("Валюта закупочной цены", PurchaseCurrency, PriceCurrencies.Normalize(d.PurchaseCurrency)),
        ];
        GroupId = d.GroupId;
        Article = d.Article;
        OriginCountryCode = d.OriginCountryCode;
        OriginCountryName = d.OriginCountryName;
        CustomsDeclaration = d.CustomsDeclaration;
        TnVedCode = d.TnVedCode;
        WeightKg = d.WeightKg;
        VolumeM3 = d.VolumeM3;
        MinStock = d.MinStock;
        PurchasePrice = d.PurchasePrice;
        PurchaseCurrency = PriceCurrencies.Normalize(d.PurchaseCurrency);
        return pairs.Where(p => p.Before != p.After).Select(p => new FieldChange(p.Field, p.Before, p.After)).ToList();
    }

    public ItemDetails Details => new(GroupId, Article, OriginCountryCode, OriginCountryName, CustomsDeclaration, TnVedCode, WeightKg, VolumeM3,
        MinStock, PurchasePrice, PurchaseCurrency);

    /// <summary>
    /// Модификация (D82): тип, единица, НДС, группа и сведения карточки — как у основной позиции; код, название, остаток,
    /// штрихкоды и цены — свои. У модификации не бывает своих модификаций.
    /// </summary>
    public static Item CreateModification(Item parent, string code, string name, string variantKey, DateTime nowUtc)
    {
        if (parent.IsModification)
        {
            throw new BusinessRuleException("catalog.variant.nested", "У модификации не может быть своих модификаций — создайте их у основной позиции.");
        }

        if (parent.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        if (!ItemTypes.IsStocked(parent.Type))
        {
            throw new BusinessRuleException("catalog.variant.not_goods", "Модификации бывают только у товаров — у услуги и комплекта их нет.");
        }

        var item = Create(parent.OrganizationId, code, name, parent.Type, parent.UnitId, parent.Description, nowUtc);
        item.ParentItemId = parent.Id;
        item.VariantKey = variantKey;
        item.VatRateId = parent.VatRateId;
        item.SetDetails(parent.Details);
        item.SetTrade(parent.Trade with { Gtin = null }); // D305: GTIN у каждой модификации свой
        return item;
    }

    /// <summary>Новый набор значений характеристик модификации.</summary>
    public void SetVariantKey(string variantKey)
    {
        if (!IsModification)
        {
            throw new BusinessRuleException("catalog.variant.not_modification", "Это не модификация.");
        }

        VariantKey = variantKey;
    }

    public static Item Create(long organizationId, string code, string name, ItemType type, long unitId, string? description, DateTime nowUtc)
    {
        var item = new Item { OrganizationId = organizationId, CreatedAtUtc = nowUtc };
        item.Set(code, name, type, unitId, description);
        item.PaymentSubject = ItemTypes.DefaultPaymentSubject(type);
        return item;
    }

    /// <summary>Смена ставки НДС. Возвращает изменение для журнала (номера ставок) или null, если ставка та же.</summary>
    public FieldChange? SetVatRate(long? vatRateId)
    {
        if (vatRateId == VatRateId)
        {
            return null;
        }

        var change = new FieldChange(nameof(VatRateId), VatRateId?.ToString(), vatRateId?.ToString());
        VatRateId = vatRateId;
        return change;
    }

    public IReadOnlyList<FieldChange> Update(string code, string name, ItemType type, long unitId, string? description)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        if (type != Type && IsModification)
        {
            throw new BusinessRuleException("catalog.variant.type", "Тип модификации — как у основной позиции; меняйте тип основной позиции.");
        }

        var before = (Code, Name, Type, UnitId, Description);
        Set(code, name, type, unitId, description);
        var changes = new List<FieldChange>();
        if (!ItemTypes.IsStocked(Type) && ItemTypes.IsStocked(before.Type))
        {
            // D300, D302: при переводе в услуги или комплекты складские сведения очищаются (остаток и партии проверяет сервис и
            // триггер базы). У комплекта вес и объём остаются — для доставки.
            static string? N(decimal? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var service = Type == ItemType.Service;
            foreach (var (field, value) in new[] { ("Вес, кг", service ? WeightKg : null), ("Объём, м³", service ? VolumeM3 : null), ("Неснижаемый остаток", MinStock) })
            {
                if (value is not null)
                {
                    changes.Add(new FieldChange(field, N(value), null));
                }
            }

            WeightKg = service ? null : WeightKg;
            VolumeM3 = service ? null : VolumeM3;
            MinStock = null;
        }

        // D305: у услуги предмет расчёта «услуга» и нет маркировки; услуга, ставшая товаром, — «товар».
        if (Type == ItemType.Service && (PaymentSubject != PaymentSubject.Service || IsMarked || Gtin is not null))
        {
            PaymentSubject = PaymentSubject.Service;
            IsMarked = false;
            TrackingType = TrackingType.None;
            Gtin = null;
        }
        else if (Type != ItemType.Service && PaymentSubject == PaymentSubject.Service)
        {
            PaymentSubject = PaymentSubject.Goods;
        }

        void Add(string field, string? b, string? a)
        {
            if (b != a)
            {
                changes.Add(new FieldChange(field, b, a));
            }
        }

        Add("Код", before.Code, Code);
        Add("Наименование", before.Name, Name);
        Add("Тип", ItemTypes.Name(before.Type), ItemTypes.Name(Type));
        Add(nameof(UnitId), before.UnitId.ToString(), UnitId.ToString());
        Add("Описание", before.Description, Description);
        return changes;
    }

    public void Archive(DateTime nowUtc)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция уже в архиве.");
        }

        IsArchived = true;
        ArchivedAtUtc = nowUtc;
    }

    public void Restore()
    {
        if (!IsArchived)
        {
            throw new BusinessRuleException("catalog.not_archived", "Позиция не в архиве.");
        }

        IsArchived = false;
        ArchivedAtUtc = null;
    }

    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private void Set(string code, string name, ItemType type, long unitId, string? description)
    {
        var c = NormalizeCode(code);
        if (c.Length is 0 or > CodeMaxLength || c.Any(char.IsWhiteSpace))
        {
            throw new BusinessRuleException("catalog.item.code_invalid", $"Код: 1–{CodeMaxLength} символов без пробелов.");
        }

        if (!ItemTypes.IsDefined(type))
        {
            throw new BusinessRuleException("catalog.item.type_invalid", "Выберите тип номенклатуры.");
        }

        Code = c;
        Name = DomainText.Require(name, NameMaxLength, "Наименование");
        Type = type;
        UnitId = unitId;
        Description = DomainText.Optional(description, DescriptionMaxLength, "Описание");
    }
}
