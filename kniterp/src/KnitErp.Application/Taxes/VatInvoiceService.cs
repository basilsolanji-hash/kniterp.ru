using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Taxes;

/// <summary>Период журнала. LegalEntityId — журнал одного своего юрлица (D78): каждый налогоплательщик ведёт свой журнал.</summary>
public sealed record VatJournalFilter(DateOnly From, DateOnly To, string? Search = null, bool IncludeCancelled = false, long? LegalEntityId = null);

/// <summary>Выданный счёт-фактура = УПД по отгрузке (D68). Annulled — отгрузка сторнирована, в итоги не входит.</summary>
public sealed record IssuedVatInvoiceDto(
    long DocumentId, string InvoiceNumber, string Number, DateOnly Date, string Customer, string? Inn, string? Kpp, string OrderNumber,
    decimal AmountWithoutVat, decimal VatAmount, bool Annulled, string? Seller = null)
{
    public decimal Amount => AmountWithoutVat + VatAmount;
}

public sealed record ReceivedVatInvoiceDto(
    long Id, string SupplierNumber, DateOnly Date, long SupplierId, string Supplier, string? Inn, string? Kpp, long ReceiptId, string ReceiptNumber,
    DateOnly ReceiptDate, decimal Amount, decimal VatAmount, decimal ReceiptAmount, string? Comment, ReceivedVatInvoiceStatus Status,
    string? CancelReason, byte[] RowVersion, string? Buyer = null)
{
    public decimal AmountWithoutVat => Amount - VatAmount;

    /// <summary>Разница с приёмкой по ценам заказа: не ноль — сверьте документ поставщика с заказом.</summary>
    public decimal Difference => Amount - ReceiptAmount;
}

/// <summary>Приёмка по заказу, к которой ещё не зарегистрирован счёт-фактура; суммы — по ценам заказа.</summary>
public sealed record ReceiptToRegisterDto(
    long Id, string Number, DateOnly Date, long SupplierId, string Supplier, string OrderNumber, string? SupplierInvoice, decimal Amount, decimal VatAmount);

public sealed record RegisterVatInvoiceCommand(long ReceiptId, string? Number, DateOnly Date, decimal Amount, decimal VatAmount, string? Comment);

