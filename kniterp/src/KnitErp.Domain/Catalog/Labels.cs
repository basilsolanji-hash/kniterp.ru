using System.Text.Json;
using System.Text.Json.Serialization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>Что выводит строка этикетки (D316).</summary>
public enum LabelField : byte
{
    Name = 1,
    Code = 2,
    Article = 3,
    Price = 4,
    Barcode = 5,
    Unit = 6,
    Characteristics = 7,
    Country = 8,
    Description = 9,
    Group = 10,
    Organization = 11,
    CustomText = 12,
    PrintDate = 13,
}

public enum LabelAlign : byte
{
    Left = 1,
    Center = 2,
    Right = 3,
}

/// <summary>Рулон термопринтера (одна этикетка на страницу) или лист A4 с сеткой самоклеящихся этикеток.</summary>
public enum LabelSheet : byte
{
    Roll = 1,
    A4 = 2,
}

/// <summary>
/// Строка этикетки: поле, подпись перед значением (у «своего текста» — сам текст), шрифт, жирность, выравнивание, перенос,
/// высота штрихкода в мм (для поля «Штрихкод»).
/// </summary>
public sealed record LabelElement(LabelField Field, string? Text, byte FontPt, bool Bold, LabelAlign Align, bool Wrap, byte BarcodeHeightMm);

/// <summary>Готовый образец шаблона (размер, лист, строки) — с него начинают свой шаблон.</summary>
public sealed record LabelPreset(string Key, string Name, decimal WidthMm, decimal HeightMm, LabelSheet Sheet, byte Columns, byte Rows,
    IReadOnlyList<LabelElement> Elements);

