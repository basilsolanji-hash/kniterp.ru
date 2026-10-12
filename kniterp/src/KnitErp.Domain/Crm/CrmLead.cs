using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Crm;

public enum CrmLeadStatus : byte
{
    New = 1,
    InWork = 2,
    Qualified = 3,
    Rejected = 4,
}

/// <summary>Валюта суммы CRM (D608): валюты стран, где работает knitERP, и основные валюты внешней торговли.</summary>
public static class CrmCurrency
{
    public static readonly IReadOnlyList<string> Codes =
        Countries.All.Select(c => c.CurrencyCode).Concat(["USD", "EUR", "CNY"]).Distinct().ToArray();

    public static string Ensure(string? code, string fallback)
    {
        var c = string.IsNullOrWhiteSpace(code) ? fallback : code.Trim().ToUpperInvariant();
        return Codes.Contains(c) ? c : throw new BusinessRuleException("crm.currency", "Неизвестная валюта.");
    }

    /// <summary>Сумма — не меньше нуля, до копеек, не больше триллиона.</summary>
    public static decimal? Amount(decimal? amount)
    {
        if (amount is not { } a)
        {
            return null;
        }

        return a >= 0 && a < 1_000_000_000_000m && decimal.Round(a, 2) == a
            ? a
            : throw new BusinessRuleException("crm.amount", "Сумма — не меньше нуля, до копеек.");
    }
}

/// <summary>Поля лида, которые вводит менеджер. ResponsibleUserId null — ответственный автор.</summary>
public sealed record CrmLeadValues(
    string? Title, long? SourceId = null, long? ChannelId = null, long? ResponsibleUserId = null, string? ContactName = null, string? Phone = null,
    string? Email = null, string? Messenger = null, string? CompanyName = null, string? Inn = null, string? Need = null, decimal? ExpectedAmount = null,
    string? CurrencyCode = null);

/// <summary>
/// Лид (D604) — первое, ещё не проверенное обращение: «написали в Telegram про 300 свитеров», заявка с сайта, звонок.
/// Контакт и компания до квалификации — текстом. Квалификация (одна команда в транзакции) создаёт контакт, находит или создаёт
/// контрагента и заводит сделку; лид остаётся в истории со ссылками на них и повторно не квалифицируется.
/// </summary>
public sealed class CrmLead
{
    public const string NumberPrefix = "Л";
    public const int TitleMaxLength = 200;
    public const int NameMaxLength = 200;
    public const int NeedMaxLength = 2000;
    public const int CommentMaxLength = 500;

    private CrmLead()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public long? SourceId { get; private set; }

    /// <summary>Канал продаж базы (справочник Lookup, D77).</summary>
    public long? ChannelId { get; private set; }

    public long ResponsibleUserId { get; private set; }
    public string? ContactName { get; private set; }

    /// <summary>Телефон в E.164 — по нему ищутся дубликаты.</summary>
    public string? Phone { get; private set; }

    /// <summary>Почта в нижнем регистре.</summary>
    public string? Email { get; private set; }

    public string? Messenger { get; private set; }
    public string? CompanyName { get; private set; }
    public string? Inn { get; private set; }
    public string? Need { get; private set; }
    public decimal? ExpectedAmount { get; private set; }
    public string CurrencyCode { get; private set; } = "RUB";
    public CrmLeadStatus Status { get; private set; }
    public long? RejectReasonId { get; private set; }
    public string? RejectComment { get; private set; }

    /// <summary>Лид объединён с другим (дубликат): этот закрыт отказом, история и ссылка — на основной.</summary>
    public long? MergedIntoLeadId { get; private set; }

