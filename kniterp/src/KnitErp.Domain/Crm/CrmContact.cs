using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Поля контакта. ResponsibleUserId null — ответственный автор.</summary>
public sealed record CrmContactValues(
    string? FullName, string? Position = null, long? CounterpartyId = null, string? Phone = null, string? Email = null, string? Messenger = null,
    string? Comment = null, long? ResponsibleUserId = null);

/// <summary>
/// Контактное лицо покупателя (D606): ФИО, должность, основная компания (контрагент базы), телефон, почта, мессенджер.
/// Компания в CRM — это контрагент; своей «компании» CRM не заводит. Не удаляется — архивируется.
/// Этап 1 хранит один телефон и одну почту; несколько, согласия 152-ФЗ и рассылок — этап 2 (D610–D619).
/// </summary>
public sealed class CrmContact
{
    public const int NameMaxLength = 200;
    public const int PositionMaxLength = 150;
    public const int CommentMaxLength = 1000;

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
    public long ResponsibleUserId { get; private set; }
    public bool IsArchived { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmContact Create(long organizationId, CrmContactValues values, long userId, DateTime nowUtc)
    {
        var c = new CrmContact
        {
            OrganizationId = organizationId,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ResponsibleUserId = values.ResponsibleUserId ?? userId,
        };
        c.Apply(values);
        return c;
    }

    public void Update(CrmContactValues values)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Контакт в архиве. Сначала верните его из архива.");
        }

        Apply(values);
        if (values.ResponsibleUserId is { } responsible)
        {
            ResponsibleUserId = responsible;
        }
    }

    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    public string Describe() =>
        $"{FullName}{(Position is null ? "" : $", {Position}")}, тел. {CrmText.MaskPhone(Phone) ?? "—"}, почта {CrmText.MaskEmail(Email) ?? "—"}";

    private void Apply(CrmContactValues v)
    {
        FullName = DomainText.Require(v.FullName, NameMaxLength, "ФИО");
        Position = DomainText.Optional(v.Position, PositionMaxLength, "Должность");
        CounterpartyId = v.CounterpartyId;
        Phone = CrmText.NormalizePhone(v.Phone);
        Email = CrmText.NormalizeEmail(v.Email);
        Messenger = CrmText.Messenger(v.Messenger);
        Comment = DomainText.Optional(v.Comment, CommentMaxLength, "Комментарий");
    }
}
