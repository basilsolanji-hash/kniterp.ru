using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Значения подстановок письма: номер и дата документа, сумма, покупатель, этап, юрлицо-продавец.</summary>
internal sealed record SalesMailValues(
    long OrderId, long CustomerId, string Number, DateOnly Date, decimal? Amount, string Currency, string Customer, string? Stage, string Organization);

/// <summary>
/// Автоматические письма клиенту (D1124). Вызывается сервисами событий внутри их транзакции: письмо ставится в очередь вместе с событием
/// и не появляется для отменённого действия. Условия: шаблон вида с «отправлять автоматически», для этапа — этап в списке,
/// у покупателя указана почта и не стоит «не отправлять автоматически». Одно событие — одно письмо (DedupKey).
/// Права проверил вызывающий сервис; письмо отправляется фоновым отправителем от имени организации.
/// </summary>
internal static class SalesMailOutbox
{
    public static Task OnOrderConfirmedAsync(IKnitErpDbContext db, AccessContext ctx, long orderId, DateTime nowUtc, CancellationToken ct) =>
        QueueAutoAsync(db, ctx, SalesMailKind.Order, orderId, orderId, null, $"order:{orderId}", nowUtc, ct);

    public static async Task OnInvoiceIssuedAsync(IKnitErpDbContext db, AccessContext ctx, long invoiceId, DateTime nowUtc, CancellationToken ct)
    {
        var orderId = await db.CustomerInvoices.AsNoTracking().Where(i => i.Id == invoiceId && i.OrganizationId == ctx.OrganizationId)
            .Select(i => i.SalesOrderId).SingleAsync(ct);
        await QueueAutoAsync(db, ctx, SalesMailKind.Invoice, orderId, invoiceId, null, $"invoice:{invoiceId}", nowUtc, ct);
    }

    public static async Task OnShipmentPostedAsync(IKnitErpDbContext db, AccessContext ctx, long documentId, DateTime nowUtc, CancellationToken ct)
    {
        var orderId = await db.StockDocuments.AsNoTracking()
            .Where(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId && d.Kind == StockOperationKind.Shipment)
            .Select(d => d.SalesOrderId).SingleOrDefaultAsync(ct);
        if (orderId is { } id)
        {
            await QueueAutoAsync(db, ctx, SalesMailKind.Upd, id, documentId, null, $"upd:{documentId}", nowUtc, ct);
        }
    }

    /// <summary>Этап заказа сменился (вручную или автоматически, D110): письмо о статусе — одно на пару «заказ, этап».</summary>
    public static Task OnStageChangedAsync(IKnitErpDbContext db, AccessContext ctx, long orderId, long? stageId, DateTime nowUtc, CancellationToken ct) =>
        stageId is { } sid ? QueueAutoAsync(db, ctx, SalesMailKind.Stage, orderId, orderId, sid, $"stage:{orderId}:{sid}", nowUtc, ct) : Task.CompletedTask;

    private static async Task QueueAutoAsync(IKnitErpDbContext db, AccessContext ctx, SalesMailKind kind, long orderId, long documentId, long? stageId,
        string dedupKey, DateTime nowUtc, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var template = await db.SalesMailTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.OrganizationId == org && t.Kind == kind, ct);
        if (template is not { AutoSend: true })
        {
            return;
        }

        if (stageId is { } sid && !await db.SalesMailStages.AsNoTracking().AnyAsync(s => s.OrganizationId == org && s.StageId == sid, ct))
        {
            return;
        }

