using KnitErp.Application.Common;
using KnitErp.Application.Sales;

namespace KnitErp.Web.Api;

/// <summary>Выгрузки отгрузки (D1108) одной строкой в Program.cs: CSV кодов маркировки проведённой отгрузки — для вывода из оборота.</summary>
public static class SalesShippingSetup
{
    public static void MapSalesShipping(this WebApplication app) =>
        app.MapGet("/sales-marking/{id:long}/codes.csv", async (long id, SalesService sales, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var (content, fileName) = await sales.MarkingCsvAsync(id, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(content, "text/csv; charset=utf-8", fileName);
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
