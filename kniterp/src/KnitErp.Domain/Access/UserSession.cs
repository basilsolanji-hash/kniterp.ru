using System.Net;
using System.Net.Sockets;

namespace KnitErp.Domain.Access;

/// <summary>
/// D122: серверная запись сессии входа. Не зависит от организации: при смене организации сессия та же.
/// Cookie несёт только её внешний GUID; отозванная или неизвестная запись закрывает cookie при очередной проверке (не реже раза в минуту).
/// Минимизация ПДн: полный User-Agent и полный IP не хранятся — только «Браузер, система» и сеть /24 (IPv4) или /48 (IPv6).
/// </summary>
public sealed class UserSession
{
    public const int LabelMaxLength = 200;
    public const int AddressMaxLength = 64;

    private UserSession()
    {
    }

    public long Id { get; private set; }

    /// <summary>Внешний идентификатор — в cookie и в адресе кнопки «Завершить».</summary>
    public Guid PublicId { get; private set; }

    public long UserId { get; private set; }

    /// <summary>Штамп пользователя при входе: сменился (блокировка, смена роли, пароля) — сессия больше не действует.</summary>
    public Guid SecurityStamp { get; private set; }

    /// <summary>«Chrome, Windows» — из заголовка User-Agent.</summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>Сеть клиента при входе: «203.0.113.0/24» или «2001:db8:1::/48».</summary>
    public string? AddressPrefix { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>D127: сессия закроется в этот момент (вход с другого устройства или конец времени работы) — за 30 с предупреждение.</summary>
    public DateTime? EndsAtUtc { get; private set; }

    /// <summary>D127: почему сессия закроется; задаётся вместе с <see cref="EndsAtUtc"/>.</summary>
    public SessionEndReason? EndReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static UserSession Create(long userId, Guid securityStamp, string? userAgent, string? clientAddress, DateTime nowUtc) => new()
    {
        PublicId = Guid.NewGuid(),
        UserId = userId,
        SecurityStamp = securityStamp,
        Label = SessionDevice.Label(userAgent),
        AddressPrefix = SessionDevice.Network(clientAddress),
        CreatedAtUtc = nowUtc,
        LastSeenAtUtc = nowUtc,
    };

    /// <summary>Сессия действует: не завершена, штамп не менялся, cookie не могла истечь от бездействия.</summary>
    public bool IsActive(Guid securityStamp, DateTime nowUtc) =>
        RevokedAtUtc is null && SecurityStamp == securityStamp && nowUtc < LastSeenAtUtc + SignInPolicy.SessionIdleLimit
        && (EndsAtUtc is null || nowUtc < EndsAtUtc);

    /// <summary>D127: сколько длится предупреждение перед закрытием сессии.</summary>
    public static readonly TimeSpan EndWarning = TimeSpan.FromSeconds(30);

    public void Revoke(DateTime nowUtc) => RevokedAtUtc ??= nowUtc;

    /// <summary>D127: сессия закроется в этот момент; уже назначенный более ранний момент не откладывается.</summary>
    public void ScheduleEnd(DateTime atUtc, SessionEndReason reason)
    {
        if (EndsAtUtc is null || atUtc < EndsAtUtc)
        {
            (EndsAtUtc, EndReason) = (atUtc < CreatedAtUtc ? CreatedAtUtc : atUtc, reason);
        }
    }
}

/// <summary>D122: короткие и обезличенные сведения об устройстве сессии.</summary>
public static class SessionDevice
{
    /// <summary>Сколько символов заголовка User-Agent вообще разбирается; сам заголовок не хранится.</summary>
    public const int UserAgentMaxLength = 200;

    /// <summary>«Chrome, Windows» — чтобы пользователь узнал устройство в списке.</summary>
    public static string Label(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        if (ua.Length > UserAgentMaxLength)
        {
            ua = ua[..UserAgentMaxLength];
        }

        var browser = ua switch
        {
            _ when ua.Contains("YaBrowser", StringComparison.Ordinal) => "Яндекс Браузер",
            _ when ua.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when ua.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            _ when ua.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            _ when ua.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
            _ when ua.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => "Браузер",
        };
        var system = ua switch
        {
            _ when ua.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) => "iOS",
            _ when ua.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when ua.Contains("Mac OS X", StringComparison.Ordinal) => "macOS",
            _ when ua.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null,
        };
        return system is null ? browser : $"{browser}, {system}";
    }

    /// <summary>Сеть адреса: IPv4 — /24 (последний октет обнулён), IPv6 — /48. Нераспознанный адрес — null.</summary>
    public static string? Network(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 100 || !IPAddress.TryParse(address.Trim(), out var ip))
        {
            return null;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return $"{new IPAddress(bytes)}/24";
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Array.Clear(bytes, 6, bytes.Length - 6);
            return $"{new IPAddress(bytes)}/48";
        }

        return null;
    }
}
