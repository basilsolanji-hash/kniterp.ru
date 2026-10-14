using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Вид активности. Значение хранится в базе — не перенумеровывать.</summary>
public enum CrmActivityKind : byte
{
    Call = 1,
    Meeting = 2,
    Email = 3,
    Task = 4,
    SamplesSent = 5,
    OfferSent = 6,
    Note = 7,

    /// <summary>Повторное обращение клиента (заявка из API с тем же телефоном или почтой, D641).</summary>
    RepeatRequest = 8,

    /// <summary>Сообщение в мессенджере (WhatsApp, Telegram, MAX — D644).</summary>
    Message = 9,

    // D651 (требование владельца 13.10, п. 2): типовые виды работы с клиентом фабрики — для ручного ввода, без особой логики.
    VideoCall = 10,
    ClientVisit = 11,
    FactoryVisit = 12,
    Presentation = 13,
    SampleApproval = 14,
    Negotiation = 15,
    Exhibition = 16,
}

/// <summary>К чему относится активность.</summary>
public enum CrmTarget : byte
{
    Lead = 1,
    Deal = 2,
    Contact = 3,
}

/// <summary>Откуда пришла активность: введена вручную, правилом автоматизации, из API, телефонии, письмом с сервера.</summary>
public enum CrmActivitySource : byte
{
    Manual = 1,
    Automation = 2,
    Api = 3,
    Telephony = 4,
    Mail = 5,

    /// <summary>Мессенджер: WhatsApp, Telegram, MAX (D644).</summary>
    Messenger = 6,
}

/// <summary>
/// Активность CRM (D620): звонок, встреча, письмо, задача, образцы, КП, заметка, повторное обращение — на лиде, сделке или
/// контакте. Срок и исполнитель — у плановых; «выполнено» с датой и результатом. Выполненную нельзя тихо «отменить»:
/// возврат в работу пишется в журнал с причиной, а дата выполнения остаётся в журнале. Готова принять события почты и
/// телефонии (Source, ExternalRef — внешний id звонка или письма).
/// </summary>
public sealed class CrmActivity
{
    public const int SubjectMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int ResultMaxLength = 1000;
    public const int ExternalRefMaxLength = 100;

    private CrmActivity()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public CrmActivityKind Kind { get; private set; }
    public CrmTarget Target { get; private set; }
    public long TargetId { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string? Text { get; private set; }
    public DateTime? DueAtUtc { get; private set; }
    public long AssigneeUserId { get; private set; }
    public string? Result { get; private set; }
    public bool IsDone { get; private set; }
    public DateTime? DoneAtUtc { get; private set; }
    public long? DoneByUserId { get; private set; }
    public CrmActivitySource Source { get; private set; }
    public string? ExternalRef { get; private set; }

    /// <summary>Собеседник в мессенджере (D644): id пользователя MAX, Telegram, WhatsApp — кому отвечать из ленты.</summary>
    public string? Peer { get; private set; }

    /// <summary>КП (D624): номер и сумма отправленного предложения.</summary>
    public long? OfferId { get; private set; }

    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsOverdue(DateTime nowUtc) => !IsDone && DueAtUtc is { } due && due < nowUtc;

    /// <summary>Новая активность. done — сразу выполненная (звонок состоялся, письмо отправлено).</summary>
    public static CrmActivity Create(
        long organizationId, CrmActivityKind kind, CrmTarget target, long targetId, string? subject, string? text, DateTime? dueAtUtc, long assigneeUserId,
        long userId, DateTime nowUtc, bool done = false, string? result = null, CrmActivitySource source = CrmActivitySource.Manual, string? externalRef = null,
        long? offerId = null)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(target) || !Enum.IsDefined(source))
        {
            throw new BusinessRuleException("crm.activity.kind", "Неизвестный вид активности.");
        }

        var a = new CrmActivity
        {
            OrganizationId = organizationId,
            Kind = kind,
            Target = target,
            TargetId = targetId,
            AssigneeUserId = assigneeUserId,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            Source = source,
            ExternalRef = DomainText.Optional(externalRef, ExternalRefMaxLength, "Внешний номер"),
            OfferId = offerId,
        };
        a.Set(subject ?? KindName(kind), text, dueAtUtc);
        if (done)
        {
            a.Complete(result, userId, nowUtc);
        }

