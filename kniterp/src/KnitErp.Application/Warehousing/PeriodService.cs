using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

public sealed record PeriodDto(DateOnly? ClosedThrough, string? ChangedBy, DateTime? ChangedAtUtc, bool CanManage, byte[]? RowVersion);

/// <summary>
/// Закрытый период склада: после сверки за месяц Владелец закрывает его, и документы с датой по границу включительно
/// не проводятся и не сторнируются. Открыть период назад можно только с причиной (допущение D44).
/// </summary>
public sealed class PeriodService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<PeriodDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        string[] viewers = [Permissions.ClosedPeriodReopen, Permissions.WarehouseReportView, Permissions.WarehouseDocumentCreate,
            Permissions.WarehouseDocumentPost, Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove];
        if (!viewers.Any(ctx.Permissions.Has))
        {
            await guard.DenyAsync(ctx, Permissions.WarehouseReportView, ct);
        }

        var p = await db.PeriodClosures.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        var by = p is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == p.ChangedByUserId).Select(u => u.DisplayName).SingleAsync(ct);
        return new PeriodDto(p?.ClosedThrough, by, p?.ChangedAtUtc, ctx.Permissions.Has(Permissions.ClosedPeriodReopen), p?.RowVersion);
    }

    public async Task CloseAsync(DateOnly through, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ClosedPeriodReopen, ct);
        var p = await LoadAsync(ctx, rowVersion, ct);
        var before = p.ClosedThrough;
        p.CloseThrough(through, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.PeriodClosed, Text(before), Text(p.ClosedThrough), null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Открытие: новая граница раньше текущей или null — закрытого периода больше нет.</summary>
    public async Task ReopenAsync(DateOnly? newClosedThrough, string? reason, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ClosedPeriodReopen, ct);
        var p = await LoadAsync(ctx, rowVersion, ct);
        var before = p.ClosedThrough;
        var text = p.Reopen(newClosedThrough, reason, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.PeriodReopened, Text(before), Text(p.ClosedThrough), text);
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<PeriodClosure> LoadAsync(AccessContext ctx, byte[]? rowVersion, CancellationToken ct)
    {
        var p = await db.PeriodClosures.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (p is null)
        {
            if (rowVersion is not null)
            {
                throw new ConcurrencyConflictException();
            }

            p = PeriodClosure.Start(ctx.OrganizationId, ctx.UserId, clock.UtcNow);
            db.PeriodClosures.Add(p);
            return p;
        }

        return rowVersion is null ? throw new ConcurrencyConflictException() : p.EnsureVersion(p.RowVersion, rowVersion);
    }

    private static string Text(DateOnly? d) => d is { } x ? $"закрыт по {x:dd.MM.yyyy}" : "открыт";

    private void Audit(AccessContext ctx, string action, string before, string after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(PeriodClosure),
            ctx.OrganizationId.ToString(), before, after, reason, currentUser.CorrelationId));
}

/// <summary>Проверка закрытого периода перед записью движений. Второй рубеж — триггер базы tr_stock_movements_closed_period.</summary>
internal static class ClosedPeriod
{
    /// <summary>По какую дату включительно закрыт период; null — не закрывался.</summary>
    public static async Task<DateOnly?> ClosedThroughAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct) =>
        await db.PeriodClosures.AsNoTracking().Where(p => p.OrganizationId == organizationId)
            .Select(p => (DateOnly?)p.ClosedThrough).SingleOrDefaultAsync(ct);

    public static async Task EnsureOpenAsync(IKnitErpDbContext db, long organizationId, DateOnly date, CancellationToken ct)
    {
        var through = await db.PeriodClosures.AsNoTracking().Where(p => p.OrganizationId == organizationId)
            .Select(p => p.ClosedThrough).SingleOrDefaultAsync(ct);
        if (through is { } t && date <= t)
        {
            throw Closed(t, date);
        }
    }

    /// <summary>Сохранение движений: если период закрыли в эту же секунду, ответ триггера превращается в понятную ошибку.</summary>
    public static async Task SaveAsync(IKnitErpDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.GetBaseException().Message.StartsWith("Период закрыт", StringComparison.Ordinal))
        {
            throw new BusinessRuleException("period.closed", "Период закрыт — документ с такой датой провести нельзя.");
        }
    }

    public static BusinessRuleException Closed(DateOnly through, DateOnly date) =>
        new("period.closed", $"Период закрыт по {through:dd.MM.yyyy}: документ с датой {date:dd.MM.yyyy} провести нельзя. Открыть период может Владелец.");
}
