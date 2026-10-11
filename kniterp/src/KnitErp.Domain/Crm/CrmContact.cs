using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Основание обработки персональных данных контакта (152-ФЗ, D611).</summary>
public enum CrmProcessingBasis : byte
{
    /// <summary>Договор или заявка самого клиента (ст. 6 ч. 1 п. 5 152-ФЗ).</summary>
    Contract = 1,

    /// <summary>Согласие — с датой и способом получения.</summary>
    Consent = 2,

    /// <summary>Иное — с пояснением.</summary>
    Other = 3,
}

/// <summary>Вид дополнительного канала связи.</summary>
public enum CrmChannelKind : byte
{
    Phone = 1,
    Email = 2,
    Messenger = 3,
}

/// <summary>Поля контакта. ResponsibleUserId null — ответственный автор.</summary>
public sealed record CrmContactValues(
    string? FullName, string? Position = null, long? CounterpartyId = null, string? Phone = null, string? Email = null, string? Messenger = null,
    string? Comment = null, long? ResponsibleUserId = null, DateOnly? BirthDate = null);

/// <summary>Основание обработки и согласие на рекламные рассылки (D611). MarketingConsent без даты — дата сегодня.</summary>
public sealed record CrmConsentValues(
    CrmProcessingBasis Basis, DateOnly? BasisDate, string? BasisNote, bool MarketingConsent, DateOnly? MarketingConsentDate = null);

/// <summary>
/// Контактное лицо покупателя (D606, D610): ФИО, должность, основная компания (контрагент базы), основной телефон и почта
/// плюс дополнительные каналы, мессенджер, день рождения, ответственный, архив. Компания в CRM — контрагент базы.
/// Персональные данные (D611–D613): основание обработки (по умолчанию «договор / заявка клиента»), согласие на рекламные
/// рассылки — отдельный флажок с датой, по умолчанию выключен; удаление ПДн по запросу заменяет ФИО и контакты на
/// «удалено по запросу», сделки и история остаются.
/// </summary>
public sealed class CrmContact
{
    public const int NameMaxLength = 200;
    public const int PositionMaxLength = 150;
    public const int CommentMaxLength = 1000;
    public const int BasisNoteMaxLength = 300;
    public const int ChannelLabelMaxLength = 50;
    public const int MaxChannels = 10;
    public const string ErasedName = "удалено по запросу";

    private readonly List<CrmContactChannel> _channels = [];

