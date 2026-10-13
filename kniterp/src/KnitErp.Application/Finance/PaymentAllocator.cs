using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

/// <summary>Разноска платежа по заказу. RowVersion — для снятия.</summary>
public sealed record PaymentAllocationDto(long Id, long OrderId, string OrderNumber, decimal Amount, DateTime CreatedAtUtc, string CreatedBy, byte[] RowVersion);

/// <summary>Заказ контрагента, по которому ещё есть что оплатить: Total — сумма заказа, Paid — уже разнесено.</summary>
public sealed record OpenOrderDto(long Id, string Number, DateOnly Date, DateOnly? Due, decimal Total, decimal Paid)
{
    public decimal Outstanding => Total - Paid;
}

/// <summary>Платёж с разносками и заказами того же контрагента и юрлица, на которые можно разнести остаток.</summary>
public sealed record PaymentAllocationsDto(
    long PaymentId, string Number, DateOnly Date, string Counterparty, decimal Amount, decimal Allocated, bool Posted,
    IReadOnlyList<PaymentAllocationDto> Allocations, IReadOnlyList<OpenOrderDto> OpenOrders)
{
    /// <summary>Не разнесено — аванс (переплата) контрагента.</summary>
    public decimal Unallocated => Posted ? Amount - Allocated : 0;
}

/// <summary>Сумма, оплаченная по заказам: действующие разноски проведённых платежей (D85).</summary>
public static class PaidByOrder
{
    public static IQueryable<CustomerPaymentAllocation> Sales(IKnitErpDbContext db, long org) =>
        db.CustomerPaymentAllocations.AsNoTracking().Where(a => a.OrganizationId == org && a.RemovedAtUtc == null
            && db.CustomerPayments.Any(p => p.Id == a.PaymentId && p.Status == CustomerPaymentStatus.Posted));

    public static IQueryable<SupplierPaymentAllocation> Purchases(IKnitErpDbContext db, long org) =>
        db.SupplierPaymentAllocations.AsNoTracking().Where(a => a.OrganizationId == org && a.RemovedAtUtc == null
            && db.SupplierPayments.Any(p => p.Id == a.PaymentId && p.Status == SupplierPaymentStatus.Posted));

    public static Task<Dictionary<long, decimal>> SalesAsync(IKnitErpDbContext db, long org, IReadOnlyCollection<long> orderIds, CancellationToken ct) =>
        Sales(db, org).Where(a => orderIds.Contains(a.OrderId)).GroupBy(a => a.OrderId)
            .Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

    public static Task<Dictionary<long, decimal>> PurchasesAsync(IKnitErpDbContext db, long org, IReadOnlyCollection<long> orderIds, CancellationToken ct) =>
        Purchases(db, org).Where(a => orderIds.Contains(a.OrderId)).GroupBy(a => a.OrderId)
            .Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
}

/// <summary>
/// Разноска платежей по заказам (D85) — одна логика для покупателей (Sales = true) и поставщиков. Права проверяет вызывающий
/// сервис. Правила: заказ того же контрагента, подтверждён или закрыт; если у платежа указан счёт или касса — того же юрлица,
/// что заказ; на заказ — не больше его остатка к оплате; всего — не больше суммы платежа. Не разнесённое — аванс.
/// Все изменения разносок одного контрагента идут по очереди (блокировка контрагента), поэтому одновременная разноска
/// двух платежей на один заказ не превысит его сумму.
/// D133 (D132 п. 2): дата разноски — поздняя из дат оплаты и заказа (раньше обоих зачёт состояться не мог); разноска, её изменение
/// и снятие с такой датой в закрытом периоде запрещены (`period.closed`), автоматическая разноска и зачёт авансов такие пары пропускают.
/// </summary>
internal sealed class PaymentAllocator(IKnitErpDbContext db, IClock clock, ICurrentUser currentUser, bool sales)
{
    // Свойства с init, а не позиционные записи: EF переводит в SQL фильтры поверх такой проекции.
    private sealed record PaymentInfo
    {
        public long Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateOnly Date { get; init; }
        public long CounterpartyId { get; init; }
        public decimal Amount { get; init; }
        public bool Posted { get; init; }
        public long? AccountId { get; init; }
    }

