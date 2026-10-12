using KnitErp.Domain.Access;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D127: разбор и сравнение сетей CIDR (IPv4, IPv6), окно времени работы с переходом через полночь и часовым поясом.</summary>
public sealed class SignInRestrictionRulesTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData(" 203.0.113.0/24 ", "203.0.113.0/24")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("2001:DB8::/32", "2001:db8::/32")]
    [InlineData("2001:db8:0:0::1", "2001:db8::1/128")]
    [InlineData("2001:db8:abcd:12::/64", "2001:db8:abcd:12::/64")]
    public void Valid_entries_are_normalized(string entry, string expected) => Assert.Equal(expected, IpNetworks.Normalize(entry));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("010.0.0.1")]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/0")]
    [InlineData("1.2.3.4/024")]
    [InlineData("1.2.3.4/")]
    [InlineData("2001:db8::/129")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:1.2.3.4")]
    [InlineData("example.org")]
    [InlineData("1.2.3.4-1.2.3.9")]
    [InlineData("*")]
    public void Invalid_entries_are_rejected(string entry)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => IpNetworks.Normalize(entry));
        Assert.Equal("access.restriction.network_invalid", ex.Code);
    }

    [Fact]
    public void Network_with_host_bits_is_rejected_with_suggestion()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => IpNetworks.Normalize("192.168.1.10/24"));
        Assert.Equal("access.restriction.network_host_bits", ex.Code);
        Assert.Contains("192.168.1.0/24", ex.Message);

        var v6 = Assert.Throws<BusinessRuleException>(() => IpNetworks.Normalize("2001:db8::1/64"));
        Assert.Equal("access.restriction.network_host_bits", v6.Code);
    }

    [Fact]
    public void List_accepts_lines_commas_and_drops_duplicates_but_not_more_than_twenty()
    {
        Assert.Equal(["203.0.113.0/24", "198.51.100.7/32", "2001:db8::/48"],
            IpNetworks.ParseList("203.0.113.0/24\n198.51.100.7, 2001:db8::/48; 203.0.113.0/24"));
        Assert.Empty(IpNetworks.ParseList("  \n "));

        var many = string.Join("\n", Enumerable.Range(1, 21).Select(i => $"10.0.0.{i}"));
        Assert.Equal("access.restriction.networks_count", Assert.Throws<BusinessRuleException>(() => IpNetworks.ParseList(many)).Code);
    }

    [Theory]
    [InlineData("203.0.113.57", true)]
    [InlineData("203.0.114.1", false)]
    [InlineData("::ffff:203.0.113.9", true)] // IPv4, отображённый в IPv6 (Kestrel с двойным стеком)
    [InlineData("198.51.100.7", true)]
    [InlineData("198.51.100.8", false)]
    [InlineData("2001:db8:abcd:12::5", true)]
    [InlineData("2001:db8:abce::1", false)]
    [InlineData("fe80::1%3", false)]
    [InlineData(null, false)]
    [InlineData("не адрес", false)]
    public void Address_matches_ipv4_and_ipv6_networks(string? address, bool expected) =>
        Assert.Equal(expected, IpNetworks.Contains(["203.0.113.0/24", "198.51.100.7/32", "2001:db8:abcd::/48"], address));

    [Fact]
    public void Current_address_becomes_single_entry()
    {
        Assert.Equal("203.0.113.57/32", IpNetworks.SingleAddress("::ffff:203.0.113.57"));
        Assert.Equal("2001:db8::5/128", IpNetworks.SingleAddress("2001:db8::5"));
        Assert.Null(IpNetworks.SingleAddress(null));
    }

    [Fact]
    public void Restriction_without_networks_allows_any_address_and_describes_itself()
    {
        var r = SignInRestriction.Create(1, 2, DateTime.UtcNow);
        Assert.True(r.IsEmpty);
        Assert.True(r.AllowsAddress(null));
        Assert.Equal("нет", r.Describe());

        r.Update(true, ["203.0.113.0/24"], WorkWindow.Create(0b0011111, 8 * 60, 20 * 60), 5, DateTime.UtcNow);
        Assert.False(r.IsEmpty);
        Assert.False(r.AllowsAddress(null));
        Assert.True(r.AllowsAddress("203.0.113.1"));
        Assert.Equal("одно устройство; сети: 203.0.113.0/24; время: пн, вт, ср, чт, пт 08:00–20:00", r.Describe());
    }

    // 12.10.2026 — понедельник.
    private static readonly DateTime Monday = new(2026, 10, 12);

    [Theory]
    [InlineData(0, 7, 59, false)]
    [InlineData(0, 8, 0, true)]
    [InlineData(0, 19, 59, true)]
    [InlineData(0, 20, 0, false)]
    [InlineData(5, 10, 0, false)] // суббота
    [InlineData(6, 10, 0, false)] // воскресенье
    public void Day_window_on_weekdays(int dayOffset, int hour, int minute, bool allowed)
    {
        var window = WorkWindow.Create(0b0011111, 8 * 60, 20 * 60);
        var local = Monday.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        Assert.Equal(allowed, window.CurrentWindowEnd(local) is not null);
    }

    [Theory]
    [InlineData(0, 21, 59, null)]
    [InlineData(0, 22, 0, "2026-10-13 06:00")] // пн 22:00 — смена понедельника до вт 06:00
    [InlineData(1, 3, 0, "2026-10-13 06:00")]  // вт 03:00 — ещё смена понедельника
    [InlineData(1, 6, 0, null)]                // вт 06:00 — конец
    [InlineData(4, 23, 0, "2026-10-17 06:00")] // пт 23:00 — смена пятницы до сб 06:00
    [InlineData(5, 5, 59, "2026-10-17 06:00")] // сб 05:59 — ещё смена пятницы
    [InlineData(5, 22, 30, null)]              // сб 22:30 — суббота не разрешена
    [InlineData(6, 2, 0, null)]                // вс 02:00 — смены субботы нет
    public void Night_shift_crosses_midnight_and_belongs_to_start_day(int dayOffset, int hour, int minute, string? expectedEnd)
    {
        var window = WorkWindow.Create(0b0011111, 22 * 60, 6 * 60); // пн–пт 22:00–06:00
        var local = Monday.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        Assert.Equal(expectedEnd, window.CurrentWindowEnd(local)?.ToString("yyyy-MM-dd HH:mm"));
    }

    [Fact]
    public void Equal_start_and_end_means_whole_allowed_days()
    {
        var window = WorkWindow.Create(0b0000011, 0, 0); // пн и вт круглые сутки
        Assert.Equal(new DateTime(2026, 10, 14), window.CurrentWindowEnd(Monday.AddHours(3)));
        Assert.Null(window.CurrentWindowEnd(Monday.AddDays(2).AddHours(3)));
    }

    [Fact]
    public void Window_uses_organization_time_zone()
    {
        var window = WorkWindow.Create(0b0011111, 8 * 60, 17 * 60); // пн–пт 08:00–17:00
        var yekaterinburg = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg"); // UTC+5
        var moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); // UTC+3

        // Пн 04:00 UTC = 09:00 в Екатеринбурге (окно), 07:00 в Москве (ещё нет).
        var utc = new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc), window.CurrentWindowEndUtc(utc, yekaterinburg));
        Assert.Null(window.CurrentWindowEndUtc(utc, moscow));

        // Вс 22:00 UTC = пн 03:00 в Екатеринбурге — вне окна; пн 06:00 UTC — 11:00, окно до 12:00 UTC.
        Assert.Null(window.CurrentWindowEndUtc(new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc), yekaterinburg));
        Assert.NotNull(window.CurrentWindowEndUtc(new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc), moscow));
    }

    [Theory]
    [InlineData(0, 480, 1200)]
    [InlineData(128, 480, 1200)]
    [InlineData(1, -1, 1200)]
    [InlineData(1, 480, 1440)]
    public void Invalid_window_is_rejected(int days, int start, int end) =>
        Assert.Throws<BusinessRuleException>(() => WorkWindow.Create((byte)Math.Min(days, 255), start, end));

    [Fact]
    public void Session_with_end_moment_is_inactive_after_it()
    {
        var now = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        var session = UserSession.Create(1, Guid.NewGuid(), null, null, now);
        session.ScheduleEnd(now.AddSeconds(30), SessionEndReason.OtherDevice);
        Assert.True(session.IsActive(session.SecurityStamp, now.AddSeconds(29)));
        Assert.False(session.IsActive(session.SecurityStamp, now.AddSeconds(30)));

        // Более поздний момент не откладывает уже назначенный.
        session.ScheduleEnd(now.AddMinutes(5), SessionEndReason.Schedule);
        Assert.Equal(now.AddSeconds(30), session.EndsAtUtc);
        Assert.Equal(SessionEndReason.OtherDevice, session.EndReason);
    }
}
