using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>
/// D1146 (ТЗ продаж §5): вводный остаток взаиморасчётов с покупателем на дату начала учёта — по юрлицу продавца. Больше нуля — долг
/// покупателя перед нами (как отгрузка этой датой, срок оплаты — та же дата), меньше нуля — наш долг (аванс покупателя). Один действующий
/// остаток на покупателя и юрлицо (UNIQUE в SQL Server); не удаляется — аннулируется с причиной, исправление — новым остатком.
/// </summary>
public sealed class CustomerOpeningBalance
{
    public const int CommentMaxLength = 500;

    private CustomerOpeningBalance()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long LegalEntityId { get; private set; }
    public long CustomerId { get; private set; }
    public DateOnly BalanceDate { get; private set; }
    public decimal Amount { get; private set; }
    public string? Comment { get; private set; }
    public bool IsCancelled { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CustomerOpeningBalance Create(
        long organizationId, long legalEntityId, long customerId, DateOnly date, decimal amount, string? comment, long userId, DateTime nowUtc)
    {
        if (amount == 0 || Money.Round(amount) != amount || Math.Abs(amount) > 999_999_999_999m)
        {
            throw new BusinessRuleException("sales.opening.amount", "Сальдо — не ноль, до копеек: долг покупателя со знаком плюс, его аванс — со знаком минус.");
        }

        return new CustomerOpeningBalance
        {
            OrganizationId = organizationId, LegalEntityId = legalEntityId, CustomerId = customerId, BalanceDate = date, Amount = amount,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"), CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    public void Cancel(string? reason, long userId, DateTime nowUtc)
    {
        if (IsCancelled)
        {
            throw new BusinessRuleException("sales.opening.cancelled", "Остаток уже аннулирован.");
        }

        CancelReason = DomainText.Require(reason, CommentMaxLength, "Причина аннулирования");
        IsCancelled = true;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }
}
