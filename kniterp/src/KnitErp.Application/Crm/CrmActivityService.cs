using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>Новая активность. DueLocal — срок по местному времени организации; AssigneeUserId null — я.</summary>
public sealed record CrmActivityCommand(
    CrmTarget Target, long TargetId, CrmActivityKind Kind, string? Subject, string? Text = null, DateTime? DueLocal = null, long? AssigneeUserId = null,
    bool Done = false, string? Result = null);

public sealed record CrmActivityDto(
    long Id, CrmActivityKind Kind, CrmTarget Target, long TargetId, string Subject, string? Text, DateTime? DueAtUtc, long AssigneeUserId, string Assignee,
    string? Result, bool IsDone, DateTime? DoneAtUtc, CrmActivitySource Source, DateTime CreatedAtUtc, bool Overdue, byte[] RowVersion, string? TargetTitle = null,
    string? Href = null)
{
    public string KindName => CrmActivity.KindName(Kind);
}

public enum CrmFeedKind : byte
{
    Activity = 1,
    Stage = 2,
    Order = 3,
    Invoice = 4,
    Payment = 5,
    Shipment = 6,
    Comment = 7,
    Offer = 8,
}

/// <summary>Запись ленты карточки (D621): одна хронология активностей, этапов, документов базы и комментариев обсуждения.</summary>
public sealed record CrmFeedItemDto(DateTime AtUtc, CrmFeedKind Kind, string Title, string? Text, string? Href, string? Author, CrmActivityDto? Activity = null,
    string? PrintHref = null);

public sealed record CrmFeedPageDto(IReadOnlyList<CrmFeedItemDto> Items, bool HasMore);

/// <summary>Блок «Мои задачи CRM»: просроченные и ближайшие задачи, сделки без следующего шага, лиды без действий.</summary>
public sealed record CrmMyTasksDto(
    IReadOnlyList<CrmActivityDto> Overdue, IReadOnlyList<CrmActivityDto> Upcoming, IReadOnlyList<CrmDealRowDto> DealsWithoutNextStep,
    IReadOnlyList<CrmLeadRowDto> StaleLeads);

/// <summary>Уведомление CRM для колокольчика базы.</summary>
public sealed record CrmAlertDto(string Text, string Href, bool Warning, string Key);

/// <summary>
/// Активности CRM (D620–D623): звонки, встречи, письма, задачи, заметки на лидах, сделках и контактах; лента карточки;
/// «Мои задачи»; напоминания в колокольчик; письмо клиенту с сервера (Б6). Объект активности должен быть виден пользователю.
/// </summary>
public sealed class CrmActivityService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender mail)
{
    public const int FeedPage = 50;

    public bool MailConfigured => mail.IsConfigured;

