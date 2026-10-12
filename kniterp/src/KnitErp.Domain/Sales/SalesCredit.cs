using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>
/// Разрешение отгрузить заказ сверх кредитного лимита или при просрочке оплаты (D1111): кто, когда, причина и сумма.
/// Действует на подтверждение и отгрузки этого заказа, пока сумма заказа (и отгружено по нему с текущей отгрузкой) не больше
/// разрешённой. Debt, Limit и Overdue — снимок на момент разрешения, для истории. Не меняется и не удаляется: новое разрешение —
/// новая запись (действует последняя).
/// </summary>
public sealed class SalesCreditApproval
{
    public const int ReasonMaxLength = 500;

    private SalesCreditApproval()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long OrderId { get; private set; }

    /// <summary>Разрешённая сумма — сумма заказа с НДС на момент разрешения.</summary>
    public decimal Amount { get; private set; }

    public decimal Debt { get; private set; }
    public decimal? Limit { get; private set; }
    public decimal Overdue { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public long ApprovedByUserId { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }

    public static SalesCreditApproval Create(
        long organizationId, long orderId, decimal amount, decimal debt, decimal? limit, decimal overdue, string? reason, long userId, DateTime nowUtc)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new BusinessRuleException("sales.credit.reason", "Укажите причину, по которой отгрузка разрешена сверх лимита или при просрочке.");
        }

        if (text.Length > ReasonMaxLength)
        {
            throw new BusinessRuleException("sales.credit.reason_length", $"Причина — не длиннее {ReasonMaxLength} символов.");
        }

        if (amount <= 0)
        {
            throw new BusinessRuleException("sales.credit.amount", "В заказе нет суммы — разрешать нечего.");
        }

        return new SalesCreditApproval
        {
            OrganizationId = organizationId,
            OrderId = orderId,
            Amount = Money.Round(amount),
            Debt = Money.Round(debt),
            Limit = limit is { } l ? Money.Round(l) : null,
            Overdue = Money.Round(overdue),
            Reason = text,
            ApprovedByUserId = userId,
            ApprovedAtUtc = nowUtc,
        };
    }
}
