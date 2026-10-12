using System.Net.Http.Json;
using System.Text.Json;
using KnitErp.Application.Crm;
using KnitErp.Application.Workspace;
using Microsoft.Extensions.Logging;

namespace KnitErp.Infrastructure.External;

/// <param name="BaseUrl">Адрес MAX Bot API (по умолчанию https://platform-api.max.ru); пусто — интеграция выключена.</param>
public sealed record MaxOptions(string? BaseUrl);

/// <summary>
/// MAX Bot API (D648): GET /me — проверка токена, POST /subscriptions — вебхук на входящие (update_types: message_created, секрет в заголовке
/// X-Max-Bot-Api-Secret), POST /messages?user_id= — сообщение клиенту. Токен — в заголовке Authorization. Формат сверяется с документацией
/// MAX при подключении первого бота (D648); адрес API — в настройках сервера (Max:BaseUrl). Ошибки сети не выбрасываются наружу, кроме /me.
/// </summary>
public sealed class MaxMessengerClient(HttpClient http, MaxOptions options, ILogger<MaxMessengerClient> logger) : ICrmMaxClient
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(options.BaseUrl);

    public async Task<string?> BotNameAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = Request(HttpMethod.Get, "/me", token);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            var doc = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            return doc.TryGetProperty("name", out var n) ? n.GetString() : doc.TryGetProperty("username", out var u) ? u.GetString() : "MAX";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "MAX: проверка токена не удалась");
            throw new ExternalServiceUnavailableException("MAX недоступен.");
        }
    }

    public async Task<bool> SubscribeAsync(string token, string url, string secret, CancellationToken ct = default) =>
        await PostAsync("/subscriptions", token, new { url, update_types = new[] { "message_created" }, secret }, ct);

    public async Task<bool> SendAsync(string token, string userId, string text, CancellationToken ct = default) =>
        await PostAsync($"/messages?user_id={Uri.EscapeDataString(userId)}", token, new { text }, ct);

    private async Task<bool> PostAsync(string path, string token, object body, CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Post, path, token);
            req.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("MAX {Path}: {Status}", path.Split('?')[0], (int)res.StatusCode);
            }

            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "MAX {Path}: сеть недоступна", path.Split('?')[0]);
            return false;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, options.BaseUrl!.TrimEnd('/') + path);
        req.Headers.TryAddWithoutValidation("Authorization", token);
        return req;
    }
}
