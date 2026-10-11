using System.Net;
using System.Text;
using KnitErp.Application.Exchange.MoySklad;
using KnitErp.Infrastructure.Exchange;

namespace KnitErp.IntegrationTests;

/// <summary>
/// HTTP-клиент МойСклад (D310) без сети: подставной обработчик отвечает записанными ответами. Проверяются фиксированный адрес и список путей,
/// токен только в заголовке, пауза по X-Lognex-Retry-After на 429, отказ токена, ограничение размера и понятная ошибка вместо сетевой.
/// </summary>
public sealed class CatMoySkladHttpTests
{
    private const string Token = "test-token-0000000000000000abcd";

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    private static (MoySkladHttpClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler);
        MoySkladHttpClient.Configure(http);
        return (new MoySkladHttpClient(http), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fixed_address_token_in_header_and_retry_after_on_429()
    {
        var (client, handler) = Create((_, n) =>
        {
            if (n == 1)
            {
                var busy = new HttpResponseMessage((HttpStatusCode)429);
                busy.Headers.Add("X-Lognex-Retry-After", "150");
                return busy;
            }

            return Json("""{"meta":{"size":1},"rows":[{"id":"o-1","name":"ООО «Пример»"}]}""");
        });

        Assert.Equal("ООО «Пример»", await client.CheckAsync(Token));
        Assert.Equal(2, handler.Requests.Count);
        var last = handler.Requests[^1];
        Assert.StartsWith("https://api.moysklad.ru/api/remap/1.2/entity/organization", last.RequestUri!.ToString());
        Assert.Equal(("Bearer", Token), (last.Headers.Authorization!.Scheme, last.Headers.Authorization.Parameter));
        Assert.DoesNotContain(Token, last.RequestUri.ToString());
        Assert.Contains("gzip", last.Headers.AcceptEncoding.ToString());
    }

    [Fact]
    public async Task Rejected_token_oversized_and_broken_responses_become_MoySklad_errors()
    {
        var (denied, _) = Create((_, _) => Json("""{"errors":[{"error":"Ошибка аутентификации"}]}""", HttpStatusCode.Unauthorized));
        Assert.Equal(401, (await Assert.ThrowsAsync<MoySkladException>(() => denied.CheckAsync(Token))).Status);

        var (huge, _) = Create((_, _) =>
        {
            var r = Json("{}");
            r.Content.Headers.ContentLength = MoySkladHttpClient.MaxResponseBytes + 1;
            return r;
        });
        Assert.Contains("32 МБ", (await Assert.ThrowsAsync<MoySkladException>(() => huge.ListAsync(Token, MoySkladEntities.Product, 0, 100))).Message);

        var (html, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>прокси</html>") });
        Assert.Contains("JSON", (await Assert.ThrowsAsync<MoySkladException>(() => html.ListAsync(Token, MoySkladEntities.Product, 0, 100))).Message);
    }

    [Fact]
    public async Task Only_allowed_paths_and_expand_values()
    {
        var (client, handler) = Create((_, _) => Json("""{"meta":{"size":0},"rows":[]}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(Token, "https://evil.example/steal", 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(Token, "entity/../../admin", 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(Token, MoySkladEntities.Bundle, 0, 10, "components&x=1"));
        Assert.Empty(handler.Requests);

        await client.ListAsync(Token, MoySkladEntities.Bundle, -5, 5000, "components.assortment");
        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Contains("offset=0", query);
        Assert.Contains("limit=1000", query);
    }
}
