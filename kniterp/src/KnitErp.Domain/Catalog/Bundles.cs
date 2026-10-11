using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Строка состава комплекта (D302): «Набор шапка + шарф» = 1 шапка + 1 шарф. Комплект — коммерческий набор: на складе не
/// хранится, остаток считается по компонентам, при отгрузке списываются компоненты (аналог bundle в МойСклад).
/// Компонент — товар или его модификация (не услуга и не комплект), поэтому вложенных комплектов и циклов не бывает.
/// Пара (комплект, компонент) уникальна. Строка состава — признак позиции, а не документ: удаляется с записью в журнал.
/// </summary>
public sealed class BundleComponent
{
    public const int MaxPerBundle = 50;
    public const decimal MaxQuantity = 999_999m;

    private BundleComponent()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long BundleItemId { get; private set; }
    public long ComponentItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public int SortOrder { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Проверки ссылок (тип комплекта и компонента, архив, организация) — <see cref="BundleRules"/>; здесь — количество.</summary>
    public static BundleComponent Create(long organizationId, long bundleItemId, long componentItemId, decimal quantity, byte precision, int sortOrder)
    {
        if (bundleItemId == componentItemId)
        {
            throw new BusinessRuleException("catalog.bundle.self", "Комплект не может входить в свой состав.");
        }

        return new BundleComponent
        {
            OrganizationId = organizationId, BundleItemId = bundleItemId, ComponentItemId = componentItemId,
            Quantity = BundleRules.CheckQuantity(quantity, precision), SortOrder = sortOrder,
        };
    }

    public void SetQuantity(decimal quantity, byte precision) => Quantity = BundleRules.CheckQuantity(quantity, precision);

    public void SetSortOrder(int sortOrder) => SortOrder = sortOrder;
}

public static class BundleRules
{
    /// <summary>Позиция-комплект: тип «Комплекты» и не в архиве.</summary>
    public static void EnsureBundle(Item bundle)
    {
        if (bundle.Type != ItemType.Bundle)
        {
            throw new BusinessRuleException("catalog.bundle.not_bundle", $"Позиция {bundle.Code} — не комплект. Состав бывает только у типа «Комплекты».");
        }

        if (bundle.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }
    }

    /// <summary>Компонент — действующий товар (или модификация); не услуга и не комплект. Организацию проверяет сервис (чужая — 404).</summary>
    public static void EnsureComponent(Item bundle, Item component)
    {
        if (component.Id != 0 && component.Id == bundle.Id)
        {
            throw new BusinessRuleException("catalog.bundle.self", "Комплект не может входить в свой состав.");
        }

        if (component.Type == ItemType.Bundle)
        {
            throw new BusinessRuleException("catalog.bundle.nested", $"{component.Code} — комплект. Вложенные комплекты не поддерживаются: добавьте его компоненты.");
        }

        if (component.Type == ItemType.Service)
        {
            throw new BusinessRuleException("catalog.bundle.service", $"{component.Code} — услуга. В комплект входят только товары: услугу добавьте в заказ отдельной строкой.");
        }

        if (component.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Позиция {component.Code} в архиве.");
        }
    }

    /// <summary>Количество компонента в одном комплекте: больше нуля, по точности единицы компонента.</summary>
    public static decimal CheckQuantity(decimal quantity, byte precision)
    {
        if (quantity <= 0 || quantity > BundleComponent.MaxQuantity)
        {
            throw new BusinessRuleException("catalog.bundle.quantity", "Количество компонента в комплекте — больше нуля.");
        }

        if (decimal.Round(quantity, precision) != quantity)
        {
            throw precision == 0
                ? new BusinessRuleException("catalog.bundle.precision", "Количество компонента в его единице должно быть целым.")
                : new BusinessRuleException("catalog.bundle.precision", $"Для единицы компонента допустимо не больше {precision} знаков после запятой.");
        }

        return quantity;
    }
}

/// <summary>Строка состава для расчётов: комплект, компонент, количество на один комплект, точность единицы компонента.</summary>
public sealed record BundleComponentSpec(long BundleItemId, long ComponentItemId, decimal Quantity, byte Precision);

/// <summary>Подсказка цены комплекта: сумма цен компонентов и скидка набора относительно неё.</summary>
public sealed record BundlePriceHint(decimal? ComponentsTotal, decimal? BundlePrice, IReadOnlyList<long> ComponentsWithoutPrice)
{
    /// <summary>Скидка набора: сумма цен компонентов − цена комплекта (минус — комплект дороже суммы).</summary>
    public decimal? Discount => ComponentsTotal is { } t && BundlePrice is { } p ? t - p : null;

