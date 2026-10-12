using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Вид строки расчёта себестоимости (D740).</summary>
public enum DevCostLineKind : byte
{
    Yarn = 1,
    Accessory = 2,
    Labor = 3,
}

/// <summary>Как считается цена (D742): от наценки на себестоимость или задана целевая цена.</summary>
public enum DevPriceMode : byte
{
    Markup = 1,
    TargetPrice = 2,
}

/// <summary>
/// Фурнитура и упаковка модели (D741): позиция каталога «Фурнитура», «Упаковка» или «Материал» и количество на изделие. Справочник
/// модели — меняется, расчёт берёт текущий список; при передаче в производство строки уходят в черновик техкарты.
/// </summary>
public sealed class DevModelAccessory
{
    public const decimal MaxQuantity = 100_000m;

    private DevModelAccessory()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevModelAccessory Create(long organizationId, long modelId, long itemId, decimal quantity) =>
        new() { OrganizationId = organizationId, ModelId = modelId, ItemId = itemId, Quantity = Check(quantity) };

    public void Set(decimal quantity) => Quantity = Check(quantity);

    private static decimal Check(decimal quantity) =>
        quantity is > 0 and <= MaxQuantity && decimal.Round(quantity, 6) == quantity
            ? quantity
            : throw new BusinessRuleException("dev.accessory.quantity", "Количество фурнитуры — больше нуля, не больше 100 000, до 6 знаков.");
}

/// <summary>Строка исходных данных расчёта: количество на изделие без отходов, цена за единицу, процент отходов.</summary>
public sealed record DevCostInputLine(DevCostLineKind Kind, long? ItemId, string Name, string Unit, decimal Quantity, decimal UnitPrice, decimal WastePercent);

/// <summary>Строка результата: количество с отходами и сумма (до копеек).</summary>
public sealed record DevCostResultLine(DevCostLineKind Kind, long? ItemId, string Name, string Unit, decimal Quantity, decimal WastePercent, decimal UnitPrice,
    decimal Amount);

/// <summary>Итог расчёта (D740–D743).</summary>
public sealed record DevCostResult(
    IReadOnlyList<DevCostResultLine> Lines, decimal Yarn, decimal Accessories, decimal Labor, decimal Overhead, decimal Cost, decimal Price,
    decimal Margin, decimal? MarginPercent, decimal? VersusTargetCost);

/// <summary>
/// Себестоимость и цена изделия (D740–D743): пряжа и фурнитура — количество × (1 + отходы %) × цена; работа — расценка операции или минуты ×
/// ставка; накладные — % от прямых затрат; цена — себестоимость × (1 + наценка %) или целевая; маржа — цена − себестоимость.
/// Суммы округляются до копеек построчно; итоги — суммы строк.
/// </summary>
public static class DevCosting
{
    public const decimal MaxPercent = 1000m;
    public const decimal MaxPrice = 1_000_000_000m;

    public static DevCostResult Compute(IReadOnlyList<DevCostInputLine> lines, decimal overheadPercent, DevPriceMode mode, decimal? markupPercent,
        decimal? targetPrice, decimal? targetCost)
    {
        EnsurePercent(overheadPercent, "Накладные расходы");
        var result = new List<DevCostResultLine>();
        foreach (var l in lines)
        {
            if (l.Quantity < 0 || l.UnitPrice is < 0 or > MaxPrice || l.WastePercent is < 0 or > 100)
            {
                throw new BusinessRuleException("dev.cost.line", $"«{l.Name}»: количество и цена — не меньше нуля, отходы — от 0 до 100 %.");
            }

            var quantity = decimal.Round(l.Quantity * (100 + l.WastePercent) / 100m, 6, MidpointRounding.AwayFromZero);
            result.Add(new DevCostResultLine(l.Kind, l.ItemId, l.Name, l.Unit, quantity, l.WastePercent, l.UnitPrice, Money(quantity * l.UnitPrice)));
        }

        var yarn = result.Where(r => r.Kind == DevCostLineKind.Yarn).Sum(r => r.Amount);
        var accessories = result.Where(r => r.Kind == DevCostLineKind.Accessory).Sum(r => r.Amount);
        var labor = result.Where(r => r.Kind == DevCostLineKind.Labor).Sum(r => r.Amount);
        var overhead = Money((yarn + accessories + labor) * overheadPercent / 100m);
        var cost = yarn + accessories + labor + overhead;
        decimal price;
        if (mode == DevPriceMode.TargetPrice)
        {
            price = targetPrice is { } t && t is >= 0 and <= MaxPrice
                ? Money(t)
                : throw new BusinessRuleException("dev.cost.price", "Укажите целевую цену — от 0 до 1 млрд.");
        }
        else
        {
            var markup = markupPercent ?? 0;
            EnsurePercent(markup, "Наценка");
            price = Money(cost * (100 + markup) / 100m);
        }

        var margin = price - cost;
        return new DevCostResult(result, yarn, accessories, labor, overhead, cost, price, margin,
            price == 0 ? null : decimal.Round(margin * 100m / price, 2, MidpointRounding.AwayFromZero),
            targetCost is { } tc ? cost - tc : null);
    }