    public async Task<long> CreateAsync(CrmActivityCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var title = await TargetTitleAsync(ctx, cmd.Target, cmd.TargetId, ct) ?? throw new NotFoundException("Объект");
        var assignee = cmd.AssigneeUserId ?? ctx.UserId;
        await EnsureMemberAsync(ctx, assignee, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        var a = CrmActivity.Create(ctx.OrganizationId, cmd.Kind, cmd.Target, cmd.TargetId, cmd.Subject, cmd.Text,
            cmd.DueLocal is { } due ? CrmEvents.ToUtc(due, tz) : null, assignee, ctx.UserId, clock.UtcNow, cmd.Done, cmd.Result);
        db.CrmActivities.Add(a);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmActivityCreated, a.Id, null, $"{CrmActivity.KindName(a.Kind)}: {a.Subject}{(a.IsDone ? " (выполнено)" : "")}", title);
        if (cmd.Target == CrmTarget.Lead && await db.CrmLeads.SingleOrDefaultAsync(l => l.Id == cmd.TargetId && l.Status == CrmLeadStatus.New, ct) is { } lead)
        {
            lead.StartWork(); // первое действие по новому лиду берёт его в работу
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return a.Id;
    }

    public async Task UpdateAsync(long id, string? subject, string? text, DateTime? dueLocal, long assigneeUserId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, a) = await LoadAsync(id, rowVersion, ct);
        await EnsureMemberAsync(ctx, assigneeUserId, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        var before = $"{a.Subject}, срок {a.DueAtUtc:dd.MM.yyyy HH:mm}, исполнитель {a.AssigneeUserId}";
        a.Update(subject, text, dueLocal is { } d ? CrmEvents.ToUtc(d, tz) : null, assigneeUserId);
        Audit(ctx, AuditActions.CrmActivityChanged, id, before, $"{a.Subject}, срок {a.DueAtUtc:dd.MM.yyyy HH:mm}, исполнитель {a.AssigneeUserId}", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task CompleteAsync(long id, string? result, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, a) = await LoadAsync(id, rowVersion, ct);
        a.Complete(result, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.CrmActivityDone, id, null, a.Result ?? "выполнено", a.Subject);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Вернуть выполненную в работу — с причиной; прежняя отметка о выполнении сохраняется в журнале (D620).</summary>
    public async Task ReopenAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, a) = await LoadAsync(id, rowVersion, ct);
        var was = a.Reopen(reason);
        Audit(ctx, AuditActions.CrmActivityReopened, id, was, "в работе", a.Subject);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Лента карточки (D621): активности, смены этапов, КП, заказы, счета, оплаты, отгрузки по связанным заказам (с правами базы) и
    /// комментарии обсуждения — одна хронология, новые сверху, порциями по 50.
    /// </summary>
    public async Task<CrmFeedPageDto> FeedAsync(CrmTarget target, long targetId, int page = 0, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        _ = await TargetTitleAsync(ctx, target, targetId, ct) ?? throw new NotFoundException("Объект");
        var take = (Math.Max(0, page) + 1) * FeedPage + 1;
        var items = new List<CrmFeedItemDto>();
        var activities = await ActivitiesQuery(ctx).Where(a => a.Target == target && a.TargetId == targetId)
            .OrderByDescending(a => a.CreatedAtUtc).Take(take).ToListAsync(ct);
        var names = await UserNamesAsync(activities.Select(a => a.AssigneeUserId).Concat(activities.Select(a => a.CreatedByUserId)), ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        items.AddRange(activities.Select(a => new CrmFeedItemDto(a.IsDone && a.DoneAtUtc is { } done ? done : a.CreatedAtUtc, CrmFeedKind.Activity,
            $"{CrmActivity.KindName(a.Kind)}: {a.Subject}", VisibleText(a, prices), null, names.GetValueOrDefault(a.CreatedByUserId), ToDto(a, names, prices: prices))));

        var taggedEntity = target switch { CrmTarget.Lead => TaggedEntity.CrmLead, CrmTarget.Deal => TaggedEntity.CrmDeal, _ => TaggedEntity.CrmContact };
        var comments = await (from c in db.DiscussionComments.AsNoTracking()
                              join u in db.Users.AsNoTracking() on c.AuthorUserId equals u.Id
                              where c.OrganizationId == ctx.OrganizationId && c.Target == taggedEntity && c.TargetId == targetId && c.DeletedAtUtc == null
                              orderby c.CreatedAtUtc descending
                              select new { c.CreatedAtUtc, c.Text, u.DisplayName }).Take(take).ToListAsync(ct);
        items.AddRange(comments.Select(c => new CrmFeedItemDto(c.CreatedAtUtc, CrmFeedKind.Comment, "Комментарий", c.Text, "#discussion", c.DisplayName)));

        if (target == CrmTarget.Deal)
        {
            await AddDealEventsAsync(ctx, targetId, take, items, ct);
        }

        var ordered = items.OrderByDescending(i => i.AtUtc).ToList();
        var shown = ordered.Take((Math.Max(0, page) + 1) * FeedPage).ToList();
        return new CrmFeedPageDto(shown, ordered.Count > shown.Count);
    }

    /// <summary>«Мои задачи CRM» (D623): просроченные и на неделю вперёд, мои сделки без следующего шага, мои лиды без действий.</summary>
    public async Task<CrmMyTasksDto> MyTasksAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var now = clock.UtcNow;
        var open = await ActivitiesQuery(ctx).Where(a => a.AssigneeUserId == ctx.UserId && !a.IsDone && (a.DueAtUtc == null || a.DueAtUtc < now.AddDays(7)))
            .OrderBy(a => a.DueAtUtc).Take(200).ToListAsync(ct);
        var names = await UserNamesAsync(open.Select(a => a.AssigneeUserId), ct);
        var titles = await TitlesAsync(ctx, open, ct);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var dtos = open.Select(a => ToDto(a, names, titles, amounts)).ToList();
        var noStep = await (from d in WithoutNextStep(ctx).Where(d => d.ResponsibleUserId == ctx.UserId)
                            join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                            join u in db.Users.AsNoTracking() on d.ResponsibleUserId equals u.Id
                            join s in db.CrmStages.AsNoTracking() on d.StageId equals s.Id
                            join p in db.CrmPipelines.AsNoTracking() on d.PipelineId equals p.Id
                            orderby d.Id descending
                            select new CrmDealRowDto(d.Id, d.Number, d.Title, c.Name, p.Name, s.Name, s.Color, d.Outcome, amounts ? d.Amount : null, d.CurrencyCode,
                                d.Probability, d.ExpectedCloseDate, u.DisplayName, d.CreatedAtUtc)).Take(100).ToListAsync(ct);
        var settings = await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct);
        var stale = await (from l in StaleLeads(ctx, settings.StaleLeadDays).Where(l => l.ResponsibleUserId == ctx.UserId)
                           join u in db.Users.AsNoTracking() on l.ResponsibleUserId equals u.Id
                           orderby l.Id
                           select new CrmLeadRowDto(l.Id, l.Number, l.Title, l.Status, null, u.DisplayName, l.ContactName, l.CompanyName,
                               amounts ? l.ExpectedAmount : null, l.CurrencyCode, l.CreatedAtUtc)).Take(100).ToListAsync(ct);
        return new CrmMyTasksDto(dtos.Where(a => a.Overdue).ToList(), dtos.Where(a => !a.Overdue).ToList(), noStep, stale);
    }

    /// <summary>
    /// Напоминания CRM для колокольчика (D623): просроченные мои задачи, мои сделки без следующего шага, мои лиды без действий.
    /// Вызывается из NotificationService базы; без права crm.view — пусто.
    /// </summary>
    public static async Task<IReadOnlyList<CrmAlertDto>> AlertsAsync(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.CrmView))
        {
            return [];
        }

