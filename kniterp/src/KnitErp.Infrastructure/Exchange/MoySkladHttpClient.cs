using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KnitErp.Application.Exchange.MoySklad;

namespace KnitErp.Infrastructure.Exchange;

/// <summary>
/// Клиент JSON API МойСклад 1.2 (D310). Безопасность и лимиты:
/// <list type="bullet">
/// <item>адрес фиксирован (<see cref="MoySkladEntities.BaseUrl"/>), путь — только из списка <see cref="MoySkladEntities.Allowed"/>,
/// перенаправления не выполняются: произвольный URL не принимается (защита от SSRF);</item>
/// <item>не больше 45 запросов за 3 секунды и 5 параллельных на процесс — как требует МойСклад для одного пользователя;</item>
/// <item>429 — пауза по заголовку X-Lognex-Retry-After (мс), 5xx и обрыв — повтор с паузой, до 4 попыток;</item>
/// <item>ответ — gzip, не больше 32 МБ; таймаут запроса — 60 секунд.</item>
/// </list>
/// Токен передаётся только в заголовке Authorization и нигде не пишется в лог.
/// </summary>
public sealed class MoySkladHttpClient(HttpClient http) : IMoySkladClient
{
    public const long MaxResponseBytes = 32L * 1024 * 1024;
    public const int MaxAttempts = 4;
    public const string AcceptHeader = "application/json;charset=utf-8";
    public const string ContentTypeHeader = "application/json";
    private const int WindowRequests = 45;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private static readonly SemaphoreSlim Parallel = new(5, 5);
    private static readonly Queue<DateTime> Recent = new();
    private static readonly object Gate = new();

