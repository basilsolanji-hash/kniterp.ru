using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// Условия кредитного контроля заказа (D1111): лимит и отсрочка — из соглашения заказа, а чего в нём нет — из договора соглашения.
/// Без соглашения у заказа нет ни лимита, ни отсрочки. LimitDocument — номер соглашения или договора, откуда взят лимит.
/// </summary>
public sealed record SalesCreditTerms(decimal? Limit, string? LimitDocument, bool LimitFromContract, int? PaymentDays)
{
    public static readonly SalesCreditTerms None = new(null, null, false, null);
}

/// <summary>
/// Неоплаченная часть отгрузки (D1112). Срок оплаты — дата отгрузки + отсрочка заказа (без отсрочки — в день отгрузки);
/// Left — сколько по ней ещё не оплачено после разнесения оплат и возвратов покупателя по старым отгрузкам (FIFO).
/// </summary>
public sealed record OpenShipmentDto(long DocumentId, string Number, string OrderNumber, DateOnly Date, int? PaymentDays, decimal Amount, decimal Left)
{
    public DateOnly Due => Date.AddDays(PaymentDays ?? 0);

    public int OverdueDays(DateOnly on) => on.DayNumber - Due.DayNumber;
}

/// <summary>
/// Состояние кредитного контроля для суммы Amount (заказ или отгрузка) на дату (D1105): Excess — на сколько долг + сумма больше
/// лимита (0 — в пределах, null — лимита нет); Overdue — просроченный долг по отгрузкам с отсрочкой (без отсрочки просрочка не
/// проверяется, D1112); OverdueDays — самая давняя просрочка.
/// </summary>
public sealed record SalesCreditCheckDto(
    decimal? Limit, string? LimitDocument, bool LimitFromContract, int? PaymentDays, decimal Debt, decimal Amount, decimal? Excess, decimal Overdue,
    int OverdueDays)
{
    /// <summary>Откуда лимит — для журнала и тестов («соглашение СГ-1», «договор № 5»); в интерфейсе — через перевод.</summary>
    public string? LimitSource => LimitDocument is null ? null : LimitFromContract ? $"договор № {LimitDocument}" : $"соглашение {LimitDocument}";

    public bool OverLimit => Excess > 0;
    public bool Blocked => OverLimit || Overdue > 0;
}

/// <summary>Расчёты покупателя для дебиторки: долг как в «Расчётах с покупателями» и открытые отгрузки после FIFO.</summary>
public sealed class CustomerLedger(long customerId)
{
    public long CustomerId { get; } = customerId;
    public decimal ShippedRaw { get; set; }
    public decimal ReturnedRaw { get; set; }
    public decimal Paid { get; set; }
    public List<OpenShipmentDto> Open { get; } = [];

    /// <summary>Отгружено − возвращено − оплачено, с округлением как в SalesService.ComputeBalancesAsync.</summary>
    public decimal Debt => Money.Round(ShippedRaw) - Money.Round(ReturnedRaw) - Paid;

    /// <summary>Просроченный долг на дату; onlyWithTerms — только по отгрузкам с отсрочкой (правило блокировки D1105).</summary>
    public (decimal Amount, int Days) Overdue(DateOnly on, bool onlyWithTerms)
    {
        var late = Open.Where(o => (!onlyWithTerms || o.PaymentDays is not null) && o.OverdueDays(on) > 0).ToList();
        return late.Count == 0 ? (0m, 0) : (Money.Round(late.Sum(o => o.Left)), late.Max(o => o.OverdueDays(on)));
    }

    /// <summary>
    /// Разбивка долга по срокам (D1112): не просрочено, 1–30, 31–60, 61–90, больше 90 дней. Сумма корзин равна долгу
    /// (копейки округления — в самую большую корзину); при авансе (долг ≤ 0) — нули.
    /// </summary>
    public decimal[] Buckets(DateOnly on)
    {
        var result = new decimal[5];
        if (Debt <= 0)
        {
            return result;
        }

        foreach (var o in Open)
        {
            var days = o.OverdueDays(on);
            result[days <= 0 ? 0 : days <= 30 ? 1 : days <= 60 ? 2 : days <= 90 ? 3 : 4] += o.Left;
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Money.Round(result[i]);
        }

        var diff = Debt - result.Sum();
        if (diff != 0)
        {
            result[Array.IndexOf(result, result.Max())] += diff;
        }

        return result;
    }
}

