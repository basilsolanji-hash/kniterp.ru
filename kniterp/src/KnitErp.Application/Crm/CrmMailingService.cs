using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmMailingRowDto(long Id, string Subject, CrmMailingStatus Status, int Recipients, int Delivered, DateTime CreatedAtUtc, DateTime? SentAtUtc,
    string? Tag, byte[] RowVersion, DateTime? ScheduledAtUtc = null, int Pending = 0);

public sealed record CrmMailingDto(long Id, string Subject, string Text, long? TagId, CrmMailingStatus Status, int Audience, int Recipients, int Delivered,
    byte[] RowVersion, DateTime? ScheduledAtUtc = null, int Pending = 0);

/// <summary>
/// Рассылки (D615, предложение В3): письмо контактам с согласием на рекламу (38-ФЗ ст. 18) и почтой; удалённые, архивные и без согласия
/// не попадают никогда. Каждое письмо — со ссылкой отписки (без входа). Кто, когда и кому отправил — в журнале (адреса не пишутся).
/// Право crm.mailing; почта сервера должна быть настроена. До 2000 получателей; отправка — очередью в фоне (D675): получатели фиксируются
/// сразу, письма уходят пачками с правами поставившего в очередь; можно запланировать на дату и время. Текст — с переменными шаблона (D674),
/// письмо — HTML с текстовой версией и заголовком List-Unsubscribe.
/// </summary>
public sealed class CrmMailingService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender mail)
{
    public bool MailConfigured => mail.IsConfigured;

