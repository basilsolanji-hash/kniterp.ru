using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

/// <summary>
/// Маршрут изделия (D401): этапы из стандартного порядка «вязание → сборка → ВТО → контроль качества → упаковка», которые проходит изделие.
/// Вязание и контроль качества обязательны (без них нет годного и брака). Хранится битовой маской: бит (этап − 1); полный маршрут — 31.
/// Номера этапов — постоянные коды операций сдельных расценок, маршрут их не меняет, а только выбирает.
/// </summary>
public readonly record struct ProductionRoute
{
    public const byte FullMask = 0b1_1111;
    public const byte RequiredMask = 0b0_1001; // вязание (бит 0) и контроль качества (бит 3)

    public static readonly ProductionRoute Full = new(FullMask);

    private ProductionRoute(byte mask) => Mask = mask;

    public byte Mask { get; }

    public List<ProductionStage> Stages => ProductionFlow.Stages.Where(Contains).ToList();

    public ProductionStage Last => Stages[^1];

    public static ProductionRoute From(byte mask)
    {
        if ((mask & ~FullMask) != 0 || (mask & RequiredMask) != RequiredMask)
        {
            throw new BusinessRuleException("production.route.invalid", "В маршруте обязательны вязание и контроль качества.");
        }

        return new ProductionRoute(mask);
    }

    public static ProductionRoute Of(IEnumerable<ProductionStage> stages) =>
        From((byte)stages.Distinct().Aggregate(0, (m, s) => ProductionFlow.Stages.Contains(s) ? m | Bit(s) : m));

    public bool Contains(ProductionStage stage) => ProductionFlow.Stages.Contains(stage) && (Mask & Bit(stage)) != 0;

    /// <summary>Предыдущий этап маршрута; у первого (вязания) — нет.</summary>
    public ProductionStage? Previous(ProductionStage stage)
    {
        var list = Stages;
        var i = list.IndexOf(stage);
        return i > 0 ? list[i - 1] : null;
    }

    /// <summary>Следующий этап маршрута; у последнего — нет.</summary>
    public ProductionStage? Next(ProductionStage stage)
    {
        var list = Stages;
        var i = list.IndexOf(stage);
        return i >= 0 && i < list.Count - 1 ? list[i + 1] : null;
    }

    public string Describe() => string.Join(" → ", Stages.Select(ProductionFlow.StageName));

    private static int Bit(ProductionStage stage) => 1 << ((int)stage - 1);
}
