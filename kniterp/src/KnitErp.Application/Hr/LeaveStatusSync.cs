using KnitErp.Application.Common;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <summary>
/// Статус «В отпуске» по проведённым приказам Т-6 (D210): отпуск начался — «В отпуске», закончился — «Работает».
/// Возвращает только тех, кого в отпуск отправил приказ: отпуск, поставленный кнопкой без приказа, не трогается.
/// Выполняется фоновой задачей приложения; в журнал — действие без пользователя с номером приказа.
/// </summary>
public sealed class LeaveStatusSync(IKnitErpDbContext db, IClock clock)
{
    /// <returns>Сколько карточек изменено.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var changed = 0;
        var orgs = await db.Organizations.AsNoTracking().Select(o => new { o.Id, o.TimeZoneId }).ToListAsync(ct);
        foreach (var org in orgs)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, org.TimeZoneId));
            var leaves = await (from h in db.EmploymentHistory.AsNoTracking()
                                join o in db.PersonnelOrders.AsNoTracking() on h.OrderId equals o.Id
                                where h.OrganizationId == org.Id && h.Kind == PersonnelOrderKind.Leave && h.EffectiveFrom <= today
                                select new { h.EmployeeId, h.EffectiveFrom, h.EffectiveTo, o.Number }).ToListAsync(ct);
            if (leaves.Count == 0)
            {
                continue;
            }

            var ids = leaves.Select(l => l.EmployeeId).Distinct().ToList();
            var employees = await db.Employees.Where(e => e.OrganizationId == org.Id && ids.Contains(e.Id) && e.Status != EmploymentStatus.Dismissed)
                .ToListAsync(ct);
            foreach (var e in employees)
            {
                var own = leaves.Where(l => l.EmployeeId == e.Id).ToList();
                var current = own.FirstOrDefault(l => l.EffectiveTo >= today);
                var ended = own.Where(l => l.EffectiveTo < today).MaxBy(l => l.EffectiveTo);
                var toLeave = e.Status == EmploymentStatus.Working && current is not null;
                // Возврат — если последний закончившийся приказ закончился позже, чем мог начаться ручной отпуск: берём только
                // отпуск, закончившийся за последние 31 день, чтобы не трогать давние ручные отметки.
                var back = e.Status == EmploymentStatus.OnLeave && current is null && ended is not null && ended.EffectiveTo >= today.AddDays(-31);
                if (!toLeave && !back)
                {
                    continue;
                }

                var change = e.SetOnLeave(toLeave);
                db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org.Id, null, AuditActions.EmployeeChanged, nameof(Employee), e.Id.ToString(),
                    change.Before, change.After, $"Автоматически по приказу № {(toLeave ? current!.Number : ended!.Number)}", null));
                changed++;
            }

            await db.SaveChangesAsync(ct);
        }

        return changed;
    }
}
