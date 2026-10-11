using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Событие заказа покупателя, по которому этап может смениться сам (D110).</summary>
public enum StageTrigger : byte
{
    Confirmed = 1,
    Invoiced = 2,
    PartiallyPaid = 3,
    Paid = 4,
    PartiallyShipped = 5,
    Shipped = 6,
    ProductionStarted = 7,
    ProductionCompleted = 8,
}

/// <summary>
/// Правило автоматической смены этапа (D110): «событие → этап». Одно правило на событие в организации; этап не задан —
/// событие этап не меняет. Правила — настройка организации, как сами этапы.
/// </summary>
public sealed class SalesStageRule
{
    private SalesStageRule()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public StageTrigger Trigger { get; private set; }
    public long? StageId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesStageRule Create(long organizationId, StageTrigger trigger, long? stageId)
    {
        if (!Enum.IsDefined(trigger))
        {
            throw new BusinessRuleException("sales.stage_rule.trigger", "Неизвестное событие.");
        }

        return new SalesStageRule { OrganizationId = organizationId, Trigger = trigger, StageId = stageId };
    }

    public void SetStage(long? stageId) => StageId = stageId;
}

/// <summary>Что сейчас известно о заказе — из документов, а не из ручных отметок.</summary>
public sealed record SalesOrderFacts(bool Confirmed, bool Invoiced, decimal Total, decimal Paid, bool ShippedAny, bool ShippedAll);

/// <summary>Правило с порядком его этапа в списке этапов (чем больше, тем дальше по процессу).</summary>
public sealed record StageRuleTarget(StageTrigger Trigger, long StageId, int StageOrder);

/// <summary>
/// Автоматическая смена этапа (D110). Этап двигается только вперёд по списку этапов: поздняя оплата не вернёт заказ из «В работе»
/// в «Оплачен», а смена этапа вручную на более поздний не откатывается автоматикой. Из достигнутых событий берётся то, чей этап
/// дальше всех по списку.
/// </summary>
public static class SalesStageAutomation
{
    /// <summary>Правила новой организации — по названиям этапов по умолчанию (<see cref="SalesOrderStage.Defaults"/>).</summary>
    public static readonly IReadOnlyList<(StageTrigger Trigger, string Stage)> Defaults =
    [
        (StageTrigger.Confirmed, "В обработке"),
        (StageTrigger.Invoiced, "Ждём оплату"),
        (StageTrigger.Paid, "Оплачен — в производство"),
        (StageTrigger.ProductionStarted, "В работе"),
        (StageTrigger.ProductionCompleted, "Собран"),
        (StageTrigger.Shipped, "Отгружен"),
    ];

    public static readonly IReadOnlyList<StageTrigger> All =
    [
        StageTrigger.Confirmed, StageTrigger.Invoiced, StageTrigger.PartiallyPaid, StageTrigger.Paid,
        StageTrigger.ProductionStarted, StageTrigger.ProductionCompleted, StageTrigger.PartiallyShipped, StageTrigger.Shipped,
    ];

    public static string Name(StageTrigger trigger) => trigger switch
    {
        StageTrigger.Confirmed => "Заказ подтверждён",
        StageTrigger.Invoiced => "Выставлен счёт",
        StageTrigger.PartiallyPaid => "Оплачен частично",
        StageTrigger.Paid => "Оплачен полностью (100 %)",
        StageTrigger.PartiallyShipped => "Отгружен частично",
        StageTrigger.Shipped => "Отгружен полностью",
        StageTrigger.ProductionStarted => "Производство начато",
        StageTrigger.ProductionCompleted => "Производство завершено",
        _ => trigger.ToString(),
    };

    /// <summary>События, которые заказ уже прошёл. Полная оплата — с точностью до копейки.</summary>
    public static IReadOnlySet<StageTrigger> Reached(SalesOrderFacts f)
    {
        var set = new HashSet<StageTrigger>();
        if (f.Confirmed)
        {
            set.Add(StageTrigger.Confirmed);
        }

        if (f.Invoiced)
        {
            set.Add(StageTrigger.Invoiced);
        }

        if (f.Paid > 0)
        {
            set.Add(f.Total > 0 && f.Paid >= f.Total - 0.005m ? StageTrigger.Paid : StageTrigger.PartiallyPaid);
        }

        if (f.ShippedAll)
        {
            set.Add(StageTrigger.Shipped);
        }
        else if (f.ShippedAny)
        {
            set.Add(StageTrigger.PartiallyShipped);
        }

        return set;
    }

    /// <summary>
    /// Новый этап или null, если менять не нужно. <paramref name="currentOrder"/> — порядок текущего этапа заказа (null — этапа нет
    /// или он в архиве).
    /// </summary>
    public static StageRuleTarget? Pick(int? currentOrder, long? currentStageId, IEnumerable<StageRuleTarget> rules, IReadOnlySet<StageTrigger> reached)
    {
        var best = rules.Where(r => reached.Contains(r.Trigger)).OrderByDescending(r => r.StageOrder).ThenByDescending(r => r.Trigger).FirstOrDefault();
        if (best is null || best.StageId == currentStageId)
        {
            return null;
        }

        return currentOrder is { } cur && best.StageOrder <= cur ? null : best;
    }
}
