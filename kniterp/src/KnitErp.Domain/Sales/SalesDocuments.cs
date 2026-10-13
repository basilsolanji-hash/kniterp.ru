using System.Globalization;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Изображение для печатных форм продаж (D1120): логотип, печать, факсимиле подписей руководителя и главного бухгалтера.</summary>
public enum SalesDocumentImageKind : byte
{
    Logo = 1,
    Stamp = 2,
    DirectorSignature = 3,
    AccountantSignature = 4,
}

/// <summary>
/// Изображение своего юрлица для печатных форм (D1120). Тип — PNG или JPEG по сигнатуре файла (заголовку браузера не верим), до 1 МБ.
/// Содержимое хранится зашифрованным мастер-ключом (печать и подпись — инструмент подделки документов). Одно изображение каждого вида
/// на юрлицо: загрузка заменяет прежнее, удаление — настройка, а не документ, поэтому удаляется физически (с записью в журнал).
/// </summary>
public sealed class SalesDocumentImage
{
    public const int MaxBytes = 1_048_576;

    private SalesDocumentImage()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long LegalEntityId { get; private set; }
    public SalesDocumentImageKind Kind { get; private set; }
    public string ContentType { get; private set; } = string.Empty;
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];
    public DateTime UploadedAtUtc { get; private set; }
    public long UploadedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesDocumentImage Create(long organizationId, long legalEntityId, SalesDocumentImageKind kind, string? contentType, byte[] content,
        DateTime nowUtc, long userId)
    {
        var image = new SalesDocumentImage { OrganizationId = organizationId, LegalEntityId = legalEntityId, Kind = kind };
        image.Replace(contentType, content, nowUtc, userId);
        return image;
    }

    public void Replace(string? contentType, byte[] content, DateTime nowUtc, long userId)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new BusinessRuleException("sales.document_image.kind", "Неизвестный вид изображения.");
        }

        if (content is null || content.Length == 0)
        {
            throw new BusinessRuleException("sales.document_image.empty", "Файл пустой.");
        }

        if (content.Length > MaxBytes)
        {
            throw new BusinessRuleException("sales.document_image.size", "Изображение — не больше 1 МБ.");
        }

        var type = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (type is not ("image/png" or "image/jpeg") || !FileSignatures.Matches(type, content))
        {
            throw new BusinessRuleException("sales.document_image.type", "Изображение — PNG или JPEG.");
        }

        ContentType = type;
        SizeBytes = content.Length;
        Content = content;
        UploadedAtUtc = nowUtc;
        UploadedByUserId = userId;
    }

    public static string KindName(SalesDocumentImageKind kind) => kind switch
    {
        SalesDocumentImageKind.Logo => "Логотип",
        SalesDocumentImageKind.Stamp => "Печать",
        SalesDocumentImageKind.DirectorSignature => "Подпись руководителя",
        SalesDocumentImageKind.AccountantSignature => "Подпись главного бухгалтера",
        _ => "Изображение",
    };

    public static readonly IReadOnlyList<SalesDocumentImageKind> Kinds =
        [SalesDocumentImageKind.Logo, SalesDocumentImageKind.Stamp, SalesDocumentImageKind.DirectorSignature, SalesDocumentImageKind.AccountantSignature];
}

/// <summary>Что выводить в печатных формах юрлица (D1120). Без записи — всё включено: изображение выводится, только если загружено.</summary>
public sealed record SalesDocumentFlags(
    bool LogoOnInvoice = true, bool LogoOnOrder = true, bool StampOnInvoice = true, bool StampOnUpd = true, bool StampOnOrder = true,
    bool QrOnInvoice = true)
{
    public static readonly SalesDocumentFlags Default = new();

    public string Describe() =>
        string.Join(", ", new[]
        {
            LogoOnInvoice ? "логотип в счёте" : null, LogoOnOrder ? "логотип в заказе" : null, StampOnInvoice ? "печать в счёте" : null,
            StampOnUpd ? "печать в УПД" : null, StampOnOrder ? "печать в заказе" : null, QrOnInvoice ? "QR в счёте" : null,
        }.Where(x => x is not null)) is { Length: > 0 } s ? s : "ничего";
}

/// <summary>Настройки оформления документов своего юрлица (D1120): одна запись на юрлицо.</summary>
public sealed class SalesDocumentSettings
{
    private SalesDocumentSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long LegalEntityId { get; private set; }
    public bool LogoOnInvoice { get; private set; } = true;
    public bool LogoOnOrder { get; private set; } = true;
    public bool StampOnInvoice { get; private set; } = true;
    public bool StampOnUpd { get; private set; } = true;
    public bool StampOnOrder { get; private set; } = true;
    public bool QrOnInvoice { get; private set; } = true;
    public byte[] RowVersion { get; private set; } = [];

