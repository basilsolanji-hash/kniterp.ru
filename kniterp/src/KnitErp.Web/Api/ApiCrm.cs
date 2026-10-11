using KnitErp.Application.Crm;
using KnitErp.Domain.Crm;

namespace KnitErp.Web.Api;

/// <summary>
/// API v1 CRM (D640–D643, ТЗ CRM §7) — отдельным файлом-расширением: заявки с сайта и маркетплейсов (дубликат по телефону или
/// почте → «повторное обращение» к найденному лиду), события телефонии, чтение лидов и сделок. Изменяющие запросы принимают
/// Idempotency-Key; лимит — общий лимит API. Токен: crm.leads.create (сайт, АТС) или crm.view / crm.edit. Вебхуки: crm.lead.created,
/// crm.deal.won, crm.deal.lost.
/// </summary>
public static class ApiCrm
{
    public static void MapCrmApi(this RouteGroupBuilder api)
    {
        api.MapPost("/crm/leads", async (CrmIntakeService intake, CrmIntakeLead body, CancellationToken ct) =>
            {
                var r = await intake.CreateLeadAsync(body, ct);
                return r.Repeat ? ApiResult.Ok(r) : new ApiResult(StatusCodes.Status201Created, r);
            })
            .AddEndpointFilter(ApiV1.IdempotencyAsync)
            .WithSummary("Заявка с сайта или маркетплейса: новый лид (201) или повторное обращение к найденному по телефону/почте (200)");

        api.MapPost("/crm/calls", async (CrmIntakeService intake, CrmCallEvent body, CancellationToken ct) =>
                ApiResult.Ok(await intake.RegisterCallAsync(body, ct)))
            .AddEndpointFilter(ApiV1.IdempotencyAsync)
            .WithSummary("Звонок из телефонии: активность к контакту, сделке или лиду по номеру; неизвестный номер — новый лид");

        api.MapGet("/crm/leads", async (CrmLeadService leads, CrmLeadStatus? status, string? search, int? page, CancellationToken ct) =>
                ApiResult.Ok(await leads.ListAsync(new CrmLeadFilter(status, search, Page: page ?? 0), ct)))
            .WithSummary("Лиды (по 100, page с 0); без crm.all — только свои");

        api.MapGet("/crm/deals", async (CrmDealService deals, long? pipelineId, CrmDealOutcome? outcome, string? search, int? page, CancellationToken ct) =>
                ApiResult.Ok(await deals.ListAsync(new CrmDealFilter(pipelineId, Outcome: outcome, Search: search, Page: page ?? 0), ct)))
            .WithSummary("Сделки (по 100, page с 0); суммы — с правом «Цены и суммы»");

        api.MapGet("/crm/deals/{id:long}", async (CrmDealService deals, long id, CancellationToken ct) => ApiResult.Ok(await deals.GetAsync(id, ct)))
            .WithSummary("Сделка: этап, история, позиции, заказы покупателя");
    }
}
