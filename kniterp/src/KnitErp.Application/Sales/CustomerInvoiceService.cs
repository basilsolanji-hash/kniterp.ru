using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Оплаченность счёта — по оплатам его заказа.</summary>
public enum InvoicePaymentState : byte
{
    Unpaid = 0,
    Partial = 1,
    Paid = 2,
}

public sealed record CustomerInvoiceFilter(string? Search = null, DateOnly? From = null, DateOnly? To = null, bool IncludeCancelled = false);

public sealed record CustomerInvoiceRowDto(
    long Id, string Number, DateOnly Date, DateOnly? DueDate, long CustomerId, string Customer, long OrderId, string OrderNumber,
    decimal Total, decimal Paid, CustomerInvoiceStatus Status, bool Overdue)
{
    public string StatusName => CustomerInvoice.StatusName(Status);
    public InvoicePaymentState PaymentState => CustomerInvoiceService.StateOf(Total, Paid);
}

public sealed record CustomerInvoiceLineDto(
    string Code, string Name, string UnitSymbol, byte Precision, decimal Quantity, decimal Price, decimal? VatPercent, decimal Amount, decimal VatAmount);

public sealed record CustomerInvoiceDto(
    long Id, string Number, DateOnly Date, DateOnly? DueDate, long CustomerId, string Customer, long OrderId, string OrderNumber,
    bool PricesIncludeVat, CustomerInvoiceStatus Status, string? Comment, string CreatedBy, DateTime CreatedAtUtc, string? CancelReason,
    IReadOnlyList<CustomerInvoiceLineDto> Lines, decimal Total, decimal VatTotal, decimal Paid, bool CanEdit, byte[] RowVersion)
{
    public string StatusName => CustomerInvoice.StatusName(Status);
    public InvoicePaymentState PaymentState => CustomerInvoiceService.StateOf(Total, Paid);
}