    public SalesDocumentFlags Flags => new(LogoOnInvoice, LogoOnOrder, StampOnInvoice, StampOnUpd, StampOnOrder, QrOnInvoice);

    public static SalesDocumentSettings Create(long organizationId, long legalEntityId) =>
        new() { OrganizationId = organizationId, LegalEntityId = legalEntityId };

    public void Set(SalesDocumentFlags f) =>
        (LogoOnInvoice, LogoOnOrder, StampOnInvoice, StampOnUpd, StampOnOrder, QrOnInvoice) =
        (f.LogoOnInvoice, f.LogoOnOrder, f.StampOnInvoice, f.StampOnUpd, f.StampOnOrder, f.QrOnInvoice);
}

/// <summary>Повод письма клиенту (D1122, D1124): подтверждение заказа, счёт, УПД по отгрузке, смена этапа заказа.</summary>
public enum SalesMailKind : byte
{
    Order = 1,
    Invoice = 2,
    Upd = 3,
    Stage = 4,

    /// <summary>D148: напоминание об оплате счёта до срока.</summary>
    PaymentReminder = 5,

    /// <summary>D148: напоминание о просроченной оплате счёта.</summary>
    PaymentOverdue = 6,
}

public enum SalesMailStatus : byte
{
    Queued = 1,
    Sent = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>Документ во вложении письма — PDF формируется при отправке.</summary>
public enum SalesMailDocument : byte
{
    Order = 1,
    Invoice = 2,
    Upd = 3,
}

/// <summary>
/// Шаблон письма клиенту (D1124): тема и текст с подстановками {Номер}, {Этап}, {Дата}, {Покупатель}, {Сумма}, {Организация};
/// AutoSend — отправлять автоматически по событию. Нет записи — шаблон по умолчанию, автоотправка выключена.
/// </summary>
public sealed class SalesMailTemplate
{
    public const int SubjectMaxLength = 300;
    public const int BodyMaxLength = 4000;

    private SalesMailTemplate()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public SalesMailKind Kind { get; private set; }
    public bool AutoSend { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public byte[] RowVersion { get; private set; } = [];

    public static SalesMailTemplate Create(long organizationId, SalesMailKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("sales.mail.kind", "Неизвестный вид письма.");
        }

        var (subject, body) = SalesMailText.Default(kind);
        return new SalesMailTemplate { OrganizationId = organizationId, Kind = kind, Subject = subject, Body = body };
    }

    public void Set(bool autoSend, string? subject, string? body)
    {
        var s = (subject ?? string.Empty).Trim();
        var b = (body ?? string.Empty).Trim();
        if (s.Length == 0 || b.Length == 0)
        {
            throw new BusinessRuleException("sales.mail.template_empty", "Укажите тему и текст письма.");
        }

        if (s.Length > SubjectMaxLength || b.Length > BodyMaxLength)
        {
            throw new BusinessRuleException("sales.mail.template_length", $"Тема — до {SubjectMaxLength} символов, текст — до {BodyMaxLength}.");
        }

        if (s.Contains('\n') || s.Contains('\r'))
        {
            throw new BusinessRuleException("sales.mail.subject_line", "Тема письма — одной строкой.");
        }

        AutoSend = autoSend;
        Subject = s;
        Body = b;
    }
}

/// <summary>Этап заказа, при переходе на который клиенту уходит письмо о статусе (D1124).</summary>
public sealed class SalesMailStage
{
    private SalesMailStage()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long StageId { get; private set; }

    public static SalesMailStage Create(long organizationId, long stageId) => new() { OrganizationId = organizationId, StageId = stageId };
}

/// <summary>Почта покупателя для документов (D1124): адрес (до 5 через запятую) и «не отправлять автоматически».</summary>
public sealed class CustomerMailSettings
{
    private CustomerMailSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long CounterpartyId { get; private set; }
    public string? Email { get; private set; }
    public bool NoAutoSend { get; private set; }

    /// <summary>D148: покупатель отписался от напоминаний об оплате (ссылкой из письма или менеджер по его просьбе).</summary>
    public bool NoPaymentReminders { get; private set; }

