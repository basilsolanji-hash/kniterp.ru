using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

/// <summary>
/// Сменное задание (D92, дополнение): мастер назначает сотруднику этап производственного заказа и количество на дату — это его норма на смену.
/// Факт — отметки «Сделано» и исправления этого сотрудника по заказу и этапу за ту же дату (терминал цеха). Не удаляется: отменяется.
/// Если сделано меньше плана, сотрудник указывает причину (из того же списка, что на терминале) и комментарий.
/// </summary>
public sealed class ShiftTask
{
    public const int CommentMaxLength = 300;
    public const int ReasonMaxLength = 100;

    private ShiftTask()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public DateOnly Date { get; private set; }
    public long EmployeeId { get; private set; }
    public long ProductionOrderId { get; private set; }
    public ProductionStage Stage { get; private set; }
    public decimal PlannedQuantity { get; private set; }
    public string? Comment { get; private set; }
    public string? ShortfallReason { get; private set; }
    public string? ShortfallComment { get; private set; }
    public DateTime? ShortfallAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsCancelled => CancelledAtUtc is not null;

    public static ShiftTask Create(
        long organizationId, DateOnly date, long employeeId, long orderId, ProductionStage stage, decimal planned, string? comment,
        long userId, DateTime nowUtc)
    {
        if (!ProductionFlow.Stages.Contains(stage))
        {
            throw new BusinessRuleException("production.stage.unknown", "Выберите этап.");
        }

        if (planned <= 0)
        {
            throw new BusinessRuleException("production.shift.planned", "Количество по заданию должно быть больше нуля.");
        }

        return new ShiftTask
        {
            OrganizationId = organizationId,
            Date = date,
            EmployeeId = employeeId,
            ProductionOrderId = orderId,
            Stage = stage,
            PlannedQuantity = planned,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Причина невыполнения от сотрудника; можно уточнить, пока задание не отменено.</summary>
    public void ReportShortfall(string reason, string? comment, DateTime nowUtc)
    {
        EnsureActive();
        if (!ShopFloorShortfall.Reasons.Contains(reason))
        {
            throw new BusinessRuleException("production.shop.reason", "Выберите причину из списка.");
        }

        ShortfallReason = reason;
        ShortfallComment = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
        ShortfallAtUtc = nowUtc;
    }

    public void Cancel(long userId, DateTime nowUtc)
    {
        EnsureActive();
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }

    /// <summary>Процент выполнения, округлённый до целого; больше 100 — перевыполнение.</summary>
    public static int Percent(decimal planned, decimal fact) =>
        planned <= 0 ? 0 : (int)Math.Round(Math.Max(fact, 0) / planned * 100m, MidpointRounding.AwayFromZero);

    private void EnsureActive()
    {
        if (IsCancelled)
        {
            throw new BusinessRuleException("production.shift.cancelled", "Задание отменено.");
        }
    }
}
