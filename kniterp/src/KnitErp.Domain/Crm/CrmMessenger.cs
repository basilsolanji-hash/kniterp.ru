using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Мессенджер чатов с клиентами (D670): MAX (VK), Telegram (Bot API), WhatsApp (Business Cloud API, Meta).</summary>
public enum CrmMessengerKind : byte
{
    Max = 1,
    Telegram = 2,
    WhatsApp = 3,
}

/// <summary>
/// Подключённый мессенджер организации (D670, развитие D648): токен бота (и для WhatsApp — id номера и секрет приложения для подписи вебхука)
/// хранятся зашифрованными мастер-ключом и наружу не отдаются; секрет адреса вебхука — только SHA-256. Отключение не удаляет строку:
/// реквизиты стираются, остаётся кто и когда подключал. Входящие обрабатываются с правами подключившего (как токен API, D88).
/// </summary>
public sealed class CrmMessengerChannel
{
    public const int TokenMaxLength = 500;
    public const int AccountIdMaxLength = 64;
    public const int AppSecretMaxLength = 200;
    public const int NameMaxLength = 200;

    private CrmMessengerChannel()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public CrmMessengerKind Kind { get; private set; }
    public string? Token { get; private set; }

    /// <summary>WhatsApp: id номера телефона (Phone number ID) в Meta.</summary>
    public string? AccountId { get; private set; }

    /// <summary>WhatsApp: секрет приложения Meta — им проверяется подпись X-Hub-Signature-256 входящих.</summary>
    public string? AppSecret { get; private set; }

    public byte[]? SecretHash { get; private set; }

    /// <summary>Имя бота или номер, которые вернул мессенджер при проверке токена.</summary>
    public string? AccountName { get; private set; }

    public long ConnectedByUserId { get; private set; }
    public DateTime ConnectedAtUtc { get; private set; }
    public DateTime? DisconnectedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsConnected => Token is not null && SecretHash is not null;

    public static CrmMessengerChannel Create(long organizationId, CrmMessengerKind kind) =>
        Enum.IsDefined(kind) ? new CrmMessengerChannel { OrganizationId = organizationId, Kind = kind }
            : throw new BusinessRuleException("crm.messenger.kind", "Неизвестный мессенджер.");

    public void Connect(string? token, string? accountId, string? appSecret, string? accountName, byte[] secretHash, long userId, DateTime nowUtc)
    {
        Token = DomainText.Require(token, TokenMaxLength, "Токен");
        if (Kind == CrmMessengerKind.WhatsApp)
        {
            AccountId = DomainText.Require(accountId, AccountIdMaxLength, "Phone number ID");
            if (!AccountId.All(char.IsAsciiDigit))
            {
                throw new BusinessRuleException("crm.messenger.account", "Phone number ID — только цифры, из кабинета WhatsApp Business.");
            }

            AppSecret = DomainText.Require(appSecret, AppSecretMaxLength, "Секрет приложения");
        }
        else
        {
            AccountId = null;
            AppSecret = null;
        }

        if (secretHash is not { Length: 32 })
        {
            throw new ArgumentException("SHA-256 секрета — 32 байта.", nameof(secretHash));
        }

        AccountName = DomainText.Optional(accountName, NameMaxLength, "Имя бота");
        SecretHash = secretHash;
        ConnectedByUserId = userId;
        ConnectedAtUtc = nowUtc;
        DisconnectedAtUtc = null;
    }

    public void Disconnect(DateTime nowUtc)
    {
        Token = null;
        AppSecret = null;
        SecretHash = null;
        DisconnectedAtUtc = nowUtc;
    }

    public static string KindName(CrmMessengerKind kind) => kind switch
    {
        CrmMessengerKind.Max => "MAX",
        CrmMessengerKind.Telegram => "Telegram",
        CrmMessengerKind.WhatsApp => "WhatsApp",
        _ => kind.ToString(),
    };

    /// <summary>Ключ мессенджера в API приёма сообщений (D644) и в адресе вебхука.</summary>
    public static string Key(CrmMessengerKind kind) => kind switch
    {
        CrmMessengerKind.Max => "max",
        CrmMessengerKind.Telegram => "telegram",
        _ => "whatsapp",
    };
}
