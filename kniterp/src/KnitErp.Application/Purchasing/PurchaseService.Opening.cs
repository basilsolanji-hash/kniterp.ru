using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Purchasing;

public sealed record SupplierOpeningBalanceDto(
    long Id, long LegalEntityId, string LegalEntity, DateOnly Date, decimal Amount, string? Comment, bool IsCancelled, string? CancelReason,
    string CreatedBy, byte[] RowVersion);

public sealed record SupplierOpeningBalancesDto(
    bool CanEdit, bool IsSupplier, decimal Debt, IReadOnlyList<(long Id, string Name)> LegalEntities, IReadOnlyList<SupplierOpeningBalanceDto> Rows);

/// <summary>
/// D133 (по образцу D1146): вводные остатки взаиморасчётов с поставщиком — в долг поставщику (расчёты с поставщиками, главный экран,
/// карточка контрагента) и в акт сверки. Смотреть — «Закупки: просмотр» и «Цены и суммы»; вводить и аннулировать — «Закупки: заказы
/// поставщикам и оплаты» и «Цены и суммы» (как оплаты поставщикам).
/// </summary>
public sealed partial class PurchaseService
{
    public async Task<SupplierOpeningBalancesDto> OpeningBalancesAsync(long supplierId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var supplier = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.Id == supplierId)
                           .Select(c => new { c.IsSupplier }).SingleOrDefaultAsync(ct)
                       ?? throw new NotFoundException("Контрагент");
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName).Select(e => new { e.Id, e.ShortName }).ToListAsync(ct);
        var rows = await (from b in db.SupplierOpeningBalances.AsNoTracking()
                          where b.OrganizationId == ctx.OrganizationId && b.SupplierId == supplierId
                          join u in db.Users.AsNoTracking() on b.CreatedByUserId equals u.Id
                          orderby b.IsCancelled, b.BalanceDate descending, b.Id descending
                          select new { b, u.DisplayName }).ToListAsync(ct);
        var names = entities.ToDictionary(e => e.Id, e => e.ShortName);
        var debt = (await ComputeBalancesAsync(ctx.OrganizationId, null, supplierId, ct)).SingleOrDefault()?.Debt ?? 0m;
        return new SupplierOpeningBalancesDto(ctx.Permissions.Has(Permissions.PurchaseEdit), supplier.IsSupplier, debt,
            entities.Select(e => (e.Id, e.ShortName)).ToList(),
            rows.Select(r => new SupplierOpeningBalanceDto(r.b.Id, r.b.LegalEntityId, names.GetValueOrDefault(r.b.LegalEntityId, "—"), r.b.BalanceDate,
                r.b.Amount, r.b.Comment, r.b.IsCancelled, r.b.CancelReason, r.DisplayName, r.b.RowVersion)).ToList());
    }

    public async Task<long> AddOpeningBalanceAsync(long supplierId, long legalEntityId, DateOnly date, decimal amount, string? comment, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var supplier = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.Id == supplierId, ct)
                       ?? throw new NotFoundException("Контрагент");
        if (!supplier.IsSupplier)
        {
            throw new BusinessRuleException("stock.document.not_supplier", $"«{supplier.Name}» не отмечен как поставщик.");
        }

        if (!await db.LegalEntities.AsNoTracking().AnyAsync(e => e.OrganizationId == ctx.OrganizationId && e.Id == legalEntityId, ct))
        {
            throw new NotFoundException("Юрлицо");
        }

        var balance = SupplierOpeningBalance.Create(ctx.OrganizationId, legalEntityId, supplierId, date, amount, comment, ctx.UserId, clock.UtcNow);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            // Два одновременных ввода по одному поставщику идут по очереди; UNIQUE в SQL Server — последняя защита.
            await db.LockAsync($"kniterp.opening.supplier.{ctx.OrganizationId}.{supplierId}", ct);
            if (await db.SupplierOpeningBalances.AsNoTracking().AnyAsync(b => b.OrganizationId == ctx.OrganizationId && b.SupplierId == supplierId
                                                                             && b.LegalEntityId == legalEntityId && !b.IsCancelled, ct))
            {
                throw new BusinessRuleException("purchase.opening.exists", "По этому юрлицу остаток уже введён — аннулируйте его с причиной и введите новый.");
            }

            db.SupplierOpeningBalances.Add(balance);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.SupplierOpeningBalanceChanged, nameof(SupplierOpeningBalance), balance.Id, null,
                $"поставщик {supplierId}, юрлицо {legalEntityId}, на {date:dd.MM.yyyy}: {amount:0.00}", comment);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        return balance.Id;
    }

    public async Task CancelOpeningBalanceAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var balance = await db.SupplierOpeningBalances.SingleOrDefaultAsync(b => b.OrganizationId == ctx.OrganizationId && b.Id == id, ct)
                      ?? throw new NotFoundException("Вводный остаток");
        try
        {
            balance.EnsureVersion(balance.RowVersion, rowVersion);
            balance.Cancel(reason, ctx.UserId, clock.UtcNow);
            Audit(ctx, AuditActions.SupplierOpeningBalanceChanged, nameof(SupplierOpeningBalance), id,
                $"{balance.Amount:0.00} на {balance.BalanceDate:dd.MM.yyyy}", "аннулирован", reason);
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }
}

/// <summary>D133: вводные остатки в расчётах с поставщиками — общий расчёт для долга поставщику, главного экрана и акта сверки.</summary>
public static class PurchaseOpeningBalances
{
    /// <summary>Действующие остатки на дату (по всем юрлицам): поставщик → (дата, сумма).</summary>
    public static async Task<Dictionary<long, List<(DateOnly Date, decimal Amount)>>> BySupplierAsync(
        IKnitErpDbContext db, long org, IReadOnlyCollection<long>? suppliers, DateOnly? asOf, CancellationToken ct) =>
        (await db.SupplierOpeningBalances.AsNoTracking()
            .Where(b => b.OrganizationId == org && !b.IsCancelled && (asOf == null || b.BalanceDate <= asOf)
                        && (suppliers == null || suppliers.Contains(b.SupplierId)))
            .Select(b => new { b.SupplierId, b.BalanceDate, b.Amount }).ToListAsync(ct))
        .GroupBy(b => b.SupplierId).ToDictionary(g => g.Key, g => g.Select(b => (b.BalanceDate, b.Amount)).ToList());

    /// <summary>Строки акта сверки: наш долг поставщику — в кредит (как поступление), наш аванс поставщику — в дебет (как оплата).</summary>
    public static async Task<IReadOnlyList<SettlementRowDto>> SettlementRowsAsync(
        IKnitErpDbContext db, long org, long supplierId, long entityId, DateOnly to, CancellationToken ct) =>
        (await db.SupplierOpeningBalances.AsNoTracking()
            .Where(b => b.OrganizationId == org && b.SupplierId == supplierId && b.LegalEntityId == entityId && !b.IsCancelled && b.BalanceDate <= to)
            .Select(b => new { b.BalanceDate, b.Amount }).ToListAsync(ct))
        .Select(b => new SettlementRowDto(b.BalanceDate, "Вводный остаток расчётов с поставщиком", b.Amount < 0 ? -b.Amount : 0, b.Amount > 0 ? b.Amount : 0, -1))
        .ToList();
}
