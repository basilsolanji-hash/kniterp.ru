using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Access;

/// <summary>
/// D127: ограничения входа участника организации — задаёт администратор, по умолчанию выключены.
/// Принадлежат участию в организации (пользователь может работать в нескольких), действуют, когда сессия работает в этой организации.
/// «Одно устройство»: новый вход закрывает прежние сессии через 30 с. «Разрешённые сети»: внешний адрес (или CIDR) — браузер не
/// сообщает имя Wi-Fi, поэтому «только Wi-Fi фабрики» — это внешний IP-адрес интернета фабрики. «Время работы»: дни недели и окно
/// в часовом поясе организации, окно может переходить через полночь (ночная смена относится к дню начала).
/// Владельца ограничить нельзя (организация не должна остаться без входа), себя — тоже.
/// </summary>
public sealed class SignInRestriction
{
    public const int MaxNetworks = 20;
    public const int NetworksMaxLength = 1000;

    private SignInRestriction()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Участие в организации (organization_members.Id) — составной FK (OrganizationId, MemberId).</summary>
    public long MemberId { get; private set; }

    public bool SingleDevice { get; private set; }

    /// <summary>Нормализованные сети через пробел («203.0.113.0/24 2001:db8::/48») или null — без ограничения по адресу.</summary>
    public string? AllowedNetworks { get; private set; }

    /// <summary>Дни недели битами: пн = 1, вт = 2 … вс = 64; null — без ограничения по времени.</summary>
    public byte? AllowedDays { get; private set; }

    /// <summary>Начало окна, минуты от полуночи (местное время организации).</summary>
    public short? WindowStartMinute { get; private set; }

    /// <summary>Конец окна, минуты от полуночи; меньше начала — окно переходит через полночь; равно — сутки.</summary>
    public short? WindowEndMinute { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Кто изменил; null — аварийная команда сервера.</summary>
    public long? UpdatedByUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsEmpty => !SingleDevice && AllowedNetworks is null && AllowedDays is null;

    public static SignInRestriction Create(long organizationId, long memberId, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        MemberId = memberId,
        UpdatedAtUtc = nowUtc,
    };

    /// <summary>Новые значения. Сети проверяются строго (<see cref="IpNetworks.ParseList"/>), окно — <see cref="WorkWindow"/>.</summary>
    public void Update(bool singleDevice, IReadOnlyList<string> networks, WorkWindow? window, long? byUserId, DateTime nowUtc)
    {
        if (networks.Count > MaxNetworks)
        {
            throw new BusinessRuleException("access.restriction.networks_count", $"Не больше {MaxNetworks} адресов или сетей.");
        }

        var joined = networks.Count == 0 ? null : string.Join(' ', networks);
        if (joined is { Length: > NetworksMaxLength })
        {
            throw new BusinessRuleException("access.restriction.networks_length", "Список сетей слишком длинный.");
        }

        SingleDevice = singleDevice;
        AllowedNetworks = joined;
        AllowedDays = window?.Days;
        WindowStartMinute = window?.StartMinute;
        WindowEndMinute = window?.EndMinute;
        UpdatedByUserId = byUserId;
        UpdatedAtUtc = nowUtc;
    }

    public IReadOnlyList<string> Networks => AllowedNetworks?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public WorkWindow? Window => AllowedDays is { } days && WindowStartMinute is { } start && WindowEndMinute is { } end
        ? new WorkWindow(days, start, end)
        : null;

    /// <summary>Адрес клиента входит в разрешённые сети. Без ограничения — true; адрес неизвестен или не разобран — false.</summary>
    public bool AllowsAddress(string? clientAddress) => AllowedNetworks is null || IpNetworks.Contains(Networks, clientAddress);

    /// <summary>Описание для журнала аудита: «одно устройство; сети: …; время: пн, вт 08:00–20:00» или «нет».</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (SingleDevice)
        {
            parts.Add("одно устройство");
        }

        if (AllowedNetworks is not null)
        {
            parts.Add("сети: " + string.Join(", ", Networks));
        }

        if (Window is { } w)
        {
            parts.Add("время: " + w.Describe());
        }

        return parts.Count == 0 ? "нет" : string.Join("; ", parts);
    }
}

