namespace KnitErp.Domain.Development;

/// <summary>Деталь для расчёта норм (D751): сколько штук в изделии, вес одной детали и доли цветов палитры в процентах.</summary>
public sealed record DevNormDetail(string Name, int Quantity, decimal? WeightGrams, IReadOnlyDictionary<long, decimal> SharePercentByColor);

/// <summary>Итог расчёта: граммы пряжи на изделие по цвету палитры и предупреждения (чего не хватило для расчёта).</summary>
public sealed record DevNormResult(IReadOnlyDictionary<long, decimal> GramsByColor, IReadOnlyList<DevNote> Warnings);

/// <summary>
/// Нормы пряжи на одно изделие для техкарты производства (D751): вес детали (факт образца, иначе план) × штук в изделии × доля цвета.
/// Доли — из факта цеха; у одноцветной модели вся деталь — единственный цвет. Отходы и усушку задаёт производство в своей техкарте.
/// </summary>
public static class DevNorms
{
    public static DevNormResult Compute(IReadOnlyList<DevNormDetail> details, IReadOnlyCollection<long> colorIds)
    {
        var grams = new Dictionary<long, decimal>();
        var warnings = new List<DevNote>();
        foreach (var d in details)
        {
            if (d.WeightGrams is not { } weight || weight <= 0)
            {
                warnings.Add(DevNote.Of(DevNotes.NoWeight, d.Name));
                continue;
            }

            var shares = d.SharePercentByColor.Where(s => colorIds.Contains(s.Key) && s.Value > 0).ToList();
            if (shares.Count == 0 && colorIds.Count == 1)
            {
                shares = [new(colorIds.First(), 100m)];
            }

            if (shares.Count == 0)
            {
                warnings.Add(DevNote.Of(DevNotes.NoShares, d.Name));
                continue;
            }

            var total = shares.Sum(s => s.Value);
            foreach (var (color, percent) in shares)
            {
                grams[color] = grams.GetValueOrDefault(color) + weight * d.Quantity * percent / total;
            }
        }

        return new DevNormResult(grams.ToDictionary(g => g.Key, g => Math.Round(g.Value, 3, MidpointRounding.AwayFromZero)), warnings);
    }

    /// <summary>Граммы в единицу материала: килограмм (код ОКЕИ 166) или грамм (163); иначе — null (норму вводит производство).</summary>
    public static decimal? ToUnit(decimal grams, string unitCode, byte precision)
    {
        var value = unitCode switch
        {
            "166" => grams / 1000m,
            "163" => grams,
            _ => (decimal?)null,
        };
        if (value is not { } v)
        {
            return null;
        }

        var step = 1m / (decimal)Math.Pow(10, precision);
        return Math.Max(step, Math.Ceiling(v / step) * step);
    }
}
