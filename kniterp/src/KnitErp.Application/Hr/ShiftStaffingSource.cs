using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <summary>
/// «Смена сегодня» на главном экране (контракт D111 базовой ветки, реализация — D221). По плану — у кого по графику работы (без графика —
/// пятидневка) сегодня рабочий день; не вышли — из запланированных те, у кого в табеле на сегодня неявка (больничный, прогул, неявка без
/// причины…) или отпуск по приказу Т-6 либо по отметке «В отпуске»; вышли — запланированные без неявки и вышедшие сверх графика
/// (рабочий код в табеле в нерабочий день). Видно с правом на табель, в пределах его подразделений; без прав или если в организации
/// не ведутся ни табели, ни графики — null (блок не показывается). Имена — «Фамилия И.», без причины неявки.
/// </summary>
public sealed class ShiftStaffingSource : IShiftStaffingSource
{
    /// <summary>Сколько имён не вышедших показать на главном.</summary>
    public const int MaxNames = 5;

    public async Task<ShiftStaffingDto?> TodayAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.TimesheetEdit) && !ctx.Permissions.Has(Permissions.TimesheetClose))
        {
            return null;
        }

        var org = ctx.OrganizationId;
        if (!await db.EmployeeSchedules.AnyAsync(a => a.OrganizationId == org, ct)
            && !await db.Timesheets.AnyAsync(t => t.OrganizationId == org && t.Year == today.Year && t.Month == today.Month, ct))
        {
            return null;
        }

        var visible = await VisibleAsync(db, ctx, ct);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == org && e.HiredOn <= today && (e.DismissedOn == null || e.DismissedOn >= today))
            .Select(e => new { e.Id, e.DepartmentId, e.LastName, e.FirstName, e.Status }).ToListAsync(ct);
        employees = employees.Where(e => visible is null || visible.Contains(e.DepartmentId)).ToList();
        if (employees.Count == 0)
        {
            return null;
        }

        var ids = employees.Select(e => e.Id).ToList();
        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CountryCode).SingleAsync(ct);
        var holidays = ProductionCalendar.Holidays(country, today, today.AddDays(1)).Select(h => h.Date).ToHashSet();
        var schedules = (await (from a in db.EmployeeSchedules.AsNoTracking()
                                join s in db.WorkSchedules.AsNoTracking() on a.ScheduleId equals s.Id
                                where a.OrganizationId == org && ids.Contains(a.EmployeeId) && a.ValidFrom <= today
                                select new { a.EmployeeId, a.ValidFrom, Schedule = s }).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.ValidFrom)!.Schedule);
        var onLeave = (await db.EmploymentHistory.AsNoTracking()
                .Where(h => h.OrganizationId == org && ids.Contains(h.EmployeeId) && h.Kind == PersonnelOrderKind.Leave
                            && h.EffectiveFrom <= today && h.EffectiveTo >= today)
                .Select(h => h.EmployeeId).ToListAsync(ct)).ToHashSet();

        // Сегодняшний день — из последней версии табеля подразделения за месяц.
        var sheets = (await db.Timesheets.AsNoTracking().Where(t => t.OrganizationId == org && t.Year == today.Year && t.Month == today.Month)
                .Select(t => new { t.Id, t.DepartmentId, t.Version }).ToListAsync(ct))
            .GroupBy(t => t.DepartmentId).Select(g => g.MaxBy(t => t.Version)!.Id).ToList();
        var days = (await db.TimesheetDays.AsNoTracking().Where(d => sheets.Contains(d.TimesheetId) && d.Date == today && ids.Contains(d.EmployeeId))
                .Select(d => new { d.EmployeeId, d.Code }).ToListAsync(ct))
            .GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.First().Code);

        int planned = 0, onShift = 0;
        var absent = new List<string>();
        foreach (var e in employees.OrderBy(e => e.LastName).ThenBy(e => e.FirstName))
        {
            var plan = schedules.TryGetValue(e.Id, out var schedule) ? schedule.PlanFor(today, holidays) : WorkSchedule.DefaultPlan(today, holidays);
            var code = days.GetValueOrDefault(e.Id);
            var worked = code is not null && TimesheetCodes.IsWorked(code);
            if (!plan.Work)
            {
                if (worked)
                {
                    planned++; // вышел сверх графика — на смене и в плане дня
                    onShift++;
                }

                continue;
            }

            planned++;
            var away = onLeave.Contains(e.Id) || e.Status == EmploymentStatus.OnLeave
                       || (code is not null && !worked && code != TimesheetCodes.Weekend);
            if (away)
            {
                absent.Add(e.FirstName.Length > 0 ? $"{e.LastName} {e.FirstName[0]}." : e.LastName);
            }
            else
            {
                onShift++;
            }
        }

        return planned == 0 ? null : new ShiftStaffingDto(onShift, planned, absent.Count, "hr/timesheet", absent.Take(MaxNames).ToList());
    }

    /// <summary>Подразделения в области права на табель: заполнение или закрытие; null — все.</summary>
    private static async Task<HashSet<long>?> VisibleAsync(IKnitErpDbContext db, AccessContext ctx, CancellationToken ct)
    {
        var edit = ctx.Permissions.Has(Permissions.TimesheetEdit) ? await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.TimesheetEdit, ct) : [];
        var close = ctx.Permissions.Has(Permissions.TimesheetClose) ? await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.TimesheetClose, ct) : [];
        return edit is null || close is null ? null : [.. edit, .. close];
    }
}
