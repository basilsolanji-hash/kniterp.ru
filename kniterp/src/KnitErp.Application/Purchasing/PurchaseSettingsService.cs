using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Purchasing;

public sealed record PurchaseSettingsDto(decimal OverReceiptPercent, string? UpdatedBy, DateTime? UpdatedAtUtc, byte[]? RowVersion, bool CanEdit);

/// <summary>
/// Настройки закупок организации (D133, решение D132 п. 3): допуск поступления сверх заказа поставщику. Смотреть — «Закупки: просмотр»,
/// менять — «Организация: изменение» (как настройки производства): менеджер закупок сам себе допуск не расширяет. Изменение — в журнал аудита.
/// </summary>
public sealed class PurchaseSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<PurchaseSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseView, ct);
        var row = await db.PurchaseSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
        var s = row ?? PurchaseSettings.Default(ctx.OrganizationId);
        var by = s.UpdatedByUserId is { } u ? await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.DisplayName).SingleOrDefaultAsync(ct) : null;
        return new PurchaseSettingsDto(s.OverReceiptPercent, by, s.UpdatedAtUtc, row?.RowVersion, ctx.Permissions.Has(Permissions.OrganizationEdit));
    }

    /// <summary>Сохранение; <paramref name="rowVersion"/> — null, если настройки ещё не сохранялись.</summary>
    public async Task SaveAsync(decimal overReceiptPercent, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseView, ct);
        await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.purchase.settings.{ctx.OrganizationId}", ct);
            var settings = await db.PurchaseSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
            if (settings is null)
            {
                if (rowVersion is not null)
                {
                    throw new ConcurrencyConflictException();
                }

                settings = PurchaseSettings.Default(ctx.OrganizationId);
                db.PurchaseSettings.Add(settings);
            }
            else
            {
                settings.EnsureVersion(settings.RowVersion, rowVersion ?? []);
            }

            var before = settings.Describe();
            settings.Update(overReceiptPercent, ctx.UserId, clock.UtcNow);
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.PurchaseSettingsChanged, nameof(PurchaseSettings),
                ctx.OrganizationId.ToString(), before, settings.Describe(), null, currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Допуск поступления сверх заказа для проверок других сервисов (без проверки прав — их проверяет вызывающий).</summary>
    internal static async Task<decimal> OverReceiptPercentAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.PurchaseSettings.AsNoTracking().Where(s => s.OrganizationId == organizationId).Select(s => (decimal?)s.OverReceiptPercent)
            .SingleOrDefaultAsync(ct) ?? 0m;
}
