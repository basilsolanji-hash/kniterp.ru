using System.Security.Cryptography;
using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Integrations;

/// <summary>
/// Токен собственного API (D88). Секрет «knt_…» показывается один раз при выпуске; в базе — только SHA-256 секрета
/// (секрет случайный, 256 бит, поэтому простого хэша достаточно) и первые символы для узнавания. Права токена — выбранные
/// при выпуске, но не шире текущих прав создателя: при каждом запросе действует их пересечение, поэтому понижение прав
/// или блокировка создателя сразу ограничивает и токен. Отзыв действует немедленно.
/// </summary>
public sealed class ApiToken
{
    public const string SecretPrefix = "knt_";
    public const int NameMaxLength = 100;
    public const int PrefixLength = 12;
    public const int MaxDays = 366;

    /// <summary>
    /// Права, которые через API не выдаются: управление пользователями и правами, выдача прав, изменение реквизитов и интеграций,
    /// открытие закрытого периода, персональные данные сотрудников (152-ФЗ: минимизация).
    /// </summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        Access.Permissions.UserManage, Access.Permissions.AdminGrant, Access.Permissions.OrganizationEdit, Access.Permissions.ClosedPeriodReopen,
        Access.Permissions.PersonalDataView,
    };

    private ApiToken()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Prefix { get; private set; } = string.Empty;
    public byte[] TokenHash { get; private set; } = [];

    /// <summary>Коды прав через запятую.</summary>
    public string Permissions { get; private set; } = string.Empty;

    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public long? RevokedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> PermissionList => Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    /// <summary>Выпуск: возвращает токен и секрет (секрет больше нигде не хранится).</summary>
    public static (ApiToken Token, string Secret) Issue(long organizationId, string? name, IEnumerable<string> permissions, long userId, DateTime nowUtc,
        int? days)
    {
        var codes = permissions.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (codes.Count == 0)
        {
            throw new BusinessRuleException("api.token.permissions", "Выберите хотя бы одно право для токена.");
        }

        if (codes.FirstOrDefault(c => !Access.Permissions.IsKnown(c) || Forbidden.Contains(c)) is { } bad)
        {
            throw new BusinessRuleException("api.token.permission_forbidden", $"Право «{Access.Permissions.Describe(bad)}» через API не выдаётся.");
        }

        if (days is { } d && (d < 1 || d > MaxDays))
        {
            throw new BusinessRuleException("api.token.expiry", $"Срок действия токена — от 1 до {MaxDays} дней.");
        }

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken
        {
            OrganizationId = organizationId,
            Name = DomainText.Require(name, NameMaxLength, "Название токена"),
            Prefix = secret[..PrefixLength],
            TokenHash = Hash(secret),
            Permissions = string.Join(',', codes),
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = days is { } n ? nowUtc.AddDays(n) : null,
        };
        return (token, secret);
    }

    public void Revoke(long userId, DateTime nowUtc)
    {
        if (RevokedAtUtc is not null)
        {
            throw new BusinessRuleException("api.token.revoked", "Токен уже отозван.");
        }

        RevokedAtUtc = nowUtc;
        RevokedByUserId = userId;
    }

    /// <summary>Отметка использования — не чаще раза в минуту, чтобы не писать в базу на каждый запрос.</summary>
    public bool Touch(DateTime nowUtc)
    {
        if (LastUsedAtUtc is { } last && nowUtc - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        LastUsedAtUtc = nowUtc;
        return true;
    }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Подписка на события (вебхук, D88). Секрет подписи «whsec_…» хранится зашифрованным мастер-ключом и показывается один раз.</summary>
public sealed class WebhookSubscription
{
    public const string SecretPrefix = "whsec_";
    public const int UrlMaxLength = 500;

    private WebhookSubscription()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Url { get; private set; } = string.Empty;

    /// <summary>Типы событий через запятую.</summary>
    public string Events { get; private set; } = string.Empty;

    public string Secret { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    /// <summary>Позиция в журнале аудита: события после неё ещё не поставлены в доставку.</summary>
    public long LastAuditId { get; private set; }

    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> EventList => Events.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static (WebhookSubscription Subscription, string Secret) Create(long organizationId, string url, IEnumerable<string> events, long lastAuditId,
        long userId, DateTime nowUtc)
    {
        var list = events.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            throw new BusinessRuleException("webhook.events", "Выберите хотя бы одно событие.");
        }

        if (list.FirstOrDefault(e => !WebhookEvents.IsKnown(e)) is { } bad)
        {
            throw new BusinessRuleException("webhook.event_unknown", $"Неизвестное событие «{bad}».");
        }

        var secret = SecretPrefix + ApiToken.Base64Url(RandomNumberGenerator.GetBytes(32));
        return (new WebhookSubscription
        {
            OrganizationId = organizationId,
            Url = url,
            Events = string.Join(',', list),
            Secret = secret,
            IsActive = true,
            LastAuditId = lastAuditId,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        }, secret);
    }

    public void SetActive(bool active, long lastAuditId)
    {
        // Включённая заново подписка не присылает накопившееся за время выключения.
        if (active && !IsActive)
        {
            LastAuditId = lastAuditId;
        }

        IsActive = active;
    }

    public void Advance(long auditId)
    {
        if (auditId > LastAuditId)
        {
            LastAuditId = auditId;
        }
    }
}

public enum WebhookDeliveryStatus : byte
{
    Pending = 1,
    Delivered = 2,
    Failed = 9,
}

/// <summary>Доставка события подписчику: попытки с нарастающей паузой до ~суток; Sequence — порядок событий организации.</summary>
public sealed class WebhookDelivery
{
    public const int ErrorMaxLength = 500;

    /// <summary>Паузы между попытками: 1, 5, 15 минут, 1, 3, 6, 12 часов — всего около 22 часов, затем «не доставлено».</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3),
        TimeSpan.FromHours(6), TimeSpan.FromHours(12),
    ];

    private WebhookDelivery()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long SubscriptionId { get; private set; }
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public long Sequence { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public WebhookDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public int? LastStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }

    public static WebhookDelivery Create(long organizationId, long subscriptionId, Guid eventId, string eventType, long sequence, string payload,
        DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        SubscriptionId = subscriptionId,
        EventId = eventId,
        EventType = eventType,
        Sequence = sequence,
        Payload = payload,
        Status = WebhookDeliveryStatus.Pending,
        NextAttemptAtUtc = nowUtc,
        CreatedAtUtc = nowUtc,
    };

    public void Succeeded(int statusCode, DateTime nowUtc)
    {
        Attempts++;
        Status = WebhookDeliveryStatus.Delivered;
        LastStatusCode = statusCode;
        LastError = null;
        DeliveredAtUtc = nowUtc;
    }

    public void FailedAttempt(int? statusCode, string? error, DateTime nowUtc)
    {
        Attempts++;
        LastStatusCode = statusCode;
        LastError = error is null ? null : error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        if (Attempts > Backoff.Count)
        {
            Status = WebhookDeliveryStatus.Failed;
            return;
        }

        NextAttemptAtUtc = nowUtc + Backoff[Attempts - 1];
    }

    /// <summary>Подпись: HMAC-SHA256 от «метка_времени.тело» секретом подписки, в hex. Получатель проверяет её и свежесть метки.</summary>
    public static string Sign(string secret, long timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
}

/// <summary>Повтор запроса API с тем же ключом (Idempotency-Key) возвращает первый результат, а не выполняет действие ещё раз.</summary>
public sealed class ApiIdempotencyRecord
{
    public const int KeyMaxLength = 100;

    private ApiIdempotencyRecord()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long TokenId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public int StatusCode { get; private set; }
    public string ResponseBody { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static ApiIdempotencyRecord Create(long organizationId, long tokenId, string key, string requestHash, int statusCode, string body, DateTime nowUtc) =>
        new()
        {
            OrganizationId = organizationId,
            TokenId = tokenId,
            Key = key,
            RequestHash = requestHash,
            StatusCode = statusCode,
            ResponseBody = body,
            CreatedAtUtc = nowUtc,
        };
}

/// <summary>События вебхуков: тип события ← действие журнала аудита.</summary>
public static class WebhookEvents
{
    public const string Ping = "ping";

    public static readonly IReadOnlyDictionary<string, (string Event, string Title)> ByAuditAction = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        [Audit.AuditActions.SalesOrderConfirmed] = ("sales_order.confirmed", "Заказ покупателя подтверждён"),
        [Audit.AuditActions.PurchaseOrderConfirmed] = ("purchase_order.confirmed", "Заказ поставщику подтверждён"),
        [Audit.AuditActions.StockDocumentPosted] = ("stock_document.posted", "Складской документ проведён"),
        [Audit.AuditActions.CustomerPaymentCreated] = ("customer_payment.created", "Оплата покупателя записана"),
        [Audit.AuditActions.SupplierPaymentCreated] = ("supplier_payment.created", "Оплата поставщику записана"),
        [Audit.AuditActions.MoneyOperationCreated] = ("money_operation.created", "Денежная операция проведена"),
        [Audit.AuditActions.ExpenseReportApproved] = ("expense_report.approved", "Авансовый отчёт утверждён"),
        // D102: события для обмена с 1С и магазином — создание и отмена заказов, сторно, новые партии.
        [Audit.AuditActions.SalesOrderCreated] = ("sales_order.created", "Заказ покупателя создан"),
        [Audit.AuditActions.SalesOrderCancelled] = ("sales_order.cancelled", "Заказ покупателя отменён"),
        [Audit.AuditActions.PurchaseOrderCreated] = ("purchase_order.created", "Заказ поставщику создан"),
        [Audit.AuditActions.StockDocumentReversed] = ("stock_document.reversed", "Складской документ сторнирован"),
        [Audit.AuditActions.StockBatchCreated] = ("stock_batch.created", "Партия заведена"),
    };

    public static IReadOnlyList<(string Event, string Title)> All => ByAuditAction.Values.OrderBy(v => v.Event, StringComparer.Ordinal).ToList();

    public static bool IsKnown(string eventType) => ByAuditAction.Values.Any(v => v.Event == eventType);
}
