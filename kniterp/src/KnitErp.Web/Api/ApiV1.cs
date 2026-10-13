using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Application.Tagging;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Integrations;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using KnitErp.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Web.Api;

/// <summary>JSON API: camelCase, перечисления строками, rowVersion — Base64.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Ответ API с телом JSON. Его тело и код сохраняются для повторов с тем же Idempotency-Key.</summary>
public sealed class ApiResult(int status, object? body) : IResult
{
    public int Status { get; } = status;
    public object? Body { get; } = body;

    public string Serialize() => JsonSerializer.Serialize(Body, ApiJson.Options);

    public Task ExecuteAsync(HttpContext http)
    {
        http.Response.StatusCode = Status;
        return Body is null ? Task.CompletedTask : http.Response.WriteAsJsonAsync(Body, ApiJson.Options);
    }

    public static ApiResult Ok(object? body) => new(StatusCodes.Status200OK, body);

    public static ApiResult Created(object? body) => new(StatusCodes.Status201Created, body);
}

/// <summary>Ошибки в формате application/problem+json: code — стабильный машинный код правила, detail — понятный текст.</summary>
public static class ApiErrors
{
    public sealed record ProblemDto(string Type, string Title, int Status, string Code, string Detail);

    public static ProblemDto Problem(int status, string code, string detail) =>
        new($"https://kniterp.ru/errors/{code}", status switch
        {
            400 => "Неверный запрос",
            401 => "Нужен вход",
            403 => "Нет прав",
            404 => "Не найдено",
            409 => "Конфликт",
            422 => "Нарушено правило",
            429 => "Слишком много запросов",
            _ => "Ошибка",
        }, status, code, detail);

    /// <summary>Ключ лимита API: SHA-256 заголовка Authorization (сам токен в памяти лимитера не хранится); без токена — адрес клиента.</summary>
    public static string RateKey(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        return auth.Length > 0
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth)))[..16]
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static IResult Result(int status, string code, string detail) =>
        Results.Json(Problem(status, code, detail), ApiJson.Options, "application/problem+json", status);
}

/// <summary>Страница списка: items, смещение и следующее смещение (null — последняя страница).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Offset, int Limit, int? NextOffset);

/// <summary>
/// Собственный API knitERP, версия 1 (D88). Все операции идут через те же сервисы, что и интерфейс: права, изоляция организации,
/// бизнес-правила, журнал аудита одинаковы. Изменяющие запросы принимают Idempotency-Key — повтор не выполняет действие дважды.
/// Описание — /api/v1/openapi.json, примеры — docs/api.md.
/// </summary>
public static class ApiV1
{
    public const int MaxLimit = 500;

    public sealed record MeDto(long OrganizationId, long UserId, long TokenId, string Token, IReadOnlyList<string> Permissions);

    public sealed record IdDto(long Id);

    public sealed record OrderLineInput(long ItemId, decimal Quantity, decimal Price, decimal? VatPercent, decimal DiscountPercent = 0);

    public sealed record SalesOrderInput(
        DateOnly OrderDate, long CustomerId, long WarehouseId, DateOnly? ShipDate, string? CustomerReference, bool PricesIncludeVat, string? Comment,
        long? LegalEntityId, IReadOnlyList<OrderLineInput> Lines, bool Confirm = false);

    public sealed record RowVersionInput(string RowVersion);

    public sealed record CustomerPaymentInput(
        DateOnly Date, long CustomerId, long? OrderId, decimal Amount, string? Comment, string? DocumentNumber, long? MoneyAccountId);

    // D102: складские документы, заказы поставщикам, партии и теги.
    public sealed record BatchPartInput(long BatchId, decimal Quantity, int? Packages, decimal? GrossWeightKg);

    public sealed record StockLineInput(long ItemId, decimal Quantity, IReadOnlyList<BatchPartInput>? Batches);

    public sealed record StockDocumentInput(
        StockOperationKind Kind, long WarehouseId, long? TargetWarehouseId, long? CounterpartyId, long? ReasonId, DateOnly DocumentDate, string? Comment,
        long? PurchaseOrderId, long? SalesOrderId, IReadOnlyList<StockLineInput> Lines, bool Post = false);