/// <summary>
/// Журналы счетов-фактур (D70). Выданные — проведённые и сторнированные отгрузки по заказам покупателей (суммы по ценам заказа);
/// права — «Продажи: просмотр» и «Цены и суммы». Полученные — регистрация счёта-фактуры поставщика к приёмке по заказу;
/// смотреть — «Закупки: просмотр» и «Цены и суммы», регистрировать и отменять — «Закупки: заказы поставщикам и оплаты».
/// </summary>
public sealed class VatInvoiceService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRows = 5000;
    public const int MaxPeriodDays = 731;

    public async Task<IReadOnlyList<IssuedVatInvoiceDto>> IssuedAsync(VatJournalFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        EnsurePeriod(filter);
        var q = from d in db.StockDocuments.AsNoTracking()
                // D354 (сессия 4, Р1): акт оказания услуг — тоже УПД (счёт-фактура), если не вошёл в УПД отгрузки той же датой.
                where d.OrganizationId == ctx.OrganizationId && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.ServiceAct)
                      && d.SalesOrderId != null
                      && (d.Status == StockDocumentStatus.Posted || (filter.IncludeCancelled && d.Status == StockDocumentStatus.Reversed))
                      && d.DocumentDate >= filter.From && d.DocumentDate <= filter.To
                join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                join o in db.SalesOrders.AsNoTracking() on d.SalesOrderId equals o.Id
                join e in db.LegalEntities.AsNoTracking() on o.LegalEntityId equals e.Id
                // Освобождённые от НДС (D78) журнал выданных счетов-фактур не ведут.
                where !e.VatExempt && (filter.LegalEntityId == null || e.Id == filter.LegalEntityId)
                select new { d, c.Name, c.Inn, c.Kpp, OrderNumber = o.Number, Seller = e.ShortName };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            q = q.Where(x => x.d.Number.Contains(text) || x.Name.Contains(text) || x.Inn == text || x.OrderNumber.Contains(text));
        }

        var docs = await q.OrderBy(x => x.d.DocumentDate).ThenBy(x => x.d.Number).Take(MaxRows)
            .Select(x => new { x.d.Id, x.d.Number, x.d.DocumentDate, x.d.Status, OrderId = x.d.SalesOrderId!.Value, x.Name, x.Inn, x.Kpp, x.OrderNumber, x.Seller,
                Lines = x.d.Lines.Select(l => new { l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }).ToList() })
            .ToListAsync(ct);
        var orderIds = docs.Select(d => d.OrderId).Distinct().ToList();
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        // D354: акты, вошедшие в УПД отгрузки (UpdMerge), — строками этой отгрузки, а не отдельным счётом-фактурой.
        var candidates = await KnitErp.Application.Printing.UpdMerge.CandidatesAsync(db, ctx.OrganizationId, orderIds,
            docs.Select(d => d.DocumentDate).Distinct().ToList(), ct);
        var hosts = KnitErp.Application.Printing.UpdMerge.Hosts(candidates);
        var merged = candidates.Where(c => hosts.ContainsKey(c.Id)).ToLookup(c => hosts[c.Id]);
        return docs.Where(d => !hosts.ContainsKey(d.Id)).Select(d =>
        {
            var order = orders[d.OrderId];
            // D352 (сессия 4, Р2): комплект — строкой заказа, как в УПД.
            var ordered = BundleLines.InOrderTerms(d.Lines.Select(l => (0, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity)))
                .Select(l => (l.ItemId, l.Quantity))
                .Concat(d.Status == StockDocumentStatus.Posted ? merged[d.Id].SelectMany(a => a.OrderLines) : []);
            var (amount, vat) = ordered.Aggregate((Amount: 0m, Vat: 0m), (acc, l) =>
            {
                var (a, v) = Money.LineAmounts(l.Quantity, order.Lines.Single(x => x.ItemId == l.ItemId).NetPrice,
                    order.Lines.Single(x => x.ItemId == l.ItemId).VatPercent, order.PricesIncludeVat);
                return (acc.Amount + a, acc.Vat + v);
            });
            return new IssuedVatInvoiceDto(d.Id, KnitErp.Application.Printing.PrintService.VatInvoiceNumber(d.Number), d.Number, d.DocumentDate, d.Name, d.Inn, d.Kpp, d.OrderNumber, amount - vat, vat,
                d.Status == StockDocumentStatus.Reversed, d.Seller);
        }).ToList();
    }

    /// <summary>Свои юрлица — для фильтра журнала полученных: журнал ведёт каждое юрлицо-покупатель (D84).</summary>
    public async Task<IReadOnlyList<KnitErp.Application.Structure.LookupDto>> BuyersAsync(CancellationToken ct = default)
    {
        var ctx = await DemandPurchaseViewAsync(ct);
        return await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName)
            .Select(e => new KnitErp.Application.Structure.LookupDto(e.Id, e.ShortName)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ReceivedVatInvoiceDto>> ReceivedAsync(VatJournalFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandPurchaseViewAsync(ct);
        EnsurePeriod(filter);
        var q = from i in db.ReceivedVatInvoices.AsNoTracking()
                where i.OrganizationId == ctx.OrganizationId && i.InvoiceDate >= filter.From && i.InvoiceDate <= filter.To
                      && (filter.IncludeCancelled || i.Status == ReceivedVatInvoiceStatus.Registered)
                join c in db.Counterparties.AsNoTracking() on i.SupplierId equals c.Id
                join d in db.StockDocuments.AsNoTracking() on i.ReceiptDocumentId equals d.Id
                join o in db.PurchaseOrders.AsNoTracking() on d.PurchaseOrderId equals o.Id
                join e in db.LegalEntities.AsNoTracking() on o.LegalEntityId equals e.Id
                where filter.LegalEntityId == null || e.Id == filter.LegalEntityId
                select new { i, c.Name, c.Inn, c.Kpp, ReceiptNumber = d.Number, d.DocumentDate, Buyer = e.ShortName };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            q = q.Where(x => x.i.SupplierNumber.Contains(text) || x.Name.Contains(text) || x.Inn == text || x.ReceiptNumber.Contains(text));
        }

        var rows = await q.OrderBy(x => x.i.InvoiceDate).ThenBy(x => x.i.Id).Take(MaxRows).ToListAsync(ct);
        var values = await ReceiptValuesAsync(ctx, rows.Select(r => r.i.ReceiptDocumentId).ToList(), ct);
        return rows.Select(r => new ReceivedVatInvoiceDto(r.i.Id, r.i.SupplierNumber, r.i.InvoiceDate, r.i.SupplierId, r.Name, r.Inn, r.Kpp,
                r.i.ReceiptDocumentId, r.ReceiptNumber, r.DocumentDate, r.i.Amount, r.i.VatAmount, values.GetValueOrDefault(r.i.ReceiptDocumentId).Amount,
                r.i.Comment, r.i.Status, r.i.CancelReason, r.i.RowVersion, r.Buyer))
            .ToList();
    }

    /// <summary>Проведённые приёмки по заказам без действующего счёта-фактуры — к регистрации.</summary>
    public async Task<IReadOnlyList<ReceiptToRegisterDto>> ReceiptsToRegisterAsync(CancellationToken ct = default)
    {
        var ctx = await DemandPurchaseViewAsync(ct);
        var rows = await (from d in db.StockDocuments.AsNoTracking()
                          // D354 (сессия 4, Р1): счёт-фактура поставщика бывает и к акту услуг (доставка, доработка).
                          where d.OrganizationId == ctx.OrganizationId && d.PurchaseOrderId != null
                                && (d.Kind == StockOperationKind.Receipt || d.Kind == StockOperationKind.SupplierServiceAct)
                                && d.Status == StockDocumentStatus.Posted
                                && !db.ReceivedVatInvoices.Any(i => i.ReceiptDocumentId == d.Id && i.Status == ReceivedVatInvoiceStatus.Registered)
                          join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                          join o in db.PurchaseOrders.AsNoTracking() on d.PurchaseOrderId equals o.Id
                          orderby d.DocumentDate descending, d.Id descending
                          select new { d.Id, d.Number, d.DocumentDate, SupplierId = c.Id, c.Name, OrderNumber = o.Number, o.SupplierInvoice })
            .Take(MaxRows).ToListAsync(ct);
        var values = await ReceiptValuesAsync(ctx, rows.Select(r => r.Id).ToList(), ct);
        return rows.Select(r => new ReceiptToRegisterDto(r.Id, r.Number, r.DocumentDate, r.SupplierId, r.Name, r.OrderNumber, r.SupplierInvoice,
            values[r.Id].Amount, values[r.Id].Vat)).ToList();
    }

    public async Task<long> RegisterAsync(RegisterVatInvoiceCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var receipt = await db.StockDocuments.AsNoTracking()
                          .SingleOrDefaultAsync(d => d.Id == cmd.ReceiptId && d.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Документ");
        if (receipt.Kind is not (StockOperationKind.Receipt or StockOperationKind.SupplierServiceAct) || receipt.PurchaseOrderId is null
            || receipt.Status != StockDocumentStatus.Posted)
        {
            throw new BusinessRuleException("purchase.vat_invoice.receipt",
                "Счёт-фактура регистрируется к проведённой приёмке или акту услуг поставщика по заказу поставщику.");
        }

        ReceivedVatInvoice.Validate(cmd.Number, cmd.Amount, cmd.VatAmount, cmd.Comment);
        var number = cmd.Number!.Trim();
        var supplierId = receipt.CounterpartyId!.Value;
        if (await db.ReceivedVatInvoices.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.ReceiptDocumentId == receipt.Id
                                                       && i.Status == ReceivedVatInvoiceStatus.Registered, ct))
        {
            throw new BusinessRuleException("purchase.vat_invoice.exists", $"К приёмке {receipt.Number} счёт-фактура уже зарегистрирован.");
        }

        if (await db.ReceivedVatInvoices.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.SupplierId == supplierId && i.SupplierNumber == number
                                                       && i.InvoiceDate == cmd.Date && i.Status == ReceivedVatInvoiceStatus.Registered, ct))
        {
            throw new BusinessRuleException("purchase.vat_invoice.duplicate", $"Счёт-фактура № {number} от {cmd.Date:dd.MM.yyyy} этого поставщика уже зарегистрирован.");
        }

        var invoice = ReceivedVatInvoice.Register(ctx.OrganizationId, supplierId, receipt.Id, number, cmd.Date, cmd.Amount, cmd.VatAmount, cmd.Comment,
            ctx.UserId, clock.UtcNow);
        db.ReceivedVatInvoices.Add(invoice);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ReceivedVatInvoiceRegistered, invoice.Id, null, $"{invoice.Amount:0.00}, НДС {invoice.VatAmount:0.00}",
            $"№ {number} от {cmd.Date:dd.MM.yyyy} к {receipt.Number}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return invoice.Id;
    }

    public async Task CancelAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var invoice = await db.ReceivedVatInvoices.SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Счёт-фактура");
        invoice.EnsureVersion(invoice.RowVersion, rowVersion);
        invoice.Cancel(ctx.UserId, reason, clock.UtcNow);
        Audit(ctx, AuditActions.ReceivedVatInvoiceCancelled, id, "Зарегистрирован", "Отменён", $"№ {invoice.SupplierNumber}: {invoice.CancelReason}");
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<AccessContext> DemandPurchaseViewAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private static void EnsurePeriod(VatJournalFilter filter)
    {
        if (filter.To < filter.From || filter.To.DayNumber - filter.From.DayNumber > MaxPeriodDays)
        {
            throw new BusinessRuleException("report.period", $"Период — от даты «с» до даты «по», не длиннее {MaxPeriodDays} дней.");
        }
    }

    /// <summary>Сумма и НДС приёмок по ценам их заказов.</summary>
    private async Task<Dictionary<long, (decimal Amount, decimal Vat)>> ReceiptValuesAsync(AccessContext ctx, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var docs = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && ids.Contains(d.Id) && d.PurchaseOrderId != null)
            .Select(d => new { d.Id, OrderId = d.PurchaseOrderId!.Value, Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList() })
            .ToListAsync(ct);
        var orderIds = docs.Select(d => d.OrderId).Distinct().ToList();
        var orders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        return docs.ToDictionary(d => d.Id, d =>
        {
            var order = orders[d.OrderId];
            return d.Lines.Aggregate((Amount: 0m, Vat: 0m), (acc, l) =>
            {
                var line = order.Lines.Single(x => x.ItemId == l.ItemId);
                var (a, v) = Money.LineAmounts(l.Quantity, line.Price, line.VatPercent, order.PricesIncludeVat);
                return (acc.Amount + a, acc.Vat + v);
            });
        });
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(ReceivedVatInvoice), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
