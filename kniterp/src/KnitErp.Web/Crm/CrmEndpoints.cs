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

        // D648: вебхук MAX — без входа, организация по секрету в адресе (хранится хэшем) и заголовку X-Max-Bot-Api-Secret. Обрабатывается только
        // message_created; ответ 200 — чтобы MAX не повторял доставку; неизвестный секрет — 404. Тело — не больше 64 КБ.
        app.MapPost("/hooks/crm/max/{secret}", async (HttpContext http, string secret, IKnitErpDbContextFactory factory, IClock clock, CancellationToken ct) =>
        {
            if (http.Request.Headers.TryGetValue("X-Max-Bot-Api-Secret", out var header) && header.ToString() != secret)
            {
                return Results.NotFound();
            }

            if (http.Request.ContentLength is > 65_536)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            System.Text.Json.JsonElement doc;
            try
            {
                doc = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(http.Request.Body, cancellationToken: ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }

            await using var db = factory.Create();
            if (ParseMax(doc) is not { } update)
            {
                // Другие события (бот добавлен, сообщение изменено) — подтверждаем, если секрет известен.
                return await KnitErp.Application.Crm.CrmMessengerService.KnownSecretAsync(db, secret, ct) ? Results.Ok() : Results.NotFound();
            }

            return await KnitErp.Application.Crm.CrmMessengerService.InboundAsync(db, clock, secret, update, ct) ? Results.Ok() : Results.NotFound();
        }).AllowAnonymous().DisableAntiforgery();

        app.MapGet("/crm/import-template.xlsx", (CrmImportService import, CrmImportKind kind, CancellationToken ct) =>
            FileAsync(() => import.TemplateAsync(kind, ct), $"shablon-crm-{kind.ToString().ToLowerInvariant()}.xlsx"));
    }

    /// <summary>message_created: message.sender.user_id, name, username; message.body.text, mid.</summary>
    internal static CrmMaxUpdate? ParseMax(System.Text.Json.JsonElement doc)
    {
        if (doc.ValueKind != System.Text.Json.JsonValueKind.Object || !doc.TryGetProperty("update_type", out var type) || type.GetString() != "message_created"
            || !doc.TryGetProperty("message", out var m) || !m.TryGetProperty("sender", out var sender) || !m.TryGetProperty("body", out var body))
        {
            return null;
        }

        var userId = sender.TryGetProperty("user_id", out var u) ? u.ToString() : null;
        var text = body.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new CrmMaxUpdate(userId, sender.TryGetProperty("name", out var n) ? n.GetString() : null,
            sender.TryGetProperty("username", out var un) ? un.GetString() : null, text, body.TryGetProperty("mid", out var mid) ? mid.GetString() : null);
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
