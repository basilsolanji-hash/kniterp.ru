using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KnitErp.Application.Crm;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Crm;
using Microsoft.Extensions.Logging;

namespace KnitErp.Infrastructure.External;

/// <summary>
/// Адреса Bot API мессенджеров (D670) из настроек сервера (Messengers:MaxUrl, :TelegramUrl, :WhatsAppUrl); пусто — мессенджер выключен.
/// </summary>
public sealed record MessengerOptions(string? MaxUrl, string? TelegramUrl, string? WhatsAppUrl)
{
    public static readonly MessengerOptions Default = new("https://platform-api.max.ru", "https://api.telegram.org", "https://graph.facebook.com/v21.0");
}

/// <summary>Общее для HTTP-клиентов мессенджеров: ошибки сети не выбрасываются наружу (кроме проверки токена), токен в лог не пишется.</summary>
public abstract class MessengerClientBase(HttpClient http, ILogger logger) : ICrmMessengerClient
{
    public abstract CrmMessengerKind Kind { get; }
    protected abstract string? BaseUrl { get; }
    public bool IsAvailable => !string.IsNullOrWhiteSpace(BaseUrl);

    public abstract Task<string?> AccountNameAsync(CrmMessengerCredentials credentials, CancellationToken ct = default);
    public abstract Task<bool> SubscribeAsync(CrmMessengerCredentials credentials, string url, string secret, CancellationToken ct = default);
    public abstract Task<bool> SendAsync(CrmMessengerCredentials credentials, string peer, string text, CancellationToken ct = default);

    protected string Url(string path) => BaseUrl!.TrimEnd('/') + path;

    /// <summary>GET для проверки токена: 2xx — JSON, иначе null; сеть — ExternalServiceUnavailableException.</summary>
    protected async Task<JsonElement?> GetJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using (req)
            using (var res = await http.SendAsync(req, ct))
            {
                return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<JsonElement>(ct) : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "{Messenger}: проверка токена не удалась", Kind);
            throw new ExternalServiceUnavailableException($"{CrmMessengerChannel.KindName(Kind)} недоступен.");
        }
    }

    protected async Task<bool> PostAsync(HttpRequestMessage req, object body, string what, CancellationToken ct)
    {
        try
        {
            using (req)
            {
                req.Content = JsonContent.Create(body);
                using var res = await http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                {
                    logger.LogWarning("{Messenger} {What}: {Status}", Kind, what, (int)res.StatusCode);
                }

                return res.IsSuccessStatusCode;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "{Messenger} {What}: сеть недоступна", Kind, what);
            return false;
        }
    }
}

/// <summary>
/// MAX Bot API (D648): GET /me — проверка токена, POST /subscriptions — вебхук (update_types: message_created, секрет в заголовке
/// X-Max-Bot-Api-Secret), POST /messages?user_id= — сообщение клиенту. Токен — в заголовке Authorization.
/// </summary>
public sealed class MaxMessengerClient(HttpClient http, MessengerOptions options, ILogger<MaxMessengerClient> logger) : MessengerClientBase(http, logger)
{
    public override CrmMessengerKind Kind => CrmMessengerKind.Max;
    protected override string? BaseUrl => options.MaxUrl;

    public override async Task<string?> AccountNameAsync(CrmMessengerCredentials c, CancellationToken ct = default) =>
        await GetJsonAsync(Request(HttpMethod.Get, "/me", c.Token), ct) is { } doc
            ? doc.TryGetProperty("name", out var n) ? n.GetString() : doc.TryGetProperty("username", out var u) ? u.GetString() : "MAX"
            : null;

    public override Task<bool> SubscribeAsync(CrmMessengerCredentials c, string url, string secret, CancellationToken ct = default) =>
        PostAsync(Request(HttpMethod.Post, "/subscriptions", c.Token), new { url, update_types = new[] { "message_created" }, secret }, "subscriptions", ct);

    public override Task<bool> SendAsync(CrmMessengerCredentials c, string peer, string text, CancellationToken ct = default) =>
        PostAsync(Request(HttpMethod.Post, $"/messages?user_id={Uri.EscapeDataString(peer)}", c.Token), new { text }, "messages", ct);

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, Url(path));
        req.Headers.TryAddWithoutValidation("Authorization", token);
        return req;
    }
}

