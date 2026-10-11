using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Catalog;

/// <summary>Вид договора (D400): с кем и о чём. Числа хранятся в базе — не перенумеровывать.</summary>
public enum ContractKind : byte
{
    /// <summary>Договор с покупателем: поставка нашей продукции.</summary>
    Sale = 1,

    /// <summary>Договор с поставщиком: закупка пряжи, фурнитуры, услуг.</summary>
    Purchase = 2,

    /// <summary>Договор комиссии (маркетплейс, магазин-комиссионер).</summary>
    Commission = 3,

    /// <summary>Давальческое сырьё, подряд, аренда и прочее.</summary>
    Other = 9,
}

public enum ContractStatus : byte
{
    Active = 1,
    Closed = 2,
}

/// <summary>
/// Договор с контрагентом (D400): номер, дата, вид, срок действия, отсрочка оплаты, лимит суммы.
/// Не удаляется — закрывается; закрытый договор можно открыть снова. Скан подписанного договора — <see cref="CounterpartyContractFile"/>.
/// </summary>
public sealed class CounterpartyContract
{
    public const int NumberMaxLength = 50;
    public const int SubjectMaxLength = 300;
    public const int CommentMaxLength = 1000;
    public const int MaxPaymentDays = 365;

    private CounterpartyContract()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long CounterpartyId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly ContractDate { get; private set; }
    public ContractKind Kind { get; private set; }
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>null — бессрочный.</summary>
    public DateOnly? ValidTo { get; private set; }

    public string? Subject { get; private set; }

    /// <summary>Отсрочка оплаты в календарных днях; null — не оговорена.</summary>
    public int? PaymentDays { get; private set; }

    /// <summary>Лимит суммы договора в валюте организации; null — без лимита.</summary>
    public decimal? AmountLimit { get; private set; }

    public string? Comment { get; private set; }
    public ContractStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static string KindName(ContractKind kind) => kind switch
    {
        ContractKind.Sale => "С покупателем",
        ContractKind.Purchase => "С поставщиком",
        ContractKind.Commission => "Комиссии",
        _ => "Прочий",
    };

    public static string StatusName(ContractStatus status) => status == ContractStatus.Active ? "Действует" : "Закрыт";

    public static CounterpartyContract Create(long organizationId, long counterpartyId, ContractTerms terms, DateTime nowUtc, long userId)
    {
        var c = new CounterpartyContract
        {
            OrganizationId = organizationId,
            CounterpartyId = counterpartyId,
            Status = ContractStatus.Active,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = userId,
        };
        c.Apply(terms);
        return c;
    }

