using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Domain.Sales;

namespace KnitErp.Web.Api;

/// <summary>
/// Подключение документов продаж (D1120–D1125) одной строкой в Program.cs: фоновый отправитель писем клиенту, вкладка «Почта для документов»
/// карточки контрагента и выгрузка PDF документов. Отправитель выключается ключом SalesMail:WorkerDisabled (тестовые стенды).
/// </summary>
public static class SalesDocumentsSetup
{
    public static void AddSalesDocuments(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("SalesMail:WorkerDisabled"))
        {
            builder.Services.AddHostedService<SalesMailWorker>();
        }

        KnitErp.Web.Modules.ModuleUi.AddCounterpartyTab("sales-mail", KnitErp.Web.Modules.ModuleUi.Text("Почта для документов"),
            typeof(KnitErp.Web.Components.Pages.SalesCustomerMailTab), KnitErp.Domain.Access.Permissions.SalesView);
    }

    /// <summary>D1140: XML УПД для оператора ЭДО — по проведённой отгрузке, права печатной формы, коды маркировки строк (D1108); вложение в windows-1251.</summary>
    public static void MapUpdXml(this WebApplication app) =>
        app.MapGet("/sales-documents/upd/{id:long}.xml", async (long id, KnitErp.Application.Printing.PrintService print, SalesService sales, IClock clock,
            HttpContext http, CancellationToken ct) =>
        {
            try
            {
                // D1108: коды маркировки строк (КИЗ) — проданные по отгрузке, для вывода из оборота через ЭДО.
                var codes = await sales.CodesForShipmentAsync(id, ct);
                var file = await print.UpdXmlAsync(id, codes.Count == 0 ? null : codes, clock.UtcNow, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(file.Content, "application/xml", file.FileName);
            }
            catch (AccessDeniedException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (KnitErp.Domain.Common.BusinessRuleException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

    /// <summary>D1142: прайс-лист в Excel и PDF — те же права и отбор, что на экране «Прайс-лист».</summary>
    public static void MapPriceList(this WebApplication app)
    {
        app.MapGet("/sales-price-list.{format}", async (string format, long? priceType, long? group, bool? stock, long? warehouse, string? q,
            SalesPriceListService prices, ISalesPdfRenderer renderer, ISpreadsheetFormat spreadsheet, HttpContext http, CancellationToken ct) =>
        {
            var filter = new PriceListFilter(priceType, group, stock ?? false, warehouse, q);
            try
            {
                http.Response.Headers.CacheControl = "no-store";
                var name = $"prais-list-{DateTime.UtcNow:yyyy-MM-dd}";
                return format switch
                {
                    "xlsx" => Results.File(await prices.ExcelAsync(filter, spreadsheet, id => $"{http.Request.Scheme}://{http.Request.Host}/catalog/photos/{id}", ct),
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx"),
                    "pdf" => Results.File(renderer.PriceList(await prices.BuildAsync(filter, ct)), "application/pdf", name + ".pdf"),
                    _ => Results.NotFound(),
                };
            }
            catch (AccessDeniedException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (KnitErp.Domain.Common.BusinessRuleException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

    /// <summary>PDF заказа, счёта или УПД — те же права и организация, что у печатной формы; отдаётся вложением.</summary>
    public static void MapSalesDocuments(this WebApplication app) =>
        app.MapGet("/sales-documents/{document}/{id:long}.pdf", async (string document, long id, SalesOrderDocumentsService documents,
            ISalesPdfRenderer renderer, HttpContext http, CancellationToken ct) =>
        {
            SalesMailDocument? kind = document switch
            {
                "order" => SalesMailDocument.Order,
                "invoice" => SalesMailDocument.Invoice,
                "upd" => SalesMailDocument.Upd,
                _ => null,
            };
            if (kind is null)
            {
                return Results.NotFound();
            }

            try
            {
                var file = await documents.PdfAsync(kind.Value, id, renderer, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            catch (AccessDeniedException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
            catch (KnitErp.Domain.Common.BusinessRuleException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
}

/// <summary>Фоновая отправка писем клиенту (D1123): раз в 10 секунд — подошедшие письма очереди.</summary>
public sealed class SalesMailWorker(IServiceScopeFactory scopes, ILogger<SalesMailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<SalesMailDispatcher>().DeliverAsync(stoppingToken);
                if (sent > 0)
                {
                    log.LogInformation("Письма клиентам отправлены: {Count}.", sent);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Отправка писем клиентам: ошибка шага, повтор через 10 секунд.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