/// <summary>
/// Telegram Bot API (D670): getMe — проверка токена, setWebhook с secret_token (Telegram присылает его в заголовке
/// X-Telegram-Bot-Api-Secret-Token) и allowed_updates = message, sendMessage — ответ в чат. Токен — часть адреса (/bot{token}/…), в лог не пишется.
/// </summary>
public sealed class TelegramMessengerClient(HttpClient http, MessengerOptions options, ILogger<TelegramMessengerClient> logger) : MessengerClientBase(http, logger)
{
    public override CrmMessengerKind Kind => CrmMessengerKind.Telegram;
    protected override string? BaseUrl => options.TelegramUrl;

    public override async Task<string?> AccountNameAsync(CrmMessengerCredentials c, CancellationToken ct = default)
    {
        if (!ValidToken(c.Token))
        {
            return null;
        }

        return await GetJsonAsync(new HttpRequestMessage(HttpMethod.Get, Url($"/bot{c.Token}/getMe")), ct) is { } doc
               && doc.TryGetProperty("ok", out var ok) && ok.GetBoolean() && doc.TryGetProperty("result", out var r)
            ? r.TryGetProperty("username", out var u) ? "@" + u.GetString() : r.TryGetProperty("first_name", out var f) ? f.GetString() : "Telegram"
            : null;
    }

    public override Task<bool> SubscribeAsync(CrmMessengerCredentials c, string url, string secret, CancellationToken ct = default) =>
        PostAsync(new HttpRequestMessage(HttpMethod.Post, Url($"/bot{c.Token}/setWebhook")),
            new { url, secret_token = secret, allowed_updates = new[] { "message" }, drop_pending_updates = false }, "setWebhook", ct);

    public override Task<bool> SendAsync(CrmMessengerCredentials c, string peer, string text, CancellationToken ct = default) =>
        PostAsync(new HttpRequestMessage(HttpMethod.Post, Url($"/bot{c.Token}/sendMessage")), new { chat_id = peer, text }, "sendMessage", ct);

    /// <summary>Токен бота — «123456:ABC-…»: без него в адресе нельзя подставить чужой путь.</summary>
    private static bool ValidToken(string token) =>
        token.Length is > 10 and < 100 && token.Contains(':') && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ':' or '_' or '-');
}

/// <summary>
/// WhatsApp Business Cloud API (Meta, D670): GET /{phone-number-id}?fields=display_phone_number,verified_name — проверка токена,
/// POST /{phone-number-id}/messages — ответ клиенту (в течение 24 часов после его сообщения). Вебхук указывается в кабинете Meta
/// (адрес и Verify token), подпись входящих — X-Hub-Signature-256 секретом приложения. Токен — Bearer.
/// </summary>
public sealed class WhatsAppMessengerClient(HttpClient http, MessengerOptions options, ILogger<WhatsAppMessengerClient> logger) : MessengerClientBase(http, logger)
{
    public override CrmMessengerKind Kind => CrmMessengerKind.WhatsApp;
    protected override string? BaseUrl => options.WhatsAppUrl;

    public override async Task<string?> AccountNameAsync(CrmMessengerCredentials c, CancellationToken ct = default)
    {
        if (c.AccountId is not { Length: > 0 } id || !id.All(char.IsAsciiDigit))
        {
            return null;
        }

        var doc = await GetJsonAsync(Request(HttpMethod.Get, $"/{id}?fields=display_phone_number,verified_name", c.Token), ct);
        return doc is { } d
            ? $"{(d.TryGetProperty("verified_name", out var n) ? n.GetString() : "WhatsApp")} {(d.TryGetProperty("display_phone_number", out var p) ? p.GetString() : "")}".Trim()
            : null;
    }

    public override Task<bool> SubscribeAsync(CrmMessengerCredentials c, string url, string secret, CancellationToken ct = default) => Task.FromResult(false);

    public override Task<bool> SendAsync(CrmMessengerCredentials c, string peer, string text, CancellationToken ct = default) =>
        PostAsync(Request(HttpMethod.Post, $"/{c.AccountId}/messages", c.Token),
            new { messaging_product = "whatsapp", recipient_type = "individual", to = peer, type = "text", text = new { preview_url = false, body = text } }, "messages", ct);

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, Url(path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
