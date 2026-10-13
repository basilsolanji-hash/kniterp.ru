using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Причина возврата от покупателя (D1153): справочник организации, не удаляется — архив.</summary>
public sealed class SalesReturnReason
{
    public const int NameMaxLength = 150;
    public const int MaxReasons = 100;

    public static readonly IReadOnlyList<string> Defaults =
        ["Не подошёл размер", "Брак", "Пересорт", "Не понравился товар", "Другое"];

    private SalesReturnReason()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesReturnReason Create(long organizationId, string? name) =>
        new() { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Причина возврата") };

    public void SetArchived(bool archived) => IsArchived = archived;
}

/// <summary>Качество возвращённого товара (D1153): годен — на склад заказа; брак — в карантин (другой склад) или в списание.</summary>
public enum ReturnQuality : byte
{
    Good = 1,
    Quarantine = 2,
    WriteOff = 3,
}

/// <summary>
/// Сведения возврата от покупателя (D1153) к складскому документу возврата: причина, качество по строкам, склад карантина и причина
/// списания брака. Сам возврат приходует весь товар на склад заказа (учёт «возвращено» по заказу не меняется); при его проведении
/// брак тем же действием перемещается в карантин и/или списывается — отдельными документами перемещения и списания, ссылки на них
/// здесь. Меняется только пока возврат — черновик.
/// </summary>
public sealed class SalesReturnDetails
{
    public const int CommentMaxLength = 500;

    private readonly List<SalesReturnLine> _lines = [];

    private SalesReturnDetails()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long StockDocumentId { get; private set; }
    public long? ReasonId { get; private set; }
    public string? Comment { get; private set; }
    public long? QuarantineWarehouseId { get; private set; }
    public long? WriteOffReasonId { get; private set; }
    public long? TransferDocumentId { get; private set; }
    public long? WriteOffDocumentId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<SalesReturnLine> Lines => _lines;

    public static SalesReturnDetails Create(long organizationId, long stockDocumentId) =>
        new() { OrganizationId = organizationId, StockDocumentId = stockDocumentId };

    public void SetHeader(long? reasonId, string? comment, long? quarantineWarehouseId, long? writeOffReasonId)
    {
        EnsureOpen();
        ReasonId = reasonId;
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий к возврату");
        QuarantineWarehouseId = quarantineWarehouseId;
        WriteOffReasonId = writeOffReasonId;
    }

    /// <summary>Качество строки: всё количество строки — одного качества (годен — строка убирается из брака).</summary>
    public void SetLine(long itemId, ReturnQuality quality, decimal quantity)
    {
        EnsureOpen();
        if (!Enum.IsDefined(quality))
        {
            throw new BusinessRuleException("sales.return.quality", "Выберите качество: годен, брак в карантин или брак в списание.");
        }

        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (quality == ReturnQuality.Good || quantity == 0)
        {
            if (line is not null)
            {
                _lines.Remove(line);
            }

            return;
        }

        Quantities.EnsurePositive(quantity);
        if (line is null)
        {
            _lines.Add(new SalesReturnLine(OrganizationId, itemId, quality, quantity));
        }
        else
        {
            line.Quality = quality;
            line.Quantity = quantity;
        }
    }

    /// <summary>Перед проведением возврата: брак — не больше строки, есть склад карантина и причина списания.</summary>
    public void Validate(long returnWarehouseId, IReadOnlyDictionary<long, decimal> documentLines)
    {
        foreach (var line in _lines)
        {
            if (!documentLines.TryGetValue(line.ItemId, out var qty))
            {
                throw new BusinessRuleException("sales.return.quality_line", "Качество указано для позиции, которой нет в строках возврата.");
            }

            if (line.Quantity > qty)
            {
                throw new BusinessRuleException("sales.return.quality_exceeds",
                    $"Брака {Quantities.Format(line.Quantity)} больше, чем в строке возврата ({Quantities.Format(qty)}).");
            }
        }

        if (_lines.Any(l => l.Quality == ReturnQuality.Quarantine))
        {
            if (QuarantineWarehouseId is null)
            {
                throw new BusinessRuleException("sales.return.quarantine_required", "Укажите склад карантина для брака.");
            }

            if (QuarantineWarehouseId == returnWarehouseId)
            {
                throw new BusinessRuleException("sales.return.quarantine_same", "Склад карантина должен отличаться от склада возврата.");
            }
        }

        if (_lines.Any(l => l.Quality == ReturnQuality.WriteOff) && WriteOffReasonId is null)
        {
            throw new BusinessRuleException("sales.return.writeoff_reason", "Укажите причину списания брака.");
        }
    }