    public static void EnsurePercent(decimal value, string name)
    {
        if (value is < 0 or > MaxPercent)
        {
            throw new BusinessRuleException("dev.cost.percent", $"{name} — от 0 до 1000 %.");
        }
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Сохранённая версия расчёта (D744) — только вставка (триггер): номер версии по модели, условия (накладные, способ цены, наценка),
/// итоги и строки. Сценарий «другая пряжа» — новая версия с заменой материала, прежние версии не меняются.
/// </summary>
public sealed class DevCostRun
{
    public const int NoteMaxLength = 300;
    private readonly List<DevCostLine> _lines = [];

    private DevCostRun()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public int Version { get; private set; }
    public string? Note { get; private set; }
    public decimal OverheadPercent { get; private set; }
    public DevPriceMode PriceMode { get; private set; }
    public decimal? MarkupPercent { get; private set; }
    public decimal Yarn { get; private set; }
    public decimal Accessories { get; private set; }
    public decimal Labor { get; private set; }
    public decimal Overhead { get; private set; }
    public decimal Cost { get; private set; }
    public decimal Price { get; private set; }
    public decimal Margin { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public IReadOnlyList<DevCostLine> Lines => _lines;

    public static DevCostRun Create(long organizationId, long modelId, int version, string? note, decimal overheadPercent, DevPriceMode mode,
        decimal? markupPercent, DevCostResult r, DateTime nowUtc, long userId)
    {
        var run = new DevCostRun
        {
            OrganizationId = organizationId,
            ModelId = modelId,
            Version = version,
            Note = DomainText.Optional(note, NoteMaxLength, "Комментарий к расчёту"),
            OverheadPercent = overheadPercent,
            PriceMode = mode,
            MarkupPercent = mode == DevPriceMode.Markup ? markupPercent ?? 0 : null,
            Yarn = r.Yarn,
            Accessories = r.Accessories,
            Labor = r.Labor,
            Overhead = r.Overhead,
            Cost = r.Cost,
            Price = r.Price,
            Margin = r.Margin,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = userId,
        };
        foreach (var l in r.Lines)
        {
            run._lines.Add(new DevCostLine(organizationId, l));
        }

        return run;
    }
}

/// <summary>Строка версии расчёта (D744), только вставка.</summary>
public sealed class DevCostLine
{
    public const int NameMaxLength = 300;

    private DevCostLine()
    {
    }

    internal DevCostLine(long organizationId, DevCostResultLine l)
    {
        OrganizationId = organizationId;
        Kind = l.Kind;
        ItemId = l.ItemId;
        Name = l.Name.Length > NameMaxLength ? l.Name[..NameMaxLength] : l.Name;
        Unit = l.Unit.Length > 20 ? l.Unit[..20] : l.Unit;
        Quantity = l.Quantity;
        WastePercent = l.WastePercent;
        UnitPrice = l.UnitPrice;
        Amount = l.Amount;
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long RunId { get; private set; }
    public DevCostLineKind Kind { get; private set; }
    public long? ItemId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Unit { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal WastePercent { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Amount { get; private set; }
}