/// <summary>Почему сессия закроется через 30 секунд (user_sessions.EndReason).</summary>
public enum SessionEndReason : byte
{
    /// <summary>Выполнен вход с другого устройства («одно устройство»).</summary>
    OtherDevice = 1,

    /// <summary>Закончилось разрешённое время работы.</summary>
    Schedule = 2,
}

/// <summary>D127: разбор и сравнение адресов и сетей CIDR (IPv4 и IPv6).</summary>
public static partial class IpNetworks
{
    /// <summary>Строгий IPv4: четыре числа 0–255 без ведущих нулей («1.2.3» и «010.0.0.1» не принимаются).</summary>
    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"^[0-9A-Fa-f:.]+$")]
    private static partial Regex Ipv6Chars();

    [GeneratedRegex(@"^(0|[1-9]\d{0,2})$")]
    private static partial Regex Prefix();

    /// <summary>
    /// Список из текста (строки, запятые, пробелы, точки с запятой): каждая запись — адрес или сеть CIDR, сеть без лишних битов узла.
    /// Возвращает нормализованные записи без повторов. Ошибка — BusinessRuleException с неверной записью.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? text)
    {
        var entries = (text ?? string.Empty).Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > SignInRestriction.MaxNetworks)
        {
            throw new BusinessRuleException("access.restriction.networks_count", $"Не больше {SignInRestriction.MaxNetworks} адресов или сетей.");
        }

        var result = new List<string>();
        foreach (var entry in entries)
        {
            var normalized = Normalize(entry);
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    /// <summary>Одна запись: «203.0.113.7» → «203.0.113.7/32», «2001:DB8::/32» → «2001:db8::/32». Неверная — BusinessRuleException.</summary>
    public static string Normalize(string entry)
    {
        var value = entry.Trim();
        if (TryParse(value, out var network, out var hostBits))
        {
            return network.ToString();
        }

        if (hostBits)
        {
            throw new BusinessRuleException("access.restriction.network_host_bits",
                $"«{value}»: у сети не должно быть битов узла — укажите адрес сети, например «{network}».");
        }

        var shown = value.Length > 60 ? value[..60] + "…" : value;
        throw new BusinessRuleException("access.restriction.network_invalid",
            $"«{shown}» — не адрес IPv4/IPv6 и не сеть вида 203.0.113.0/24.");
    }

    /// <summary>Строгий разбор: адрес без маски — /32 или /128; hostBits = true — сеть с лишними битами узла (network — исправленная).</summary>
    public static bool TryParse(string? value, out IPNetwork network, out bool hostBits)
    {
        network = default;
        hostBits = false;
        if (string.IsNullOrEmpty(value) || value.Length > 50)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value[..slash];
        var prefixText = slash < 0 ? null : value[(slash + 1)..];

        IPAddress? address;
        int maxPrefix;
        if (Ipv4().IsMatch(addressText))
        {
            maxPrefix = 32;
        }
        else if (addressText.Contains(':') && Ipv6Chars().IsMatch(addressText))
        {
            maxPrefix = 128;
        }
        else
        {
            return false;
        }

        if (!IPAddress.TryParse(addressText, out address)
            || address.AddressFamily != (maxPrefix == 32 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return false;
        }

        var prefix = maxPrefix;
        if (prefixText is not null)
        {
            if (!Prefix().IsMatch(prefixText) || !int.TryParse(prefixText, out prefix) || prefix < 1 || prefix > maxPrefix)
            {
                return false;
            }
        }

        var masked = Mask(address, prefix);
        network = new IPNetwork(masked, prefix);
        if (!masked.Equals(address))
        {
            hostBits = true;
            return false;
        }

        return true;
    }

    /// <summary>Адрес клиента входит хотя бы в одну сеть. IPv4, отображённый в IPv6 («::ffff:1.2.3.4»), сравнивается как IPv4.</summary>
    public static bool Contains(IEnumerable<string> networks, string? clientAddress)
    {
        if (ParseClient(clientAddress) is not { } ip)
        {
            return false;
        }

        foreach (var text in networks)
        {
            if (TryParse(text, out var network, out _) && network.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Адрес клиента из соединения; зона IPv6 («%eth0») отбрасывается.</summary>
    public static IPAddress? ParseClient(string? clientAddress)
    {
        if (string.IsNullOrWhiteSpace(clientAddress) || clientAddress.Length > 100 || !IPAddress.TryParse(clientAddress.Trim(), out var ip))
        {
            return null;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
        {
            ip = new IPAddress(ip.GetAddressBytes());
        }

        return ip;
    }

    /// <summary>Адрес клиента одной записью (для кнопки «Добавить мой текущий адрес»): «203.0.113.7/32» или null.</summary>
    public static string? SingleAddress(string? clientAddress) =>
        ParseClient(clientAddress) is { } ip ? new IPNetwork(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128).ToString() : null;

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }
}

/// <summary>
/// D127: разрешённое время работы — дни недели и окно «с — по» в местном времени организации.
/// Окно начинается в разрешённый день; если «по» раньше «с», оно заканчивается на следующий день (ночная смена пн 22:00 → вт 06:00
/// относится к понедельнику). «С» = «по» — круглые сутки в разрешённые дни.
/// </summary>
public sealed record WorkWindow(byte Days, short StartMinute, short EndMinute)
{
    public const byte AllDays = 0x7F;
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Проверенное окно. Ошибка — BusinessRuleException.</summary>
    public static WorkWindow Create(byte days, int startMinute, int endMinute)
    {
        if (days == 0 || days > AllDays)
        {
            throw new BusinessRuleException("access.restriction.days", "Выберите хотя бы один день недели.");
        }

        if (startMinute is < 0 or >= MinutesPerDay || endMinute is < 0 or >= MinutesPerDay)
        {
            throw new BusinessRuleException("access.restriction.time", "Время должно быть от 00:00 до 23:59.");
        }

        return new WorkWindow(days, (short)startMinute, (short)endMinute);
    }

    /// <summary>Бит дня недели: пн = 1 … вс = 64.</summary>
    public static byte DayBit(DayOfWeek day) => (byte)(1 << (((int)day + 6) % 7));

    public bool AllowsDay(DayOfWeek day) => (Days & DayBit(day)) != 0;

    public int LengthMinutes => EndMinute == StartMinute ? MinutesPerDay : (EndMinute - StartMinute + MinutesPerDay) % MinutesPerDay;

    /// <summary>
    /// Окно, в которое попадает местное время, и его конец (местное время); null — вне разрешённого времени.
    /// Проверяются окна, начатые сегодня и вчера (переход через полночь).
    /// </summary>
    public DateTime? CurrentWindowEnd(DateTime local)
    {
        foreach (var day in new[] { local.Date, local.Date.AddDays(-1) })
        {
            if (!AllowsDay(day.DayOfWeek))
            {
                continue;
            }

            var start = day.AddMinutes(StartMinute);
            var end = start.AddMinutes(LengthMinutes);
            if (local >= start && local < end)
            {
                // Соседние окна (круглые сутки несколько дней подряд) склеиваются: конец — конец последнего подряд.
                while (LengthMinutes == MinutesPerDay && AllowsDay(end.DayOfWeek) && end - start < TimeSpan.FromDays(8))
                {
                    end = end.AddMinutes(MinutesPerDay);
                }

                return end;
            }
        }

        return null;
    }

    /// <summary>Сейчас (UTC) разрешено и до какого момента (UTC); null — вне окна.</summary>
    public DateTime? CurrentWindowEndUtc(DateTime utcNow, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        if (CurrentWindowEnd(local) is not { } end)
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(unspecified))
        {
            // Перевод часов вперёд: несуществующее время — первая существующая минута после него.
            unspecified = unspecified.AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static readonly string[] DayNames = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

    public string Describe() =>
        $"{string.Join(", ", Enumerable.Range(0, 7).Where(i => (Days & (1 << i)) != 0).Select(i => DayNames[i]))} "
        + $"{StartMinute / 60:00}:{StartMinute % 60:00}–{EndMinute / 60:00}:{EndMinute % 60:00}";
}
