using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <param name="CalculatedMonths">Месяцы с расчётом заработка (новые сверху) — для Excel сверки; пусто, если нет права видеть заработок.</param>
public sealed record HrSettingsDto(PayrollSettingsValues Payroll, HrRulesValues Rules, bool CanEditPayroll, bool CanEditRules, byte[]? RowVersion,
    IReadOnlyList<DateOnly> CalculatedMonths);

/// <summary>
/// Настройки кадров и заработка организации (D216). Параметры заработка и коды ЗУП меняет тот, кто считает заработок
/// (`hr.payroll.calculate`); кадровые правила — тот, кто меняет реквизиты организации (`organization.requisites.edit`).
/// Каждое изменение — в журнал (без сумм ставок сотрудников).
/// </summary>
public sealed class HrSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<HrSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var canPayroll = ctx.Permissions.Has(Permissions.PayrollCalculate);
        var canRules = ctx.Permissions.Has(Permissions.OrganizationEdit);
        if (!canPayroll && !canRules && !ctx.Permissions.Has(Permissions.PayrollView) && !ctx.Permissions.Has(Permissions.EmployeeEdit))
        {
            await guard.DenyAsync(ctx, Permissions.OrganizationEdit, ct);
        }

        var s = await ForOrganizationAsync(db, ctx.OrganizationId, ct);
        IReadOnlyList<DateOnly> months = [];
        if (ctx.Permissions.Has(Permissions.PayrollView))
        {
            months = await db.PayrollRuns.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId)
                .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(24).Select(r => r.From).ToListAsync(ct);
        }

        return new HrSettingsDto(s.Payroll, s.Rules, canPayroll, canRules, s.Id == 0 ? null : s.RowVersion, months);
    }

    public async Task SavePayrollAsync(PayrollSettingsValues values, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PayrollCalculate, ct);
        var (s, isNew) = await LoadAsync(ctx, rowVersion, ct);
        string? change;
        try
        {
            change = s.SetPayroll(values, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges(); // отклонённое значение не должно уйти в базу со следующим сохранением вкладки
            throw;
        }

        if (isNew)
        {
            db.HrSettings.Add(s);
        }

        if (change is not null)
        {
            Audit(ctx, s, change, "заработок");
        }

        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Кадровые правила; «Кадровик закрывает табель» переключает право роли «Кадровик» — видно на странице ролей.</summary>
    public async Task SaveRulesAsync(HrRulesValues values, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var (s, isNew) = await LoadAsync(ctx, rowVersion, ct);
        if (isNew)
        {
            db.HrSettings.Add(s);
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        if (s.SetRules(values, clock.UtcNow) is { } change)
        {
            var role = await db.Roles.Include(r => r.Permissions)
                .SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Code == SystemRoles.HrOfficer && r.IsSystem, ct);
            role?.Toggle(Permissions.TimesheetClose, PermissionLevel.Full, values.HrOfficerClosesTimesheet);
            Audit(ctx, s, change, "кадровые правила");
        }

        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Настройки организации или значения по умолчанию (не сохраняются) — для сервисов кадров и заработка.</summary>
    internal static async Task<HrSettings> ForOrganizationAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.HrSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct)
        ?? HrSettings.Default(organizationId, DateTime.UtcNow);

    private async Task<(HrSettings Settings, bool IsNew)> LoadAsync(AccessContext ctx, byte[]? rowVersion, CancellationToken ct)
    {
        var s = await db.HrSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (s is null)
        {
            if (rowVersion is { Length: > 0 })
            {
                throw new ConcurrencyConflictException();
            }

            return (HrSettings.Default(ctx.OrganizationId, clock.UtcNow), true);
        }

        return rowVersion is not null && s.RowVersion.AsSpan().SequenceEqual(rowVersion) ? (s, false) : throw new ConcurrencyConflictException();
    }

    private void Audit(AccessContext ctx, HrSettings s, string change, string section) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.HrSettingsChanged, nameof(HrSettings),
            ctx.OrganizationId.ToString(), null, change, section, currentUser.CorrelationId));
}