    public void AttachDocuments(long? transferDocumentId, long? writeOffDocumentId)
    {
        TransferDocumentId = transferDocumentId;
        WriteOffDocumentId = writeOffDocumentId;
    }

    public static string QualityName(ReturnQuality quality) => quality switch
    {
        ReturnQuality.Good => "Годен",
        ReturnQuality.Quarantine => "Брак — в карантин",
        ReturnQuality.WriteOff => "Брак — списать",
        _ => quality.ToString(),
    };

    private void EnsureOpen()
    {
        if (TransferDocumentId is not null || WriteOffDocumentId is not null)
        {
            throw new BusinessRuleException("sales.return.posted", "Брак по возврату уже оформлен — изменения запрещены.");
        }
    }
}

public sealed class SalesReturnLine
{
    private SalesReturnLine()
    {
    }

    internal SalesReturnLine(long organizationId, long itemId, ReturnQuality quality, decimal quantity)
    {
        OrganizationId = organizationId;
        ItemId = itemId;
        Quality = quality;
        Quantity = quantity;
    }

    public long Id { get; private set; }
    public long DetailsId { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public ReturnQuality Quality { get; internal set; }
    public decimal Quantity { get; internal set; }
}

public enum CustomerRefundStatus : byte
{
    Posted = 2,
    Cancelled = 9,
}

/// <summary>
/// Возврат денег покупателю (D1154): из аванса (не разнесённого остатка) его оплаты. Деньги уходят денежной операцией «выдача»
/// (РКО из кассы или списание со счёта — MoneyOperationId); оплата покупателя считается уменьшенной на сумму возврата —
/// в авансах, разноске и расчётах. Не удаляется — отменяется вместе с денежной операцией.
/// </summary>
public sealed class CustomerRefund
{
    public const int ReasonMaxLength = 500;

    private CustomerRefund()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public DateOnly RefundDate { get; private set; }
    public long CustomerId { get; private set; }
    public long PaymentId { get; private set; }
    public long? SalesOrderId { get; private set; }
    public long? ReturnDocumentId { get; private set; }
    public long MoneyOperationId { get; private set; }
    public decimal Amount { get; private set; }
    public CustomerRefundStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CustomerRefund Create(
        long organizationId, DateOnly date, long customerId, long paymentId, long? orderId, long? returnDocumentId, long moneyOperationId,
        decimal amount, long userId, DateTime nowUtc)
    {
        if (amount <= 0 || Money.Round(amount) != amount)
        {
            throw new BusinessRuleException("sales.refund.amount", "Сумма возврата — больше нуля, до копеек.");
        }

        return new CustomerRefund
        {
            OrganizationId = organizationId,
            RefundDate = date,
            CustomerId = customerId,
            PaymentId = paymentId,
            SalesOrderId = orderId,
            ReturnDocumentId = returnDocumentId,
            MoneyOperationId = moneyOperationId,
            Amount = amount,
            Status = CustomerRefundStatus.Posted,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    public void Cancel(long userId, string? reason, DateTime nowUtc)
    {
        if (Status != CustomerRefundStatus.Posted)
        {
            throw new BusinessRuleException("sales.refund.cancelled", "Возврат денег уже отменён.");
        }

        CancelReason = DomainText.Require(reason, ReasonMaxLength, "Причина отмены");
        Status = CustomerRefundStatus.Cancelled;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }
}