    public async Task<IReadOnlyList<CrmMailingRowDto>> ListAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        return await (from m in db.CrmMailings.AsNoTracking()
                      join t in db.Tags.AsNoTracking() on m.TagId equals t.Id into tj
                      from t in tj.DefaultIfEmpty()
                      where m.OrganizationId == ctx.OrganizationId
                      orderby m.Id descending
                      select new CrmMailingRowDto(m.Id, m.Subject, m.Status, m.Recipients.Count, m.Recipients.Count(r => r.Delivered), m.CreatedAtUtc, m.SentAtUtc,
                          t == null ? null : t.Name, m.RowVersion, m.ScheduledAtUtc, m.Recipients.Count(r => r.AttemptedAtUtc == null)))
            .Take(200).ToListAsync(ct);
    }

    public async Task<CrmMailingDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        var m = await db.CrmMailings.AsNoTracking().Include(x => x.Recipients).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Рассылка");
        return new CrmMailingDto(m.Id, m.Subject, m.Text, m.TagId, m.Status, await Audience(db, ctx.OrganizationId, m.TagId).CountAsync(ct), m.Recipients.Count,
            m.Recipients.Count(r => r.Delivered), m.RowVersion, m.ScheduledAtUtc, m.Recipients.Count(r => r.AttemptedAtUtc == null));
    }

    /// <summary>Сколько контактов получит письмо: с согласием, почтой, не в архиве (и с тегом, если выбран).</summary>
    public async Task<int> AudienceCountAsync(long? tagId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        return await Audience(db, ctx.OrganizationId, tagId).CountAsync(ct);
    }

    public async Task<long> CreateAsync(string? subject, string? text, long? tagId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        await EnsureTagAsync(ctx, tagId, ct);
        var m = CrmMailing.Create(ctx.OrganizationId, subject, text, tagId, ctx.UserId, clock.UtcNow);
        db.CrmMailings.Add(m);
        await db.SaveChangesAsync(ct);
        return m.Id;
    }

    public async Task UpdateAsync(long id, string? subject, string? text, long? tagId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        await EnsureTagAsync(ctx, tagId, ct);
        var m = await LoadAsync(ctx, id, ct);
        m.EnsureVersion(m.RowVersion, rowVersion);
        m.Update(subject, text, tagId);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Отправить (D675): получатели фиксируются сейчас, рассылка больше не меняется, письма уходят пачками фоновой задачей.
    /// Возвращает число получателей.
    /// </summary>
    public async Task<int> SendAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        EnsureMail();
        await using var tx = await db.BeginTransactionAsync(ct);
        var m = await LoadAsync(ctx, id, ct);
        m.EnsureVersion(m.RowVersion, rowVersion);
        if (m.Status == CrmMailingStatus.Scheduled)
        {
            m.Unschedule();
        }

        var count = await QueueAsync(db, m, ctx.UserId, ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmMailingQueued, nameof(CrmMailing), id.ToString(),
            null, $"«{m.Subject}»: в очереди {count} получателей", null, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }

    /// <summary>Запланировать на местное время организации (D675): получатели отбираются в момент отправки.</summary>
    public async Task ScheduleAsync(long id, DateTime atLocal, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        EnsureMail();
        var m = await LoadAsync(ctx, id, ct);
        m.EnsureVersion(m.RowVersion, rowVersion);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        m.Schedule(CrmEvents.ToUtc(atLocal, tz), ctx.UserId, clock.UtcNow);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmMailingScheduled, nameof(CrmMailing), id.ToString(),
            null, $"«{m.Subject}»: запланирована на {atLocal:dd.MM.yyyy HH:mm}", null, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    public async Task UnscheduleAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        var m = await LoadAsync(ctx, id, ct);
        m.EnsureVersion(m.RowVersion, rowVersion);
        m.Unschedule();
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmMailingScheduled, nameof(CrmMailing), id.ToString(),
            "запланирована", "снята с расписания", null, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Фоновая отправка (D675), вызывается раз в полминуты: запланированные, чьё время пришло, ставятся в очередь; из очереди уходит до
    /// <paramref name="batch"/> писем. Права проверяются у поставившего в очередь: отозвали crm.mailing — запланированная возвращается
    /// в черновик, очередь стоит. Контакт отписался после постановки в очередь — письмо не уходит. Возвращает число отправленных.
    /// </summary>
    public static async Task<int> ProcessQueueAsync(IKnitErpDbContext db, IClock clock, IEmailSender mail, int batch = 100, CancellationToken ct = default)
    {
        if (!mail.IsConfigured)
        {
            return 0;
        }

        var now = clock.UtcNow;
        foreach (var id in await db.CrmMailings.AsNoTracking().Where(m => m.Status == CrmMailingStatus.Scheduled && m.ScheduledAtUtc <= now)
                     .OrderBy(m => m.ScheduledAtUtc).Select(m => m.Id).Take(20).ToListAsync(ct))
        {
            var m = await db.CrmMailings.Include(x => x.Recipients).SingleAsync(x => x.Id == id, ct);
            var allowed = await CanMailAsync(db, clock, m, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            string after;
            if (!allowed)
            {
                m.Unschedule();
                after = "не отправлена: у запланировавшего нет права на рассылки — возвращена в черновики";
            }
            else
            {
                try
                {
                    after = $"в очереди {await QueueAsync(db, m, m.QueuedByUserId!.Value, ct)} получателей (по расписанию)";
                }
                catch (BusinessRuleException ex) when (ex.Code == "crm.mailing.empty")
                {
                    m.Unschedule();
                    after = "не отправлена: нет получателей с согласием — возвращена в черновики";
                }
            }

            db.AuditEntries.Add(AuditEntry.Create(now, m.OrganizationId, null, AuditActions.CrmMailingQueued, nameof(CrmMailing), m.Id.ToString(), null,
                $"«{m.Subject}»: {after}", "по расписанию"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var sent = 0;
        foreach (var id in await db.CrmMailings.AsNoTracking().Where(m => m.Status == CrmMailingStatus.Sending).OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct))
        {
            if (sent >= batch)
            {
                break;
            }

            var m = await db.CrmMailings.Include(x => x.Recipients).SingleAsync(x => x.Id == id, ct);
            if (!await CanMailAsync(db, clock, m, ct))
            {
                continue;
            }

            var pending = m.Recipients.Where(r => r.AttemptedAtUtc == null).OrderBy(r => r.Id).Take(batch - sent).ToList();
            var ids = pending.Select(r => r.ContactId).ToList();
            var contacts = await (from c in db.CrmContacts.AsNoTracking().Where(c => ids.Contains(c.Id))
                                  join cp in db.Counterparties.AsNoTracking() on c.CounterpartyId equals cp.Id into cpj
                                  from cp in cpj.DefaultIfEmpty()
                                  join u in db.Users.AsNoTracking() on c.ResponsibleUserId equals u.Id
                                  select new { c.Id, c.FullName, c.UnsubscribeToken, c.MarketingConsent, c.IsArchived, c.ErasedAtUtc, Company = cp == null ? null : cp.Name,
                                      Manager = u.DisplayName }).ToDictionaryAsync(c => c.Id, ct);
            var org = await db.Organizations.AsNoTracking().Where(o => o.Id == m.OrganizationId).Select(o => o.ShortName).SingleAsync(ct);
            foreach (var r in pending)
            {
                // Сначала отмечается попытка, потом уходит письмо: сбой посередине не отправит письмо дважды.
                r.SetDelivered(false, clock.UtcNow);
                await db.SaveChangesAsync(ct);
                if (contacts.GetValueOrDefault(r.ContactId) is not { MarketingConsent: true, IsArchived: false, ErasedAtUtc: null } c)
                {
                    continue;
                }

                var values = new Dictionary<string, string?>
                {
                    ["Имя"] = c.FullName, ["Компания"] = c.Company, ["Менеджер"] = c.Manager, ["Организация"] = org, ["Сделка"] = null, ["Сумма"] = null,
                };
                var text = CrmEmailTemplate.Render(m.Text, values);
                if (!m.Text.Contains("{{", StringComparison.Ordinal))
                {
                    text = $"Здравствуйте, {c.FullName}!\n\n{text}";
                }

                var unsubscribe = $"{mail.PublicUrl}/account/crm-unsubscribe/{c.UnsubscribeToken:N}";
                var footer = $"Вы получили это письмо, потому что дали {org} согласие на рассылки. Отписаться: {unsubscribe}";
                var ok = await mail.SendAsync(new EmailMessage(r.Email, CrmEmailTemplate.Render(m.Subject, values), $"{text}\n\n—\n{footer}",
                    CrmEmailHtml.FromText(text, footer), UnsubscribeUrl: unsubscribe), ct);
                r.SetDelivered(ok, clock.UtcNow);
                sent += ok ? 1 : 0;
                await db.SaveChangesAsync(ct);
            }

            if (m.Recipients.All(r => r.AttemptedAtUtc != null))
            {
                m.Complete(clock.UtcNow);
                db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, m.OrganizationId, m.QueuedByUserId, AuditActions.CrmMailingSent, nameof(CrmMailing), m.Id.ToString(),
                    null, $"«{m.Subject}»: получателей {m.Recipients.Count}, доставлено {m.Recipients.Count(x => x.Delivered)}", null));
                await db.SaveChangesAsync(ct);
            }
        }

        return sent;
    }

    private static async Task<int> QueueAsync(IKnitErpDbContext db, CrmMailing m, long userId, CancellationToken ct)
    {
        var audience = await Audience(db, m.OrganizationId, m.TagId).OrderBy(c => c.Id).Select(c => new { c.Id, c.Email })
            .Take(CrmMailing.MaxRecipients + 1).ToListAsync(ct);
        foreach (var c in audience)
        {
            m.AddRecipient(c.Id, c.Email!);
        }

        m.Queue(userId);
        return m.Recipients.Count;
    }

    /// <summary>Есть ли у поставившего в очередь право на рассылки сейчас (заблокирован, исключён, право отозвано — нет).</summary>
    private static async Task<bool> CanMailAsync(IKnitErpDbContext db, IClock clock, CrmMailing m, CancellationToken ct)
    {
        if (m.QueuedByUserId is not { } userId)
        {
            return false;
        }

        try
        {
            await new AccessGuard(db, new SystemUser(userId, m.OrganizationId), clock).DemandAsync(Permissions.CrmMailing, ct);
            return true;
        }
        catch (Exception ex) when (ex is AccessDeniedException or NotFoundException)
        {
            return false;
        }
    }

    private void EnsureMail()
    {
        if (!mail.IsConfigured)
        {
            throw new BusinessRuleException("crm.mail.not_configured", "Почта сервера не настроена — обратитесь к администратору.");
        }
    }

    private sealed class SystemUser(long userId, long organizationId) : ICurrentUser
    {
        public long? UserId => userId;
        public long? OrganizationId => organizationId;
        public string? CorrelationId => "crm-mailing-queue";
    }

    /// <summary>
    /// Отписка по ссылке из письма (D615) — без входа, по ключу контакта. Возвращает true, если согласие было и снято.
    /// Ключ не раскрывает ни организацию, ни данные контакта.
    /// </summary>
    public static async Task<bool> UnsubscribeAsync(IKnitErpDbContext db, Guid token, DateTime nowUtc, CancellationToken ct = default)
    {
        var contact = await db.CrmContacts.SingleOrDefaultAsync(c => c.UnsubscribeToken == token, ct);
        if (contact is null || !contact.Unsubscribe())
        {
            return false;
        }

        db.AuditEntries.Add(AuditEntry.Create(nowUtc, contact.OrganizationId, null, AuditActions.CrmUnsubscribed, nameof(CrmContact), contact.Id.ToString(),
            "согласие на рассылки", "отписан по ссылке из письма"));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static IQueryable<CrmContact> Audience(IKnitErpDbContext db, long organizationId, long? tagId)
    {
        var q = db.CrmContacts.AsNoTracking().Where(c => c.OrganizationId == organizationId && c.MarketingConsent && c.Email != null && !c.IsArchived
                                                         && c.ErasedAtUtc == null);
        return tagId is { } t ? q.Where(c => db.TagLinks.Any(l => l.TagId == t && l.Entity == TaggedEntity.CrmContact && l.EntityId == c.Id)) : q;
    }

    private async Task EnsureTagAsync(AccessContext ctx, long? tagId, CancellationToken ct)
    {
        if (tagId is { } t && !await db.Tags.AnyAsync(x => x.Id == t && x.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Тег");
        }
    }

    private async Task<CrmMailing> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.CrmMailings.Include(x => x.Recipients).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Рассылка");
}
