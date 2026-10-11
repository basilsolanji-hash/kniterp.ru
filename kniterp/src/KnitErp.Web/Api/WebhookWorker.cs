using System.Net;
using System.Net.Sockets;
using System.Text;
using KnitErp.Application.Common;
using KnitErp.Application.Integrations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Web.Api;

/// <summary>
/// Отправка вебхука (D88): POST JSON, тайм-аут 10 с, без перенаправлений и без прокси. Адрес проверяется после разрешения имени —
/// подключение к внутренней сети запрещено даже если имя «перепривязали» на внутренний адрес (DNS rebinding).
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _client;

    public HttpWebhookSender(IntegrationSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (!settings.AllowPrivateNetworks && addresses.Any(NetworkGuard.IsPrivate))
                {
                    throw new HttpRequestException("Адрес вебхука указывает во внутреннюю сеть — отправка запрещена.");
                }

                var address = addresses.FirstOrDefault() ?? throw new HttpRequestException("Имя адреса вебхука не разрешается.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("knitERP-Webhooks/1.0");
    }

    public async Task<(int? StatusCode, string? Error)> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return ((int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return (null, ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Фоновая рассылка: раз в 5 секунд ставит новые события и отправляет подошедшие; раз в час чистит ключи повторов старше суток.</summary>
public sealed class WebhookWorker(IServiceScopeFactory scopes, ILogger<WebhookWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<WebhookDispatcher>();
                await dispatcher.EnqueueAsync(stoppingToken);
                await dispatcher.DeliverAsync(stoppingToken);
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    var db = scope.ServiceProvider.GetRequiredService<IKnitErpDbContext>();
                    var border = DateTime.UtcNow.AddDays(-1);
                    await db.ApiIdempotency.Where(r => r.CreatedAtUtc < border).ExecuteDeleteAsync(stoppingToken);
                    lastCleanup = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Рассылка вебхуков: ошибка шага, повтор через 5 секунд.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