        var customerId = await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId && o.OrganizationId == org)
            .Select(o => (long?)o.CustomerId).SingleOrDefaultAsync(ct);
        if (customerId is null)
        {
            return;
        }

        var settings = await db.CustomerMailSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == org && s.CounterpartyId == customerId, ct);
        if (settings is not { Email: not null, NoAutoSend: false })
        {
            return;
        }

        if (db.SalesMailMessages.Local.Any(m => m.OrganizationId == org && m.DedupKey == dedupKey)
            || await db.SalesMailMessages.AsNoTracking().AnyAsync(m => m.OrganizationId == org && m.DedupKey == dedupKey, ct))
        {
            return;
        }

        var document = kind switch
        {
            SalesMailKind.Order => SalesMailDocument.Order,
            SalesMailKind.Invoice => SalesMailDocument.Invoice,
            SalesMailKind.Upd => SalesMailDocument.Upd,
            _ => (SalesMailDocument?)null,
        };
        var values = await ValuesAsync(db, org, kind, orderId, documentId, stageId, ct);
        var message = SalesMailMessage.Create(org, kind, orderId, values.CustomerId, settings.Email, Render(template.Subject, values),
            Render(template.Body, values), true, dedupKey, ctx.UserId, nowUtc, document is { } d ? [(d, documentId)] : []);
        db.SalesMailMessages.Add(message);
        db.AuditEntries.Add(AuditEntry.Create(nowUtc, org, ctx.UserId, AuditActions.SalesMailQueued, nameof(SalesOrder), orderId.ToString(), null,
            SalesMailMessage.KindName(kind), $"{values.Number}: автоматически", null));
    }

    /// <summary>Шаблон вида: свой или по умолчанию.</summary>
    internal static async Task<(string Subject, string Body, bool Auto)> TemplateAsync(IKnitErpDbContext db, long organizationId, SalesMailKind kind,
        CancellationToken ct)
    {
        var t = await db.SalesMailTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Kind == kind, ct);
        if (t is not null)
        {
            return (t.Subject, t.Body, t.AutoSend);
        }

        var (subject, body) = SalesMailText.Default(kind);
        return (subject, body, false);
    }

    internal static string Render(string template, SalesMailValues v) =>
        SalesMailText.Render(template, v.Number, v.Date, v.Customer, v.Amount, v.Currency, v.Stage, v.Organization);

    /// <summary>Подстановки по документу письма: заказ — его номер и сумма; счёт — счёта; УПД — отгрузки (по ценам заказа).</summary>
    internal static async Task<SalesMailValues> ValuesAsync(IKnitErpDbContext db, long org, SalesMailKind kind, long orderId, long documentId, long? stageId,
        CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == order.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var seller = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && e.Id == order.LegalEntityId).Select(e => e.ShortName)
            .SingleAsync(ct);
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CurrencyCode).SingleAsync(ct);
        var stageKey = stageId ?? order.StageId;
        var stageName = stageKey is { } sk
            ? await db.SalesOrderStages.AsNoTracking().Where(s => s.OrganizationId == org && s.Id == sk).Select(s => s.Name).SingleOrDefaultAsync(ct)
            : null;
        switch (kind)
        {
            case SalesMailKind.Invoice:
                var invoice = await db.CustomerInvoices.AsNoTracking().Include(i => i.Lines)
                                  .SingleOrDefaultAsync(i => i.Id == documentId && i.OrganizationId == org && i.SalesOrderId == orderId, ct)
                              ?? throw new NotFoundException("Счёт покупателю");
                return new SalesMailValues(orderId, order.CustomerId, invoice.Number, invoice.InvoiceDate, invoice.Total, currency, customer, stageName, seller);
            case SalesMailKind.Upd:
                var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                              .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == org && d.SalesOrderId == orderId, ct)
                          ?? throw new NotFoundException("Документ");
                var total = doc.Lines.Sum(l => order.Lines.FirstOrDefault(o => o.ItemId == l.ItemId) is { } ol
                    ? Money.LineAmounts(l.Quantity, ol.NetPrice, ol.VatPercent, order.PricesIncludeVat).Amount
                    : 0m);
                return new SalesMailValues(orderId, order.CustomerId, doc.Number, doc.DocumentDate, total, currency, customer, stageName, seller);
            default:
                return new SalesMailValues(orderId, order.CustomerId, order.Number, order.OrderDate, order.Total, currency, customer, stageName, seller);
        }
    }
}

