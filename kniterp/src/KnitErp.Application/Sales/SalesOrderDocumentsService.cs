using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record SalesMailRowDto(
    long Id, SalesMailKind Kind, string Recipients, string Subject, SalesMailStatus Status, bool IsAuto, int Attempts, string? LastError,
    DateTime CreatedAtUtc, DateTime? SentAtUtc, string CreatedBy, string Attachments, byte[] RowVersion)
{
    public string KindName => SalesMailMessage.KindName(Kind);
    public string StatusName => SalesMailMessage.StatusName(Status);
}

/// <summary>Документ заказа для печати и отправки: сам заказ, действующий счёт, проведённые отгрузки (УПД).</summary>
public sealed record SalesPrintableDto(SalesMailDocument Document, long Id, string Number, DateOnly Date, string PrintHref)
{
    public string DocumentName => SalesMailMessage.DocumentName(Document);
}

/// <summary>Почему учётную связь нельзя снять здесь и где её менять (D1125).</summary>
public enum SalesLinkHint : byte
{
    None = 0,
    StockDocument = 1,
    Invoice = 2,
    Production = 3,
    Crm = 4,
}

/// <summary>
/// Связанный документ заказа (D1125). Manual — ручная связь (её можно отвязать: LinkId); учётная связь отвязывается только там, где это уже
/// безопасно — снятие разноски оплаты (AllocationId), иначе Hint подсказывает, где её менять. Hidden — нет права видеть документ.
/// Party — контрагент или позиция (данные); State и SubKind — названия статуса и вида из домена (переводятся при выводе).
/// </summary>
public sealed record SalesLinkedRowDto(
    SalesLinkKind Kind, long DocumentId, string? Number, DateOnly? Date, string? Party, string? State, string? SubKind, decimal? Amount, string? Href,
    bool Manual, long? LinkId = null, byte[]? LinkRowVersion = null, long? AllocationId = null, byte[]? AllocationRowVersion = null,
    SalesLinkHint Hint = SalesLinkHint.None, string? Comment = null)
{
    public string KindName => SalesDocumentLink.KindName(Kind);
    public bool Hidden => Number is null;
}

public sealed record SalesOrderDocumentsDto(
    long OrderId, bool CanSend, bool CanLink, bool CanSeePrices, bool MailConfigured, string? CustomerEmail, bool CustomerNoAutoSend,
    IReadOnlyList<SalesPrintableDto> Printables, IReadOnlyList<SalesMailRowDto> Mails, IReadOnlyList<SalesLinkedRowDto> Links);

public sealed record SalesMailDocumentRef(SalesMailDocument Document, long Id);

public sealed record SalesMailDraftDto(
    long OrderId, string? To, string Subject, string Body, IReadOnlyList<SalesPrintableDto> Documents, IReadOnlyList<SalesMailDocumentRef> Selected,
    bool MailConfigured);

public sealed record SalesMailRequest(long OrderId, string? To, string? Subject, string? Body, IReadOnlyList<SalesMailDocumentRef> Documents);

public sealed record SalesLinkCandidateDto(long Id, string Number, DateOnly? Date, string? Info);

public sealed record CustomerMailSettingsDto(long CounterpartyId, string? Email, bool NoAutoSend, bool CanEdit, byte[]? RowVersion);

/// <summary>
/// Документы заказа покупателя (D1122–D1125): печать и отправка клиенту (письмо в очередь, PDF при отправке), журнал писем,
/// связанные документы (учётные и ручные «связать / отвязать»), почта покупателя для документов.
/// Смотреть — «Продажи: просмотр»; отправлять — «Продажи: заказы и оплаты» и «Цены и суммы»; связывать — «Продажи: заказы и оплаты»
/// и право смотреть документ связываемого вида (закупки, производство, склад своей области, цены для счетов и оплат). Чужое — 404.
/// </summary>
public sealed class SalesOrderDocumentsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender email)
{
    public const int MaxMails = 50;
    public const int MaxCandidates = 20;