    public static void Configure(HttpClient client)
    {
        client.BaseAddress = new Uri(MoySkladEntities.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(60);
        // D334: МойСклад принимает Accept только дословно «application/json;charset=utf-8» (ошибка 1062 на реальном аккаунте).
        // Типизированный заголовок .NET пишет «application/json; charset=utf-8» — с пробелом, поэтому значение — строкой без проверки.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", AcceptHeader);
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("knitERP/1.0");
    }

    public static HttpMessageHandler Handler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<string?> CheckAsync(string token, CancellationToken ct = default)
    {
        var page = await GetAsync<MsPage<MsRow>>(token, MoySkladEntities.Organization, "limit=1", ct);
        return page.Rows is { Count: > 0 } rows ? rows[0].Name : null;
    }

    public Task<MsPage<MsRow>> ListAsync(string token, string entity, int offset, int limit, string? expand = null, CancellationToken ct = default)
    {
        var query = $"offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 1000)}";
        if (expand is not null)
        {
            if (expand.Any(c => !(char.IsAsciiLetter(c) || c is '.' or ',')))
            {
                throw new ArgumentException("Недопустимый параметр expand.", nameof(expand));
            }

            query += "&expand=" + expand;
        }

        return GetAsync<MsPage<MsRow>>(token, entity, query, ct);
    }

    public Task<MsPage<MsRow>> StockAsync(string token, int offset, int limit, CancellationToken ct = default) =>
        GetAsync<MsPage<MsRow>>(token, MoySkladEntities.StockAll, $"offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 1000)}", ct);

    public Task<MsPage<MsRow>> StockByStoreAsync(string token, int offset, int limit, CancellationToken ct = default) =>
        GetAsync<MsPage<MsRow>>(token, MoySkladEntities.StockByStore, $"offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 1000)}", ct);

    public async Task<IReadOnlyList<MsPriceType>> PriceTypesAsync(string token, CancellationToken ct = default) =>
        await GetAsync<List<MsPriceType>>(token, MoySkladEntities.PriceTypes, null, ct);

    public async Task<IReadOnlyList<MsRow>> UpdatePricesAsync(string token, string entity, IReadOnlyList<MsPriceUpdate> updates, CancellationToken ct = default)
    {
        if (!MoySkladEntities.Writable.Contains(entity))
        {
            throw new ArgumentException($"В «{entity}» knitERP не записывает.", nameof(entity));
        }

        if (updates.Count is 0 or > MoySkladEntities.MaxBatch)
        {
            throw new ArgumentException($"За раз — от 1 до {MoySkladEntities.MaxBatch} объектов.", nameof(updates));
        }

        var type = entity[(entity.LastIndexOf('/') + 1)..];
        object Meta(string path, string metaType) => new { href = MoySkladEntities.BaseUrl + path, type = metaType, mediaType = "application/json" };
        var body = updates.Select(u =>
        {
            if (!MoySkladEntities.IsSafeId(u.Id) || u.SalePrices.Any(p => !MoySkladEntities.IsSafeId(p.PriceTypeId)))
            {
                throw new ArgumentException("Недопустимый id объекта МойСклад.", nameof(updates));
            }

            return new Dictionary<string, object?>
            {
                ["meta"] = Meta($"{entity}/{u.Id}", type),
                ["salePrices"] = u.SalePrices.Select(p => new { value = p.Value, priceType = new { meta = Meta($"{MoySkladEntities.PriceTypes}/{p.PriceTypeId}", "pricetype") } }),
                ["buyPrice"] = u.BuyPrice is { } buy ? new { value = buy } : null,
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        }).ToList();
        var json = JsonSerializer.Serialize(body, MoySkladIds.Json);
        return await SendAsync<List<MsRow>>(token, entity, null, HttpMethod.Post, json, ct);
    }

    private Task<T> GetAsync<T>(string token, string entity, string? query, CancellationToken ct) => SendAsync<T>(token, entity, query, HttpMethod.Get, null, ct);

    private async Task<T> SendAsync<T>(string token, string entity, string? query, HttpMethod method, string? json, CancellationToken ct)
    {
        if (!MoySkladEntities.Allowed.Contains(entity))
        {
            throw new ArgumentException($"Путь «{entity}» не входит в список разрешённых.", nameof(entity));
        }

        var uri = new Uri(entity + (query is null ? "" : "?" + query), UriKind.Relative);
        for (var attempt = 1; ; attempt++)
        {
            await Parallel.WaitAsync(ct);
            HttpResponseMessage? response = null;
            try
            {
                await ThrottleAsync(ct);
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (json is not null)
                {
                    // D336: тип тела — дословно «application/json» (JSON всегда UTF-8): StringContent дописал бы «; charset=utf-8» с пробелом,
                    // а МойСклад сверяет заголовки дословно (так было с Accept, ошибка 1062).
                    request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", ContentTypeHeader);
                }
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                Parallel.Release();
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Последняя попытка или тайм-аут: наружу — понятная ошибка МойСклад, без подробностей сети.
                Parallel.Release();
                throw new MoySkladException(ex is TaskCanceledException ? "нет ответа (тайм-аут)" : "нет связи с сервером МойСклад");
            }
            catch
            {
                Parallel.Release();
                throw;
            }

            try
            {
                var status = (int)response.StatusCode;
                if (status == 429 && attempt < MaxAttempts)
                {
                    var wait = response.Headers.TryGetValues("X-Lognex-Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out var ms)
                        ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, 60_000))
                        : Backoff(attempt);
                    await Task.Delay(wait, ct);
                    continue;
                }

                if (status >= 500 && attempt < MaxAttempts)
                {
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }

                if (status is 401 or 403)
                {
                    throw new MoySkladException("доступ запрещён: токен неверный или без прав", status);
                }

                if (status is < 200 or >= 300)
                {
                    // Ответ пользователю: раздел, код и текст ошибки МойСклад (например, «код 1062: …»), без токена и без адреса аккаунта.
                    throw new MoySkladException($"{EntityTitle(entity)}: ответ {status}{await ErrorTextAsync(response, ct)}", status);
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    throw new MoySkladException("ответ больше 32 МБ");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var limited = new LimitedStream(stream, MaxResponseBytes);
                return await JsonSerializer.DeserializeAsync<T>(limited, MoySkladIds.Json, ct)
                       ?? throw new MoySkladException("пустой ответ");
            }
            catch (JsonException)
            {
                throw new MoySkladException("ответ не в формате JSON API 1.2");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new MoySkladException("связь прервалась при чтении ответа");
            }
            finally
            {
                response.Dispose();
                Parallel.Release();
            }
        }
    }

    /// <summary>Текст ошибки из тела ответа МойСклад ({"errors":[{"error":"…","code":1062}]}) — до 300 символов, без управляющих.</summary>
    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var limited = new LimitedStream(stream, 64 * 1024);
            using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var first = errors[0];
            var code = first.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? $"код {c.GetInt32()}: " : "";
            var text = first.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            var clean = new string((code + text).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            return clean.Length == 0 ? string.Empty : " (" + (clean.Length > 300 ? clean[..300] + "…" : clean) + ")";
        }
        catch (Exception ex) when (ex is JsonException or IOException or MoySkladException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static string EntityTitle(string entity) => entity switch
    {
        MoySkladEntities.Organization => "проверка доступа",
        MoySkladEntities.Uom => "единицы измерения",
        MoySkladEntities.Country => "страны",
        MoySkladEntities.Folder => "группы",
        MoySkladEntities.Product => "товары",
        MoySkladEntities.Service => "услуги",
        MoySkladEntities.Bundle => "комплекты",
        MoySkladEntities.Variant => "модификации",
        MoySkladEntities.PriceTypes => "виды цен",
        MoySkladEntities.StockAll => "остатки",
        MoySkladEntities.StockByStore => "остатки по складам",
        _ => entity,
    };

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>Скользящее окно: не больше 45 запросов за 3 секунды.</summary>
    private static async Task ThrottleAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                while (Recent.Count > 0 && now - Recent.Peek() > Window)
                {
                    Recent.Dequeue();
                }

                if (Recent.Count < WindowRequests)
                {
                    Recent.Enqueue(now);
                    return;
                }

                wait = Window - (now - Recent.Peek()) + TimeSpan.FromMilliseconds(10);
            }

            await Task.Delay(wait, ct);
        }
    }

    /// <summary>Поток с ограничением длины: больше — ошибка, а не память процесса.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int n)
        {
            _read += n;
            return _read > max ? throw new MoySkladException("ответ больше 32 МБ") : n;
        }
    }
}