/// <summary>
/// Отправитель писем клиенту (D1123) — шаг фонового процесса: берёт подошедшие письма очереди, формирует PDF вложений (на момент отправки)
/// и отправляет через почту сервера. Без настроенной почты письмо сразу получает статус «не отправлено» с понятной причиной; ошибка
/// почтового сервера — повтор с паузой (до пяти попыток). Документы формируются от имени поставившего письмо в пределах его организации.
/// </summary>
public sealed class SalesMailDispatcher(IKnitErpDbContext db, IEmailSender sender, ISalesPdfRenderer renderer, IClock clock)
{
    public const int Batch = 20;
    public const string NotConfigured = "Почта сервера не настроена (SMTP): письмо не отправлено. Настройте почту и нажмите «Повторить».";

    /// <summary>Отправить подошедшие письма. Возвращает число отправленных.</summary>
    public async Task<int> DeliverAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.SalesMailMessages.Include(m => m.Attachments)
            .Where(m => m.Status == SalesMailStatus.Queued && m.NextAttemptAtUtc <= now)
            .OrderBy(m => m.Id).Take(Batch).ToListAsync(ct);
        var sent = 0;
        foreach (var m in due)
        {
            if (!sender.IsConfigured)
            {
                m.MarkFailed(NotConfigured, clock.UtcNow, permanent: true);
                await SaveAsync(ct);
                continue;
            }

            List<EmailAttachment> files;
            string? senderName;
            try
            {
                var guard = new SalesMailAccessGuard(m.CreatedByUserId, m.OrganizationId);
                files = [];
                foreach (var a in m.Attachments.OrderBy(a => a.Document).ThenBy(a => a.DocumentId))
                {
                    files.Add(await SalesDocumentPdf.BuildAsync(db, guard, renderer, a.Document, a.DocumentId, ct));
                }

                senderName = await db.SalesOrders.AsNoTracking().Where(o => o.Id == m.SalesOrderId && o.OrganizationId == m.OrganizationId)
                    .Join(db.LegalEntities.AsNoTracking(), o => new { o.OrganizationId, Id = o.LegalEntityId }, e => new { e.OrganizationId, e.Id },
                        (o, e) => e.ShortName)
                    .SingleOrDefaultAsync(ct);
            }
            catch (Exception ex) when (ex is BusinessRuleException or NotFoundException or AccessDeniedException)
            {
                m.MarkFailed("Документ не сформирован: " + ex.Message, clock.UtcNow, permanent: true);
                await SaveAsync(ct);
                continue;
            }

            if (await sender.SendAsync(new EmailMessage(m.Recipients, m.Subject, m.Body, Attachments: files, SenderName: senderName), ct))
            {
                m.MarkSent(clock.UtcNow);
                sent++;
            }
            else
            {
                m.MarkFailed("Почтовый сервер не принял письмо — повтор позже.", clock.UtcNow, permanent: false);
            }

            await SaveAsync(ct);
        }

        return sent;
    }

    /// <summary>Письмо успели отменить или повторить в карточке — изменение пользователя главнее, состояние отправителя забываем.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
        }
    }
}

/// <summary>
/// Права отправителя писем (D1123): только просмотр продаж и цен в организации письма — чтобы сформировать PDF документов,
/// которые пользователь уже решил отправить. Ничего другого этим контекстом сделать нельзя.
/// </summary>
internal sealed class SalesMailAccessGuard(long userId, long organizationId) : IAccessGuard
{
    private readonly AccessContext _ctx = new(userId, organizationId, EffectivePermissionSet.Compute(
    [
        new PermissionGrant(Permissions.SalesView, PermissionLevel.Full, false),
        new PermissionGrant(Permissions.PriceView, PermissionLevel.Full, false),
    ]));

    public Task<AccessContext> CurrentAsync(CancellationToken ct = default) => Task.FromResult(_ctx);

    public Task<AccessContext> DemandAsync(string permissionCode, CancellationToken ct = default) =>
        _ctx.Permissions.Has(permissionCode) ? Task.FromResult(_ctx) : throw new AccessDeniedException(permissionCode);

    public Task<EffectivePermissionSet> LoadAsync(long userId, long organizationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<EffectivePermissionSet> PreviewWithRoleAsync(long? userId, long organizationId, Role newRole, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DenyAsync(AccessContext? context, string permissionCode, CancellationToken ct = default) => throw new AccessDeniedException(permissionCode);
}
