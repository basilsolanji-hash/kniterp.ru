using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Автоматизация CRM (D660–D669, `docs/tz/crm-automation.md`): события сделки и их следствия. Вызывается внутри транзакции
/// сценария, сохраняет вызывающий. Правила выключаются в настройках организации (<see cref="CrmSettings"/>).
/// </summary>
public static class CrmEvents
{
    public static async Task<CrmSettings> SettingsAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.CrmSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == organizationId, ct) ?? CrmSettings.Default(organizationId);

    public static async Task<string> TimeZoneAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.TimeZoneId).SingleAsync(ct);

    public static DateOnly Today(DateTime nowUtc, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(nowUtc, timeZoneId));

    /// <summary>Местное время организации → UTC (срок задачи вводится по местному времени).</summary>
    public static DateTime ToUtc(DateTime local, string timeZoneId) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

    /// <summary>Срок «через N дней в 10:00 по местному времени».</summary>
    public static DateTime DueInDays(DateTime nowUtc, string timeZoneId, int days) =>
        ToUtc(Today(nowUtc, timeZoneId).AddDays(days).ToDateTime(new TimeOnly(10, 0)), timeZoneId);

    /// <summary>
    /// После смены этапа: в журнал — «выиграна» / «проиграна» (по ним уходят вебхуки crm.deal.won и crm.deal.lost, D642);
    /// на выигрыше — задача «Предложить повторный заказ» через N дней (В2, D666), если правило включено.
    /// </summary>
    public static async Task AfterStageChangedAsync(IKnitErpDbContext db, AccessContext ctx, CrmDeal deal, CrmStage stage, DateTime nowUtc, string? correlationId,
        CancellationToken ct)
    {
        if (stage.Kind == CrmStageKind.Open)
        {
            return;
        }

        db.AuditEntries.Add(AuditEntry.Create(nowUtc, ctx.OrganizationId, ctx.UserId,
            stage.Kind == CrmStageKind.Won ? AuditActions.CrmDealWon : AuditActions.CrmDealLost, nameof(CrmDeal), deal.Id.ToString(),
            null, deal.Describe(), deal.Number, correlationId));
        if (stage.Kind != CrmStageKind.Won)
        {
            return;
        }

        var settings = await SettingsAsync(db, ctx.OrganizationId, ct);
        if (settings.RepeatSaleDays is { } days)
        {
            var tz = await TimeZoneAsync(db, ctx.OrganizationId, ct);
            db.CrmActivities.Add(CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Task, CrmTarget.Deal, deal.Id,
                $"Предложить повторный заказ ({deal.Number})", "Сделка выиграна — напомните клиенту о новой партии.", DueInDays(nowUtc, tz, days),
                deal.ResponsibleUserId, ctx.UserId, nowUtc, source: CrmActivitySource.Automation));
        }
    }

    /// <summary>
    /// Б2 (D665): событие двигает открытую сделку на этап с этим названием — только вперёд по воронке и если правило включено.
    /// Возвращает true, если этап сменился.
    /// </summary>
    public static async Task<bool> AdvanceToAsync(IKnitErpDbContext db, AccessContext ctx, CrmDeal deal, string stageName, DateTime nowUtc, string? correlationId,
        string reason, CancellationToken ct)
    {
        if (deal.Outcome != CrmDealOutcome.Open || !(await SettingsAsync(db, ctx.OrganizationId, ct)).StageByEvents)
        {
            return false;
        }

        var stages = await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == deal.PipelineId && !s.IsArchived && s.Kind == CrmStageKind.Open)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);
        var target = stages.FindIndex(s => string.Equals(s.Name, stageName, StringComparison.OrdinalIgnoreCase));
        var current = stages.FindIndex(s => s.Id == deal.StageId);
        if (target < 0 || target <= current)
        {
            return false;
        }

        var from = current >= 0 ? stages[current].Name : null;
        db.CrmDealStageHistory.Add(deal.MoveTo(stages[target], ctx.UserId, nowUtc));
        db.AuditEntries.Add(AuditEntry.Create(nowUtc, ctx.OrganizationId, ctx.UserId, AuditActions.CrmDealStageChanged, nameof(CrmDeal), deal.Id.ToString(),
            from, stages[target].Name, reason, correlationId));
        return true;
    }

    /// <summary>Плановая дата закрытия по умолчанию (Б4, D667): сегодня + средняя длительность выигранных сделок (нет — 30 дней).</summary>
    public static async Task<DateOnly> DefaultCloseDateAsync(IKnitErpDbContext db, long organizationId, DateTime nowUtc, CancellationToken ct)
    {
        var won = await db.CrmDeals.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Outcome == CrmDealOutcome.Won && d.ClosedAtUtc != null)
            .OrderByDescending(d => d.Id).Take(200).Select(d => new { d.CreatedAtUtc, d.ClosedAtUtc }).ToListAsync(ct);
        var days = won.Count == 0 ? 30 : (int)Math.Ceiling(won.Average(d => (d.ClosedAtUtc!.Value - d.CreatedAtUtc).TotalDays));
        return Today(nowUtc, await TimeZoneAsync(db, organizationId, ct)).AddDays(Math.Clamp(days, 1, 365));
    }

    /// <summary>
    /// А6 (D664): ответственный для заявки из API, телефонии или импорта — по очереди между менеджерами продаж или на ответственного
    /// по умолчанию; никого нет — fallback (обычно автор запроса). Курсор очереди сохраняет вызывающий вместе с заявкой.
    /// </summary>
    public static async Task<long> AssigneeAsync(IKnitErpDbContext db, long organizationId, long fallback, CancellationToken ct)
    {
        var settings = await db.CrmSettings.SingleOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (settings is null)
        {
            return fallback;
        }

        if (settings.AssignMode == CrmAssignMode.RoundRobin)
        {
            var managers = await (from a in db.RoleAssignments.AsNoTracking()
                                  join r in db.Roles.AsNoTracking() on a.RoleId equals r.Id
                                  join m in db.OrganizationMembers.AsNoTracking() on new { a.UserId, a.OrganizationId } equals new { m.UserId, m.OrganizationId }
                                  where a.OrganizationId == organizationId && a.RevokedAtUtc == null && r.Code == SystemRoles.SalesManager
                                        && m.Status == MembershipStatus.Active
                                  select a.UserId).Distinct().ToListAsync(ct);
            if (settings.NextAssignee(managers) is { } next)
            {
                return next;
            }
        }

        return settings.DefaultResponsibleUserId ?? fallback;
    }
}