/// <summary>
/// Счета покупателям (D68): выставление по подтверждённому заказу, список с оплаченностью, отмена с причиной.
/// Счёт — только про деньги: смотреть — «Продажи: просмотр» и «Цены и суммы», выставлять и отменять — «Продажи: заказы и оплаты».
/// </summary>
public sealed class CustomerInvoiceService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRows = 2000;

    public static InvoicePaymentState StateOf(decimal total, decimal paid) =>
        paid >= total && total > 0 ? InvoicePaymentState.Paid : paid > 0 ? InvoicePaymentState.Partial : InvoicePaymentState.Unpaid;

    public async Task<IReadOnlyList<CustomerInvoiceRowDto>> ListAsync(CustomerInvoiceFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var q = db.CustomerInvoices.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId);
        if (!filter.IncludeCancelled)
        {
            q = q.Where(i => i.Status == CustomerInvoiceStatus.Issued);
        }

        if (filter.From is { } from)
        {
            q = q.Where(i => i.InvoiceDate >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(i => i.InvoiceDate <= to);
        }

        var rows = from i in q
                   join c in db.Counterparties.AsNoTracking() on i.CustomerId equals c.Id
                   join o in db.SalesOrders.AsNoTracking() on i.SalesOrderId equals o.Id
                   select new { i, Customer = c.Name, c.Inn, OrderNumber = o.Number };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            rows = rows.Where(r => r.i.Number.Contains(text) || r.Customer.Contains(text) || r.Inn == text || r.OrderNumber.Contains(text));
        }

        var list = await rows.OrderByDescending(r => r.i.InvoiceDate).ThenByDescending(r => r.i.Id).Take(MaxRows)
            .Select(r => new { r.i.Id, r.i.Number, r.i.InvoiceDate, r.i.DueDate, r.i.CustomerId, r.Customer, r.i.SalesOrderId, r.OrderNumber,
                Total = r.i.Lines.Sum(l => l.Amount), r.i.Status })
            .ToListAsync(ct);
        var paid = await PaidAsync(ctx, list.Select(r => r.SalesOrderId).Distinct().ToList(), ct);
        var today = await TodayAsync(ctx, ct);
        return list.Select(r =>
        {
            var p = Math.Min(paid.GetValueOrDefault(r.SalesOrderId), r.Total);
            return new CustomerInvoiceRowDto(r.Id, r.Number, r.InvoiceDate, r.DueDate, r.CustomerId, r.Customer, r.SalesOrderId, r.OrderNumber,
                r.Total, p, r.Status, r.Status == CustomerInvoiceStatus.Issued && r.DueDate < today && p < r.Total);
        }).ToList();
    }

    public async Task<CustomerInvoiceDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var invoice = await db.CustomerInvoices.AsNoTracking().Include(i => i.Lines)
                          .SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Счёт покупателю");
        var itemIds = invoice.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision })
            .ToDictionaryAsync(x => x.Id, ct);
        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == invoice.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var order = await db.SalesOrders.AsNoTracking().Where(o => o.Id == invoice.SalesOrderId).Select(o => o.Number).SingleAsync(ct);
        var author = await db.Users.AsNoTracking().Where(u => u.Id == invoice.CreatedByUserId).Select(u => u.DisplayName).SingleAsync(ct);
        var paid = (await PaidAsync(ctx, [invoice.SalesOrderId], ct)).GetValueOrDefault(invoice.SalesOrderId);
        var lines = invoice.Lines.Select(l =>
        {
            var item = items[l.ItemId];
            return new CustomerInvoiceLineDto(item.Code, item.Name, item.Symbol, item.Precision, l.Quantity, l.Price, l.VatPercent, l.Amount, l.VatAmount);
        }).OrderBy(l => l.Code).ToList();
        return new CustomerInvoiceDto(invoice.Id, invoice.Number, invoice.InvoiceDate, invoice.DueDate, invoice.CustomerId, customer,
            invoice.SalesOrderId, order, invoice.PricesIncludeVat, invoice.Status, invoice.Comment, author, invoice.CreatedAtUtc, invoice.CancelReason,
            lines, invoice.Total, invoice.VatTotal, Math.Min(paid, invoice.Total),
            ctx.Permissions.Has(Permissions.SalesEdit) && invoice.Status == CustomerInvoiceStatus.Issued, invoice.RowVersion);
    }

    /// <summary>Действующий счёт заказа, если есть.</summary>
    public async Task<long?> FindForOrderAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        return await db.CustomerInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.SalesOrderId == orderId && i.Status == CustomerInvoiceStatus.Issued)
            .Select(i => (long?)i.Id).SingleOrDefaultAsync(ct);
    }

    /// <summary>Счёт по заказу на сегодня: строки, цены и НДС копируются из заказа. У заказа один действующий счёт.</summary>
    public async Task<long> CreateAsync(long orderId, DateOnly? dueDate, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var existing = await db.CustomerInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.SalesOrderId == orderId && i.Status == CustomerInvoiceStatus.Issued)
            .Select(i => i.Number).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            throw new BusinessRuleException("sales.invoice.exists", $"По заказу уже выставлен счёт {existing}. Нужен другой — сначала отмените его.");
        }

        var today = await TodayAsync(ctx, ct);
        CustomerInvoice.EnsureCanIssue(order, today, dueDate, comment);
        // Юрлицо и счёт заказа (D78): счёт выписывается от действующего юрлица; без выбранного счёта — его основной счёт.
        (await db.LegalEntities.AsNoTracking().SingleAsync(e => e.OrganizationId == ctx.OrganizationId && e.Id == order.LegalEntityId, ct)).EnsureActive();
        var bankAccountId = order.BankAccountId ?? await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == order.LegalEntityId && a.IsDefault)
            .Select(a => (long?)a.Id).FirstOrDefaultAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CustomerInvoice.NumberPrefix, ct);
        var invoice = CustomerInvoice.Create(ctx.OrganizationId, number, today, dueDate, order, comment, ctx.UserId, clock.UtcNow, bankAccountId);
        db.CustomerInvoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CustomerInvoiceIssued, invoice.Id, null, $"{invoice.Total:0.00}", $"{number} по заказу {order.Number}");
        await db.SaveChangesAsync(ct);
        await StageAutomation.ApplyAsync(db, ctx, [orderId], clock.UtcNow, currentUser.CorrelationId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return invoice.Id;
    }

    public async Task CancelAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var invoice = await db.CustomerInvoices.SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Счёт покупателю");
        invoice.EnsureVersion(invoice.RowVersion, rowVersion);
        invoice.Cancel(ctx.UserId, reason, clock.UtcNow);
        Audit(ctx, AuditActions.CustomerInvoiceCancelled, id, "Выставлен", "Отменён", $"{invoice.Number}: {invoice.CancelReason}");
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<Dictionary<long, decimal>> PaidAsync(AccessContext ctx, IReadOnlyCollection<long> orderIds, CancellationToken ct) =>
        await KnitErp.Application.Finance.PaidByOrder.SalesAsync(db, ctx.OrganizationId, orderIds, ct);

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CustomerInvoice), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
