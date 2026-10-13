using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record CustomerOpeningBalanceDto(
    long Id, long LegalEntityId, string LegalEntity, DateOnly Date, decimal Amount, string? Comment, bool IsCancelled, string? CancelReason,
    string CreatedBy, byte[] RowVersion);

public sealed record CustomerOpeningBalancesDto(bool CanEdit, IReadOnlyList<(long Id, string Name)> LegalEntities, IReadOnlyList<CustomerOpeningBalanceDto> Rows);

/// <summary>
/// D1146: вводные остатки взаиморасчётов с покупателем — в долг покупателя (кредитный контроль, дебиторка, баланс в заказе) и в акт
/// сверки. Смотреть — «Продажи: просмотр» и «Цены и суммы»; вводить и аннулировать — «Продажи: заказы и оплаты» и «Цены и суммы».
/// </summary>
public sealed partial class SalesService
{
    public async Task<CustomerOpeningBalancesDto> OpeningBalancesAsync(long customerId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        await EnsureCustomerAsync(ctx, customerId, ct);
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName).Select(e => new { e.Id, e.ShortName }).ToListAsync(ct);
        var rows = await (from b in db.CustomerOpeningBalances.AsNoTracking()
                          where b.OrganizationId == ctx.OrganizationId && b.CustomerId == customerId
                          join u in db.Users.AsNoTracking() on b.CreatedByUserId equals u.Id
                          orderby b.IsCancelled, b.BalanceDate descending, b.Id descending
                          select new { b, u.DisplayName }).ToListAsync(ct);
        var names = entities.ToDictionary(e => e.Id, e => e.ShortName);
        return new CustomerOpeningBalancesDto(ctx.Permissions.Has(Permissions.SalesEdit), entities.Select(e => (e.Id, e.ShortName)).ToList(),
            rows.Select(r => new CustomerOpeningBalanceDto(r.b.Id, r.b.LegalEntityId, names.GetValueOrDefault(r.b.LegalEntityId, "—"), r.b.BalanceDate,
                r.b.Amount, r.b.Comment, r.b.IsCancelled, r.b.CancelReason, r.DisplayName, r.b.RowVersion)).ToList());
    }

    public async Task<long> AddOpeningBalanceAsync(long customerId, long legalEntityId, DateOnly date, decimal amount, string? comment, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        await EnsureCustomerAsync(ctx, customerId, ct);
        if (!await db.LegalEntities.AsNoTracking().AnyAsync(e => e.OrganizationId == ctx.OrganizationId && e.Id == legalEntityId, ct))
        {
            throw new NotFoundException("Юрлицо");
        }

        var balance = CustomerOpeningBalance.Create(ctx.OrganizationId, legalEntityId, customerId, date, amount, comment, ctx.UserId, clock.UtcNow);
        if (await db.CustomerOpeningBalances.AsNoTracking().AnyAsync(b => b.OrganizationId == ctx.OrganizationId && b.CustomerId == customerId
                                                                         && b.LegalEntityId == legalEntityId && !b.IsCancelled, ct))
        {
            throw new BusinessRuleException("sales.opening.exists", "По этому юрлицу остаток уже введён — аннулируйте его с причиной и введите новый.");
        }

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            db.CustomerOpeningBalances.Add(balance);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.CustomerOpeningBalanceChanged, nameof(CustomerOpeningBalance), balance.Id, null,
                $"покупатель {customerId}, юрлицо {legalEntityId}, на {date:dd.MM.yyyy}: {amount:0.00}", comment);
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
        var balance = await db.CustomerOpeningBalances.SingleOrDefaultAsync(b => b.OrganizationId == ctx.OrganizationId && b.Id == id, ct)
                      ?? throw new NotFoundException("Вводный остаток");
        balance.EnsureVersion(balance.RowVersion, rowVersion);
        balance.Cancel(reason, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.CustomerOpeningBalanceChanged, nameof(CustomerOpeningBalance), id, $"{balance.Amount:0.00} на {balance.BalanceDate:dd.MM.yyyy}",
            "аннулирован", reason);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureCustomerAsync(AccessContext ctx, long customerId, CancellationToken ct)
    {
        if (!await db.Counterparties.AsNoTracking().AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.Id == customerId, ct))
        {
            throw new NotFoundException("Контрагент");
        }
    }
}

/// <summary>D1146: вводные остатки в расчётах — общий расчёт для долга покупателя и акта сверки.</summary>
public static class SalesOpeningBalances
{
    /// <summary>Действующие остатки на дату (по всем юрлицам): покупатель → (дата, сумма).</summary>
    public static async Task<Dictionary<long, List<(DateOnly Date, decimal Amount)>>> ByCustomerAsync(
        IKnitErpDbContext db, long org, IReadOnlyCollection<long>? customers, DateOnly? asOf, CancellationToken ct) =>
        (await db.CustomerOpeningBalances.AsNoTracking()
            .Where(b => b.OrganizationId == org && !b.IsCancelled && (asOf == null || b.BalanceDate <= asOf)
                        && (customers == null || customers.Contains(b.CustomerId)))
            .Select(b => new { b.CustomerId, b.BalanceDate, b.Amount }).ToListAsync(ct))
        .GroupBy(b => b.CustomerId).ToDictionary(g => g.Key, g => g.Select(b => (b.BalanceDate, b.Amount)).ToList());

    /// <summary>Строки акта сверки: долг покупателя — в дебет, его аванс — в кредит.</summary>
    public static async Task<IReadOnlyList<SettlementRowDto>> SettlementRowsAsync(
        IKnitErpDbContext db, long org, long customerId, long entityId, DateOnly to, CancellationToken ct) =>
        (await db.CustomerOpeningBalances.AsNoTracking()
            .Where(b => b.OrganizationId == org && b.CustomerId == customerId && b.LegalEntityId == entityId && !b.IsCancelled && b.BalanceDate <= to)
            .Select(b => new { b.BalanceDate, b.Amount }).ToListAsync(ct))
        .Select(b => new SettlementRowDto(b.BalanceDate, "Вводный остаток расчётов", b.Amount > 0 ? b.Amount : 0, b.Amount < 0 ? -b.Amount : 0, -1)).ToList();
}