        return a;
    }

    public void SetPeer(string? peer) => Peer = DomainText.Optional(peer, ExternalRefMaxLength, "Собеседник");

    /// <summary>
    /// Удаление ПДн по запросу (D677, 152-ФЗ ст. 21): переписка с клиентом (сообщения мессенджеров и письма) обезличивается — текст, тема
    /// и id собеседника заменяются; факт обращения, дата и связь с карточкой остаются.
    /// </summary>
    public void ErasePersonalData()
    {
        Subject = $"{KindName(Kind)} ({CrmContact.ErasedName})";
        Text = CrmContact.ErasedName;
        Peer = null;
        Result = Result is null ? null : CrmContact.ErasedName;
    }

    public void Update(string? subject, string? text, DateTime? dueAtUtc, long assigneeUserId)
    {
        if (IsDone)
        {
            throw new BusinessRuleException("crm.activity.done", "Активность выполнена — менять её нельзя. Верните в работу с причиной.");
        }

        Set(subject, text, dueAtUtc);
        AssigneeUserId = assigneeUserId;
    }

    public void Complete(string? result, long userId, DateTime nowUtc)
    {
        if (IsDone)
        {
            throw new BusinessRuleException("crm.activity.done", "Активность уже выполнена.");
        }

        Result = DomainText.Optional(result, ResultMaxLength, "Результат");
        IsDone = true;
        DoneAtUtc = nowUtc;
        DoneByUserId = userId;
    }

    /// <summary>Вернуть выполненную в работу — только с причиной; прежняя отметка остаётся в журнале аудита.</summary>
    public string Reopen(string? reason)
    {
        if (!IsDone)
        {
            throw new BusinessRuleException("crm.activity.not_done", "Активность и так в работе.");
        }

        var why = DomainText.Require(reason, ResultMaxLength, "Причина");
        var was = $"выполнено {DoneAtUtc:dd.MM.yyyy HH:mm}{(Result is null ? "" : $", результат: {Result}")}";
        IsDone = false;
        DoneAtUtc = null;
        DoneByUserId = null;
        Result = null;
        return $"{was}; причина возврата: {why}";
    }

    public static string KindName(CrmActivityKind kind) => kind switch
    {
        CrmActivityKind.Call => "Звонок",
        CrmActivityKind.Meeting => "Встреча",
        CrmActivityKind.Email => "Письмо",
        CrmActivityKind.Task => "Задача",
        CrmActivityKind.SamplesSent => "Образцы отправлены",
        CrmActivityKind.OfferSent => "КП отправлено",
        CrmActivityKind.Note => "Заметка",
        CrmActivityKind.RepeatRequest => "Повторное обращение",
        CrmActivityKind.Message => "Сообщение",
        CrmActivityKind.VideoCall => "Видеозвонок",
        CrmActivityKind.ClientVisit => "Визит к клиенту",
        CrmActivityKind.FactoryVisit => "Визит клиента на фабрику",
        CrmActivityKind.Presentation => "Показ коллекции",
        CrmActivityKind.SampleApproval => "Согласование образца",
        CrmActivityKind.Negotiation => "Переговоры по условиям",
        CrmActivityKind.Exhibition => "Встреча на выставке",
        _ => kind.ToString(),
    };

    private void Set(string? subject, string? text, DateTime? dueAtUtc)
    {
        Subject = DomainText.Require(subject, SubjectMaxLength, "Тема");
        Text = DomainText.Optional(text, TextMaxLength, "Текст");
        DueAtUtc = dueAtUtc;
    }
}

/// <summary>
/// Коммерческое предложение (D624): номер КП-000001, дата, срок действия, условия, вид цен и снимок позиций на момент
/// составления — чтобы распечатанное и отправленное КП не менялось вслед за сделкой. Не меняется после создания.
/// </summary>
public sealed class CrmOffer
{
    public const string NumberPrefix = "КП";
    public const int TermsMaxLength = 2000;
    public const int MaxLines = CrmDeal.MaxLines;

    private readonly List<CrmOfferLine> _lines = [];

    private CrmOffer()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public long DealId { get; private set; }
    public DateOnly OfferDate { get; private set; }
    public DateOnly ValidUntil { get; private set; }
    public string? Terms { get; private set; }
    public long? PriceTypeId { get; private set; }
    public string CurrencyCode { get; private set; } = "RUB";
    public decimal Amount { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public IReadOnlyList<CrmOfferLine> Lines => _lines;

    public static CrmOffer Create(
        long organizationId, string number, long dealId, DateOnly date, DateOnly validUntil, string? terms, long? priceTypeId, string currency,
        IEnumerable<(long ItemId, string Code, string Name, string Unit, decimal Quantity, decimal Price)> lines, long userId, DateTime nowUtc)
    {
        if (validUntil < date)
        {
            throw new BusinessRuleException("crm.offer.valid_until", "Срок действия КП — не раньше его даты.");
        }

        var offer = new CrmOffer
        {
            OrganizationId = organizationId, Number = number, DealId = dealId, OfferDate = date, ValidUntil = validUntil, PriceTypeId = priceTypeId,
            CurrencyCode = currency, CreatedByUserId = userId, CreatedAtUtc = nowUtc,
            Terms = DomainText.Optional(terms, TermsMaxLength, "Условия"),
        };
        var order = 0;
        foreach (var l in lines)
        {
            Quantities.EnsurePositive(l.Quantity);
            offer._lines.Add(new CrmOfferLine(organizationId, ++order, l.ItemId, l.Code, l.Name, l.Unit, l.Quantity, l.Price));
        }

        if (offer._lines.Count == 0)
        {
            throw new BusinessRuleException("crm.offer.empty", "В КП нет позиций: добавьте позиции каталога в сделку.");
        }

        offer.Amount = offer._lines.Sum(l => l.Amount);
        return offer;
    }
}

public sealed class CrmOfferLine
{
    private CrmOfferLine()
    {
    }

    internal CrmOfferLine(long organizationId, int lineNo, long itemId, string code, string name, string unit, decimal quantity, decimal price)
    {
        OrganizationId = organizationId;
        LineNo = lineNo;
        ItemId = itemId;
        Code = code;
        Name = name;
        UnitSymbol = unit;
        Quantity = quantity;
        Price = price;
        Amount = Money.Round(quantity * price);
    }

    public long Id { get; private set; }

    /// <summary>Организация — для составных внешних ключей на КП и номенклатуру (D682).</summary>
    public long OrganizationId { get; private set; }

    public long OfferId { get; private set; }
    public int LineNo { get; private set; }
    public long ItemId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string UnitSymbol { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal Price { get; private set; }
    public decimal Amount { get; private set; }
}
