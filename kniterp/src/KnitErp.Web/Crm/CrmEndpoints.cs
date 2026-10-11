using KnitErp.Application.Common;
using KnitErp.Application.Crm;

namespace KnitErp.Web.Crm;

/// <summary>
/// Файлы CRM (D614, D630, D645): выгрузка контактов (все или только с согласием на рассылки) и отчёта в Excel, шаблоны импорта.
/// Права проверяют сервисы; отказ — 403, чужое — 404. Ссылки на страницах — с download и data-enhance-nav="false".
/// </summary>
public static class CrmEndpoints
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapCrm(WebApplication app)
    {
        app.MapGet("/crm/contacts.xlsx", (CrmContactService contacts, bool? mailing, CancellationToken ct) =>
            FileAsync(() => contacts.ExportAsync(mailing ?? false, ct), mailing == true ? $"crm-rassylka-{DateTime.UtcNow:yyyy-MM-dd}.xlsx" : $"crm-kontakty-{DateTime.UtcNow:yyyy-MM-dd}.xlsx"));

        app.MapGet("/crm/report.xlsx", (CrmReportService reports, DateOnly from, DateOnly to, long? pipeline, long? responsible, CancellationToken ct) =>
            FileAsync(() => reports.ExportAsync(new CrmReportFilter(from, to, pipeline, responsible), ct), $"crm-otchet-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx"));

        app.MapGet("/crm/import-template.xlsx", (CrmImportService import, CrmImportKind kind, CancellationToken ct) =>
            FileAsync(() => import.TemplateAsync(kind, ct), $"shablon-crm-{kind.ToString().ToLowerInvariant()}.xlsx"));
    }

    private static async Task<IResult> FileAsync(Func<Task<byte[]>> build, string fileName)
    {
        try
        {
            return Results.File(await build(), Xlsx, fileName);
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
            return Results.BadRequest(ex.Message);
        }
    }
}
