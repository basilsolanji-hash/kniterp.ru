using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record SalesCancelReasonDto(long Id, string Name, bool IsArchived, byte[] RowVersion);

public sealed partial class SalesService
{
    /// <summary>
    /// D1137: отмена с причиной из справочника и пояснением; резерв снимается сам — отменённый заказ ничего не держит (D76).
    /// </summary>
    public Task CancelOrderAsync(long id, long reasonId, string? comment, byte[] rowVersion, CancellationToken ct = default) =>
        CancelCoreAsync(id, rowVersion, reasonId, comment, ct);

    /// <summary>
    /// Причины отмены организации. Пока справочник пуст, при первом открытии пользователем с правом изменения заказов он заполняется
    /// типовыми причинами (с записью в журнал) — чтобы отменить заказ можно было сразу.
    /// </summary>
    public async Task<IReadOnlyList<SalesCancelReasonDto>> CancelReasonsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        if (ctx.Permissions.Has(Permissions.SalesEdit) && !await db.SalesCancelReasons.AnyAsync(r => r.OrganizationId == ctx.OrganizationId, ct))
        {
            foreach (var name in SalesCancelReason.Defaults)
            {
                db.SalesCancelReasons.Add(SalesCancelReason.Create(ctx.OrganizationId, name));
            }

            Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesCancelReason), ctx.OrganizationId, null, string.Join(", ", SalesCancelReason.Defaults),
                "типовые причины отмены");
            await db.SaveChangesAsync(ct);
        }

        return await db.SalesCancelReasons.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && (includeArchived || !r.IsArchived))
            .OrderBy(r => r.IsArchived).ThenBy(r => r.Name).Select(r => new SalesCancelReasonDto(r.Id, r.Name, r.IsArchived, r.RowVersion)).ToListAsync(ct);
    }

    public async Task<long> CreateCancelReasonAsync(string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        if (await db.SalesCancelReasons.CountAsync(r => r.OrganizationId == ctx.OrganizationId, ct) >= SalesCancelReason.MaxReasons)
        {
            throw new BusinessRuleException("sales.cancel_reason.too_many", $"Причин отмены не больше {SalesCancelReason.MaxReasons}.");
        }

        var reason = SalesCancelReason.Create(ctx.OrganizationId, name);
        if (await db.SalesCancelReasons.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Name == reason.Name, ct))
        {
            throw new BusinessRuleException("sales.cancel_reason.duplicate", $"Причина «{reason.Name}» уже есть.");
        }

        db.SalesCancelReasons.Add(reason);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesCancelReason), reason.Id, null, reason.Name, "причина отмены");
        await db.SaveChangesAsync(ct);
        return reason.Id;
    }

    public async Task SetCancelReasonArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var reason = await db.SalesCancelReasons.SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Причина отмены");
        reason.EnsureVersion(reason.RowVersion, rowVersion);
        reason.SetArchived(archived);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesCancelReason), id, archived ? "действует" : "в архиве", archived ? "в архиве" : "действует", reason.Name);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Причина отмены заказа для карточки: название и пояснение.</summary>
    private async Task<string?> CancelReasonTextAsync(SalesOrder order, CancellationToken ct) =>
        order.CancelReasonId is { } rid
            ? await db.SalesCancelReasons.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Name).SingleOrDefaultAsync(ct)
              + (order.CancelComment is null ? "" : " — " + order.CancelComment)
            : null;
}