    public long? ContactId { get; private set; }
    public long? CounterpartyId { get; private set; }
    public long? DealId { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsClosed => Status is CrmLeadStatus.Qualified or CrmLeadStatus.Rejected;

    public static CrmLead Create(long organizationId, string number, CrmLeadValues values, string currencyFallback, long userId, DateTime nowUtc)
    {
        var lead = new CrmLead
        {
            OrganizationId = organizationId,
            Number = number,
            Status = CrmLeadStatus.New,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ResponsibleUserId = values.ResponsibleUserId ?? userId,
        };
        lead.Apply(values, currencyFallback);
        return lead;
    }

    public void Update(CrmLeadValues values)
    {
        EnsureOpen();
        Apply(values, CurrencyCode);
        if (values.ResponsibleUserId is { } responsible)
        {
            ResponsibleUserId = responsible;
        }
    }

    /// <summary>«Взять в работу»: менеджер связался с клиентом.</summary>
    public void StartWork()
    {
        if (Status != CrmLeadStatus.New)
        {
            throw new BusinessRuleException("crm.lead.status", "В работу берётся только новый лид.");
        }

        Status = CrmLeadStatus.InWork;
    }

    public void Reject(long reasonId, string? comment, DateTime nowUtc)
    {
        EnsureOpen();
        Status = CrmLeadStatus.Rejected;
        RejectReasonId = reasonId;
        RejectComment = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
        ClosedAtUtc = nowUtc;
    }

    /// <summary>Вернуть отклонённый по ошибке лид в работу (не объединённый — у того история в основном лиде).</summary>
    public void Reopen()
    {
        if (Status != CrmLeadStatus.Rejected || MergedIntoLeadId is not null)
        {
            throw new BusinessRuleException("crm.lead.status", "Вернуть в работу можно только отклонённый лид.");
        }

        Status = CrmLeadStatus.InWork;
        RejectReasonId = null;
        RejectComment = null;
        ClosedAtUtc = null;
    }

    public void MarkQualified(long contactId, long counterpartyId, long dealId, DateTime nowUtc)
    {
        if (Status == CrmLeadStatus.Qualified)
        {
            throw new BusinessRuleException("crm.lead.qualified", "Лид уже квалифицирован — по нему есть сделка.");
        }

        EnsureOpen();
        Status = CrmLeadStatus.Qualified;
        ContactId = contactId;
        CounterpartyId = counterpartyId;
        DealId = dealId;
        ClosedAtUtc = nowUtc;
    }

    /// <summary>Объединение (D605): этот лид — дубликат <paramref name="target"/>; пустые поля основного дополняются отсюда.</summary>
    public void MergeInto(CrmLead target, long duplicateReasonId, DateTime nowUtc)
    {
        if (ReferenceEquals(target, this) || (Id != 0 && target.Id == Id))
        {
            throw new BusinessRuleException("crm.lead.merge_self", "Лид нельзя объединить сам с собой.");
        }

        EnsureOpen();
        target.EnsureOpen();
        target.ContactName ??= ContactName;
        target.Phone ??= Phone;
        target.Email ??= Email;
        target.Messenger ??= Messenger;
        target.CompanyName ??= CompanyName;
        target.Inn ??= Inn;
        target.SourceId ??= SourceId;
        target.ChannelId ??= ChannelId;
        target.ExpectedAmount ??= ExpectedAmount;
        if (Need is not null && target.Need != Need)
        {
            var merged = target.Need is null ? Need : $"{target.Need}\n{Need}";
            target.Need = merged.Length <= NeedMaxLength ? merged : merged[..NeedMaxLength];
        }

        Status = CrmLeadStatus.Rejected;
        RejectReasonId = duplicateReasonId;
        RejectComment = $"Объединён с {target.Number}";
        MergedIntoLeadId = target.Id;
        ClosedAtUtc = nowUtc;
    }

    public void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new BusinessRuleException("crm.lead.closed", Status == CrmLeadStatus.Qualified
                ? "Лид уже квалифицирован — работайте со сделкой."
                : "Лид закрыт отказом. Сначала верните его в работу.");
        }
    }

    public static string StatusName(CrmLeadStatus status) => status switch
    {
        CrmLeadStatus.New => "Новый",
        CrmLeadStatus.InWork => "В работе",
        CrmLeadStatus.Qualified => "Квалифицирован",
        CrmLeadStatus.Rejected => "Отказ",
        _ => status.ToString(),
    };

    private void Apply(CrmLeadValues v, string currencyFallback)
    {
        Title = DomainText.Require(v.Title, TitleMaxLength, "Название");
        SourceId = v.SourceId;
        ChannelId = v.ChannelId;
        ContactName = DomainText.Optional(v.ContactName, NameMaxLength, "Контактное лицо");
        Phone = CrmText.NormalizePhone(v.Phone);
        Email = CrmText.NormalizeEmail(v.Email);
        Messenger = CrmText.Messenger(v.Messenger);
        CompanyName = DomainText.Optional(v.CompanyName, NameMaxLength, "Компания");
        Inn = CrmText.NormalizeInn(v.Inn);
        Need = DomainText.Optional(v.Need, NeedMaxLength, "Потребность");
        ExpectedAmount = CrmCurrency.Amount(v.ExpectedAmount);
        CurrencyCode = CrmCurrency.Ensure(v.CurrencyCode, currencyFallback);
    }

    /// <summary>Что изменилось — для журнала; телефон и почта маской (D612).</summary>
    public string Describe() =>
        $"{Number} «{Title}», контакт {ContactName ?? "—"}, тел. {CrmText.MaskPhone(Phone) ?? "—"}, почта {CrmText.MaskEmail(Email) ?? "—"}, "
        + $"компания {CompanyName ?? "—"}{(Inn is null ? "" : $", ИНН {Inn}")}, сумма {(ExpectedAmount is { } a ? $"{a:0.00} {CurrencyCode}" : "—")}";
}
