using KnitErp.Domain.Production;

namespace KnitErp.Domain.Sales;

/// <summary>Откуда взята себестоимость позиции.</summary>
public enum CostSource : byte
{
    None = 0,
    Purchases = 1,
    TechCard = 2,

    /// <summary>D352 (сессия 4, Р2): комплект — по компонентам его строк разворота.</summary>
    Components = 3,
}

/// <summary>
/// Себестоимость единицы для прибыльности (D69). Склад ведёт количества без денег, поэтому оценка такая:
/// 1) средняя цена закупки без НДС — по приёмкам по заказам поставщикам за вычетом возвратов (покупной товар, пряжа);
/// 2) иначе — по действующей техкарте из себестоимости материалов, рекурсивно для полуфабрикатов;
/// 3) иначе — неизвестна. Циклы в техкартах (изделие из самого себя) дают «неизвестна», а не зацикливание.
/// </summary>
public static class UnitCosts
{
    public static IReadOnlyDictionary<long, (decimal Cost, CostSource Source)> Calculate(
        IReadOnlyDictionary<long, (decimal Quantity, decimal Value)> purchases, IReadOnlyDictionary<long, TechCard> activeCards, IEnumerable<long> items)
    {
        var result = new Dictionary<long, (decimal, CostSource)>();
        var known = new Dictionary<long, decimal?>();
        var visiting = new HashSet<long>();

        decimal? CostOf(long itemId)
        {
            if (known.TryGetValue(itemId, out var cached))
            {
                return cached;
            }

            decimal? cost = null;
            var source = CostSource.None;
            if (purchases.TryGetValue(itemId, out var p) && p.Quantity > 0 && p.Value >= 0)
            {
                cost = p.Value / p.Quantity;
                source = CostSource.Purchases;
            }
            else if (activeCards.TryGetValue(itemId, out var card) && visiting.Add(itemId))
            {
                cost = card.UnitCost(CostOf);
                visiting.Remove(itemId);
                source = cost is null ? CostSource.None : CostSource.TechCard;
            }

            known[itemId] = cost;
            if (cost is { } c)
            {
                result[itemId] = (c, source);
            }

            return cost;
        }

        foreach (var id in items)
        {
            CostOf(id);
        }

        return result;
    }
}