    /// <summary>D148: ключ ссылки «отписаться от напоминаний» в письме — случайный, по нему нельзя узнать покупателя.</summary>
    public Guid ReminderToken { get; private set; } = Guid.NewGuid();

    public byte[] RowVersion { get; private set; } = [];

    public static CustomerMailSettings Create(long organizationId, long counterpartyId) =>
        new() { OrganizationId = organizationId, CounterpartyId = counterpartyId, ReminderToken = Guid.NewGuid() };

    public void SetPaymentReminders(bool allowed) => NoPaymentReminders = !allowed;

    public void Set(string? email, bool noAutoSend)
    {
        Email = SalesMailText.NormalizeRecipients(email);
        NoAutoSend = noAutoSend;
    }
}

/// <summary>
/// Письмо клиенту в очереди (outbox, D1123): ставится в той же транзакции, что и событие, отправляется фоновым отправителем с повторами.
/// DedupKey — у автоматических писем: одно событие даёт одно письмо. Письма не удаляются: журнал виден в карточке заказа.
/// </summary>
public sealed class SalesMailMessage
{
    public const int RecipientsMaxLength = 600;
    public const int ErrorMaxLength = 500;
    public const int MaxAttempts = 5;

    private readonly List<SalesMailAttachment> _attachments = [];

    private SalesMailMessage()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public SalesMailKind Kind { get; private set; }
    public long SalesOrderId { get; private set; }
    public long CounterpartyId { get; private set; }
    public string Recipients { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public bool IsAuto { get; private set; }
    public string? DedupKey { get; private set; }
    public SalesMailStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<SalesMailAttachment> Attachments => _attachments;

    public static SalesMailMessage Create(long organizationId, SalesMailKind kind, long salesOrderId, long counterpartyId, string? recipients,
        string? subject, string? body, bool isAuto, string? dedupKey, long userId, DateTime nowUtc,
        IEnumerable<(SalesMailDocument Document, long DocumentId)> attachments)
    {
        var to = SalesMailText.NormalizeRecipients(recipients)
                 ?? throw new BusinessRuleException("sales.mail.no_recipient", "Укажите почту получателя.");
        var s = (subject ?? string.Empty).ReplaceLineEndings(" ").Trim();
        var b = (body ?? string.Empty).Trim();
        if (s.Length == 0 || b.Length == 0)
        {
            throw new BusinessRuleException("sales.mail.template_empty", "Укажите тему и текст письма.");
        }

        if (s.Length > SalesMailTemplate.SubjectMaxLength || b.Length > SalesMailTemplate.BodyMaxLength * 2)
        {
            throw new BusinessRuleException("sales.mail.template_length",
                $"Тема — до {SalesMailTemplate.SubjectMaxLength} символов, текст — до {SalesMailTemplate.BodyMaxLength}.");
        }

        var message = new SalesMailMessage
        {
            OrganizationId = organizationId, Kind = kind, SalesOrderId = salesOrderId, CounterpartyId = counterpartyId, Recipients = to,
            Subject = s, Body = b, IsAuto = isAuto, DedupKey = dedupKey, Status = SalesMailStatus.Queued, NextAttemptAtUtc = nowUtc,
            CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
        foreach (var (document, id) in attachments.Distinct())
        {
            message._attachments.Add(new SalesMailAttachment(organizationId, document, id));
        }

        return message;
    }

    public void MarkSent(DateTime nowUtc)
    {
        Status = SalesMailStatus.Sent;
        Attempts++;
        SentAtUtc = nowUtc;
        LastError = null;
    }

    /// <summary>Неудачная попытка: повтор с паузой (1, 5, 30 минут, 2 часа); после пятой или при permanent — «не отправлено».</summary>
    public void MarkFailed(string error, DateTime nowUtc, bool permanent)
    {
        Attempts++;
        LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        if (permanent || Attempts >= MaxAttempts)
        {
            Status = SalesMailStatus.Failed;
            return;
        }

        NextAttemptAtUtc = nowUtc + Attempts switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2),
        };
    }

    public void Retry(DateTime nowUtc)
    {
        if (Status != SalesMailStatus.Failed)
        {
            throw new BusinessRuleException("sales.mail.retry", "Повторить можно только неотправленное письмо.");
        }

        Status = SalesMailStatus.Queued;
        Attempts = 0;
        NextAttemptAtUtc = nowUtc;
    }

    public void Cancel()
    {
        if (Status is not (SalesMailStatus.Queued or SalesMailStatus.Failed))
        {
            throw new BusinessRuleException("sales.mail.cancel", "Отменить можно только письмо, которое ещё не отправлено.");
        }

        Status = SalesMailStatus.Cancelled;
    }