/// <summary>
/// Шаблон этикетки организации (D316): размер в мм, рулон или лист A4 (колонки × ряды), поля, вид цены (пусто — основной).
/// Конструктор на странице «Номенклатура → Этикетки» с живым предпросмотром. Шаблоны архивируются, не удаляются.
/// </summary>
public sealed class LabelTemplate
{
    public const int NameMaxLength = 100;
    public const int TextMaxLength = 100;
    public const int MaxElements = 15;
    public const int MaxPerOrganization = 30;
    public const decimal MinSizeMm = 15;
    public const decimal MaxSizeMm = 150;
    public const decimal A4WidthMm = 210;
    public const decimal A4HeightMm = 297;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private LabelTemplate()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal WidthMm { get; private set; }
    public decimal HeightMm { get; private set; }
    public LabelSheet Sheet { get; private set; }
    public byte Columns { get; private set; } = 1;
    public byte Rows { get; private set; } = 1;
    public decimal PaddingMm { get; private set; } = 2;
    public long? PriceTypeId { get; private set; }
    public string ElementsJson { get; private set; } = "[]";
    public bool IsDefault { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<LabelElement> Elements => JsonSerializer.Deserialize<List<LabelElement>>(ElementsJson, Json) ?? [];

    public static LabelTemplate Create(long organizationId, string? name, decimal widthMm, decimal heightMm, LabelSheet sheet, byte columns, byte rows,
        decimal paddingMm, long? priceTypeId, IReadOnlyList<LabelElement> elements, DateTime nowUtc)
    {
        var t = new LabelTemplate { OrganizationId = organizationId };
        t.Set(name, widthMm, heightMm, sheet, columns, rows, paddingMm, priceTypeId, elements, nowUtc);
        return t;
    }

    /// <summary>Все проверки — до изменения полей (отказ не оставляет полуизменённый шаблон в контексте вкладки).</summary>
    public void Set(string? name, decimal widthMm, decimal heightMm, LabelSheet sheet, byte columns, byte rows, decimal paddingMm, long? priceTypeId,
        IReadOnlyList<LabelElement> elements, DateTime nowUtc)
    {
        var n = DomainText.Require(name, NameMaxLength, "Название шаблона");
        if (widthMm is < MinSizeMm or > MaxSizeMm || heightMm is < MinSizeMm or > MaxSizeMm)
        {
            throw new BusinessRuleException("label.size", "Размер этикетки — от 15 до 150 мм по каждой стороне.");
        }

        if (!Enum.IsDefined(sheet))
        {
            throw new BusinessRuleException("label.sheet", "Выберите рулон или лист A4.");
        }

        if (sheet == LabelSheet.A4 && (columns is < 1 or > 8 || rows is < 1 or > 30 || columns * widthMm > A4WidthMm || rows * heightMm > A4HeightMm))
        {
            throw new BusinessRuleException("label.a4", "Этикетки не помещаются на лист A4 (210 × 297 мм): уменьшите размер, колонки или ряды.");
        }

        if (paddingMm is < 0 or > 10 || paddingMm * 2 >= Math.Min(widthMm, heightMm))
        {
            throw new BusinessRuleException("label.padding", "Поля этикетки — от 0 до 10 мм и меньше половины её стороны.");
        }

        if (elements.Count is 0 or > MaxElements)
        {
            throw new BusinessRuleException("label.elements", "В этикетке от 1 до 15 строк.");
        }

        var clean = new List<LabelElement>(elements.Count);
        foreach (var e in elements)
        {
            if (!Enum.IsDefined(e.Field) || !Enum.IsDefined(e.Align))
            {
                throw new BusinessRuleException("label.element", "Неизвестное поле или выравнивание строки этикетки.");
            }

            if (e.FontPt is < 5 or > 48)
            {
                throw new BusinessRuleException("label.font", "Размер шрифта — от 5 до 48 пт.");
            }

            // Подпись перед значением хранится как есть (пробел в «Арт. » нужен); «свой текст» — без пробелов по краям.
            var text = string.IsNullOrWhiteSpace(e.Text) ? null : e.Field == LabelField.CustomText ? e.Text.Trim() : e.Text;
            if (text is { Length: > TextMaxLength } || text?.Any(char.IsControl) == true)
            {
                throw new BusinessRuleException("label.text", "Текст строки этикетки — до 100 знаков в одну строку.");
            }

            if (e.Field == LabelField.CustomText && text is null)
            {
                throw new BusinessRuleException("label.custom_text", "У строки «Свой текст» заполните текст.");
            }

            if (e.Field == LabelField.Barcode && (e.BarcodeHeightMm < 5 || e.BarcodeHeightMm >= heightMm))
            {
                throw new BusinessRuleException("label.barcode_height", "Высота штрихкода — от 5 мм и меньше высоты этикетки.");
            }

            clean.Add(e with { Text = text, BarcodeHeightMm = e.Field == LabelField.Barcode ? e.BarcodeHeightMm : (byte)0 });
        }

        Name = n;
        WidthMm = widthMm;
        HeightMm = heightMm;
        Sheet = sheet;
        Columns = sheet == LabelSheet.A4 ? columns : (byte)1;
        Rows = sheet == LabelSheet.A4 ? rows : (byte)1;
        PaddingMm = paddingMm;
        PriceTypeId = priceTypeId;
        ElementsJson = JsonSerializer.Serialize(clean, Json);
        UpdatedAtUtc = nowUtc;
    }

    public void SetDefault(bool value) => IsDefault = value;

    public void SetArchived(bool value)
    {
        IsArchived = value;
        if (value)
        {
            IsDefault = false;
        }
    }

    private static LabelElement E(LabelField f, byte font, bool bold = false, LabelAlign align = LabelAlign.Left, string? text = null, bool wrap = false,
        byte barcode = 0) => new(f, text, font, bold, align, wrap, barcode);

    /// <summary>Образцы: распространённые размеры термоэтикеток и листов A4.</summary>
    public static readonly IReadOnlyList<LabelPreset> Presets =
    [
        new("price-58x40", "Ценник 58×40 мм", 58, 40, LabelSheet.Roll, 1, 1,
            [E(LabelField.Name, 9, true, wrap: true), E(LabelField.Article, 7, text: "Арт. "), E(LabelField.Price, 18, true, LabelAlign.Center),
             E(LabelField.Barcode, 7, barcode: 11)]),
        new("barcode-40x30", "Штрихкод 40×30 мм", 40, 30, LabelSheet.Roll, 1, 1,
            [E(LabelField.Name, 7, true), E(LabelField.Barcode, 7, barcode: 14), E(LabelField.Code, 6, align: LabelAlign.Center)]),
        new("clothes-43x25", "Одежда 43×25 мм", 43, 25, LabelSheet.Roll, 1, 1,
            [E(LabelField.Name, 7, true), E(LabelField.Characteristics, 6), E(LabelField.Barcode, 6, barcode: 10)]),
        new("small-30x20", "Маленькая 30×20 мм", 30, 20, LabelSheet.Roll, 1, 1,
            [E(LabelField.Barcode, 6, barcode: 11), E(LabelField.Code, 6, align: LabelAlign.Center)]),
        new("a4-70x37", "Лист A4, 3×8 (70×37 мм)", 70, 37, LabelSheet.A4, 3, 8,
            [E(LabelField.Name, 8, true, wrap: true), E(LabelField.Article, 7, text: "Арт. "), E(LabelField.Price, 13, true, LabelAlign.Right),
             E(LabelField.Barcode, 7, barcode: 11)]),
        new("a4-105x74", "Лист A4, 2×4 (105×74 мм) — коробка", 105, 74, LabelSheet.A4, 2, 4,
            [E(LabelField.Organization, 8), E(LabelField.Name, 14, true, wrap: true), E(LabelField.Characteristics, 10), E(LabelField.Country, 8, text: "Страна: "),
             E(LabelField.Barcode, 9, barcode: 22), E(LabelField.PrintDate, 7, align: LabelAlign.Right, text: "Дата: ")]),
    ];

    public static string FieldName(LabelField field) => field switch
    {
        LabelField.Name => "Наименование",
        LabelField.Code => "Код",
        LabelField.Article => "Артикул",
        LabelField.Price => "Цена",
        LabelField.Barcode => "Штрихкод",
        LabelField.Unit => "Единица",
        LabelField.Characteristics => "Характеристики",
        LabelField.Country => "Страна происхождения",
        LabelField.Description => "Описание",
        LabelField.Group => "Группа",
        LabelField.Organization => "Организация",
        LabelField.CustomText => "Свой текст",
        _ => "Дата печати",
    };
}
