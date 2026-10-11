using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Упаковка позиции (D307), аналог packs в МойСклад: «коробка = 12 шт», «мешок = 25 кг». Единица упаковки — из справочника
/// единиц и не совпадает с основной; коэффициент — сколько основных единиц в одной упаковке (decimal(18,6), по точности основной
/// единицы). Свой штрихкод — для сканера: скан упаковки даёт позицию и количество в основных единицах. Только у товаров.
/// </summary>
public sealed class ItemPack
{
    public const int MaxPerItem = 10;
    public const decimal MaxFactor = 1_000_000m;

    private ItemPack()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public long UnitId { get; private set; }
    public decimal Factor { get; private set; }
    public string? Barcode { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ItemPack Create(long organizationId, Item item, long unitId, decimal factor, byte basePrecision, string? barcode)
    {
        if (!ItemTypes.IsStocked(item.Type))
        {
            throw new BusinessRuleException("catalog.pack.not_goods", "Упаковки бывают только у товаров — не у услуг и комплектов.");
        }

        if (unitId == item.UnitId)
        {
            throw new BusinessRuleException("catalog.pack.same_unit", "Единица упаковки совпадает с основной — выберите другую (коробка, мешок, упаковка).");
        }

        var pack = new ItemPack { OrganizationId = organizationId, ItemId = item.Id, UnitId = unitId };
        pack.Set(factor, basePrecision, barcode);
        return pack;
    }

    public void Set(decimal factor, byte basePrecision, string? barcode)
    {
        Factor = Packs.CheckFactor(factor, basePrecision);
        Barcode = Packs.NormalizeBarcode(barcode);
    }
}

public static class Packs
{
    public static decimal CheckFactor(decimal factor, byte basePrecision)
    {
        if (factor <= 0 || factor > ItemPack.MaxFactor)
        {
            throw new BusinessRuleException("catalog.pack.factor", "Сколько основных единиц в упаковке — больше нуля.");
        }

        if (decimal.Round(factor, basePrecision) != factor)
        {
            throw basePrecision == 0
                ? new BusinessRuleException("catalog.pack.precision", "В упаковке должно быть целое число основных единиц.")
                : new BusinessRuleException("catalog.pack.precision", $"Для основной единицы допустимо не больше {basePrecision} знаков после запятой.");
        }

        return factor;
    }

    /// <summary>Штрихкод упаковки — по тем же правилам, что у позиции (тип по виду кода); пусто — нет.</summary>
    public static string? NormalizeBarcode(string? barcode)
    {
        var trimmed = barcode?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(trimmed) ? null : ItemBarcode.Normalize(ItemBarcode.Detect(trimmed), trimmed);
    }

    /// <summary>
    /// Количество в упаковках → в основных единицах (для ввода строк документов). Результат должен укладываться в точность основной
    /// единицы: 2,5 коробки по 12 шт = 30 шт — можно; 0,1 коробки по 12 шт = 1,2 шт — нельзя.
    /// </summary>
    public static decimal ToBase(decimal packs, decimal factor, byte basePrecision)
    {
        if (packs <= 0)
        {
            throw new BusinessRuleException("catalog.pack.quantity", "Количество упаковок — больше нуля.");
        }

        var result = packs * factor;
        if (decimal.Round(result, basePrecision) != result)
        {
            throw new BusinessRuleException("catalog.pack.not_whole", $"{packs} упак. × {factor} = {result} — не укладывается в точность основной единицы.");
        }

        return result;
    }

    /// <summary>Сколько целых упаковок и остаток в основных единицах: 30 шт при коробке 12 → 2 коробки и 6 шт.</summary>
    public static (decimal Whole, decimal Remainder) Split(decimal quantity, decimal factor) =>
        factor <= 0 ? (0, quantity) : (decimal.Floor(quantity / factor), quantity - decimal.Floor(quantity / factor) * factor);
}