    public IReadOnlyList<FieldChange> Update(ContractTerms terms)
    {
        if (Status == ContractStatus.Closed)
        {
            throw new BusinessRuleException("contract.closed", "Договор закрыт. Сначала откройте его снова.");
        }

        var before = Snapshot();
        Apply(terms);
        var after = Snapshot();
        var changes = new List<FieldChange>();
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Value != after[i].Value)
            {
                changes.Add(new FieldChange(before[i].Field, before[i].Value, after[i].Value));
            }
        }

        return changes;
    }

    public void Close()
    {
        if (Status == ContractStatus.Closed)
        {
            throw new BusinessRuleException("contract.closed", "Договор уже закрыт.");
        }

        Status = ContractStatus.Closed;
    }

    public void Reopen()
    {
        if (Status == ContractStatus.Active)
        {
            throw new BusinessRuleException("contract.active", "Договор и так действует.");
        }

        Status = ContractStatus.Active;
    }

    /// <summary>Действует на дату: не закрыт и дата внутри срока.</summary>
    public bool IsValidOn(DateOnly date) =>
        Status == ContractStatus.Active && (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || date <= ValidTo);

    private void Apply(ContractTerms t)
    {
        if (!Enum.IsDefined(t.Kind))
        {
            throw new BusinessRuleException("contract.kind", "Выберите вид договора.");
        }

        if (t.ContractDate.Year < 1990 || t.ContractDate.Year > 2100)
        {
            throw new BusinessRuleException("contract.date", "Проверьте дату договора.");
        }

        if (t.ValidFrom is { } from && t.ValidTo is { } to && to < from)
        {
            throw new BusinessRuleException("contract.period", "Дата окончания договора раньше даты начала.");
        }

        if (t.PaymentDays is < 0 or > MaxPaymentDays)
        {
            throw new BusinessRuleException("contract.payment_days", $"Отсрочка оплаты — от 0 до {MaxPaymentDays} дней.");
        }

        if (t.AmountLimit is < 0 or > 999_999_999_999m)
        {
            throw new BusinessRuleException("contract.amount", "Лимит суммы договора не может быть отрицательным.");
        }

        Number = DomainText.Require(t.Number, NumberMaxLength, "Номер договора");
        ContractDate = t.ContractDate;
        Kind = t.Kind;
        ValidFrom = t.ValidFrom;
        ValidTo = t.ValidTo;
        Subject = DomainText.Optional(t.Subject, SubjectMaxLength, "Предмет договора");
        PaymentDays = t.PaymentDays;
        AmountLimit = t.AmountLimit is { } limit ? decimal.Round(limit, 2, MidpointRounding.AwayFromZero) : null;
        Comment = DomainText.Optional(t.Comment, CommentMaxLength, "Комментарий");
    }

    private (string Field, string? Value)[] Snapshot() =>
    [
        ("Номер", Number),
        ("Дата", ContractDate.ToString("dd.MM.yyyy")),
        ("Вид", KindName(Kind)),
        ("Действует с", ValidFrom?.ToString("dd.MM.yyyy")),
        ("Действует по", ValidTo?.ToString("dd.MM.yyyy")),
        ("Предмет", Subject),
        ("Отсрочка, дней", PaymentDays?.ToString()),
        ("Лимит суммы", AmountLimit?.ToString("0.00")),
        ("Комментарий", Comment),
    ];
}

public sealed record ContractTerms(
    string Number, DateOnly ContractDate, ContractKind Kind, DateOnly? ValidFrom, DateOnly? ValidTo, string? Subject,
    int? PaymentDays, decimal? AmountLimit, string? Comment);

/// <summary>
/// Скан договора, дополнительного соглашения или спецификации (D400). Только вставка: новая версия не затирает старую.
/// Тип проверяется по сигнатуре файла, содержимое в базе зашифровано мастер-ключом.
/// </summary>
public sealed class CounterpartyContractFile
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int FileNameMaxLength = 200;

    private CounterpartyContractFile()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ContractId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];
    public DateTime UploadedAtUtc { get; private set; }
    public long UploadedByUserId { get; private set; }

    public static CounterpartyContractFile Create(long organizationId, long contractId, string? fileName, string? contentType, byte[] content,
        DateTime nowUtc, long userId)
    {
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new BusinessRuleException("contract.file_size", "Файл договора — до 5 МБ.");
        }

        var type = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (!FileSignatures.Matches(type, content))
        {
            throw new BusinessRuleException("contract.file_type", "Файл договора — PDF, JPEG или PNG.");
        }

        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        return new CounterpartyContractFile
        {
            OrganizationId = organizationId,
            ContractId = contractId,
            FileName = name.Length is 0 ? "contract" : name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name,
            ContentType = type,
            SizeBytes = content.Length,
            Content = content,
            UploadedAtUtc = nowUtc,
            UploadedByUserId = userId,
        };
    }
}

/// <summary>Проверка типа загруженного файла по первым байтам: заголовку браузера верить нельзя (HTML со скриптом под видом PDF).</summary>
public static class FileSignatures
{
    public static readonly IReadOnlySet<string> Documents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "image/jpeg", "image/png" };

    public static bool Matches(string contentType, byte[] c) => contentType.ToLowerInvariant() switch
    {
        "application/pdf" => c.Length >= 5 && c[0] == 0x25 && c[1] == 0x50 && c[2] == 0x44 && c[3] == 0x46 && c[4] == 0x2D,
        "image/jpeg" => c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF,
        "image/png" => c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47 && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A,
        _ => false,
    };
}
