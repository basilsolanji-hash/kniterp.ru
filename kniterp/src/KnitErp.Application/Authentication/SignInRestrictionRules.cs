using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

/// <summary>Чем ограничение запретило вход или сессию (D127).</summary>
public enum RestrictionDenial
{
    None,
    Network,
    Schedule,
}

/// <summary>
/// D127: ограничение участника в организации и его оценка «сейчас». Владелец не ограничивается никогда — даже если запись осталась
/// (например, пользователя сделали Владельцем позже): организация не должна остаться без входа.
/// </summary>
internal sealed record RestrictionVerdict(SignInRestriction? Restriction, RestrictionDenial Denial, DateTime? WindowEndUtc)
{
    public static readonly RestrictionVerdict None = new(null, RestrictionDenial.None, null);
}

internal static class SignInRestrictionRules
{
    /// <summary>За сколько до конца времени работы сессия получает отметку «закроется» (проверка полная — раз в минуту).</summary>
    public static readonly TimeSpan ScheduleLookahead = TimeSpan.FromSeconds(90);

    /// <param name="enforceUnknownAddress">
    /// Вход и смена организации — по HTTP, адрес известен; неизвестный адрес при ограничении по сетям — отказ. Проверка вкладки
    /// Blazor может адреса не знать — тогда сети проверяет следующий HTTP-запрос (адрес cookie сверяется на каждом запросе).
    /// </param>
    public static async Task<RestrictionVerdict> EvaluateAsync(IKnitErpDbContext db, long userId, long organizationId, string? clientAddress,
        bool enforceUnknownAddress, DateTime nowUtc, CancellationToken ct)
    {
        var restriction = await db.SignInRestrictions.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId
                        && db.OrganizationMembers.Any(m => m.OrganizationId == organizationId && m.Id == r.MemberId && m.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (restriction is null || restriction.IsEmpty || await IsOwnerAsync(db, userId, organizationId, nowUtc, ct))
        {
            return RestrictionVerdict.None;
        }

        if (restriction.AllowedNetworks is not null && (clientAddress is not null || enforceUnknownAddress)
                                                    && !restriction.AllowsAddress(clientAddress))
        {
            return new RestrictionVerdict(restriction, RestrictionDenial.Network, null);
        }

        if (restriction.Window is { } window)
        {
            var zoneId = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
            var end = window.CurrentWindowEndUtc(nowUtc, Zone(zoneId));
            return end is null
                ? new RestrictionVerdict(restriction, RestrictionDenial.Schedule, null)
                : new RestrictionVerdict(restriction, RestrictionDenial.None, end);
        }

        return new RestrictionVerdict(restriction, RestrictionDenial.None, null);
    }

    public static TimeZoneInfo Zone(string zoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Конец сессии по времени работы: окно кончается скоро — не раньше чем через 30 с предупреждения.</summary>
    public static DateTime? ScheduleEnd(RestrictionVerdict verdict, DateTime nowUtc)
    {
        if (verdict.Restriction?.Window is null)
        {
            return null;
        }

        var earliest = nowUtc + UserSession.EndWarning;
        return verdict.WindowEndUtc switch
        {
            null => earliest,
            { } end when end - nowUtc <= ScheduleLookahead => end > earliest ? end : earliest,
            _ => null,
        };
    }

    public static async Task<bool> IsOwnerAsync(IKnitErpDbContext db, long userId, long organizationId, DateTime nowUtc, CancellationToken ct) =>
        await db.RoleAssignments
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= nowUtc && (a.ValidToUtc == null || a.ValidToUtc > nowUtc))
            .Join(db.Roles, a => a.RoleId, r => (long?)r.Id, (a, r) => r.Code)
            .AnyAsync(code => code == SystemRoles.Owner, ct);

    /// <summary>Текст причины для журнала.</summary>
    public static string Describe(RestrictionDenial denial) => denial switch
    {
        RestrictionDenial.Network => "адрес вне разрешённых сетей",
        RestrictionDenial.Schedule => "вне разрешённого времени работы",
        _ => string.Empty,
    };

    /// <summary>Сообщение пользователю при смене организации.</summary>
    public static BusinessRuleException Refusal(RestrictionDenial denial) => denial == RestrictionDenial.Network
        ? new BusinessRuleException("auth.restricted.network", "Вход с этого адреса запрещён администратором.")
        : new BusinessRuleException("auth.restricted.schedule", "Вход в это время запрещён администратором.");
}