    private CrmContact()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string? Position { get; private set; }
    public long? CounterpartyId { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Messenger { get; private set; }
    public string? Comment { get; private set; }
    public DateOnly? BirthDate { get; private set; }
    public long ResponsibleUserId { get; private set; }
    public bool IsArchived { get; private set; }
    public CrmProcessingBasis ProcessingBasis { get; private set; } = CrmProcessingBasis.Contract;
    public DateOnly? BasisDate { get; private set; }
    public string? BasisNote { get; private set; }
    public bool MarketingConsent { get; private set; }
    public DateOnly? MarketingConsentDate { get; private set; }

    /// <summary>Ключ отписки от рассылок для ссылки в письме (внешний идентификатор — GUID).</summary>
    public Guid UnsubscribeToken { get; private set; } = Guid.NewGuid();

    public DateTime? ErasedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<CrmContactChannel> Channels => _channels;

    public bool IsErased => ErasedAtUtc is not null;

    public static CrmContact Create(long organizationId, CrmContactValues values, long userId, DateTime nowUtc)
    {
        var c = new CrmContact
        {
            OrganizationId = organizationId,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ResponsibleUserId = values.ResponsibleUserId ?? userId,
            BasisDate = DateOnly.FromDateTime(nowUtc),
        };
        c.Apply(values);
        return c;
    }

    public void Update(CrmContactValues values)
    {
        EnsureEditable();
        Apply(values);
        if (values.ResponsibleUserId is { } responsible)
        {
            ResponsibleUserId = responsible;
        }
    }

    /// <summary>Основание обработки и согласие на рассылки. Возвращает описание изменения для журнала.</summary>
    public string SetConsent(CrmConsentValues v, DateOnly today)
    {
        EnsureEditable();
        if (!Enum.IsDefined(v.Basis))
        {
            throw new BusinessRuleException("crm.consent.basis", "Неизвестное основание обработки персональных данных.");
        }

        if (v.Basis == CrmProcessingBasis.Consent && v.BasisDate is null)
        {
            throw new BusinessRuleException("crm.consent.date", "Укажите дату согласия на обработку персональных данных.");
        }

        if (v.Basis is CrmProcessingBasis.Consent or CrmProcessingBasis.Other && string.IsNullOrWhiteSpace(v.BasisNote))
        {
            throw new BusinessRuleException("crm.consent.note", v.Basis == CrmProcessingBasis.Consent
                ? "Укажите, как получено согласие (письменно, на сайте, в письме)."
                : "Поясните основание обработки персональных данных.");
        }

        var before = DescribeConsent();
        ProcessingBasis = v.Basis;
        BasisDate = v.BasisDate ?? BasisDate ?? today;
        BasisNote = DomainText.Optional(v.BasisNote, BasisNoteMaxLength, "Пояснение");
        if (v.MarketingConsent != MarketingConsent || (v.MarketingConsent && v.MarketingConsentDate is { } d && d != MarketingConsentDate))
        {
            MarketingConsent = v.MarketingConsent;
            MarketingConsentDate = v.MarketingConsent ? v.MarketingConsentDate ?? today : null;
        }

        return $"{before} → {DescribeConsent()}";
    }

    /// <summary>Отписка по ссылке из письма (D615): согласие на рассылки снимается.</summary>
    public bool Unsubscribe()
    {
        if (!MarketingConsent)
        {
            return false;
        }

        MarketingConsent = false;
        MarketingConsentDate = null;
        return true;
    }

    /// <summary>Дополнительный телефон, почта или мессенджер (D610). Основной — в полях контакта.</summary>
    public CrmContactChannel AddChannel(CrmChannelKind kind, string? value, string? label)
    {
        EnsureEditable();
        if (_channels.Count >= MaxChannels)
        {
            throw new BusinessRuleException("crm.contact.channels", $"Дополнительных телефонов и почт не больше {MaxChannels}.");
        }

        var normalized = kind switch
        {
            CrmChannelKind.Phone => CrmText.NormalizePhone(value),
            CrmChannelKind.Email => CrmText.NormalizeEmail(value),
            CrmChannelKind.Messenger => CrmText.Messenger(value),
            _ => throw new BusinessRuleException("crm.contact.channel_kind", "Неизвестный вид связи."),
        } ?? throw new BusinessRuleException("field.required", "Поле «Значение» обязательно.");
        if (_channels.Any(c => c.Kind == kind && c.Value == normalized) || (kind == CrmChannelKind.Phone && Phone == normalized)
            || (kind == CrmChannelKind.Email && Email == normalized))
        {
            throw new BusinessRuleException("crm.contact.channel_duplicate", "Такой телефон или почта у контакта уже есть.");
        }

        var channel = new CrmContactChannel(kind, normalized, DomainText.Optional(label, ChannelLabelMaxLength, "Вид"));
        _channels.Add(channel);
        return channel;
    }

    public void RemoveChannel(long channelId)
    {
        EnsureEditable();
        if (_channels.RemoveAll(c => c.Id == channelId) == 0)
        {
            throw new BusinessRuleException("crm.contact.channel_missing", "Такого телефона или почты у контакта нет.");
        }
    }

    /// <summary>Сделать дополнительный канал основным: прежний основной становится дополнительным.</summary>
    public void MakePrimary(long channelId)
    {
        EnsureEditable();
        var channel = _channels.FirstOrDefault(c => c.Id == channelId) ?? throw new BusinessRuleException("crm.contact.channel_missing", "Такого телефона или почты у контакта нет.");
        switch (channel.Kind)
        {
            case CrmChannelKind.Phone:
                (Phone, var old) = (channel.Value, Phone);
                Replace(channel, old);
                break;
            case CrmChannelKind.Email:
                (Email, var oldMail) = (channel.Value, Email);
                Replace(channel, oldMail);
                break;
            default:
                (Messenger, var oldMessenger) = (channel.Value, Messenger);
                Replace(channel, oldMessenger);
                break;
        }

        void Replace(CrmContactChannel c, string? previous)
        {
            _channels.Remove(c);
            if (previous is not null)
            {
                _channels.Add(new CrmContactChannel(c.Kind, previous, c.Label));
            }
        }
    }

    /// <summary>
    /// Удаление персональных данных по запросу (D613): ФИО, телефоны, почты, мессенджер, день рождения и комментарий
    /// заменяются; сделки, суммы и история остаются. Контакт уходит в архив, повторное удаление — ошибка.
    /// </summary>
    public void ErasePersonalData(DateTime nowUtc)
    {
        if (IsErased)
        {
            throw new BusinessRuleException("crm.contact.erased", "Персональные данные контакта уже удалены.");
        }

        FullName = $"{ErasedName} {nowUtc:dd.MM.yyyy}";
        Position = null;
        Phone = null;
        Email = null;
        Messenger = null;
        Comment = null;
        BirthDate = null;
        BasisNote = null;
        MarketingConsent = false;
        MarketingConsentDate = null;
        _channels.Clear();
        ErasedAtUtc = nowUtc;
        IsArchived = true;
    }

    public void Archive() => IsArchived = true;

    public void Restore()
    {
        if (IsErased)
        {
            throw new BusinessRuleException("crm.contact.erased", "Персональные данные контакта удалены — вернуть его нельзя.");
        }

        IsArchived = false;
    }

    public string Describe() =>
        $"{FullName}{(Position is null ? "" : $", {Position}")}, тел. {CrmText.MaskPhone(Phone) ?? "—"}, почта {CrmText.MaskEmail(Email) ?? "—"}";

    public string DescribeConsent() =>
        $"основание: {BasisName(ProcessingBasis)}{(BasisDate is { } d ? $" от {d:dd.MM.yyyy}" : "")}, рассылки: "
        + (MarketingConsent ? $"согласие от {MarketingConsentDate:dd.MM.yyyy}" : "нет согласия");

    public static string BasisName(CrmProcessingBasis basis) => basis switch
    {
        CrmProcessingBasis.Contract => "Договор / заявка клиента",
        CrmProcessingBasis.Consent => "Согласие",
        _ => "Иное",
    };

    private void EnsureEditable()
    {
        if (IsErased)
        {
            throw new BusinessRuleException("crm.contact.erased", "Персональные данные контакта удалены — менять его нельзя.");
        }

        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Контакт в архиве. Сначала верните его из архива.");
        }
    }

