using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

/// <summary>Этапы производства трикотажа по порядку (D92). Порядок значений — порядок маршрута.</summary>
public enum ProductionStage : byte
{
    Knitting = 1,
    Assembly = 2,
    Steaming = 3,
    QualityControl = 4,
    Packing = 5,
}

/// <summary>Вид записи о работе на этапе.</summary>
public enum ProductionOperationKind : byte
{
    /// <summary>Сделано на этапе (на контроле качества — годное).</summary>
    Done = 1,

    /// <summary>Исправление ошибочной отметки: уменьшение сделанного, с причиной.</summary>
    Correction = 2,

    /// <summary>Контроль качества: на переделку — изделия возвращаются на указанный этап и проходят его и следующие заново.</summary>
    Rework = 3,

    /// <summary>Контроль качества: брак — изделия выбывают с причиной; пряжа на них входит в расход по факту.</summary>
    Defect = 4,
}

/// <summary>
/// Запись о работе на этапе производственного заказа (D92). Только вставка: ошибка исправляется записью «Исправление»
/// с причиной, а не правкой или удалением (триггер в базе). Количества — в единицах изделия заказа.
/// </summary>
public sealed class ProductionOperation
{
    public const int ReasonMaxLength = 500;

    private ProductionOperation()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ProductionOrderId { get; private set; }
    public ProductionStage Stage { get; private set; }
    public ProductionOperationKind Kind { get; private set; }

    /// <summary>Положительное, кроме исправления — у него отрицательное.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Этап, на который возвращена переделка.</summary>
    public ProductionStage? ReturnToStage { get; private set; }

    public string? Reason { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Новая запись; что её можно применить к заказу, проверяет <see cref="ProductionFlow.EnsureCanApply"/>.</summary>
    public static ProductionOperation Create(
        long organizationId, long orderId, ProductionStep step, string? reason, DateOnly occurredOn, long userId, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        ProductionOrderId = orderId,
        Stage = step.Stage,
        Kind = step.Kind,
        Quantity = step.Quantity,
        ReturnToStage = step.ReturnTo,
        Reason = step.Kind is ProductionOperationKind.Correction or ProductionOperationKind.Defect
            ? DomainText.Require(reason, ReasonMaxLength, step.Kind == ProductionOperationKind.Defect ? "Причина брака" : "Причина исправления")
            : DomainText.Optional(reason, ReasonMaxLength, "Комментарий"),
        OccurredOn = occurredOn,
        CreatedByUserId = userId,
        CreatedAtUtc = nowUtc,
    };

    public ProductionStep Step => new(Stage, Kind, Quantity, ReturnToStage);
}

/// <summary>Работа на этапе без привязки к хранению — для расчёта потока.</summary>
public readonly record struct ProductionStep(ProductionStage Stage, ProductionOperationKind Kind, decimal Quantity, ProductionStage? ReturnTo = null);

/// <summary>
/// Поток изделий по этапам (D92): сколько сделано на каждом этапе и сколько можно отметить следующим.
/// Этап отмечается не больше, чем сделано на предыдущем и ещё не отмечено на этом; вязание — не больше количества заказа
/// плюс брак (брак довязывают). Контроль качества делит пришедшее со ВТО на годное, переделку и брак; переделка уменьшает
/// сделанное на этапе возврата и всех этапах до контроля — их проходят заново. Упаковка — не больше годного,
/// выпуск — не больше упакованного.
/// </summary>
public sealed class ProductionFlow
{
    public static readonly IReadOnlyList<ProductionStage> Stages =
        [ProductionStage.Knitting, ProductionStage.Assembly, ProductionStage.Steaming, ProductionStage.QualityControl, ProductionStage.Packing];

    private readonly decimal _orderQuantity;
    private readonly IReadOnlyList<ProductionStep> _steps;

    public ProductionFlow(decimal orderQuantity, IEnumerable<ProductionStep> steps, decimal released)
    {
        _orderQuantity = orderQuantity;
        _steps = steps.ToList();
        Released = released;
    }

    /// <summary>Выпущено на склад готовой продукции.</summary>
    public decimal Released { get; }

    /// <summary>Брак на контроле качества — всего.</summary>
    public decimal Defect => _steps.Where(s => s.Kind == ProductionOperationKind.Defect).Sum(s => s.Quantity);

    /// <summary>Отправлено на переделку — всего (для отчёта).</summary>
    public decimal Rework => _steps.Where(s => s.Kind == ProductionOperationKind.Rework).Sum(s => s.Quantity);

    /// <summary>Упаковано и ещё не выпущено — столько можно выпустить.</summary>
    public decimal ReadyForOutput => Done(ProductionStage.Packing) - Released;

