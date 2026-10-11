namespace KnitErp.Domain.Finance;

public enum BankLineDirection : byte
{
    /// <summary>Поступление на наш счёт — оплата покупателя.</summary>
    Incoming = 1,

    /// <summary>Списание с нашего счёта — оплата поставщику.</summary>
    Outgoing = 2,
}

/// <summary>
/// D119 (А1): строка банковской выписки, по которой уже создана оплата. Только вставка: отпечаток (SHA-256 номера, даты, суммы,
/// счетов и ИНН) уникален в организации — повторная загрузка той же выписки или пересекающегося периода не создаёт дублей.
/// </summary>
public sealed class BankStatementLine
{
    public const int PurposeMaxLength = 500;

    private BankStatementLine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public byte[] Fingerprint { get; private set; } = [];
    public long MoneyAccountId { get; private set; }
    public BankLineDirection Direction { get; private set; }
    public DateOnly DocumentDate { get; private set; }
    public string DocumentNumber { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public long CounterpartyId { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public long? CustomerPaymentId { get; private set; }
    public long? SupplierPaymentId { get; private set; }
    public long ImportedByUserId { get; private set; }
    public DateTime ImportedAtUtc { get; private set; }

    public static BankStatementLine Create(long organizationId, byte[] fingerprint, long moneyAccountId, BankLineDirection direction, DateOnly date,
        string number, decimal amount, long counterpartyId, string purpose, long paymentId, long userId, DateTime nowUtc)
    {
        if (fingerprint.Length != 32 || amount <= 0 || paymentId <= 0)
        {
            throw new ArgumentException("Неверная строка выписки.");
        }

        return new BankStatementLine
        {
            OrganizationId = organizationId, Fingerprint = fingerprint, MoneyAccountId = moneyAccountId, Direction = direction, DocumentDate = date,
            DocumentNumber = number.Length > 30 ? number[..30] : number, Amount = amount, CounterpartyId = counterpartyId,
            Purpose = purpose.Length > PurposeMaxLength ? purpose[..PurposeMaxLength] : purpose,
            CustomerPaymentId = direction == BankLineDirection.Incoming ? paymentId : null,
            SupplierPaymentId = direction == BankLineDirection.Outgoing ? paymentId : null,
            ImportedByUserId = userId, ImportedAtUtc = nowUtc,
        };
    }
}