    public static string StatusName(SalesMailStatus status) => status switch
    {
        SalesMailStatus.Queued => "В очереди",
        SalesMailStatus.Sent => "Отправлено",
        SalesMailStatus.Failed => "Не отправлено",
        SalesMailStatus.Cancelled => "Отменено",
        _ => "—",
    };

    public static string KindName(SalesMailKind kind) => kind switch
    {
        SalesMailKind.Order => "Заказ",
        SalesMailKind.Invoice => "Счёт",
        SalesMailKind.Upd => "УПД",
        SalesMailKind.Stage => "Статус заказа",
        SalesMailKind.PaymentReminder => "Напоминание об оплате",
        SalesMailKind.PaymentOverdue => "Просрочка оплаты",
        _ => "Письмо",
    };

    public static string DocumentName(SalesMailDocument document) => document switch
    {
        SalesMailDocument.Order => "Заказ",
        SalesMailDocument.Invoice => "Счёт",
        SalesMailDocument.Upd => "УПД",
        _ => "Документ",
    };
}

public sealed class SalesMailAttachment
{
    private SalesMailAttachment()
    {
    }

    internal SalesMailAttachment(long organizationId, SalesMailDocument document, long documentId)
    {
        OrganizationId = organizationId;
        Document = document;
        DocumentId = documentId;
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long MessageId { get; private set; }
    public SalesMailDocument Document { get; private set; }
    public long DocumentId { get; private set; }
}

/// <summary>Подстановки шаблонов и проверка адресов (D1124).</summary>
public static class SalesMailText
{
    public const int MaxRecipients = 5;

    public static readonly IReadOnlyList<string> Placeholders = ["{Номер}", "{Дата}", "{Покупатель}", "{Сумма}", "{Этап}", "{Организация}", "{Срок}"];

    public static (string Subject, string Body) Default(SalesMailKind kind) => kind switch
    {
        SalesMailKind.Order => ("Заказ {Номер} от {Дата}",
            "Здравствуйте!\n\nВаш заказ {Номер} от {Дата} на сумму {Сумма} оформлен. Заказ — во вложении.\n\nС уважением,\n{Организация}"),
        SalesMailKind.Invoice => ("Счёт на оплату {Номер} от {Дата}",
            "Здравствуйте!\n\nНаправляем счёт на оплату {Номер} от {Дата} на сумму {Сумма}. Счёт — во вложении.\n\nС уважением,\n{Организация}"),
        SalesMailKind.Upd => ("УПД {Номер} от {Дата}",
            "Здравствуйте!\n\nТовар отгружен. УПД {Номер} от {Дата} на сумму {Сумма} — во вложении.\n\nС уважением,\n{Организация}"),
        SalesMailKind.PaymentReminder => ("Напоминание об оплате счёта {Номер} от {Дата}",
            "Здравствуйте!\n\nНапоминаем: срок оплаты счёта {Номер} от {Дата} — {Срок}, к оплате {Сумма}. Счёт — во вложении. Если вы уже оплатили, не обращайте внимания на это письмо.\n\nС уважением,\n{Организация}"),
        SalesMailKind.PaymentOverdue => ("Просрочена оплата счёта {Номер} от {Дата}",
            "Здравствуйте!\n\nСрок оплаты счёта {Номер} от {Дата} — {Срок} — прошёл, к оплате {Сумма}. Счёт — во вложении. Пожалуйста, оплатите его или сообщите, если оплата уже в пути.\n\nС уважением,\n{Организация}"),
        _ => ("Заказ {Номер}: {Этап}",
            "Здравствуйте!\n\nСтатус вашего заказа {Номер} от {Дата}: {Этап}.\n\nС уважением,\n{Организация}"),
    };

    public static string Render(string template, string number, DateOnly date, string customer, decimal? amount, string currency, string? stage,
        string organization) =>
        template
            .Replace("{Номер}", number, StringComparison.Ordinal)
            .Replace("{Дата}", date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Покупатель}", customer, StringComparison.Ordinal)
            .Replace("{Сумма}", amount is { } a ? a.ToString("#,0.00", CultureInfo.GetCultureInfo("ru-RU")) + " " + currency : "—", StringComparison.Ordinal)
            .Replace("{Этап}", stage ?? "—", StringComparison.Ordinal)
            .Replace("{Организация}", organization, StringComparison.Ordinal);