    /// <summary>Сделано на этапе с учётом исправлений и возвратов на переделку (на контроле качества — годное).</summary>
    public decimal Done(ProductionStage stage)
    {
        var marked = _steps.Where(s => s.Stage == stage && s.Kind is ProductionOperationKind.Done or ProductionOperationKind.Correction).Sum(s => s.Quantity);
        if (stage >= ProductionStage.QualityControl)
        {
            return marked;
        }

        // Переделка с возвратом на этап r снимает сделанное на этапах r..ВТО — изделия проходят их заново.
        return marked - _steps.Where(s => s.Kind == ProductionOperationKind.Rework && s.ReturnTo <= stage).Sum(s => s.Quantity);
    }

    /// <summary>Сколько можно отметить на этапе сейчас.</summary>
    public decimal Available(ProductionStage stage) => Math.Max(0, stage switch
    {
        ProductionStage.Knitting => _orderQuantity + Defect - Done(ProductionStage.Knitting),
        ProductionStage.QualityControl => Done(ProductionStage.Steaming) - Done(ProductionStage.QualityControl) - Defect,
        ProductionStage.Packing => Done(ProductionStage.QualityControl) - Done(ProductionStage.Packing),
        _ => Done(stage - 1) - Done(stage),
    });

    /// <summary>Сколько сделанного на этапе уже ушло дальше — ниже этого исправление не опускает.</summary>
    public decimal UsedDownstream(ProductionStage stage) => stage switch
    {
        ProductionStage.Steaming => Done(ProductionStage.QualityControl) + Defect,
        ProductionStage.QualityControl => Done(ProductionStage.Packing),
        ProductionStage.Packing => Released,
        _ => Done(stage + 1),
    };

    /// <summary>Проверка записи перед сохранением; бросает понятную ошибку.</summary>
    public void EnsureCanApply(ProductionStep step)
    {
        if (!Stages.Contains(step.Stage))
        {
            throw new BusinessRuleException("production.stage.unknown", "Выберите этап.");
        }

        var qc = step.Stage == ProductionStage.QualityControl;
        switch (step.Kind)
        {
            case ProductionOperationKind.Done or ProductionOperationKind.Rework or ProductionOperationKind.Defect:
                Quantities.EnsurePositive(step.Quantity);
                if (step.Kind != ProductionOperationKind.Done && !qc)
                {
                    throw new BusinessRuleException("production.stage.qc_only", "Переделка и брак отмечаются на контроле качества.");
                }

                if (step.Kind == ProductionOperationKind.Rework
                    && step.ReturnTo is not (ProductionStage.Knitting or ProductionStage.Assembly or ProductionStage.Steaming))
                {
                    throw new BusinessRuleException("production.stage.rework_target", "Укажите этап, на который возвращается переделка: вязание, сборка или ВТО.");
                }

                if (step.Kind != ProductionOperationKind.Rework && step.ReturnTo is not null)
                {
                    throw new BusinessRuleException("production.stage.return_not_allowed", "Этап возврата указывается только для переделки.");
                }

                var available = Available(step.Stage);
                if (step.Quantity > available)
                {
                    throw new BusinessRuleException("production.stage.exceeds",
                        $"На этапе «{StageName(step.Stage)}» можно отметить не больше {Quantities.Format(available)} — столько пришло с предыдущего этапа и ещё не отмечено.");
                }

                break;
            case ProductionOperationKind.Correction:
                if (step.Quantity >= 0)
                {
                    throw new BusinessRuleException("production.stage.correction_sign", "Исправление уменьшает сделанное: укажите, на сколько меньше.");
                }

                if (step.ReturnTo is not null)
                {
                    throw new BusinessRuleException("production.stage.return_not_allowed", "Этап возврата указывается только для переделки.");
                }

                var left = Done(step.Stage) - UsedDownstream(step.Stage);
                if (-step.Quantity > left)
                {
                    throw new BusinessRuleException("production.stage.correction_exceeds",
                        $"На этапе «{StageName(step.Stage)}» можно уменьшить не больше чем на {Quantities.Format(Math.Max(0, left))} — остальное уже ушло на следующий этап.");
                }

                break;
            default:
                throw new BusinessRuleException("production.stage.kind", "Выберите вид отметки.");
        }
    }

    public static string StageName(ProductionStage stage) => stage switch
    {
        ProductionStage.Knitting => "Вязание",
        ProductionStage.Assembly => "Сборка",
        ProductionStage.Steaming => "ВТО",
        ProductionStage.QualityControl => "Контроль качества",
        ProductionStage.Packing => "Упаковка",
        _ => stage.ToString(),
    };

    public static string KindName(ProductionOperationKind kind) => kind switch
    {
        ProductionOperationKind.Done => "Сделано",
        ProductionOperationKind.Correction => "Исправление",
        ProductionOperationKind.Rework => "На переделку",
        ProductionOperationKind.Defect => "Брак",
        _ => kind.ToString(),
    };
}
