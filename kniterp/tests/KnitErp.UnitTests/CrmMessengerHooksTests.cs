using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnitErp.Application.Crm;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;

namespace KnitErp.UnitTests;

/// <summary>D670: разбор вебхуков Telegram, WhatsApp и MAX и проверка их подлинности.</summary>
public sealed class CrmMessengerHooksTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Telegram_private_text_message_is_parsed_with_chat_scoped_id()
    {
        var doc = Json("""{"update_id":1,"message":{"message_id":42,"from":{"id":5,"first_name":"Анна","last_name":"Клиентова","username":"anna"},"chat":{"id":5,"type":"private"},"text":"Нужно 100 шапок"}}""");
        var u = Assert.Single(CrmMessengerHooks.Parse(CrmMessengerKind.Telegram, doc));
        Assert.Equal(("5", "Анна Клиентова", "@anna", "Нужно 100 шапок", "5:42"), (u.Peer, u.Name, u.Username, u.Text, u.MessageId));
    }

    [Theory]
    [InlineData("""{"message":{"message_id":1,"chat":{"id":-100,"type":"group"},"text":"в группе"}}""")]
    [InlineData("""{"message":{"message_id":1,"chat":{"id":5,"type":"private"},"sticker":{}}}""")]
    [InlineData("""{"edited_message":{"message_id":1,"chat":{"id":5,"type":"private"},"text":"x"}}""")]
    [InlineData("""[]""")]
    public void Telegram_groups_stickers_and_edits_are_skipped(string json) =>
        Assert.Empty(CrmMessengerHooks.Parse(CrmMessengerKind.Telegram, Json(json)));

    [Fact]
    public void WhatsApp_text_messages_are_parsed_with_profile_name_and_phone()
    {
        var doc = Json("""
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
          "contacts":[{"wa_id":"79000000071","profile":{"name":"Ольга"}}],
          "messages":[{"from":"79000000071","id":"wamid.1","type":"text","text":{"body":"Здравствуйте"}},
                      {"from":"79000000071","id":"wamid.2","type":"image","image":{}}]}}]}]}
        """);
        var u = Assert.Single(CrmMessengerHooks.Parse(CrmMessengerKind.WhatsApp, doc));
        Assert.Equal(("79000000071", "Ольга", "Здравствуйте", "wamid.1", "+79000000071"), (u.Peer, u.Name, u.Text, u.MessageId, u.Phone));
        Assert.Empty(CrmMessengerHooks.Parse(CrmMessengerKind.WhatsApp, Json("""{"entry":[{"changes":[{"value":{"statuses":[{"id":"x"}]}}]}]}""")));
    }

    [Fact]
    public void Max_message_created_is_parsed()
    {
        var doc = Json("""{"update_type":"message_created","message":{"sender":{"user_id":770001,"name":"Анна"},"body":{"mid":"m1","text":"Привет"}}}""");
        var u = Assert.Single(CrmMessengerHooks.Parse(CrmMessengerKind.Max, doc));
        Assert.Equal(("770001", "Анна", "Привет", "m1"), (u.Peer, u.Name, u.Text, u.MessageId));
        Assert.Empty(CrmMessengerHooks.Parse(CrmMessengerKind.Max, Json("""{"update_type":"bot_added"}""")));
    }

    [Fact]
    public void Telegram_and_max_require_secret_header()
    {
        var body = "{}"u8.ToArray();
        Assert.True(CrmMessengerHooks.Authentic(CrmMessengerKind.Telegram, Headers(("X-Telegram-Bot-Api-Secret-Token", Secret)), Secret, null, body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.Telegram, Headers(), Secret, null, body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.Telegram, Headers(("X-Telegram-Bot-Api-Secret-Token", "x")), Secret, null, body));
        // D121: у MAX заголовок тоже обязателен — секрет только в адресе оседает в журналах прокси.
        Assert.True(CrmMessengerHooks.Authentic(CrmMessengerKind.Max, Headers(("X-Max-Bot-Api-Secret", Secret)), Secret, null, body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.Max, Headers(), Secret, null, body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.Max, Headers(("X-Max-Bot-Api-Secret", "x")), Secret, null, body));
    }

    [Fact]
    public void WhatsApp_requires_valid_hmac_signature_of_exact_body()
    {
        var body = Encoding.UTF8.GetBytes("""{"entry":[]}""");
        var sign = "sha256=" + Convert.ToHexString(HMACSHA256.HashData("app-secret"u8.ToArray(), body)).ToLowerInvariant();
        Assert.True(CrmMessengerHooks.Authentic(CrmMessengerKind.WhatsApp, Headers(("X-Hub-Signature-256", sign)), Secret, "app-secret", body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.WhatsApp, Headers(("X-Hub-Signature-256", sign)), Secret, "other", body));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.WhatsApp, Headers(("X-Hub-Signature-256", sign)), Secret, "app-secret", [.. body, 32]));
        Assert.False(CrmMessengerHooks.Authentic(CrmMessengerKind.WhatsApp, Headers(), Secret, "app-secret", body));
    }

    [Fact]
    public async Task Body_over_64_kb_is_rejected()
    {
        Assert.Null(await CrmMessengerHooks.ReadBodyAsync(new MemoryStream(new byte[CrmMessengerHooks.MaxBody + 1]), default));
        Assert.Equal(10, (await CrmMessengerHooks.ReadBodyAsync(new MemoryStream(new byte[10]), default))!.Length);
    }

    [Fact]
    public void Channel_requires_whatsapp_details_and_forgets_secrets_on_disconnect()
    {
        var hash = new byte[32];
        var wa = CrmMessengerChannel.Create(1, CrmMessengerKind.WhatsApp);
        Assert.Throws<BusinessRuleException>(() => wa.Connect("t", null, "s", "n", hash, 1, DateTime.UtcNow));
        Assert.Throws<BusinessRuleException>(() => wa.Connect("t", "12ab", "s", "n", hash, 1, DateTime.UtcNow));
        wa.Connect("t", "123", "s", "n", hash, 1, DateTime.UtcNow);
        Assert.True(wa.IsConnected);
        wa.Disconnect(DateTime.UtcNow);
        Assert.Equal((false, null, null, null), (wa.IsConnected, wa.Token, wa.AppSecret, wa.SecretHash));
        Assert.Throws<BusinessRuleException>(() => CrmMessengerChannel.Create(1, (CrmMessengerKind)9));
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static Func<string, string?> Headers(params (string Name, string Value)[] headers) =>
        name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