        var result = new List<CrmAlertDto>();
        var overdue = await db.CrmActivities.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.AssigneeUserId == ctx.UserId && !a.IsDone && a.DueAtUtc < nowUtc)
            .OrderBy(a => a.DueAtUtc).Select(a => new { a.Id, a.Subject, a.DueAtUtc }).Take(20).ToListAsync(ct);
        result.AddRange(overdue.Select(a => new CrmAlertDto($"Просрочена задача CRM: {a.Subject}", "crm/tasks", true, $"crm-task:{a.Id}:{a.DueAtUtc:yyyyMMddHHmm}")));
        var deals = await WithoutNextStepQuery(db, ctx).Where(d => d.ResponsibleUserId == ctx.UserId).CountAsync(ct);
        if (deals > 0)
        {
            result.Add(new CrmAlertDto($"Сделки без следующего шага: {deals}", "crm/tasks", false, $"crm-nostep:{deals}"));
        }

        var days = (await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct)).StaleLeadDays;
        var stale = await StaleLeadsQuery(db, ctx, nowUtc, days).Where(l => l.ResponsibleUserId == ctx.UserId).CountAsync(ct);
        if (stale > 0)
        {
            result.Add(new CrmAlertDto($"Лиды без действий дольше {days} дн.: {stale}", "crm/tasks", true, $"crm-stale:{stale}"));
        }

        // D679: чаты, где последнее сообщение — от клиента (за неделю), по моим клиентам.
        var weekAgo = nowUtc.AddDays(-7);
        var lastIds = db.CrmActivities.Where(a => a.OrganizationId == ctx.OrganizationId && a.Source == CrmActivitySource.Messenger && a.Peer != null
                                                  && a.CreatedAtUtc >= weekAgo)
            .GroupBy(a => a.Peer).Select(g => g.Max(a => a.Id));
        var waiting = await db.CrmActivities.AsNoTracking()
            .CountAsync(a => lastIds.Contains(a.Id) && a.ExternalRef != null && a.AssigneeUserId == ctx.UserId, ct);
        if (waiting > 0)
        {
            result.Add(new CrmAlertDto($"Клиенты ждут ответа в чатах: {waiting}", "crm/chats", true, $"crm-chats:{waiting}"));
        }

        return result;
    }

    /// <summary>
    /// Письмо клиенту с сервера (Б6, D626): контакту лида, сделки или самому контакту. Сохраняется активностью «Письмо» с текстом;
    /// в журнал — адрес маской. Деловая переписка — без согласия на рекламу; рассылки — только по согласию (D615).
    /// </summary>
    public async Task<long> SendEmailAsync(CrmTarget target, long targetId, string? to, string? subject, string? text, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var title = await TargetTitleAsync(ctx, target, targetId, ct) ?? throw new NotFoundException("Объект");
        var address = CrmText.NormalizeEmail(to) ?? throw new BusinessRuleException("crm.email", "Почта указана неверно: нужен адрес вида name@example.org.");
        var subj = CrmText.Require(subject, CrmActivity.SubjectMaxLength, "Тема");
        var body = CrmText.Require(text, CrmActivity.TextMaxLength, "Текст");
        if (!mail.IsConfigured)
        {
            throw new BusinessRuleException("crm.mail.not_configured", "Почта сервера не настроена — обратитесь к администратору.");
        }

        await CrmEmailTemplateService.EnsureEmailQuotaAsync(db, ctx, clock.UtcNow, ct);
        var (replyTo, replyName) = await CrmEmailTemplateService.ReplyToAsync(db, ctx.UserId, ct);
        if (!await mail.SendAsync(new EmailMessage(address, subj, body, CrmEmailHtml.FromText(body), replyTo, replyName), ct))
        {
            throw new BusinessRuleException("crm.mail.failed", "Письмо не отправлено: почтовый сервер не принял его. Попробуйте позже.");
        }

        var a = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Email, target, targetId, subj, body, null, ctx.UserId, ctx.UserId, clock.UtcNow, done: true,
            result: $"отправлено на {CrmText.MaskEmail(address)}", source: CrmActivitySource.Mail);
        db.CrmActivities.Add(a);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmEmailSent, a.Id, null, $"{subj} → {CrmText.MaskEmail(address)}", title);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return a.Id;
    }

    /// <summary>Почты для письма: основной контакт и почта лида, все почты контакта.</summary>
    public async Task<IReadOnlyList<string>> RecipientsAsync(CrmTarget target, long targetId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        _ = await TargetTitleAsync(ctx, target, targetId, ct) ?? throw new NotFoundException("Объект");
        long? contactId = target switch
        {
            CrmTarget.Contact => targetId,
            CrmTarget.Deal => await db.CrmDeals.AsNoTracking().Where(d => d.Id == targetId).Select(d => d.ContactId).SingleOrDefaultAsync(ct),
            _ => await db.CrmLeads.AsNoTracking().Where(l => l.Id == targetId).Select(l => l.ContactId).SingleOrDefaultAsync(ct),
        };
        var list = new List<string>();
        if (target == CrmTarget.Lead && await db.CrmLeads.AsNoTracking().Where(l => l.Id == targetId).Select(l => l.Email).SingleOrDefaultAsync(ct) is { } leadMail)
        {
            list.Add(leadMail);
        }

        if (contactId is { } cid)
        {
            var c = await db.CrmContacts.AsNoTracking().Include(x => x.Channels).SingleOrDefaultAsync(x => x.Id == cid, ct);
            if (c?.Email is { } e)
            {
                list.Add(e);
            }

            list.AddRange(c?.Channels.Where(x => x.Kind == CrmChannelKind.Email).Select(x => x.Value) ?? []);
        }

        return list.Distinct().ToList();
    }

    internal static IQueryable<CrmDeal> WithoutNextStepQuery(IKnitErpDbContext db, AccessContext ctx) =>
        CrmAccess.Deals(db, ctx).AsNoTracking().Where(d => d.Outcome == CrmDealOutcome.Open
            && !db.CrmActivities.Any(a => a.OrganizationId == ctx.OrganizationId && a.Target == CrmTarget.Deal && a.TargetId == d.Id && !a.IsDone));

    internal static IQueryable<CrmLead> StaleLeadsQuery(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, int days)
    {
        var border = nowUtc.AddDays(-days);
        return CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => (l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork) && l.CreatedAtUtc < border
            && !db.CrmActivities.Any(a => a.OrganizationId == ctx.OrganizationId && a.Target == CrmTarget.Lead && a.TargetId == l.Id
                                          && (!a.IsDone || a.CreatedAtUtc >= border)));
    }

    private IQueryable<CrmDeal> WithoutNextStep(AccessContext ctx) => WithoutNextStepQuery(db, ctx);

    private IQueryable<CrmLead> StaleLeads(AccessContext ctx, int days) => StaleLeadsQuery(db, ctx, clock.UtcNow, days);

    /// <summary>Активности объектов, видимых пользователю (без crm.all — свои и на своих объектах).</summary>
    private IQueryable<CrmActivity> ActivitiesQuery(AccessContext ctx)
    {
        var q = db.CrmActivities.AsNoTracking().Where(a => a.OrganizationId == ctx.OrganizationId);
        if (CrmAccess.SeesAll(ctx))
        {
            return q;
        }

        var leads = CrmAccess.Leads(db, ctx).Select(l => l.Id);
        var deals = CrmAccess.Deals(db, ctx).Select(d => d.Id);
        var contacts = CrmAccess.Contacts(db, ctx).Select(c => c.Id);
        return q.Where(a => a.AssigneeUserId == ctx.UserId
                            || (a.Target == CrmTarget.Lead && leads.Contains(a.TargetId))
                            || (a.Target == CrmTarget.Deal && deals.Contains(a.TargetId))
                            || (a.Target == CrmTarget.Contact && contacts.Contains(a.TargetId)));
    }

    private async Task AddDealEventsAsync(AccessContext ctx, long dealId, int take, List<CrmFeedItemDto> items, CancellationToken ct)
    {
        var history = await (from h in db.CrmDealStageHistory.AsNoTracking()
                             join to in db.CrmStages.AsNoTracking() on h.ToStageId equals to.Id
                             join u in db.Users.AsNoTracking() on h.ChangedByUserId equals u.Id
                             join f in db.CrmStages.AsNoTracking() on h.FromStageId equals f.Id into fj
                             from f in fj.DefaultIfEmpty()
                             where h.OrganizationId == ctx.OrganizationId && h.DealId == dealId
                             orderby h.ChangedAtUtc descending
                             select new { h.ChangedAtUtc, To = to.Name, From = f == null ? null : f.Name, u.DisplayName }).Take(take).ToListAsync(ct);
        items.AddRange(history.Select(h => new CrmFeedItemDto(h.ChangedAtUtc, CrmFeedKind.Stage,
            h.From is null ? $"Сделка создана на этапе «{h.To}»" : $"Этап: {h.From} → {h.To}", null, null, h.DisplayName)));

        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var offers = await db.CrmOffers.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId && o.DealId == dealId)
            .OrderByDescending(o => o.Id).Take(take).Select(o => new { o.Id, o.Number, o.CreatedAtUtc, o.Amount, o.CurrencyCode, o.ValidUntil }).ToListAsync(ct);
        items.AddRange(offers.Select(o => new CrmFeedItemDto(o.CreatedAtUtc, CrmFeedKind.Offer, $"КП {o.Number}",
            amounts ? $"{o.Amount:N2} {o.CurrencyCode}, действует до {o.ValidUntil:dd.MM.yyyy}" : $"действует до {o.ValidUntil:dd.MM.yyyy}", null, null,
            PrintHref: $"print/crm-offer/{o.Id}")));

        if (!ctx.Permissions.Has(Permissions.SalesView))
        {
            return;
        }

        var orderIds = await db.CrmDealOrders.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && l.DealId == dealId).Select(l => l.SalesOrderId)
            .ToListAsync(ct);
        if (orderIds.Count == 0)
        {
            return;
        }

        var orders = await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Number, o.CreatedAtUtc, o.ConfirmedAtUtc, o.Status }).ToListAsync(ct);
        foreach (var o in orders)
        {
            items.Add(new CrmFeedItemDto(o.CreatedAtUtc, CrmFeedKind.Order, $"Заказ покупателя {o.Number} создан", SalesOrder.StatusName(o.Status), $"sales-orders/{o.Id}", null));
            if (o.ConfirmedAtUtc is { } confirmed)
            {
                items.Add(new CrmFeedItemDto(confirmed, CrmFeedKind.Order, $"Заказ покупателя {o.Number} подтверждён", null, $"sales-orders/{o.Id}", null));
            }
        }

        if (amounts)
        {
            var invoices = await db.CustomerInvoices.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && orderIds.Contains(i.SalesOrderId))
                .Select(i => new { i.Id, i.Number, i.InvoiceDate, i.CreatedAtUtc, Sum = i.Lines.Sum(l => l.Amount) }).ToListAsync(ct);
            items.AddRange(invoices.Select(i => new CrmFeedItemDto(i.CreatedAtUtc, CrmFeedKind.Invoice, $"Счёт {i.Number} от {i.InvoiceDate:dd.MM.yyyy}",
                $"{i.Sum:N2}", $"customer-invoices/{i.Id}", null, PrintHref: $"print/invoice/{i.Id}")));
            var payments = await db.CustomerPaymentAllocations.AsNoTracking()
                .Where(a => orderIds.Contains(a.OrderId) && a.RemovedAtUtc == null)
                .Join(db.CustomerPayments.AsNoTracking(), a => a.PaymentId, p => p.Id, (a, p) => new { p.Number, p.PaymentDate, p.CreatedAtUtc, a.Amount })
                .ToListAsync(ct);
            items.AddRange(payments.Select(p => new CrmFeedItemDto(p.CreatedAtUtc, CrmFeedKind.Payment, $"Оплата {p.Number} от {p.PaymentDate:dd.MM.yyyy}",
                $"{p.Amount:N2}", "customer-payments", null)));
        }

        var shipments = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId != null && orderIds.Contains(d.SalesOrderId.Value)
                        && d.Kind == StockOperationKind.Shipment && d.Status == StockDocumentStatus.Posted)
            .Select(d => new { d.Id, d.Number, d.DocumentDate, d.PostedAtUtc }).ToListAsync(ct);
        items.AddRange(shipments.Select(d => new CrmFeedItemDto(d.PostedAtUtc ?? d.DocumentDate.ToDateTime(TimeOnly.MinValue), CrmFeedKind.Shipment,
            $"Отгрузка {d.Number} от {d.DocumentDate:dd.MM.yyyy}", null, $"stock-documents/{d.Id}", null, PrintHref: $"print/upd/{d.Id}")));
    }

    private async Task<Dictionary<(CrmTarget, long), (string Title, string Href)>> TitlesAsync(AccessContext ctx, IReadOnlyList<CrmActivity> list, CancellationToken ct)
    {
        var map = new Dictionary<(CrmTarget, long), (string, string)>();
        var leadIds = list.Where(a => a.Target == CrmTarget.Lead).Select(a => a.TargetId).Distinct().ToList();
        var dealIds = list.Where(a => a.Target == CrmTarget.Deal).Select(a => a.TargetId).Distinct().ToList();
        var contactIds = list.Where(a => a.Target == CrmTarget.Contact).Select(a => a.TargetId).Distinct().ToList();
        foreach (var l in await db.CrmLeads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Number, l.Title }).ToListAsync(ct))
        {
            map[(CrmTarget.Lead, l.Id)] = ($"{l.Number} {l.Title}", $"crm/leads/{l.Id}");
        }

        foreach (var d in await db.CrmDeals.AsNoTracking().Where(d => dealIds.Contains(d.Id)).Select(d => new { d.Id, d.Number, d.Title }).ToListAsync(ct))
        {
            map[(CrmTarget.Deal, d.Id)] = ($"{d.Number} {d.Title}", $"crm/deals/{d.Id}");
        }

        foreach (var c in await db.CrmContacts.AsNoTracking().Where(c => contactIds.Contains(c.Id)).Select(c => new { c.Id, c.FullName }).ToListAsync(ct))
        {
            map[(CrmTarget.Contact, c.Id)] = (c.FullName, $"crm/contacts/{c.Id}");
        }

        return map;
    }

    /// <summary>Название объекта, если он виден пользователю; иначе null.</summary>
    private async Task<string?> TargetTitleAsync(AccessContext ctx, CrmTarget target, long id, CancellationToken ct) => target switch
    {
        CrmTarget.Lead => await CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => l.Id == id).Select(l => l.Number + " " + l.Title).SingleOrDefaultAsync(ct),
        CrmTarget.Deal => await CrmAccess.Deals(db, ctx).AsNoTracking().Where(d => d.Id == id).Select(d => d.Number + " " + d.Title).SingleOrDefaultAsync(ct),
        CrmTarget.Contact => await CrmAccess.Contacts(db, ctx).AsNoTracking().Where(c => c.Id == id).Select(c => c.FullName).SingleOrDefaultAsync(ct),
        _ => null,
    };

    /// <summary>
    /// Текст активности для ленты (D680, замечание проверки): письмо с КП и активность КП содержат позиции и цены — без права
    /// «Цены и суммы» текст не показывается.
    /// </summary>
    public const string HiddenPricesText = "Позиции и суммы КП скрыты: нет права «Цены и суммы».";

    private static string? VisibleText(CrmActivity a, bool prices) => a.OfferId is not null && !prices ? HiddenPricesText : a.Text;

    private CrmActivityDto ToDto(CrmActivity a, IReadOnlyDictionary<long, string> names,
        IReadOnlyDictionary<(CrmTarget, long), (string Title, string Href)>? titles = null, bool prices = true)
    {
        var t = titles is not null && titles.TryGetValue((a.Target, a.TargetId), out var x) ? x : ((string?)null, (string?)null);
        return new CrmActivityDto(a.Id, a.Kind, a.Target, a.TargetId, a.Subject, VisibleText(a, prices), a.DueAtUtc, a.AssigneeUserId, names.GetValueOrDefault(a.AssigneeUserId) ?? "—",
            a.Result, a.IsDone, a.DoneAtUtc, a.Source, a.CreatedAtUtc, a.IsOverdue(clock.UtcNow), a.RowVersion, t.Item1, t.Item2);
    }

    private async Task<Dictionary<long, string>> UserNamesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private async Task EnsureMemberAsync(AccessContext ctx, long userId, CancellationToken ct)
    {
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId && m.Status == MembershipStatus.Active, ct))
        {
            throw new NotFoundException("Сотрудник");
        }
    }

    private async Task<(AccessContext, CrmActivity)> LoadAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        // Видимость — отдельным запросом: AsNoTracking во вложенном запросе сделал бы загрузку неотслеживаемой, и изменение не сохранилось бы.
        if (!await ActivitiesQuery(ctx).AnyAsync(x => x.Id == id, ct))
        {
            throw new NotFoundException("Активность");
        }

        var a = await db.CrmActivities.SingleAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct);
        return (ctx, a.EnsureVersion(a.RowVersion, rowVersion));
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CrmActivity), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
