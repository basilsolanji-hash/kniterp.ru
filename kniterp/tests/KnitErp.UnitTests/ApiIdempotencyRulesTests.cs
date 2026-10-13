using System.Text.RegularExpressions;

namespace KnitErp.UnitTests;

/// <summary>
/// D129 (аудит 12.10.2026, п. 6): POST, создающие заказы, складские документы и оплаты, подключают фильтр обязательного
/// Idempotency-Key, а сам фильтр отвечает 400 idempotency.key_required. Проверка по исходнику: Web не подключён к модульным тестам.
/// </summary>
public sealed partial class ApiIdempotencyRulesTests
{
    private static readonly string Api = File.ReadAllText(Path.Combine(FindRoot(), "src", "KnitErp.Web", "Api", "ApiV1.cs"));

    [Theory]
    [InlineData("/sales-orders")]
    [InlineData("/purchase-orders")]
    [InlineData("/stock-documents")]
    [InlineData("/customer-payments")]
    public void Document_and_payment_creation_requires_idempotency_key(string route)
    {
        var start = Api.IndexOf($"api.MapPost(\"{route}\",", StringComparison.Ordinal);
        Assert.True(start >= 0, route);
        var end = Api.IndexOf(".WithSummary(", start, StringComparison.Ordinal);
        Assert.Contains(".AddEndpointFilter(IdempotencyRequiredAsync)", Api[start..end]);
    }

    [Fact]
    public void Required_filter_rejects_missing_key_with_400()
    {
        var filter = RequiredFilter().Match(Api);
        Assert.True(filter.Success);
        Assert.Contains("ApiErrors.Result(400, \"idempotency.key_required\"", filter.Value);
        Assert.Contains("await IdempotencyAsync(context, next)", filter.Value);
    }

    [GeneratedRegex(@"IdempotencyRequiredAsync\(EndpointFilterInvocationContext context, EndpointFilterDelegate next\) =>[\s\S]*?;\r?\n", RegexOptions.None)]
    private static partial Regex RequiredFilter();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден KnitErp.slnx");
    }
}
