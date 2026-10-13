using KnitErp.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Конфликт правки в CRM (D698, ТЗ §8): «Сделку изменил {кто}, обновите». Кто и когда — из последней записи журнала аудита по объекту
/// (журнал пишется при каждой правке); нет записи — общее сообщение.
/// </summary>
public static class CrmConflict
{
    public static async Task<T> EnsureVersionAsync<T>(IKnitErpDbContext db, long organizationId, T entity, string entityType, long id, string objectName,
        byte[] actual, byte[] expected, CancellationToken ct)
    {
        if (actual.AsSpan().SequenceEqual(expected))
        {
            return entity;
        }

        var key = id.ToString();
        var last = await (from a in db.AuditEntries.AsNoTracking()
                          join u in db.Users.AsNoTracking() on a.ActorUserId equals u.Id
                          where a.OrganizationId == organizationId && a.EntityType == entityType && a.EntityId == key
                          orderby a.Id descending
                          select new { u.DisplayName, a.OccurredAtUtc }).FirstOrDefaultAsync(ct);
        var tz = await CrmEvents.TimeZoneAsync(db, organizationId, ct);
        throw new ConcurrencyConflictException(objectName, last?.DisplayName,
            last is null ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(last.OccurredAtUtc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(tz)));
    }
}