    public sealed record ReverseInput(string RowVersion, string? Reason);

    public sealed record PurchaseOrderInput(
        DateOnly OrderDate, long SupplierId, long WarehouseId, DateOnly? ExpectedDate, string? SupplierInvoice, bool PricesIncludeVat, string? Comment,
        long? LegalEntityId, IReadOnlyList<OrderLineInput> Lines, bool Confirm = false);

    public sealed record BatchInput(long ItemId, string? Number, string? Color, string? DyeLot, long? SupplierId, decimal? PackageWeightKg, string? Comment);

    public sealed record TagLinkInput(TaggedEntity Entity, long EntityId);

    public static void MapApiV1(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(new AuthorizationPolicyBuilder(ApiTokenAuthenticationHandler.SchemeName).RequireAuthenticatedUser().Build())
            .DisableAntiforgery()
            .AddEndpointFilter(ErrorsAsync)
            .WithTags("knitERP");

        api.MapGet("/me", async (IAccessGuard guard, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var ctx = await guard.CurrentAsync(ct);
            return ApiResult.Ok(new MeDto(ctx.OrganizationId, ctx.UserId, TokenId(user), user.Identity?.Name ?? string.Empty,
                ctx.Permissions.GrantedCodes.Order(StringComparer.Ordinal).ToList()));
        }).WithSummary("Кто я: организация, создатель токена и действующие права запроса");

        // Сессия 4: страница — в запросе к базе (раньше читались первые 2000 позиций и листались в памяти: дальше 2000-й API ничего не отдавал).
        api.MapGet("/items", async (CatalogService catalog, string? search, ItemType? type, long? tagId, int? offset, int? limit, CancellationToken ct) =>
            {
                var o = Math.Max(0, offset ?? 0);
                var l = Math.Clamp(limit ?? 100, 1, MaxLimit);
                var list = await catalog.ListItemsAsync(new ItemFilter(type, search, Take: l, TagId: tagId, Skip: o), ct);
                return ApiResult.Ok(new Page<ItemDto>(list.Items, o, l, o + list.Items.Count < list.Total ? o + list.Items.Count : null));
            })
            .WithSummary("Номенклатура");

        api.MapGet("/counterparties", async (CounterpartyService counterparties, string? search, string? role, long? tagId, int? offset, int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged((await counterparties.ListAsync(new CounterpartyFilter(
                role == "supplier" ? true : null, role == "customer" ? true : null, search, TagId: tagId), ct)).Counterparties, offset, limit)))
            .WithSummary("Контрагенты (role = customer | supplier)");

        api.MapPost("/counterparties", async (CounterpartyService counterparties, CounterpartyCommand body, CancellationToken ct) =>
            ApiResult.Created(new IdDto(await counterparties.CreateAsync(body, ct))))
            .AddEndpointFilter(IdempotencyAsync).WithSummary("Новый контрагент");

        api.MapGet("/warehouses", async (StockService stock, CancellationToken ct) => ApiResult.Ok(await stock.ListWarehousesAsync(ct)))
            .WithSummary("Склады");

        api.MapGet("/stock", async (StockService stock, long? warehouseId, ItemType? type, string? search, int? offset, int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged(await stock.BalancesAsync(new StockFilter(warehouseId, type, search), ct), offset, limit)))
            .WithSummary("Остатки по складам");

        api.MapGet("/sales-orders", async (SalesService sales, SalesOrderStatus? status, DateOnly? from, DateOnly? to, string? search, long? tagId, int? offset, int? limit,
                CancellationToken ct) =>
            ApiResult.Ok(Paged(await sales.ListOrdersAsync(new SalesOrderFilter(status, search, from, to, TagId: tagId), ct), offset, limit)))
            .WithSummary("Заказы покупателей");

        api.MapGet("/sales-orders/{id:long}", async (SalesService sales, long id, CancellationToken ct) => ApiResult.Ok(await sales.GetOrderAsync(id, ct)))
            .WithSummary("Заказ покупателя со строками, документами и оплатами");

        api.MapPost("/sales-orders", async (SalesService sales, SalesOrderInput body, CancellationToken ct) =>
        {
            // Заказ, строки и подтверждение — одной транзакцией: при ошибке в любой строке заказ не создаётся.
            var id = await sales.CreateOrderAsync(new SalesOrderHeader(body.OrderDate, body.CustomerId, body.WarehouseId, body.ShipDate,
                body.CustomerReference, body.PricesIncludeVat, body.Comment, body.LegalEntityId), ct);
            foreach (var line in body.Lines ?? [])
            {
                await sales.SetOrderLineAsync(id, line.ItemId, line.Quantity, line.Price, line.VatPercent, line.DiscountPercent,
                    (await sales.GetOrderAsync(id, ct)).RowVersion, ct);
            }

            if (body.Confirm)
            {
                await sales.ConfirmOrderAsync(id, (await sales.GetOrderAsync(id, ct)).RowVersion, ct);
            }

            return ApiResult.Created(await sales.GetOrderAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).AddEndpointFilter(TransactionAsync).WithSummary("Новый заказ покупателя со строками");

        api.MapPost("/sales-orders/{id:long}/confirm", async (SalesService sales, long id, RowVersionInput body, CancellationToken ct) =>
        {
            await sales.ConfirmOrderAsync(id, Convert.FromBase64String(body.RowVersion), ct);
            return ApiResult.Ok(await sales.GetOrderAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).WithSummary("Подтвердить заказ (rowVersion — из последнего ответа)");

        // D354 (сессия 4, Р1): черновик акта оказания услуг на незакрытые строки-услуги заказа; проводится как складской документ.
        api.MapPost("/sales-orders/{id:long}/service-act", async (SalesService sales, long id, CancellationToken ct) =>
            ApiResult.Created(new IdDto(await sales.CreateServiceActAsync(id, ct))))
            .AddEndpointFilter(IdempotencyAsync).WithSummary("Черновик акта оказания услуг по заказу покупателя");

        api.MapGet("/customer-payments", async (SalesService sales, DateOnly? from, DateOnly? to, string? search, int? offset, int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged(await sales.ListPaymentsAsync(from, to, search, ct), offset, limit)))
            .WithSummary("Оплаты покупателей");

        api.MapPost("/customer-payments", async (SalesService sales, CustomerPaymentInput body, CancellationToken ct) =>
            ApiResult.Created(new IdDto(await sales.CreatePaymentAsync(body.Date, body.CustomerId, body.OrderId, body.Amount, body.Comment,
                body.DocumentNumber, body.MoneyAccountId, ct))))
            .AddEndpointFilter(IdempotencyAsync).WithSummary("Записать оплату покупателя");

        api.MapGet("/purchase-orders", async (PurchaseService purchases, PurchaseOrderStatus? status, DateOnly? from, DateOnly? to, string? search, long? tagId, int? offset,
                int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged(await purchases.ListOrdersAsync(new PurchaseOrderFilter(status, search, from, to, TagId: tagId), ct), offset, limit)))
            .WithSummary("Заказы поставщикам");

        api.MapGet("/purchase-orders/{id:long}", async (PurchaseService purchases, long id, CancellationToken ct) =>
            ApiResult.Ok(await purchases.GetOrderAsync(id, ct))).WithSummary("Заказ поставщику");

        api.MapPost("/purchase-orders", async (PurchaseService purchases, PurchaseOrderInput body, CancellationToken ct) =>
        {
            // Заказ, строки и подтверждение — одной транзакцией.
            var id = await purchases.CreateOrderAsync(new PurchaseOrderHeader(body.OrderDate, body.SupplierId, body.WarehouseId, body.ExpectedDate,
                body.SupplierInvoice, body.PricesIncludeVat, body.Comment, body.LegalEntityId), ct);
            foreach (var line in body.Lines ?? [])
            {
                await purchases.SetOrderLineAsync(id, line.ItemId, line.Quantity, line.Price, line.VatPercent, (await purchases.GetOrderAsync(id, ct)).RowVersion, ct);
            }

            if (body.Confirm)
            {
                await purchases.ConfirmOrderAsync(id, (await purchases.GetOrderAsync(id, ct)).RowVersion, ct);
            }

            return ApiResult.Created(await purchases.GetOrderAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).AddEndpointFilter(TransactionAsync).WithSummary("Новый заказ поставщику со строками");

        api.MapPost("/purchase-orders/{id:long}/confirm", async (PurchaseService purchases, long id, RowVersionInput body, CancellationToken ct) =>
        {
            await purchases.ConfirmOrderAsync(id, Convert.FromBase64String(body.RowVersion), ct);
            return ApiResult.Ok(await purchases.GetOrderAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).WithSummary("Подтвердить заказ поставщику");

        // D354 (сессия 4, Р1): черновик акта услуг поставщика (доработка, доставка) на незакрытые строки-услуги заказа.
        api.MapPost("/purchase-orders/{id:long}/service-act", async (PurchaseService purchases, long id, CancellationToken ct) =>
            ApiResult.Created(new IdDto(await purchases.CreateServiceActAsync(id, ct))))
            .AddEndpointFilter(IdempotencyAsync).WithSummary("Черновик акта услуг поставщика по заказу поставщику");

        api.MapGet("/stock-documents", async (StockDocumentService documents, StockOperationKind? kind, StockDocumentStatus? status, DateOnly? from,
                DateOnly? to, long? warehouseId, string? search, long? tagId, int? offset, int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged(await documents.ListAsync(new StockDocumentListFilter(kind, status, from, to, warehouseId, search, TagId: tagId), ct), offset, limit)))
            .WithSummary("Складские документы (в области складов токена)");

        api.MapGet("/stock-documents/options", async (StockDocumentService documents, StockOperationKind kind, CancellationToken ct) =>
            ApiResult.Ok(await documents.GetOptionsAsync(kind, ct))).WithSummary("Склады, контрагенты и причины для нового документа этого вида");

        api.MapGet("/stock-documents/{id:long}", async (StockDocumentService documents, StockBatchService batches, long id, CancellationToken ct) =>
        {
            var doc = await documents.GetAsync(id, ct);
            var parts = await batches.ForDocumentAsync(id, ct);
            return ApiResult.Ok(new { document = doc, batches = parts.Items.Select(i => new { i.ItemId, rows = i.Rows }), batchMovements = parts.Movements });
        }).WithSummary("Складской документ со строками и партиями");

        api.MapPost("/stock-documents", async (StockDocumentService documents, StockBatchService batches, StockDocumentInput body, CancellationToken ct) =>
        {
            // Документ, строки, разбивка по партиям и проведение — одной транзакцией: ошибка в любой части — документа нет.
            var lines = (body.Lines ?? []).Select(l => (l.ItemId, l.Quantity)).ToList();
            var id = await documents.CreateWithLinesAsync(body.Kind, new StockDocumentHeader(body.WarehouseId, body.TargetWarehouseId, body.CounterpartyId,
                body.ReasonId, body.DocumentDate, body.Comment, body.PurchaseOrderId, body.SalesOrderId), lines, ct);
            foreach (var line in body.Lines ?? [])
            {
                foreach (var part in line.Batches ?? [])
                {
                    await batches.SetDocumentBatchAsync(id, line.ItemId, part.BatchId, part.Quantity, part.Packages, part.GrossWeightKg,
                        (await documents.GetAsync(id, ct)).RowVersion, ct);
                }
            }

            if (body.Post)
            {
                await documents.PostAsync(id, (await documents.GetAsync(id, ct)).RowVersion, ct);
            }

            return ApiResult.Created(await documents.GetAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).AddEndpointFilter(TransactionAsync).WithSummary("Новый складской документ (поступление, отгрузка, перемещение…)");

        api.MapPost("/stock-documents/{id:long}/post", async (StockDocumentService documents, long id, RowVersionInput body, CancellationToken ct) =>
        {
            await documents.PostAsync(id, Convert.FromBase64String(body.RowVersion), ct);
            return ApiResult.Ok(await documents.GetAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).WithSummary("Провести складской документ");

        api.MapPost("/stock-documents/{id:long}/reverse", async (StockDocumentService documents, long id, ReverseInput body, CancellationToken ct) =>
        {
            await documents.ReverseAsync(id, body.Reason, Convert.FromBase64String(body.RowVersion), ct);
            return ApiResult.Ok(await documents.GetAsync(id, ct));
        }).AddEndpointFilter(IdempotencyAsync).WithSummary("Сторнировать складской документ");

        api.MapGet("/batches", async (StockBatchService batches, long? itemId, string? search, bool? withBalance, int? offset, int? limit, CancellationToken ct) =>
            ApiResult.Ok(Paged((await batches.ListAsync(new StockBatchFilter(itemId, search, false, withBalance == true), ct))
                .Select(b => new { b.Id, code = b.Label, barcode = StockBatch.Code(b.Id), b.ItemId, b.ItemCode, b.Number, b.Color, b.DyeLot, b.SupplierId,
                    b.PackageWeightKg, b.Balance, b.UnitSymbol, b.BalanceKg, b.BalancePackages }).ToList(), offset, limit)))
            .WithSummary("Партии (цвет, крашение) с остатком");

        api.MapGet("/batches/balances", async (StockBatchService batches, long? warehouseId, string? search, CancellationToken ct) =>
            ApiResult.Ok(await batches.BalancesAsync(warehouseId, search, ct))).WithSummary("Остатки по складам с разбивкой по партиям");

        api.MapPost("/batches", async (StockBatchService batches, BatchInput body, CancellationToken ct) =>
            ApiResult.Created(new IdDto(await batches.CreateAsync(body.ItemId,
                new StockBatchData(body.Number, body.Color, body.DyeLot, body.SupplierId, body.PackageWeightKg, body.Comment), ct))))
            .AddEndpointFilter(IdempotencyAsync).WithSummary("Новая партия");

        api.MapGet("/tags", async (TagService tags, CancellationToken ct) =>
            ApiResult.Ok((await tags.ListAsync(false, ct)).Select(t => new { t.Id, t.Name, t.Color, t.Count }))).WithSummary("Теги организации");

        api.MapGet("/tags/{id:long}/objects", async (TagService tags, long id, CancellationToken ct) =>
            ApiResult.Ok((await tags.ObjectsAsync(id, ct)).Objects)).WithSummary("Объекты с тегом (в пределах прав токена)");

        api.MapPost("/tags/{id:long}/objects", async (TagService tags, long id, TagLinkInput body, CancellationToken ct) =>
        {
            await tags.AssignAsync(body.Entity, body.EntityId, id, ct: ct);
            return ApiResult.Ok(await tags.ForEntityAsync(body.Entity, body.EntityId, ct));
        }).WithSummary("Поставить тег на объект (повтор безопасен)");

        api.MapGet("/money/balances", async (MoneyService money, DateOnly? asOf, CancellationToken ct) => ApiResult.Ok(await money.BalancesAsync(asOf, ct)))
            .WithSummary("Остатки на счетах и в кассах");

        api.MapGet("/reports/cash-flow", async (CashReportsService reports, DateOnly from, DateOnly to, long? legalEntityId, CancellationToken ct) =>
            ApiResult.Ok(await reports.CashFlowAsync(from, to, legalEntityId, ct))).WithSummary("Движение денег по статьям");

        api.MapGet("/exchange/enterprise-data", async (KnitErp.Application.Exchange.EnterpriseDataExportService export, DateOnly from, DateOnly to,
                long? legalEntityId, CancellationToken ct) =>
            {
                var file = await export.ExportAsync(from, to, legalEntityId, ct);
                return Results.File(file.Xml, "application/xml", file.FileName);
            })
            .WithSummary("Выгрузка для 1С в формате EnterpriseData 1.8 (XML) за период");

        api.MapGet("/webhook-events", () => ApiResult.Ok(WebhookEvents.All.Select(e => new { e.Event, e.Title })))
            .WithSummary("Типы событий для вебхуков");

        api.MapCatalogApi(); // D307 (сессия 4): каталог — отдельным файлом ApiCatalog.cs
    }

    private static Page<T> Paged<T>(IReadOnlyList<T> all, int? offset, int? limit)
    {
        var o = Math.Max(0, offset ?? 0);
        var l = Math.Clamp(limit ?? 100, 1, MaxLimit);
        var items = all.Skip(o).Take(l).ToList();
        return new Page<T>(items, o, l, o + items.Count < all.Count ? o + items.Count : null);
    }

    private static long TokenId(ClaimsPrincipal user) => long.Parse(user.FindFirstValue(ApiTokenAuthenticationHandler.TokenClaim)!);

    /// <summary>Ошибки сервисов → problem+json с кодом правила.</summary>
    private static async ValueTask<object?> ErrorsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BusinessRuleException ex)
        {
            return ApiErrors.Result(422, ex.Code, ex.Message);
        }
        catch (NotFoundException ex)
        {
            return ApiErrors.Result(404, "not_found", ex.Message);
        }
        catch (AccessDeniedException)
        {
            return ApiErrors.Result(403, "auth.forbidden", "Недостаточно прав: у токена или его создателя нет нужного права.");
        }
        catch (ConcurrencyConflictException)
        {
            return ApiErrors.Result(409, "concurrency", "Данные изменились — получите объект заново и повторите с новым rowVersion.");
        }
        catch (FormatException)
        {
            return ApiErrors.Result(400, "format", "Неверный формат значения (например, rowVersion — Base64).");
        }
    }

    /// <summary>Несколько шагов одного запроса — одной транзакцией; сервисы присоединяются к ней.</summary>
    private static async ValueTask<object?> TransactionAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<IKnitErpDbContext>();
        await using var tx = await db.BeginTransactionAsync(context.HttpContext.RequestAborted);
        var result = await next(context);
        if (result is ApiResult { Status: >= 200 and < 300 })
        {
            await tx.CommitAsync(context.HttpContext.RequestAborted);
        }

        return result;
    }

    /// <summary>
    /// Idempotency-Key: первый успешный ответ сохраняется вместе с хэшем запроса в той же транзакции, что и действие; повтор с тем же
    /// ключом и тем же телом возвращает сохранённый ответ (заголовок Idempotent-Replayed), с другим телом — 422. Одновременные
    /// повторы идут по очереди (блокировка по ключу). Ошибки не сохраняются — запрос можно повторить.
    /// </summary>
    private static async ValueTask<object?> IdempotencyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length == 0)
        {
            return await next(context);
        }

        if (key.Length > ApiIdempotencyRecord.KeyMaxLength)
        {
            return ApiErrors.Result(400, "idempotency.key", $"Idempotency-Key — не длиннее {ApiIdempotencyRecord.KeyMaxLength} символов.");
        }

        var ct = http.RequestAborted;
        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{http.Request.Method} {http.Request.Path}\n{await reader.ReadToEndAsync(ct)}")));
        var tokenId = TokenId(http.User);
        var db = http.RequestServices.GetRequiredService<IKnitErpDbContext>();
        var clock = http.RequestServices.GetRequiredService<IClock>();

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.idem.{tokenId}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]}", ct);
        var saved = await db.ApiIdempotency.AsNoTracking().SingleOrDefaultAsync(r => r.TokenId == tokenId && r.Key == key, ct);
        if (saved is not null)
        {
            if (saved.RequestHash != requestHash)
            {
                return ApiErrors.Result(422, "idempotency.mismatch", "Этот Idempotency-Key уже использован с другим запросом.");
            }

            http.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Text(saved.ResponseBody, "application/json", Encoding.UTF8, saved.StatusCode);
        }

        var result = await next(context);
        if (result is ApiResult { Status: >= 200 and < 300 } ok)
        {
            var org = long.Parse(http.User.FindFirstValue(SessionClaims.OrganizationClaim)!);
            db.ApiIdempotency.Add(ApiIdempotencyRecord.Create(org, tokenId, key, requestHash, ok.Status, ok.Serialize(), clock.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return result;
    }
}