    public decimal? DiscountPercent => ComponentsTotal is { } t && t > 0 && Discount is { } d ? decimal.Round(d / t * 100, 2) : null;
}

/// <summary>
/// Чистые расчёты комплекта (D302): разворот в компоненты, доступность, подсказка цены, себестоимость.
/// Разворот фиксирует состав на момент расчёта — поэтому изменение состава не меняет проведённые документы.
/// </summary>
public static class BundleExpansion
{
    /// <summary>
    /// Разворот количества комплекта в компоненты. Количество компонента = количество комплекта × количество в комплекте,
    /// округлённое вверх по точности единицы компонента — чтобы не списать меньше, чем ушло в наборы. Допущение D303.
    /// </summary>
    public static IReadOnlyList<(long ItemId, decimal Quantity)> Expand(long bundleId, decimal quantity, IEnumerable<BundleComponentSpec> components)
    {
        if (quantity <= 0)
        {
            throw new BusinessRuleException("catalog.bundle.quantity", "Количество комплекта — больше нуля.");
        }

        var own = components.Where(c => c.BundleItemId == bundleId).ToList();
        if (own.Count == 0)
        {
            throw new BusinessRuleException("catalog.bundle.empty", "У комплекта не задан состав.");
        }

        return own
            .GroupBy(c => c.ComponentItemId)
            .Select(g => (g.Key, Ceiling(g.Sum(c => c.Quantity) * quantity, g.First().Precision)))
            .ToList();
    }

    /// <summary>
    /// Разворот строк документа: строки комплектов заменяются компонентами, остальные — как есть; одинаковые позиции суммируются.
    /// bundleIds — какие позиции строк являются комплектами.
    /// </summary>
    public static IReadOnlyList<(long ItemId, decimal Quantity)> ExpandLines(
        IEnumerable<(long ItemId, decimal Quantity)> lines, IReadOnlySet<long> bundleIds, IReadOnlyList<BundleComponentSpec> components)
    {
        var result = new Dictionary<long, decimal>();
        foreach (var (itemId, quantity) in lines)
        {
            var parts = bundleIds.Contains(itemId) ? Expand(itemId, quantity, components) : [(itemId, quantity)];
            foreach (var (part, qty) in parts)
            {
                result[part] = result.GetValueOrDefault(part) + qty;
            }
        }

        return result.Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>
    /// Сколько комплектов можно собрать: минимум по компонентам ⌊доступно компонента / количество в комплекте⌋ (по точности единицы
    /// комплекта). Нет состава или компонента нет в наличии — 0; отрицательного не бывает.
    /// </summary>
    public static decimal Available(long bundleId, IEnumerable<BundleComponentSpec> components, IReadOnlyDictionary<long, decimal> availableByItem,
        byte bundlePrecision = 0)
    {
        var own = components.Where(c => c.BundleItemId == bundleId).ToList();
        if (own.Count == 0)
        {
            return 0;
        }

        var min = own.Min(c => availableByItem.GetValueOrDefault(c.ComponentItemId) / c.Quantity);
        return min <= 0 ? 0 : Floor(min, bundlePrecision);
    }

    /// <summary>Сумма цен компонентов (цена × количество) и скидка набора; без цены у компонента сумма не считается.</summary>
    public static BundlePriceHint PriceHint(long bundleId, IEnumerable<BundleComponentSpec> components, IReadOnlyDictionary<long, decimal> prices,
        decimal? bundlePrice)
    {
        var own = components.Where(c => c.BundleItemId == bundleId).ToList();
        var missing = own.Where(c => !prices.ContainsKey(c.ComponentItemId)).Select(c => c.ComponentItemId).ToList();
        decimal? total = own.Count == 0 || missing.Count > 0 ? null : decimal.Round(own.Sum(c => prices[c.ComponentItemId] * c.Quantity), 2);
        return new BundlePriceHint(total, bundlePrice, missing);
    }

    /// <summary>Себестоимость комплекта = сумма себестоимостей компонентов × количество; неизвестна у одного — неизвестна у всего.</summary>
    public static decimal? Cost(long bundleId, IEnumerable<BundleComponentSpec> components, IReadOnlyDictionary<long, decimal> unitCosts)
    {
        var own = components.Where(c => c.BundleItemId == bundleId).ToList();
        if (own.Count == 0 || own.Any(c => !unitCosts.ContainsKey(c.ComponentItemId)))
        {
            return null;
        }

        return decimal.Round(own.Sum(c => unitCosts[c.ComponentItemId] * c.Quantity), 4);
    }

    private static decimal Ceiling(decimal value, byte precision)
    {
        var factor = Pow10(precision);
        return decimal.Ceiling(value * factor) / factor;
    }

    private static decimal Floor(decimal value, byte precision)
    {
        var factor = Pow10(precision);
        return decimal.Floor(value * factor) / factor;
    }

    private static decimal Pow10(byte precision)
    {
        var f = 1m;
        for (var i = 0; i < precision; i++)
        {
            f *= 10;
        }

        return f;
    }
}