    private void Apply(CrmContactValues v)
    {
        FullName = DomainText.Require(v.FullName, NameMaxLength, "ФИО");
        Position = DomainText.Optional(v.Position, PositionMaxLength, "Должность");
        CounterpartyId = v.CounterpartyId;
        Phone = CrmText.NormalizePhone(v.Phone);
        Email = CrmText.NormalizeEmail(v.Email);
        Messenger = CrmText.Messenger(v.Messenger);
        Comment = DomainText.Optional(v.Comment, CommentMaxLength, "Комментарий");
        if (v.BirthDate is { } b && (b.Year < 1900 || b > DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            throw new BusinessRuleException("crm.contact.birth", "День рождения указан неверно.");
        }

        BirthDate = v.BirthDate;
    }
}

/// <summary>Дополнительный телефон, почта или мессенджер контакта (D610). Значение нормализовано — по нему ищутся дубликаты.</summary>
public sealed class CrmContactChannel
{
    private CrmContactChannel()
    {
    }

    internal CrmContactChannel(CrmChannelKind kind, string value, string? label)
    {
        Kind = kind;
        Value = value;
        Label = label;
    }

    public long Id { get; private set; }
    public long ContactId { get; private set; }
    public CrmChannelKind Kind { get; private set; }
    public string Value { get; private set; } = string.Empty;
    public string? Label { get; private set; }
}
