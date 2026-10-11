using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

public sealed record ProductionSettingsDto(
    decimal TolerancePercent, bool PointsEnabled, int PointsTaskDone, int PointsTaskMissedWithReason, int PointsTaskMissedNoReason,
    int PointsPerDefectUnit, string? UpdatedBy, DateTime? UpdatedAtUtc, byte[]? RowVersion, bool CanEdit);

public sealed record ProductionSettingsCommand(
    decimal TolerancePercent, bool PointsEnabled, int PointsTaskDone, int PointsTaskMissedWithReason, int PointsTaskMissedNoReason, int PointsPerDefectUnit);

/// <summary>
/// Настройки производства организации (по запросу заказчика — правила задаёт организация, а не программа): допуск отклонения расхода
/// материалов и правила баллов за качество и дисциплину. Смотреть — «Организация: просмотр», менять — «Организация: изменение».
/// </summary>
public sealed class ProductionSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<ProductionSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationView, ct);
        var row = await db.ProductionSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
        var s = row ?? ProductionSettings.Default(ctx.OrganizationId);
        var by = s.UpdatedByUserId is { } u ? await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.DisplayName).SingleOrDefaultAsync(ct) : null;
        return new ProductionSettingsDto(s.TolerancePercent, s.PointsEnabled, s.PointsTaskDone, s.PointsTaskMissedWithReason, s.PointsTaskMissedNoReason,
            s.PointsPerDefectUnit, by, s.UpdatedAtUtc, row?.RowVersion, ctx.Permissions.Has(Permissions.OrganizationEdit));
    }

    /// <summary>Сохранение; <paramref name="rowVersion"/> — null, если настройки ещё не сохранялись.</summary>
    public async Task SaveAsync(ProductionSettingsCommand cmd, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.production.settings.{ctx.OrganizationId}", ct);
            var settings = await db.ProductionSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId, ct);
            if (settings is null)
            {
                if (rowVersion is not null)
                {
                    throw new ConcurrencyConflictException();
                }

                settings = ProductionSettings.Default(ctx.OrganizationId);
                db.ProductionSettings.Add(settings);
            }
            else
            {
                settings.EnsureVersion(settings.RowVersion, rowVersion ?? []);
            }

            var before = settings.Describe();
            settings.Update(cmd.TolerancePercent, cmd.PointsEnabled, cmd.PointsTaskDone, cmd.PointsTaskMissedWithReason, cmd.PointsTaskMissedNoReason,
                cmd.PointsPerDefectUnit, ctx.UserId, clock.UtcNow);
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.ProductionSettingsChanged, nameof(ProductionSettings),
                ctx.OrganizationId.ToString(), before, settings.Describe(), null, currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            // Неудача (проверка, конфликт) не оставляет вкладке отслеживаемую строку настроек.
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Настройки организации для расчётов других сервисов (без проверки прав — их проверяет вызывающий).</summary>
    internal static async Task<ProductionSettings> LoadAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.ProductionSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == organizationId, ct)
        ?? ProductionSettings.Default(organizationId);
}