    /// <summary>D148: срок оплаты в напоминании («{Срок}»); в других письмах — прочерк.</summary>
    public static string RenderDue(string text, DateOnly? due) =>
        text.Replace("{Срок}", due is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—", StringComparison.Ordinal);

    /// <summary>Адреса через запятую или точку с запятой, до пяти; пусто — null. Неверный адрес — понятный отказ.</summary>
    public static string? NormalizeRecipients(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var list = value.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()).Distinct().ToList();
        if (list.Count > MaxRecipients)
        {
            throw new BusinessRuleException("sales.mail.recipients", $"Получателей — не больше {MaxRecipients}.");
        }

        foreach (var e in list)
        {
            var at = e.IndexOf('@');
            if (e.Length > 254 || at <= 0 || at != e.LastIndexOf('@') || e.IndexOf('.', at) < at + 2 || e.EndsWith('.')
                || e.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '(' or ')' or '[' or ']' or '\\'))
            {
                throw new BusinessRuleException("sales.mail.email", $"Почта «{e}» указана неверно: нужен адрес вида name@example.org.");
            }
        }

        var joined = string.Join(", ", list);
        return joined.Length > SalesMailMessage.RecipientsMaxLength
            ? throw new BusinessRuleException("sales.mail.recipients", $"Получателей — не больше {MaxRecipients}.")
            : joined;
    }
}

/// <summary>Вид связанного документа заказа (D1125).</summary>
public enum SalesLinkKind : byte
{
    SalesOrder = 1,
    PurchaseOrder = 2,
    CustomerInvoice = 3,
    CustomerPayment = 4,
    ProductionOrder = 5,
    StockDocument = 6,
    CrmDeal = 7,
}

/// <summary>
/// Ручная связь заказа покупателя с другим документом (D1125): «связать» и «отвязать». Учётные связи (отгрузки, счета, разноски
/// оплат, производство, сделка) живут в самих документах и здесь не хранятся. Отвязка не удаляет запись — ставит отметку,
/// кто и когда отвязал (история); повторно связать можно.
/// </summary>
public sealed class SalesDocumentLink
{
    public const int CommentMaxLength = 300;

    private SalesDocumentLink()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long SalesOrderId { get; private set; }
    public SalesLinkKind Kind { get; private set; }
    public long DocumentId { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? RemovedByUserId { get; private set; }
    public DateTime? RemovedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesDocumentLink Create(long organizationId, long salesOrderId, SalesLinkKind kind, long documentId, string? comment, long userId,
        DateTime nowUtc)
    {
        if (!Enum.IsDefined(kind) || kind == SalesLinkKind.CrmDeal)
        {
            throw new BusinessRuleException("sales.link.kind", "Этот вид документа связать вручную нельзя.");
        }

        if (kind == SalesLinkKind.SalesOrder && documentId == salesOrderId)
        {
            throw new BusinessRuleException("sales.link.self", "Заказ нельзя связать с самим собой.");
        }

        var c = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (c is { Length: > CommentMaxLength })
        {
            throw new BusinessRuleException("sales.link.comment", $"Комментарий — до {CommentMaxLength} символов.");
        }

        return new SalesDocumentLink
        {
            OrganizationId = organizationId, SalesOrderId = salesOrderId, Kind = kind, DocumentId = documentId, Comment = c,
            CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    public void Remove(long userId, DateTime nowUtc)
    {
        if (RemovedAtUtc is not null)
        {
            throw new BusinessRuleException("sales.link.removed", "Связь уже снята.");
        }

        RemovedByUserId = userId;
        RemovedAtUtc = nowUtc;
    }

    public static string KindName(SalesLinkKind kind) => kind switch
    {
        SalesLinkKind.SalesOrder => "Заказ покупателя",
        SalesLinkKind.PurchaseOrder => "Заказ поставщику",
        SalesLinkKind.CustomerInvoice => "Счёт покупателю",
        SalesLinkKind.CustomerPayment => "Оплата от покупателя",
        SalesLinkKind.ProductionOrder => "Производственный заказ",
        SalesLinkKind.StockDocument => "Складской документ",
        SalesLinkKind.CrmDeal => "Сделка CRM",
        _ => "Документ",
    };

    public static readonly IReadOnlyList<SalesLinkKind> ManualKinds =
    [
        SalesLinkKind.SalesOrder, SalesLinkKind.PurchaseOrder, SalesLinkKind.CustomerInvoice, SalesLinkKind.CustomerPayment,
        SalesLinkKind.ProductionOrder, SalesLinkKind.StockDocument,
    ];
}