/// <summary>
/// Кредитный контроль и дебиторка (D1105, D1111–D1114). Долг покупателя — как в «Расчётах с покупателями»: отгружено − возвращено
/// по ценам заказов с НДС − действующие оплаты (отгрузки без заказа в расчёты не входят: у них нет цены). Оплаты и возвраты
/// разносятся FIFO по самым старым отгрузкам — так получаются сроки и просрочка, независимо от разноски оплат по заказам (D85).
/// </summary>
public static class SalesCredit
{
    /// <summary>Расчёты покупателей на дату asOf; customers null — все покупатели организации.</summary>
    public static async Task<Dictionary<long, CustomerLedger>> LedgersAsync(
        IKnitErpDbContext db, long org, IReadOnlyCollection<long>? customers, DateOnly asOf, CancellationToken ct)
    {
        var orders = db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org);
        var payments = db.CustomerPayments.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.Status == CustomerPaymentStatus.Posted && p.PaymentDate <= asOf);
        if (customers is not null)
        {
            orders = orders.Where(o => customers.Contains(o.CustomerId));
            payments = payments.Where(p => customers.Contains(p.CustomerId));
        }

        var docs = await (from d in db.StockDocuments.AsNoTracking()
                          where d.OrganizationId == org && d.SalesOrderId != null && d.Status == StockDocumentStatus.Posted && d.DocumentDate <= asOf
                                && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn)
                          join o in orders on d.SalesOrderId equals (long?)o.Id
                          select new
                          {
                              d.Id, d.Number, d.DocumentDate, d.Kind, OrderId = o.Id, OrderNumber = o.Number, o.CustomerId, o.AgreementId,
                              Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList(),
                          })
            .ToListAsync(ct);
        var orderIds = docs.Select(d => d.OrderId).Distinct().ToList();
        var lines = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && orderIds.Contains(o.Id))
            .SelectMany(o => o.Lines.Select(l => new { OrderId = o.Id, l.ItemId, l.Quantity, l.Amount }))
            .ToListAsync(ct);
        var unit = new Dictionary<(long, long), decimal>();
        foreach (var l in lines)
        {
            unit.TryAdd((l.OrderId, l.ItemId), l.Quantity == 0 ? 0m : l.Amount / l.Quantity); // как SalesOrder.UnitCostWithVat
        }

        var terms = await TermsAsync(db, org, docs.Select(d => d.AgreementId), ct);
        var paid = await payments.GroupBy(p => p.CustomerId).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        await SalesRefunds.SubtractAsync(db, org, customers, asOf, paid, ct); // D1154: возвраты денег покупателю уменьшают оплату

        var result = new Dictionary<long, CustomerLedger>();
        CustomerLedger Of(long id) => result.TryGetValue(id, out var l) ? l : result[id] = new CustomerLedger(id);
        var shipments = new Dictionary<long, List<OpenShipmentDto>>();
        foreach (var d in docs.OrderBy(d => d.DocumentDate).ThenBy(d => d.Id))
        {
            var value = d.Lines.Sum(l => l.Quantity * unit.GetValueOrDefault((d.OrderId, l.ItemId)));
            var ledger = Of(d.CustomerId);
            if (d.Kind == StockOperationKind.Shipment)
            {
                ledger.ShippedRaw += value;
                var days = d.AgreementId is { } aid && terms.TryGetValue(aid, out var t) ? t.PaymentDays : null;
                ledger.Open.Add(new OpenShipmentDto(d.Id, d.Number, d.OrderNumber, d.DocumentDate, days, value, value));
            }
            else
            {
                ledger.ReturnedRaw += value;
            }
        }

        foreach (var (customer, sum) in paid)
        {
            Of(customer).Paid = sum;
        }

        // D1146: вводный остаток — долг как отгрузка этой датой (срок — та же дата), аванс покупателя — как оплата.
        foreach (var (customer, list) in await SalesOpeningBalances.ByCustomerAsync(db, org, customers, asOf, ct))
        {
            var ledger = Of(customer);
            foreach (var (date, amount) in list)
            {
                if (amount > 0)
                {
                    ledger.ShippedRaw += amount;
                    ledger.Open.Add(new OpenShipmentDto(0, "Вводный остаток", "—", date, null, amount, amount));
                }
                else
                {
                    ledger.Paid += -amount;
                }
            }

            ledger.Open.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.DocumentId.CompareTo(b.DocumentId));
        }

        // FIFO: возвраты и оплаты гасят самые старые отгрузки.
        foreach (var ledger in result.Values)
        {
            var credit = ledger.ReturnedRaw + ledger.Paid;
            for (var i = 0; i < ledger.Open.Count && credit > 0; i++)
            {
                var take = Math.Min(credit, ledger.Open[i].Left);
                ledger.Open[i] = ledger.Open[i] with { Left = ledger.Open[i].Left - take };
                credit -= take;
            }

            ledger.Open.RemoveAll(o => o.Left <= 0);
        }

        return result;
    }

    /// <summary>Лимит и отсрочка по соглашениям (D1111): из соглашения, чего нет — из его договора.</summary>
    public static async Task<Dictionary<long, SalesCreditTerms>> TermsAsync(
        IKnitErpDbContext db, long org, IEnumerable<long?> agreementIds, CancellationToken ct)
    {
        var ids = agreementIds.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var agreements = await (from a in db.SalesAgreements.AsNoTracking()
                                where a.OrganizationId == org && ids.Contains(a.Id)
                                join c in db.CounterpartyContracts.AsNoTracking() on a.ContractId equals (long?)c.Id into cj
                                from c in cj.DefaultIfEmpty()
                                select new
                                {
                                    a.Id, a.Number, a.CreditLimit, a.PaymentDays, ContractNumber = c == null ? null : c.Number,
                                    ContractLimit = c == null ? null : c.AmountLimit, ContractDays = c == null ? null : c.PaymentDays,
                                })
            .ToListAsync(ct);
        return agreements.ToDictionary(a => a.Id, a => new SalesCreditTerms(
            a.CreditLimit ?? a.ContractLimit,
            a.CreditLimit is not null ? a.Number : a.ContractLimit is not null ? a.ContractNumber : null,
            a.CreditLimit is null && a.ContractLimit is not null,
            a.PaymentDays ?? a.ContractDays));
    }

    /// <summary>Проверка долга покупателя заказа вместе с суммой amount на дату today (D1105).</summary>
    public static async Task<SalesCreditCheckDto> CheckAsync(
        IKnitErpDbContext db, long org, long customerId, long? agreementId, decimal amount, DateOnly today, CancellationToken ct)
    {
        var terms = agreementId is { } aid && (await TermsAsync(db, org, [aid], ct)).TryGetValue(aid, out var t) ? t : SalesCreditTerms.None;
        var ledger = (await LedgersAsync(db, org, [customerId], today, ct)).GetValueOrDefault(customerId) ?? new CustomerLedger(customerId);
        var debt = ledger.Debt;
        var (overdue, days) = ledger.Overdue(today, onlyWithTerms: true);
        return new SalesCreditCheckDto(terms.Limit, terms.LimitDocument, terms.LimitFromContract, terms.PaymentDays, debt, Money.Round(amount),
            terms.Limit is { } limit ? Math.Max(0, debt + Money.Round(amount) - limit) : null, overdue, days);
    }

    /// <summary>Последнее разрешение по заказу (D1111).</summary>
    public static Task<SalesCreditApproval?> LastApprovalAsync(IKnitErpDbContext db, long org, long orderId, CancellationToken ct) =>
        db.SalesCreditApprovals.AsNoTracking().Where(a => a.OrganizationId == org && a.OrderId == orderId)
            .OrderByDescending(a => a.ApprovedAtUtc).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);

    /// <summary>Отгружено по заказу (за вычетом возвратов) по ценам заказа с НДС — проведённые документы.</summary>
    public static async Task<decimal> ShippedValueAsync(IKnitErpDbContext db, long org, SalesOrder order, CancellationToken ct)
    {
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId == order.Id && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn))
            .SelectMany(d => d.Lines.Select(l => new { d.Kind, l.ItemId, l.Quantity }))
            .ToListAsync(ct);
        return Money.Round(moved.Sum(m => (m.Kind == StockOperationKind.Shipment ? 1 : -1) * m.Quantity * order.UnitCostWithVat(m.ItemId)));
    }

    /// <summary>
    /// D1105: проведение отгрузки по заказу покупателя — лимит и просрочка покупателя. Вызывается из проведения склада
    /// (<c>StockDocumentService</c>) в его транзакции; блокировка по покупателю — две отгрузки одного покупателя не проходят
    /// проверку одновременно. Кладовщик без права на цены получает отказ без сумм.
    /// </summary>
    public static async Task EnsureShipmentAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, DateTime nowUtc, CancellationToken ct)
    {
        if (doc.Kind != StockOperationKind.Shipment || doc.SalesOrderId is not { } orderId)
        {
            return;
        }

        var org = ctx.OrganizationId;
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId && o.OrganizationId == org, ct);
        await db.LockAsync($"kniterp.sales.credit.{order.CustomerId}", ct);
        var amount = Money.Round(doc.Lines.Sum(l => l.Quantity * order.UnitCostWithVat(l.ItemId)));
        var today = await TodayAsync(db, org, nowUtc, ct);
        var check = await CheckAsync(db, org, order.CustomerId, order.AgreementId, amount, today, ct);
        if (!check.Blocked)
        {
            return;
        }

        if (await LastApprovalAsync(db, org, orderId, ct) is { } approval
            && approval.Amount >= await ShippedValueAsync(db, org, order, ct) + amount)
        {
            return;
        }

        if (!ctx.Permissions.Has(Permissions.PriceView))
        {
            throw new BusinessRuleException("sales.credit.blocked",
                $"Отгрузка по заказу {order.Number} остановлена кредитным контролем: превышен лимит или просрочена оплата покупателя. Обратитесь к менеджеру — нужна оплата или разрешение руководителя в заказе.");
        }

        if (check.Overdue > 0)
        {
            throw new BusinessRuleException("sales.credit.overdue",
                $"Отгрузка по заказу {order.Number} остановлена: у покупателя просрочена оплата на {check.Overdue:N2} (до {check.OverdueDays} дн.). Нужна оплата или разрешение руководителя в заказе.");
        }

        throw new BusinessRuleException("sales.credit.limit",
            $"Отгрузка по заказу {order.Number} остановлена: кредитный лимит превышен на {check.Excess:N2} (долг {check.Debt:N2} + отгрузка {check.Amount:N2}, лимит {check.Limit:N2}). Нужна оплата или разрешение руководителя в заказе.");
    }

    public static async Task<DateOnly> TodayAsync(IKnitErpDbContext db, long org, DateTime nowUtc, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        return KnitErp.Application.Workspace.PersonalToolsService.Today(nowUtc, tz);
    }
}