    public async Task<SalesOrderDocumentsDto> PanelAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await FindOrderAsync(ctx, orderId, ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var printables = prices ? await PrintablesAsync(ctx, order, ct) : [];
        var settings = await db.CustomerMailSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId && s.CounterpartyId == order.CustomerId, ct);
        var mails = await (from m in db.SalesMailMessages.AsNoTracking().Include(m => m.Attachments)
                           where m.OrganizationId == ctx.OrganizationId && m.SalesOrderId == orderId
                           join u in db.Users.AsNoTracking() on m.CreatedByUserId equals u.Id
                           orderby m.Id descending
                           select new { m, u.DisplayName }).Take(MaxMails).ToListAsync(ct);
        return new SalesOrderDocumentsDto(orderId, ctx.Permissions.Has(Permissions.SalesEdit) && prices, ctx.Permissions.Has(Permissions.SalesEdit), prices,
            email.IsConfigured, settings?.Email, settings?.NoAutoSend ?? false, printables,
            mails.Select(x => new SalesMailRowDto(x.m.Id, x.m.Kind, x.m.Recipients, x.m.Subject, x.m.Status, x.m.IsAuto, x.m.Attempts, x.m.LastError,
                x.m.CreatedAtUtc, x.m.SentAtUtc, x.DisplayName,
                string.Join(", ", x.m.Attachments.Select(a => SalesMailMessage.DocumentName(a.Document) + " " + Number(printables, a))), x.m.RowVersion)).ToList(),
            await LinksAsync(ctx, order, prices, ct));
    }

    /// <summary>Черновик письма: адрес покупателя, тема и текст по шаблону основного документа, отмеченный документ.</summary>
    /// <summary>D1141: документы заказа для печати комплектом — заказ, действующие счета, проведённые отгрузки. Нужны «Цены и суммы».</summary>
    public async Task<IReadOnlyList<SalesPrintableDto>> PrintSetAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return await PrintablesAsync(ctx, await FindOrderAsync(ctx, orderId, ct), ct);
    }

    public async Task<SalesMailDraftDto> DraftAsync(long orderId, SalesMailDocument document, long? documentId = null, CancellationToken ct = default)
    {
        var ctx = await DemandSendAsync(ct);
        var order = await FindOrderAsync(ctx, orderId, ct);
        var printables = await PrintablesAsync(ctx, order, ct);
        var primary = printables.FirstOrDefault(p => p.Document == document && (documentId is null || p.Id == documentId))
                      ?? throw new NotFoundException(SalesMailMessage.DocumentName(document));
        var kind = KindOf(document);
        var (subject, body, _) = await SalesMailOutbox.TemplateAsync(db, ctx.OrganizationId, kind, ct);
        var values = await SalesMailOutbox.ValuesAsync(db, ctx.OrganizationId, kind, orderId, primary.Id, null, ct);
        var to = await db.CustomerMailSettings.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && s.CounterpartyId == order.CustomerId).Select(s => s.Email).SingleOrDefaultAsync(ct);
        return new SalesMailDraftDto(orderId, to, SalesMailOutbox.Render(subject, values), SalesMailOutbox.Render(body, values), printables,
            [new SalesMailDocumentRef(primary.Document, primary.Id)], email.IsConfigured);
    }

    /// <summary>Поставить письмо в очередь. Документы — только этого заказа: сам заказ, его счёт, его проведённые отгрузки.</summary>
    public async Task<long> SendAsync(SalesMailRequest request, CancellationToken ct = default)
    {
        var ctx = await DemandSendAsync(ct);
        var order = await FindOrderAsync(ctx, request.OrderId, ct);
        if (request.Documents.Count == 0)
        {
            throw new BusinessRuleException("sales.mail.no_documents", "Отметьте документы для отправки.");
        }

        var printables = await PrintablesAsync(ctx, order, ct);
        foreach (var d in request.Documents)
        {
            if (!printables.Any(p => p.Document == d.Document && p.Id == d.Id))
            {
                throw new NotFoundException(SalesMailMessage.DocumentName(d.Document));
            }
        }

        var first = request.Documents.OrderBy(d => d.Document).First();
        var message = SalesMailMessage.Create(ctx.OrganizationId, KindOf(first.Document), order.Id, order.CustomerId, request.To, request.Subject,
            request.Body, false, null, ctx.UserId, clock.UtcNow, request.Documents.Select(d => (d.Document, d.Id)));
        db.SalesMailMessages.Add(message);
        Audit(ctx, AuditActions.SalesMailQueued, nameof(SalesOrder), order.Id, null,
            string.Join(", ", request.Documents.Select(d => SalesMailMessage.DocumentName(d.Document) + " " + printables.First(p => p.Document == d.Document && p.Id == d.Id).Number)),
            $"{order.Number}: вручную");
        await SaveAsync(ct);
        return message.Id;
    }

    public async Task RetryMailAsync(long messageId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, message) = await LoadMailAsync(messageId, rowVersion, ct);
        message.Retry(clock.UtcNow);
        Audit(ctx, AuditActions.SalesMailChanged, nameof(SalesOrder), message.SalesOrderId, "Не отправлено", "В очереди", message.Subject);
        await SaveAsync(ct);
    }

    public async Task CancelMailAsync(long messageId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, message) = await LoadMailAsync(messageId, rowVersion, ct);
        var before = SalesMailMessage.StatusName(message.Status);
        message.Cancel();
        Audit(ctx, AuditActions.SalesMailChanged, nameof(SalesOrder), message.SalesOrderId, before, "Отменено", message.Subject);
        await SaveAsync(ct);
    }

    /// <summary>PDF документа для скачивания: те же права, что у печатной формы.</summary>
    public async Task<EmailAttachment> PdfAsync(SalesMailDocument document, long id, ISalesPdfRenderer renderer, CancellationToken ct = default) =>
        await SalesDocumentPdf.BuildAsync(db, guard, renderer, document, id, ct);

    /// <summary>Поиск документа для ручной связи по номеру (часть номера), в пределах организации и права на вид документа.</summary>
    public async Task<IReadOnlyList<SalesLinkCandidateDto>> SearchAsync(long orderId, SalesLinkKind kind, string? text, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await FindOrderAsync(ctx, orderId, ct);
        await DemandKindAsync(ctx, kind, ct);
        var q = (text ?? string.Empty).Trim();
        var org = ctx.OrganizationId;
        switch (kind)
        {
            case SalesLinkKind.SalesOrder:
                return await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.Id != orderId && o.Number.Contains(q))
                    .Join(db.Counterparties.AsNoTracking(), o => o.CustomerId, c => c.Id, (o, c) => new { o.Id, o.Number, o.OrderDate, c.Name })
                    .OrderByDescending(x => x.Id).Take(MaxCandidates)
                    .Select(x => new SalesLinkCandidateDto(x.Id, x.Number, x.OrderDate, x.Name)).ToListAsync(ct);
            case SalesLinkKind.PurchaseOrder:
                return await db.PurchaseOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.Number.Contains(q))
                    .Join(db.Counterparties.AsNoTracking(), o => o.SupplierId, c => c.Id, (o, c) => new { o.Id, o.Number, o.OrderDate, c.Name })
                    .OrderByDescending(x => x.Id).Take(MaxCandidates)
                    .Select(x => new SalesLinkCandidateDto(x.Id, x.Number, x.OrderDate, x.Name)).ToListAsync(ct);
            case SalesLinkKind.CustomerInvoice:
                return await db.CustomerInvoices.AsNoTracking().Where(i => i.OrganizationId == org && i.Number.Contains(q))
                    .Join(db.Counterparties.AsNoTracking(), i => i.CustomerId, c => c.Id, (i, c) => new { i.Id, i.Number, i.InvoiceDate, c.Name })
                    .OrderByDescending(x => x.Id).Take(MaxCandidates)
                    .Select(x => new SalesLinkCandidateDto(x.Id, x.Number, x.InvoiceDate, x.Name)).ToListAsync(ct);
            case SalesLinkKind.CustomerPayment:
                return await db.CustomerPayments.AsNoTracking().Where(p => p.OrganizationId == org && p.Number.Contains(q))
                    .Join(db.Counterparties.AsNoTracking(), p => p.CustomerId, c => c.Id, (p, c) => new { p.Id, p.Number, p.PaymentDate, c.Name })
                    .OrderByDescending(x => x.Id).Take(MaxCandidates)
                    .Select(x => new SalesLinkCandidateDto(x.Id, x.Number, x.PaymentDate, x.Name)).ToListAsync(ct);
            case SalesLinkKind.ProductionOrder:
                return await db.ProductionOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.Number.Contains(q))
                    .Join(db.Items.AsNoTracking(), o => o.ItemId, i => i.Id, (o, i) => new { o.Id, o.Number, o.OrderDate, i.Name })
                    .OrderByDescending(x => x.Id).Take(MaxCandidates)
                    .Select(x => new SalesLinkCandidateDto(x.Id, x.Number, x.OrderDate, x.Name)).ToListAsync(ct);
            case SalesLinkKind.StockDocument:
                var visible = WarehouseScope.Visible(ctx, WarehouseScope.StockViews);
                var docs = db.StockDocuments.AsNoTracking().Where(d => d.OrganizationId == org && d.Number.Contains(q));
                if (visible is not null)
                {
                    docs = docs.Where(d => visible.Contains(d.WarehouseId));
                }

                var list = await docs.OrderByDescending(d => d.Id).Take(MaxCandidates).Select(d => new { d.Id, d.Number, d.DocumentDate, d.Kind }).ToListAsync(ct);
                return list.Select(d => new SalesLinkCandidateDto(d.Id, d.Number, d.DocumentDate, StockDocument.KindName(d.Kind))).ToList();
            default:
                throw new BusinessRuleException("sales.link.kind", "Этот вид документа связать вручную нельзя.");
        }
    }

    public async Task<long> LinkAsync(long orderId, SalesLinkKind kind, long documentId, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var order = await FindOrderAsync(ctx, orderId, ct);
        await DemandKindAsync(ctx, kind, ct);
        var number = (await DescribeAsync(ctx, kind, documentId, ct) ?? throw new NotFoundException(SalesDocumentLink.KindName(kind))).Number;
        if (await IsAccountingLinkAsync(ctx, order.Id, kind, documentId, ct))
        {
            throw new BusinessRuleException("sales.link.accounting", $"{SalesDocumentLink.KindName(kind)} {number} уже связан с заказом по учёту.");
        }

        if (await db.SalesDocumentLinks.AnyAsync(l => l.OrganizationId == ctx.OrganizationId && l.SalesOrderId == order.Id && l.Kind == kind
                                                      && l.DocumentId == documentId && l.RemovedAtUtc == null, ct))
        {
            throw new BusinessRuleException("sales.link.exists", $"{SalesDocumentLink.KindName(kind)} {number} уже связан с заказом.");
        }

        var link = SalesDocumentLink.Create(ctx.OrganizationId, order.Id, kind, documentId, comment, ctx.UserId, clock.UtcNow);
        db.SalesDocumentLinks.Add(link);
        Audit(ctx, AuditActions.SalesDocumentLinked, nameof(SalesOrder), order.Id, null, $"{SalesDocumentLink.KindName(kind)} {number}", order.Number);
        await SaveAsync(ct);
        return link.Id;
    }

    public async Task UnlinkAsync(long linkId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var link = await db.SalesDocumentLinks.SingleOrDefaultAsync(l => l.Id == linkId && l.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Связь документов");
        link.EnsureVersion(link.RowVersion, rowVersion);
        var order = await db.SalesOrders.AsNoTracking().Where(o => o.Id == link.SalesOrderId).Select(o => o.Number).SingleAsync(ct);
        var number = (await DescribeAsync(ctx, link.Kind, link.DocumentId, ct))?.Number ?? link.DocumentId.ToString();
        link.Remove(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.SalesDocumentUnlinked, nameof(SalesOrder), link.SalesOrderId, $"{SalesDocumentLink.KindName(link.Kind)} {number}", null, order);
        await SaveAsync(ct);
    }

    public async Task<CustomerMailSettingsDto> CustomerMailAsync(long counterpartyId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await EnsureCounterpartyAsync(ctx, counterpartyId, ct);
        var s = await db.CustomerMailSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId && x.CounterpartyId == counterpartyId, ct);
        return new CustomerMailSettingsDto(counterpartyId, s?.Email, s?.NoAutoSend ?? false, ctx.Permissions.Has(Permissions.SalesEdit), s?.RowVersion);
    }

    public async Task SetCustomerMailAsync(long counterpartyId, string? emailAddress, bool noAutoSend, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await EnsureCounterpartyAsync(ctx, counterpartyId, ct);
        var s = await db.CustomerMailSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId && x.CounterpartyId == counterpartyId, ct);
        var before = s is null ? null : Describe(s);
        if (s is null)
        {
            s = CustomerMailSettings.Create(ctx.OrganizationId, counterpartyId);
            s.Set(emailAddress, noAutoSend);
            db.CustomerMailSettings.Add(s);
        }
        else
        {
            s.EnsureVersion(s.RowVersion, rowVersion ?? []);
            try
            {
                s.Set(emailAddress, noAutoSend);
            }
            catch
            {
                db.DiscardChanges();
                throw;
            }
        }

        // Адрес в журнал не пишем: это контактные данные.
        Audit(ctx, AuditActions.CustomerMailSettingsChanged, "Counterparty", counterpartyId, before, Describe(s), null);
        await SaveAsync(ct);
    }

    private static string Describe(CustomerMailSettings s) =>
        (s.Email is null ? "почта не указана" : $"почта указана ({s.Email.Split(',').Length})") + (s.NoAutoSend ? ", не отправлять автоматически" : "");

    internal static SalesMailKind KindOf(SalesMailDocument document) => document switch
    {
        SalesMailDocument.Invoice => SalesMailKind.Invoice,
        SalesMailDocument.Upd => SalesMailKind.Upd,
        _ => SalesMailKind.Order,
    };

    private static string Number(IReadOnlyList<SalesPrintableDto> printables, SalesMailAttachment a) =>
        printables.FirstOrDefault(p => p.Document == a.Document && p.Id == a.DocumentId)?.Number ?? string.Empty;

    private async Task<List<SalesPrintableDto>> PrintablesAsync(AccessContext ctx, SalesOrder order, CancellationToken ct)
    {
        var list = new List<SalesPrintableDto> { new(SalesMailDocument.Order, order.Id, order.Number, order.OrderDate, $"print/sales-order/{order.Id}") };
        var invoices = await db.CustomerInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.SalesOrderId == order.Id && i.Status == CustomerInvoiceStatus.Issued)
            .OrderBy(i => i.Id).Select(i => new { i.Id, i.Number, i.InvoiceDate }).ToListAsync(ct);
        list.AddRange(invoices.Select(i => new SalesPrintableDto(SalesMailDocument.Invoice, i.Id, i.Number, i.InvoiceDate, $"print/invoice/{i.Id}")));
        var shipments = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == order.Id && d.Kind == StockOperationKind.Shipment
                        && d.Status == StockDocumentStatus.Posted)
            .OrderBy(d => d.Id).Select(d => new { d.Id, d.Number, d.DocumentDate }).ToListAsync(ct);
        list.AddRange(shipments.Select(d => new SalesPrintableDto(SalesMailDocument.Upd, d.Id, d.Number, d.DocumentDate, $"print/upd/{d.Id}")));
        return list;
    }

    private async Task<List<SalesLinkedRowDto>> LinksAsync(AccessContext ctx, SalesOrder order, bool prices, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var rows = new List<SalesLinkedRowDto>();
        var docs = await db.StockDocuments.AsNoTracking().Where(d => d.OrganizationId == org && d.SalesOrderId == order.Id).OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.Number, d.DocumentDate, d.Kind, d.Status }).ToListAsync(ct);
        rows.AddRange(docs.Select(d => new SalesLinkedRowDto(SalesLinkKind.StockDocument, d.Id, d.Number, d.DocumentDate, null,
            StockDocument.StatusName(d.Status), StockDocument.KindName(d.Kind), null, $"stock-documents/{d.Id}", false, Hint: SalesLinkHint.StockDocument)));
        if (prices)
        {
            var invoices = await db.CustomerInvoices.AsNoTracking().Where(i => i.OrganizationId == org && i.SalesOrderId == order.Id).OrderBy(i => i.Id)
                .Select(i => new { i.Id, i.Number, i.InvoiceDate, i.Status }).ToListAsync(ct);
            rows.AddRange(invoices.Select(i => new SalesLinkedRowDto(SalesLinkKind.CustomerInvoice, i.Id, i.Number, i.InvoiceDate, null,
                CustomerInvoice.StatusName(i.Status), null, null, $"customer-invoices/{i.Id}", false, Hint: SalesLinkHint.Invoice)));
            var allocations = await (from a in db.CustomerPaymentAllocations.AsNoTracking()
                                     where a.OrganizationId == org && a.OrderId == order.Id && a.RemovedAtUtc == null
                                     join p in db.CustomerPayments.AsNoTracking() on a.PaymentId equals p.Id
                                     orderby p.PaymentDate, a.Id
                                     select new { a.Id, a.Amount, a.RowVersion, p.Number, p.PaymentDate, PaymentId = p.Id }).ToListAsync(ct);
            rows.AddRange(allocations.Select(a => new SalesLinkedRowDto(SalesLinkKind.CustomerPayment, a.PaymentId, a.Number, a.PaymentDate, null, null, null,
                a.Amount, "customer-payments", false, AllocationId: a.Id, AllocationRowVersion: a.RowVersion)));
        }

        if (ctx.Permissions.Has(Permissions.ProductionOrderView))
        {
            var production = await db.ProductionOrders.AsNoTracking().Where(p => p.OrganizationId == org && p.SalesOrderId == order.Id).OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Number, p.OrderDate, p.Status }).ToListAsync(ct);
            rows.AddRange(production.Select(p => new SalesLinkedRowDto(SalesLinkKind.ProductionOrder, p.Id, p.Number, p.OrderDate, null,
                KnitErp.Domain.Production.ProductionOrder.StatusName(p.Status), null, null, $"production-orders/{p.Id}", false, Hint: SalesLinkHint.Production)));
        }

        if (order.CrmDealId is { } dealId)
        {
            var canCrm = ctx.Permissions.Has(Permissions.CrmView) || ctx.Permissions.Has(Permissions.CrmAll);
            var deal = await db.CrmDeals.AsNoTracking().Where(d => d.Id == dealId && d.OrganizationId == org).Select(d => new { d.Number, d.Title })
                .SingleOrDefaultAsync(ct);
            if (deal is not null)
            {
                rows.Add(new SalesLinkedRowDto(SalesLinkKind.CrmDeal, dealId, canCrm ? deal.Number : null, null, canCrm ? deal.Title : null, null, null, null,
                    canCrm ? $"crm/deals/{dealId}" : null, false, Hint: SalesLinkHint.Crm));
            }
        }

        var manual = await db.SalesDocumentLinks.AsNoTracking()
            .Where(l => l.OrganizationId == org && l.SalesOrderId == order.Id && l.RemovedAtUtc == null).OrderBy(l => l.Id).ToListAsync(ct);
        foreach (var l in manual)
        {
            var d = await CanViewKindAsync(ctx, l.Kind, l.DocumentId, ct) ? await DescribeAsync(ctx, l.Kind, l.DocumentId, ct) : null;
            rows.Add(d is { } x
                ? new SalesLinkedRowDto(l.Kind, l.DocumentId, x.Number, x.Date, x.Party, x.State, x.SubKind, x.Amount, x.Href, true, l.Id, l.RowVersion,
                    Comment: l.Comment)
                : new SalesLinkedRowDto(l.Kind, l.DocumentId, null, null, null, null, null, null, null, true, l.Id, l.RowVersion, Comment: l.Comment));
        }

        return rows;
    }

    /// <summary>Номер, дата, пояснение и ссылка документа в пределах организации; null — нет такого.</summary>
    private async Task<Described?> DescribeAsync(AccessContext ctx, SalesLinkKind kind, long id,
        CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        switch (kind)
        {
            case SalesLinkKind.SalesOrder:
                var so = await db.SalesOrders.AsNoTracking().Where(o => o.Id == id && o.OrganizationId == org)
                    .Join(db.Counterparties.AsNoTracking(), o => o.CustomerId, c => c.Id, (o, c) => new { o.Number, o.OrderDate, c.Name }).SingleOrDefaultAsync(ct);
                return so is null ? null : new Described(so.Number, so.OrderDate, so.Name, null, null, null, $"sales-orders/{id}");
            case SalesLinkKind.PurchaseOrder:
                var po = await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == id && o.OrganizationId == org)
                    .Join(db.Counterparties.AsNoTracking(), o => o.SupplierId, c => c.Id, (o, c) => new { o.Number, o.OrderDate, c.Name }).SingleOrDefaultAsync(ct);
                return po is null ? null : new Described(po.Number, po.OrderDate, po.Name, null, null, null, $"purchase-orders/{id}");
            case SalesLinkKind.CustomerInvoice:
                var ci = await db.CustomerInvoices.AsNoTracking().Where(i => i.Id == id && i.OrganizationId == org)
                    .Select(i => new { i.Number, i.InvoiceDate, i.Status }).SingleOrDefaultAsync(ct);
                return ci is null ? null : new Described(ci.Number, ci.InvoiceDate, null, CustomerInvoice.StatusName(ci.Status), null, null, $"customer-invoices/{id}");
            case SalesLinkKind.CustomerPayment:
                var cp = await db.CustomerPayments.AsNoTracking().Where(p => p.Id == id && p.OrganizationId == org)
                    .Select(p => new { p.Number, p.PaymentDate, p.Amount }).SingleOrDefaultAsync(ct);
                return cp is null ? null : new Described(cp.Number, cp.PaymentDate, null, null, null, cp.Amount, "customer-payments");
            case SalesLinkKind.ProductionOrder:
                var pr = await db.ProductionOrders.AsNoTracking().Where(p => p.Id == id && p.OrganizationId == org)
                    .Select(p => new { p.Number, p.OrderDate, p.Status }).SingleOrDefaultAsync(ct);
                return pr is null ? null : new Described(pr.Number, pr.OrderDate, null, KnitErp.Domain.Production.ProductionOrder.StatusName(pr.Status), null, null,
                    $"production-orders/{id}");
            case SalesLinkKind.StockDocument:
                var visible = WarehouseScope.Visible(ctx, WarehouseScope.StockViews);
                var sd = await db.StockDocuments.AsNoTracking().Where(d => d.Id == id && d.OrganizationId == org)
                    .Select(d => new { d.Number, d.DocumentDate, d.Kind, d.WarehouseId }).SingleOrDefaultAsync(ct);
                return sd is null || (visible is not null && !visible.Contains(sd.WarehouseId))
                    ? null
                    : new Described(sd.Number, sd.DocumentDate, null, null, StockDocument.KindName(sd.Kind), null, $"stock-documents/{id}");
            default:
                return null;
        }
    }

    private sealed record Described(string Number, DateOnly? Date, string? Party, string? State, string? SubKind, decimal? Amount, string? Href);

    private async Task<bool> IsAccountingLinkAsync(AccessContext ctx, long orderId, SalesLinkKind kind, long id, CancellationToken ct) => kind switch
    {
        SalesLinkKind.StockDocument => await db.StockDocuments.AnyAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == orderId, ct),
        SalesLinkKind.CustomerInvoice => await db.CustomerInvoices.AnyAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId && i.SalesOrderId == orderId, ct),
        SalesLinkKind.CustomerPayment => await db.CustomerPaymentAllocations.AnyAsync(a => a.PaymentId == id && a.OrganizationId == ctx.OrganizationId
                                                                                          && a.OrderId == orderId && a.RemovedAtUtc == null, ct),
        SalesLinkKind.ProductionOrder => await db.ProductionOrders.AnyAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId && p.SalesOrderId == orderId, ct),
        _ => false,
    };

    private static bool HasStockView(AccessContext ctx) => WarehouseScope.StockViews.Any(ctx.Permissions.Has);

    private Task<bool> CanViewKindAsync(AccessContext ctx, SalesLinkKind kind, long id, CancellationToken ct) => Task.FromResult(kind switch
    {
        SalesLinkKind.PurchaseOrder => ctx.Permissions.Has(Permissions.PurchaseView),
        SalesLinkKind.CustomerInvoice or SalesLinkKind.CustomerPayment => ctx.Permissions.Has(Permissions.PriceView),
        SalesLinkKind.ProductionOrder => ctx.Permissions.Has(Permissions.ProductionOrderView),
        SalesLinkKind.StockDocument => HasStockView(ctx),
        _ => true,
    });

    /// <summary>Связать можно только то, что пользователь вправе видеть; отказ пишется в журнал.</summary>
    private async Task DemandKindAsync(AccessContext ctx, SalesLinkKind kind, CancellationToken ct)
    {
        if (!SalesDocumentLink.ManualKinds.Contains(kind))
        {
            throw new BusinessRuleException("sales.link.kind", "Этот вид документа связать вручную нельзя.");
        }

        switch (kind)
        {
            case SalesLinkKind.PurchaseOrder:
                await guard.DemandAsync(Permissions.PurchaseView, ct);
                break;
            case SalesLinkKind.CustomerInvoice or SalesLinkKind.CustomerPayment:
                await guard.DemandAsync(Permissions.PriceView, ct);
                break;
            case SalesLinkKind.ProductionOrder:
                await guard.DemandAsync(Permissions.ProductionOrderView, ct);
                break;
            case SalesLinkKind.StockDocument when !HasStockView(ctx):
                await guard.DenyAsync(ctx, Permissions.WarehouseReportView, ct);
                throw new AccessDeniedException(Permissions.WarehouseReportView);
        }
    }

    private async Task<AccessContext> DemandSendAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<(AccessContext, SalesMailMessage)> LoadMailAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var message = await db.SalesMailMessages.SingleOrDefaultAsync(m => m.Id == id && m.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Письмо");
        message.EnsureVersion(message.RowVersion, rowVersion);
        return (ctx, message);
    }

    private async Task<SalesOrder> FindOrderAsync(AccessContext ctx, long id, CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        if (await SalesReceivables.VisibleManagersAsync(db, ctx, clock.UtcNow, ct) is { } visible
            && !(order.ResponsibleUserId is { } responsible && visible.Contains(responsible)))
        {
            throw new NotFoundException("Заказ покупателя"); // D1110: заказ вне видимости роли
        }

        return order;
    }

    private async Task EnsureCounterpartyAsync(AccessContext ctx, long id, CancellationToken ct)
    {
        if (!await db.Counterparties.AsNoTracking().AnyAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Контрагент");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