    private sealed record OrderInfo
    {
        public long Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateOnly Date { get; init; }
        public DateOnly? Due { get; init; }
        public long CounterpartyId { get; init; }
        public bool Open { get; init; }
        public long LegalEntityId { get; init; }
        public decimal Total { get; init; }
    }

    private string Side => sales ? "customer" : "supplier";

    public async Task<PaymentAllocationsDto> GetAsync(AccessContext ctx, long paymentId, CancellationToken ct)
    {
        var p = await PaymentAsync(ctx, paymentId, ct);
        var allocations = await AllocationsQuery(ctx.OrganizationId).Where(a => a.PaymentId == paymentId && a.RemovedAtUtc == null)
            .OrderBy(a => a.Id).ToListAsync(ct);
        var orderIds = allocations.Select(a => a.OrderId).ToList();
        var numbers = await OrderNumbersAsync(ctx.OrganizationId, orderIds, ct);
        var userIds = allocations.Select(a => a.CreatedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var counterparty = await db.Counterparties.AsNoTracking().Where(c => c.Id == p.CounterpartyId).Select(c => c.Name).SingleAsync(ct);
        var open = p.Posted ? await OpenOrdersAsync(ctx, p, ct) : [];
        return new PaymentAllocationsDto(p.Id, p.Number, p.Date, counterparty, p.Amount, allocations.Sum(a => a.Amount), p.Posted,
            allocations.Select(a => new PaymentAllocationDto(a.Id, a.OrderId, numbers[a.OrderId], a.Amount, a.CreatedAtUtc, users[a.CreatedByUserId],
                a.RowVersion)).ToList(),
            open.Where(o => o.Outstanding > 0).ToList());
    }

    /// <summary>Разнести часть платежа на заказ. Повторная разноска на тот же заказ заменяет прежнюю суммой обеих.</summary>
    public async Task AllocateAsync(AccessContext ctx, long paymentId, long orderId, decimal amount, CancellationToken ct)
    {
        var p = await PaymentAsync(ctx, paymentId, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(LockName(ctx, p.CounterpartyId), ct);
        var order = await OrderAsync(ctx, orderId, ct);
        await EnsureAllowedAsync(p, order, ct);
        EnsureOpen(await ClosedThroughAsync(ctx, ct), p, order);
        var paidByOrder = await PaidAsync(ctx.OrganizationId, [orderId], ct);
        var outstanding = order.Total - paidByOrder.GetValueOrDefault(orderId);
        if (amount > outstanding)
        {
            throw new BusinessRuleException("payment.allocation.order_exceeded",
                $"По заказу {order.Number} осталось оплатить {Math.Max(outstanding, 0):0.00} — больше на него не разнести.");
        }

        if (amount > await UnallocatedAsync(ctx, p, ct))
        {
            throw new BusinessRuleException("payment.allocation.payment_exceeded",
                $"В оплате {p.Number} не разнесено меньше {amount:0.00}. Снимите лишнюю разноску или уменьшите сумму.");
        }

        await AddAsync(ctx, p, order, amount, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Разнести весь остаток платежа по открытым заказам: сначала с ранним сроком, затем по дате. Возвращает разнесённую сумму.</summary>
    public async Task<decimal> AutoAllocateAsync(AccessContext ctx, long paymentId, CancellationToken ct)
    {
        var p = await PaymentAsync(ctx, paymentId, ct);
        EnsurePosted(p);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(LockName(ctx, p.CounterpartyId), ct);
        var left = await UnallocatedAsync(ctx, p, ct);
        var closed = await ClosedThroughAsync(ctx, ct);
        var open = (await OpenOrdersAsync(ctx, p, ct)).Where(o => !IsClosed(closed, p.Date, o.Date)).ToList();
        var plan = PaymentDistribution.Distribute(left, open.Select(o => (o.Id, o.Outstanding)));
        foreach (var (orderId, part) in plan)
        {
            await AddAsync(ctx, p, await OrderAsync(ctx, orderId, ct), part, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return plan.Sum(x => x.Amount);
    }

    /// <summary>Зачесть авансы контрагента в заказ: не разнесённые остатки его платежей — от старых к новым. Возвращает зачтённую сумму.</summary>
    public async Task<decimal> ApplyAdvancesAsync(AccessContext ctx, long orderId, CancellationToken ct)
    {
        var order = await OrderAsync(ctx, orderId, ct);
        if (!order.Open)
        {
            throw new BusinessRuleException("payment.allocation.order_state", $"Заказ {order.Number} не подтверждён или отменён.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(LockName(ctx, order.CounterpartyId), ct);
        var left = order.Total - (await PaidAsync(ctx.OrganizationId, [orderId], ct)).GetValueOrDefault(orderId);
        var applied = 0m;
        var closed = await ClosedThroughAsync(ctx, ct);
        foreach (var p in (await AdvancesAsync(ctx, order, ct)).Where(a => !IsClosed(closed, a.Payment.Date, order.Date)))
        {
            if (left <= 0)
            {
                break;
            }

            var part = Math.Min(left, p.Unallocated);
            await AddAsync(ctx, p.Payment, order, part, ct);
            left -= part;
            applied += part;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return applied;
    }

    /// <summary>Сколько авансов контрагента можно зачесть в заказ (для подсказки в карточке заказа).</summary>
    public async Task<decimal> AvailableAdvancesAsync(AccessContext ctx, long orderId, CancellationToken ct)
    {
        var order = await OrderAsync(ctx, orderId, ct);
        if (!order.Open)
        {
            return 0;
        }

        var closed = await ClosedThroughAsync(ctx, ct);
        return (await AdvancesAsync(ctx, order, ct)).Where(a => !IsClosed(closed, a.Payment.Date, order.Date)).Sum(a => a.Unallocated);
    }

    public async Task RemoveAsync(AccessContext ctx, long allocationId, byte[] rowVersion, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        PaymentAllocation allocation = sales
            ? await db.CustomerPaymentAllocations.SingleOrDefaultAsync(a => a.Id == allocationId && a.OrganizationId == ctx.OrganizationId, ct)
              ?? throw new NotFoundException("Разноска")
            : await db.SupplierPaymentAllocations.SingleOrDefaultAsync(a => a.Id == allocationId && a.OrganizationId == ctx.OrganizationId, ct)
              ?? throw new NotFoundException("Разноска");
        allocation.EnsureVersion(allocation.RowVersion, rowVersion);
        var p = await PaymentAsync(ctx, allocation.PaymentId, ct);
        await db.LockAsync(LockName(ctx, p.CounterpartyId), ct);
        EnsureOpen(await ClosedThroughAsync(ctx, ct), p, await OrderAsync(ctx, allocation.OrderId, ct));
        allocation.Remove(ctx.UserId, clock.UtcNow);
        var number = (await OrderNumbersAsync(ctx.OrganizationId, [allocation.OrderId], ct))[allocation.OrderId];
        Audit(ctx, AuditActions.PaymentAllocationRemoved, p.Id, $"{allocation.Amount:0.00}", null, $"{p.Number} → {number}");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Разноска при записи оплаты с заказом: на заказ — не больше его остатка, остальное — аванс. Вызывается внутри транзакции оплаты.</summary>
    public async Task AllocateNewAsync(AccessContext ctx, long paymentId, long counterpartyId, long orderId, decimal amount, CancellationToken ct)
    {
        await db.LockAsync(LockName(ctx, counterpartyId), ct);
        var order = await OrderAsync(ctx, orderId, ct);
        var outstanding = order.Total - (await PaidAsync(ctx.OrganizationId, [orderId], ct)).GetValueOrDefault(orderId);
        var part = Math.Min(amount, outstanding);
        if (part > 0)
        {
            var p = await PaymentAsync(ctx, paymentId, ct);
            await AddAsync(ctx, p, order, part, ct);
        }
    }

    /// <summary>Отмена заказа и разноска на него идут по очереди: под той же блокировкой контрагента.</summary>
    public Task LockCounterpartyAsync(AccessContext ctx, long counterpartyId, CancellationToken ct) =>
        db.LockAsync(LockName(ctx, counterpartyId), ct);

    public Task<bool> OrderHasAllocationsAsync(AccessContext ctx, long orderId, CancellationToken ct) =>
        sales
            ? PaidByOrder.Sales(db, ctx.OrganizationId).AnyAsync(a => a.OrderId == orderId, ct)
            : PaidByOrder.Purchases(db, ctx.OrganizationId).AnyAsync(a => a.OrderId == orderId, ct);

    private async Task AddAsync(AccessContext ctx, PaymentInfo p, OrderInfo order, decimal amount, CancellationToken ct)
    {
        var now = clock.UtcNow;
        PaymentAllocation? existing = sales
            ? await db.CustomerPaymentAllocations.SingleOrDefaultAsync(a => a.PaymentId == p.Id && a.OrderId == order.Id && a.RemovedAtUtc == null, ct)
            : await db.SupplierPaymentAllocations.SingleOrDefaultAsync(a => a.PaymentId == p.Id && a.OrderId == order.Id && a.RemovedAtUtc == null, ct);
        var total = amount;
        if (existing is not null)
        {
            existing.Remove(ctx.UserId, now);
            total += existing.Amount;

            // Снятие сохраняется раньше новой строки: уникальный индекс действующих разносок не допускает двух сразу.
            await db.SaveChangesAsync(ct);
        }

        if (sales)
        {
            db.CustomerPaymentAllocations.Add(CustomerPaymentAllocation.Create(ctx.OrganizationId, p.Id, order.Id, total, ctx.UserId, now));
        }
        else
        {
            db.SupplierPaymentAllocations.Add(SupplierPaymentAllocation.Create(ctx.OrganizationId, p.Id, order.Id, total, ctx.UserId, now));
        }

        Audit(ctx, AuditActions.PaymentAllocated, p.Id, existing is null ? null : $"{existing.Amount:0.00}", $"{total:0.00}", $"{p.Number} → {order.Number}");
    }

    private Task<DateOnly?> ClosedThroughAsync(AccessContext ctx, CancellationToken ct) =>
        KnitErp.Application.Warehousing.ClosedPeriod.ClosedThroughAsync(db, ctx.OrganizationId, ct);

    /// <summary>D133: дата разноски — поздняя из дат оплаты и заказа.</summary>
    private static DateOnly AllocationDate(DateOnly paymentDate, DateOnly orderDate) => paymentDate > orderDate ? paymentDate : orderDate;

    /// <summary>D133: разноска пары «оплата — заказ» в закрытом периоде (её дата — поздняя из двух). Общая для зачёта в аванс (D140).</summary>
    internal static bool IsClosed(DateOnly? closedThrough, DateOnly paymentDate, DateOnly orderDate) =>
        closedThrough is { } t && AllocationDate(paymentDate, orderDate) <= t;

    private static void EnsureOpen(DateOnly? closedThrough, PaymentInfo p, OrderInfo order)
    {
        if (closedThrough is { } t && IsClosed(t, p.Date, order.Date))
        {
            throw new BusinessRuleException("period.closed",
                $"Период закрыт по {t:dd.MM.yyyy}: оплата {p.Number} и заказ {order.Number} — в закрытом периоде, их разноску менять нельзя. Открыть период может Владелец.");
        }
    }

    private async Task EnsureAllowedAsync(PaymentInfo p, OrderInfo order, CancellationToken ct)
    {
        EnsurePosted(p);
        if (order.CounterpartyId != p.CounterpartyId || !order.Open)
        {
            throw new BusinessRuleException("payment.allocation.order",
                $"Заказ {order.Number} другого контрагента, не подтверждён или отменён.");
        }

        if (p.AccountId is { } account
            && await db.LegalEntityAccounts.AsNoTracking().Where(a => a.Id == account).Select(a => a.LegalEntityId).SingleAsync(ct) != order.LegalEntityId)
        {
            throw new BusinessRuleException("payment.allocation.entity",
                $"Оплата {p.Number} пришла на счёт другого юрлица, не того, что в заказе {order.Number}.");
        }
    }

    private static void EnsurePosted(PaymentInfo p)
    {
        if (!p.Posted)
        {
            throw new BusinessRuleException("payment.allocation.cancelled", $"Оплата {p.Number} отменена — разносить нечего.");
        }
    }

    private async Task<decimal> UnallocatedAsync(AccessContext ctx, PaymentInfo p, CancellationToken ct) =>
        p.Amount - await AllocationsQuery(ctx.OrganizationId).Where(a => a.PaymentId == p.Id && a.RemovedAtUtc == null).SumAsync(a => a.Amount, ct);

    /// <summary>Открытые заказы контрагента платежа (того же юрлица, если у платежа есть счёт) — в порядке разноски.</summary>
    private async Task<List<OpenOrderDto>> OpenOrdersAsync(AccessContext ctx, PaymentInfo p, CancellationToken ct)
    {
        long? entity = p.AccountId is { } account
            ? await db.LegalEntityAccounts.AsNoTracking().Where(a => a.Id == account).Select(a => (long?)a.LegalEntityId).SingleAsync(ct)
            : null;
        var orders = (await OrdersQuery(ctx.OrganizationId).Where(o => o.CounterpartyId == p.CounterpartyId && o.Open
                                                                      && (entity == null || o.LegalEntityId == entity)).ToListAsync(ct));
        var paid = await PaidAsync(ctx.OrganizationId, orders.Select(o => o.Id).ToList(), ct);
        return orders.Select(o => new OpenOrderDto(o.Id, o.Number, o.Date, o.Due, o.Total, paid.GetValueOrDefault(o.Id)))
            .OrderBy(o => o.Due ?? o.Date).ThenBy(o => o.Date).ThenBy(o => o.Id).ToList();
    }

    /// <summary>Платежи контрагента с неразнесённым остатком, подходящие к заказу по юрлицу, — от старых к новым.</summary>
    private async Task<List<(PaymentInfo Payment, decimal Unallocated)>> AdvancesAsync(AccessContext ctx, OrderInfo order, CancellationToken ct)
    {
        var payments = await PaymentsQuery(ctx.OrganizationId).Where(p => p.CounterpartyId == order.CounterpartyId && p.Posted).ToListAsync(ct);
        var ids = payments.Select(p => p.Id).ToList();
        var allocated = await AllocationsQuery(ctx.OrganizationId).Where(a => ids.Contains(a.PaymentId) && a.RemovedAtUtc == null)
            .GroupBy(a => a.PaymentId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var accountIds = payments.Where(p => p.AccountId != null).Select(p => p.AccountId!.Value).Distinct().ToList();
        var entities = await db.LegalEntityAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.LegalEntityId, ct);
        return payments
            .Where(p => p.AccountId is not { } a || entities[a] == order.LegalEntityId)
            .Select(p => (p, p.Amount - allocated.GetValueOrDefault(p.Id)))
            .Where(x => x.Item2 > 0)
            .OrderBy(x => x.p.Date).ThenBy(x => x.p.Id)
            .ToList();
    }

    private async Task<PaymentInfo> PaymentAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await PaymentsQuery(ctx.OrganizationId).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Оплата");

    private async Task<OrderInfo> OrderAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await OrdersQuery(ctx.OrganizationId).SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Заказ");

    private IQueryable<PaymentInfo> PaymentsQuery(long org) =>
        sales
            ? db.CustomerPayments.AsNoTracking().Where(p => p.OrganizationId == org).Select(p => new PaymentInfo
            {
                Id = p.Id, Number = p.Number, Date = p.PaymentDate, CounterpartyId = p.CustomerId,
                // D1154 (сессия «Продажи»): возвращённое покупателю деньгами из оплаты уже не разносится.
                Amount = p.Amount - (db.CustomerRefunds.Where(r => r.PaymentId == p.Id && r.Status == CustomerRefundStatus.Posted).Sum(r => (decimal?)r.Amount) ?? 0),
                Posted = p.Status == CustomerPaymentStatus.Posted, AccountId = p.MoneyAccountId,
            })
            : db.SupplierPayments.AsNoTracking().Where(p => p.OrganizationId == org).Select(p => new PaymentInfo
            {
                Id = p.Id, Number = p.Number, Date = p.PaymentDate, CounterpartyId = p.SupplierId, Amount = p.Amount,
                Posted = p.Status == SupplierPaymentStatus.Posted, AccountId = p.MoneyAccountId,
            });

    private IQueryable<OrderInfo> OrdersQuery(long org) =>
        sales
            ? db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org).Select(o => new OrderInfo
            {
                Id = o.Id, Number = o.Number, Date = o.OrderDate,
                Due = db.CustomerInvoices.Where(i => i.SalesOrderId == o.Id && i.Status == CustomerInvoiceStatus.Issued && i.DueDate != null)
                    .Min(i => i.DueDate) ?? o.ShipDate,
                CounterpartyId = o.CustomerId, Open = o.Status == SalesOrderStatus.Confirmed || o.Status == SalesOrderStatus.Closed,
                LegalEntityId = o.LegalEntityId, Total = o.Lines.Sum(l => (decimal?)l.Amount) ?? 0m,
            })
            : db.PurchaseOrders.AsNoTracking().Where(o => o.OrganizationId == org).Select(o => new OrderInfo
            {
                Id = o.Id, Number = o.Number, Date = o.OrderDate, Due = o.ExpectedDate, CounterpartyId = o.SupplierId,
                Open = o.Status == PurchaseOrderStatus.Confirmed || o.Status == PurchaseOrderStatus.Closed,
                LegalEntityId = o.LegalEntityId, Total = o.Lines.Sum(l => (decimal?)l.Amount) ?? 0m,
            });

    private IQueryable<PaymentAllocation> AllocationsQuery(long org) =>
        sales
            ? db.CustomerPaymentAllocations.AsNoTracking().Where(a => a.OrganizationId == org)
            : db.SupplierPaymentAllocations.AsNoTracking().Where(a => a.OrganizationId == org);

    private Task<Dictionary<long, decimal>> PaidAsync(long org, IReadOnlyCollection<long> orderIds, CancellationToken ct) =>
        sales ? PaidByOrder.SalesAsync(db, org, orderIds, ct) : PaidByOrder.PurchasesAsync(db, org, orderIds, ct);

    private Task<Dictionary<long, string>> OrderNumbersAsync(long org, IReadOnlyCollection<long> ids, CancellationToken ct) =>
        sales
            ? db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct)
            : db.PurchaseOrders.AsNoTracking().Where(o => o.OrganizationId == org && ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);

    private string LockName(AccessContext ctx, long counterpartyId) => $"kniterp.allocation.{Side}.{ctx.OrganizationId}.{counterpartyId}";

    private void Audit(AccessContext ctx, string action, long paymentId, string? before, string? after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, sales ? nameof(CustomerPayment) : nameof(SupplierPayment),
            paymentId.ToString(), before, after, reason, currentUser.CorrelationId));
}
