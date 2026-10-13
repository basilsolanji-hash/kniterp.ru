using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Automation;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Automation;

public sealed record AutomationSettingsDto(
    bool PaymentRemindersEnabled, int RemindDaysBefore, int OverdueRepeatDays, bool AutoCloseSalesOrders, bool AutoClosePurchaseOrders,
    bool RecurringOperationsEnabled, string? UpdatedBy, DateTime? UpdatedAtUtc, byte[]? RowVersion, bool CanEdit);

public sealed record AutomationSettingsCommand(
    bool PaymentRemindersEnabled, int RemindDaysBefore, int OverdueRepeatDays, bool AutoCloseSalesOrders, bool AutoClosePurchaseOrders,
    bool RecurringOperationsEnabled);

/// <summary>
/// D145: настройки автоматизации организации (каждая автоматика отключается — docs/automation.md §3). Смотреть — «Цены и суммы» и продажи
/// или закупки; менять — «Организация: изменение» (как настройки закупок D133). Изменение — в журнал аудита.
/// </summary>
public sealed class AutomationSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<AutomationSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var row = await db.AutomationSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
        var s = row ?? AutomationSettings.Default(ctx.OrganizationId);
        var by = s.UpdatedByUserId is { } u ? await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.DisplayName).SingleOrDefaultAsync(ct) : null;
        return new AutomationSettingsDto(s.PaymentRemindersEnabled, s.RemindDaysBefore, s.OverdueRepeatDays, s.AutoCloseSalesOrders, s.AutoClosePurchaseOrders,
            s.RecurringOperationsEnabled, by, s.UpdatedAtUtc, row?.RowVersion, ctx.Permissions.Has(Permissions.OrganizationEdit));
    }

    /// <summary>Сохранение; <paramref name="rowVersion"/> — null, если настройки ещё не сохранялись.</summary>
    public async Task SaveAsync(AutomationSettingsCommand cmd, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.automation.settings.{ctx.OrganizationId}", ct);
            var settings = await db.AutomationSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
            var created = settings is null;
            if (settings is null)
            {
                if (rowVersion is not null)
                {
                    throw new ConcurrencyConflictException();
                }

                settings = AutomationSettings.Default(ctx.OrganizationId);
            }
            else
            {
                settings.EnsureVersion(settings.RowVersion, rowVersion ?? []);
            }

            var before = settings.Describe();
            settings.Update(cmd.PaymentRemindersEnabled, cmd.RemindDaysBefore, cmd.OverdueRepeatDays, cmd.AutoCloseSalesOrders, cmd.AutoClosePurchaseOrders,
                cmd.RecurringOperationsEnabled, ctx.UserId, clock.UtcNow);
            if (created)
            {
                db.AutomationSettings.Add(settings); // только после проверок — иначе повтор вкладки вставит вторую строку
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.AutomationSettingsChanged,
                nameof(AutomationSettings), ctx.OrganizationId.ToString(), before, settings.Describe(), null, currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Настройки для фоновой автоматики и других сервисов (права проверяет вызывающий).</summary>
    internal static async Task<AutomationSettings> ForAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.AutomationSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == organizationId, ct)
        ?? AutomationSettings.Default(organizationId);

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        if (!ctx.Permissions.Has(Permissions.SalesView) && !ctx.Permissions.Has(Permissions.PurchaseView))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        return ctx;
    }
}
