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

        // D648, D670: вебхуки мессенджеров — без входа. Канал — по мессенджеру и секрету в адресе (хранится хэшем); дополнительно MAX и
        // Telegram присылают секрет в заголовке, WhatsApp подписывает тело HMAC-SHA256 секретом приложения. Ответ 200 — чтобы мессенджер не
        // повторял доставку; неизвестный секрет или неверная подпись — 404. Тело — не больше 64 КБ.
        app.MapPost("/hooks/crm/{messenger}/{secret}", async (HttpContext http, string messenger, string secret, IKnitErpDbContextFactory factory, IClock clock,
            CancellationToken ct) =>
        {
            if (CrmMessengerHooks.Kind(messenger) is not { } kind)
            {
                return Results.NotFound();
            }

            if (http.Request.ContentLength is > CrmMessengerHooks.MaxBody)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var body = await CrmMessengerHooks.ReadBodyAsync(http.Request.Body, ct);
            if (body is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await using var db = factory.Create();
            var hook = await CrmMessengerService.FindHookAsync(db, kind, secret, ct);
            if (hook is null || !CrmMessengerHooks.Authentic(kind, h => http.Request.Headers.TryGetValue(h, out var v) ? v.ToString() : null, secret, hook.AppSecret, body))
            {
                return Results.NotFound();
            }

            System.Text.Json.JsonElement doc;
            try
            {
                doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            foreach (var update in CrmMessengerHooks.Parse(kind, doc))
            {
                if (!await CrmMessengerService.InboundAsync(db, clock, hook, update, ct))
                {
                    return Results.NotFound();
                }
            }

            // Другие события (бот добавлен, статус доставки) — подтверждаются без обработки.
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery();

        // WhatsApp (Meta): проверка адреса при настройке вебхука — hub.verify_token должен совпасть с секретом, в ответ — hub.challenge.
        app.MapGet("/hooks/crm/{messenger}/{secret}", async (HttpContext http, string messenger, string secret, IKnitErpDbContextFactory factory,
            CancellationToken ct) =>
        {
            var q = http.Request.Query;
            if (CrmMessengerHooks.Kind(messenger) != KnitErp.Domain.Crm.CrmMessengerKind.WhatsApp || q["hub.mode"] != "subscribe"
                || !CrmMessengerHooks.SameSecret(q["hub.verify_token"].ToString(), secret) || q["hub.challenge"].ToString() is not { Length: > 0 and <= 200 } challenge)
            {
                return Results.NotFound();
            }

            await using var db = factory.Create();
            return await CrmMessengerService.FindHookAsync(db, KnitErp.Domain.Crm.CrmMessengerKind.WhatsApp, secret, ct) is null
                ? Results.NotFound()
                : Results.Text(challenge, "text/plain");
        }).AllowAnonymous();

        // D696: PDF коммерческого предложения — права и видимость как у печатной формы.
        app.MapGet("/crm/offers/{id:long}.pdf", async (CrmOfferService offers, long id, CancellationToken ct) =>
        {
            try
            {
                var file = await offers.PdfAsync(id, ct);
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
        });

        app.MapGet("/crm/import-template.xlsx", (CrmImportService import, CrmImportKind kind, CancellationToken ct) =>
            FileAsync(() => import.TemplateAsync(kind, ct), $"shablon-crm-{kind.ToString().ToLowerInvariant()}.xlsx"));

        // D692: фото сделки — только вошедшему, у кого сделка видна (CrmAccess.Deals); чужое, архивное и чужой организации — 404.
        // Кэш — только в браузере; nosniff ставят общие заголовки, тип — по сигнатуре, сохранённой при загрузке.
        app.MapGet("/crm/deal-photos/{id:long}", async (long id, CrmDealPhotoService photos, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var (contentType, content) = await photos.ContentAsync(id, ct);
                http.Response.Headers.CacheControl = "private, max-age=3600";
                http.Response.Headers.ContentDisposition = "inline";
                return Results.File(content, contentType);
            }
            catch (AccessDeniedException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (NotFoundException)
            {
                return Results.NotFound();
            }
        });
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
