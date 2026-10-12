using KnitErp.Domain.Access;

namespace KnitErp.UnitTests;

/// <summary>D122: название устройства по User-Agent, сеть вместо полного адреса, срок действия серверной сессии.</summary>
public sealed class UserSessionRulesTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36", "Chrome, Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0", "Edge, Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 YaBrowser/24.7.0.0 Safari/537.36", "Яндекс Браузер, Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0", "Firefox, Linux")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1", "Safari, iOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36", "Chrome, Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 OPR/113.0.0.0", "Opera, macOS")]
    [InlineData("curl/8.5.0", "Браузер")]
    [InlineData("", "Браузер")]
    [InlineData(null, "Браузер")]
    public void Label_is_browser_and_system_from_user_agent(string? userAgent, string expected) =>
        Assert.Equal(expected, SessionDevice.Label(userAgent));

    [Fact]
    public void Label_reads_only_the_start_of_a_long_user_agent()
    {
        // Признак браузера за пределами 200 символов не разбирается: длинный заголовок не влияет на результат и не хранится.
        var ua = "Mozilla/5.0 (Windows NT 10.0) " + new string('x', 300) + " Firefox/130.0";
        Assert.Equal("Браузер, Windows", SessionDevice.Label(ua));
    }

    [Theory]
    [InlineData("203.0.113.57", "203.0.113.0/24")]
    [InlineData("198.51.100.255", "198.51.100.0/24")]
    [InlineData("::ffff:192.0.2.10", "192.0.2.0/24")]
    [InlineData("2001:db8:abcd:12:3456:789a:bcde:f012", "2001:db8:abcd::/48")]
    [InlineData("2001:db8::1", "2001:db8::/48")]
    [InlineData("::1", "::/48")]
    [InlineData(" 10.1.2.3 ", "10.1.2.0/24")]
    [InlineData("не адрес", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Network_keeps_only_the_prefix(string? address, string? expected) =>
        Assert.Equal(expected, SessionDevice.Network(address));

    [Fact]
    public void Session_expires_after_idle_limit_and_on_stamp_change_or_revoke()
    {
        var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var stamp = Guid.NewGuid();
        var session = UserSession.Create(1, stamp, "Firefox/130.0", "203.0.113.57", now);

        Assert.NotEqual(Guid.Empty, session.PublicId);
        Assert.Equal("Firefox", session.Label);
        Assert.Equal("203.0.113.0/24", session.AddressPrefix);
        Assert.True(session.IsActive(stamp, now + SignInPolicy.SessionIdleLimit - TimeSpan.FromSeconds(1)));
        Assert.False(session.IsActive(stamp, now + SignInPolicy.SessionIdleLimit));
        Assert.False(session.IsActive(Guid.NewGuid(), now));

        session.Revoke(now.AddMinutes(1));
        session.Revoke(now.AddMinutes(2));
        Assert.Equal(now.AddMinutes(1), session.RevokedAtUtc);
        Assert.False(session.IsActive(stamp, now));
    }
}
