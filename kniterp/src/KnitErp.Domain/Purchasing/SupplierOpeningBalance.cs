using KnitErp.Domain.Common;

namespace KnitErp.Domain.Purchasing;

/// <summary>
/// D133 (по образцу D1146): вводный остаток взаиморасчётов с поставщиком на дату начала учёта — по юрлицу-покупателю. Больше нуля —
/// мы должны поставщику (как поступление этой датой), меньше нуля — поставщик должен нам (наш аванс поставщику, как оплата).
/// Один действующий остаток на поставщика и юрлицо (UNIQUE в SQL Server); не удаляется — аннулируется с причиной, исправление — новым остатком.
/// </summary>
public sealed class SupplierOpeningBalance
{
    public const int CommentMaxLength = 500;

    private SupplierOpeningBalance()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long LegalEntityId { get; private set; }
    public long SupplierId { get; private set; }
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

    public static SupplierOpeningBalance Create(
        long organizationId, long legalEntityId, long supplierId, DateOnly date, decimal amount, string? comment, long userId, DateTime nowUtc)
    {
        if (amount == 0 || Money.Round(amount) != amount || Math.Abs(amount) > 999_999_999_999m)
        {
            throw new BusinessRuleException("purchase.opening.amount",
                "Сальдо — не ноль, до копеек: наш долг поставщику со знаком плюс, наш аванс поставщику — со знаком минус.");
        }

        return new SupplierOpeningBalance
        {
            OrganizationId = organizationId, LegalEntityId = legalEntityId, SupplierId = supplierId, BalanceDate = date, Amount = amount,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"), CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    public void Cancel(string? reason, long userId, DateTime nowUtc)
    {
        if (IsCancelled)
        {
            throw new BusinessRuleException("purchase.opening.cancelled", "Остаток уже аннулирован.");
        }

        CancelReason = DomainText.Require(reason, CommentMaxLength, "Причина аннулирования");
        IsCancelled = true;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }
}
