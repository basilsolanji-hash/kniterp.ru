using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>Заявка с сайта или маркетплейса (API v1, D640). Source — название источника («Сайт», «Ozon»): нет такого — заводится.</summary>
public sealed record CrmIntakeLead(
    string? Title, string? ContactName = null, string? Phone = null, string? Email = null, string? CompanyName = null, string? Inn = null, string? Need = null,
    string? Source = null, decimal? ExpectedAmount = null, string? Messenger = null);

/// <param name="LeadId">Новый или найденный лид.</param>
/// <param name="Repeat">true — это повторное обращение: к найденному лиду добавлена активность, новый лид не создан.</param>
public sealed record CrmIntakeResult(long LeadId, string Number, bool Repeat);

/// <summary>Входящее письмо из почтового ящика организации (D676). Uid — номер письма в папке IMAP.</summary>
public sealed record CrmInboundEmail(long Uid, string? MessageId, string? FromEmail, string? FromName, string? Subject, string? Text);

/// <summary>Событие телефонии (D643): звонок по номеру; ExternalId — id звонка АТС (повтор не создаёт вторую запись).</summary>
public sealed record CrmCallEvent(string? Phone, bool Incoming = true, int? DurationSeconds = null, string? ExternalId = null, string? Comment = null);

/// <summary>
/// Сообщение из мессенджера (D644): Channel — whatsapp, telegram или max; Sender — ник или id отправителя в мессенджере; ExternalId — id
/// сообщения (повтор не создаёт вторую запись). Бот мессенджера пересылает сюда входящие — подключение ботов — следующий шаг.
/// </summary>
public sealed record CrmMessageEvent(string? Channel, string? Sender, string? Text, string? Phone = null, string? Name = null, string? ExternalId = null);

public sealed record CrmCallResult(CrmTarget Target, long TargetId, long ActivityId, bool NewLead, bool Duplicate);

/// <summary>
/// Входящие заявки и звонки (D640–D643, предложения А3, А4, А6): заявка из API проверяется на дубликаты по телефону и почте — найден открытый
/// лид, контакт или сделка — повторное обращение пишется активностью; иначе лид, ответственный по правилу распределения, и задача
/// «Связаться с клиентом» через 2 часа. Звонок АТС находит контакт (и его открытую сделку) или лид по номеру; неизвестный номер — новый лид.
/// Права: crm.leads.create (токен сайта, телефонии) или crm.edit.
/// </summary>
public sealed class CrmIntakeService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<CrmIntakeResult> CreateLeadAsync(CrmIntakeLead cmd, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var phone = CrmText.NormalizePhone(cmd.Phone);
        var email = CrmText.NormalizeEmail(cmd.Email);
        if (phone is null && email is null)
        {
            throw new BusinessRuleException("crm.intake.contact", "Заявке нужен телефон или почта клиента.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.intake.{ctx.OrganizationId}", ct);
        var existing = await db.CrmLeads.Where(l => l.OrganizationId == ctx.OrganizationId && (l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork)
                                                    && ((phone != null && l.Phone == phone) || (email != null && l.Email == email)))
            .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            db.CrmActivities.Add(CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.RepeatRequest, CrmTarget.Lead, existing.Id,
                $"Повторное обращение: {cmd.Title ?? cmd.Need ?? "заявка"}", cmd.Need, clock.UtcNow, existing.ResponsibleUserId, ctx.UserId, clock.UtcNow,
                source: CrmActivitySource.Api));
            Audit(ctx, AuditActions.CrmActivityCreated, nameof(CrmLead), existing.Id, null, "повторное обращение (API)");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new CrmIntakeResult(existing.Id, existing.Number, true);
        }

        var sourceId = await SourceAsync(ctx, cmd.Source, ct);
        var assignee = await CrmEvents.AssigneeAsync(db, ctx.OrganizationId, ctx.UserId, ct);
        var currency = await CrmCommon.CurrencyAsync(db, ctx, ct);
        var values = new CrmLeadValues(cmd.Title ?? (cmd.Need is { } n ? CrmTextParser.Parse(n).Title : null) ?? "Заявка", sourceId, null, assignee, cmd.ContactName,
            phone, email, cmd.Messenger, cmd.CompanyName, cmd.Inn, cmd.Need, cmd.ExpectedAmount);
        CrmLead.Create(ctx.OrganizationId, "Л-0", values, currency, ctx.UserId, clock.UtcNow);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
        var lead = CrmLead.Create(ctx.OrganizationId, number, values, currency, ctx.UserId, clock.UtcNow);
        db.CrmLeads.Add(lead);
        await db.SaveChangesAsync(ct);
        db.CrmActivities.Add(CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Task, CrmTarget.Lead, lead.Id, "Связаться с клиентом по заявке", cmd.Need,
            clock.UtcNow.AddHours(2), assignee, ctx.UserId, clock.UtcNow, source: CrmActivitySource.Automation));
        Audit(ctx, AuditActions.CrmLeadCreated, nameof(CrmLead), lead.Id, null, lead.Describe(), "API");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CrmIntakeResult(lead.Id, lead.Number, false);
    }

    public async Task<CrmCallResult> RegisterCallAsync(CrmCallEvent call, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var phone = CrmText.NormalizePhone(call.Phone) ?? throw new BusinessRuleException("crm.phone", "Телефон указан неверно: нужен номер с кодом страны, например +7 900 000-00-01.");
        var externalId = CrmText.Optional(call.ExternalId, CrmActivity.ExternalRefMaxLength, "Внешний номер");
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.intake.{ctx.OrganizationId}", ct);
        if (externalId is not null && await db.CrmActivities.AsNoTracking()
                .SingleOrDefaultAsync(a => a.OrganizationId == ctx.OrganizationId && a.Source == CrmActivitySource.Telephony && a.ExternalRef == externalId, ct) is { } dup)
        {
            return new CrmCallResult(dup.Target, dup.TargetId, dup.Id, false, true);
        }

        var subject = $"{(call.Incoming ? "Входящий" : "Исходящий")} звонок {CrmText.FormatPhone(phone)}"
                      + (call.DurationSeconds is { } s ? $", {s / 60}:{s % 60:00}" : "");
        CrmTarget target;
        long targetId;
        long assignee;
        var newLead = false;
        var contact = await db.CrmContacts.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.ErasedAtUtc == null
                                                                     && (c.Phone == phone || c.Channels.Any(x => x.Kind == CrmChannelKind.Phone && x.Value == phone)))
            .OrderBy(c => c.IsArchived).ThenByDescending(c => c.Id).Select(c => new { c.Id, c.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var deal = contact is null ? null : await db.CrmDeals.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.ContactId == contact.Id && d.Outcome == CrmDealOutcome.Open)
            .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var lead = contact is not null ? null : await db.CrmLeads.AsNoTracking()
            .Where(l => l.OrganizationId == ctx.OrganizationId && l.Phone == phone && (l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork))
            .OrderByDescending(l => l.Id).Select(l => new { l.Id, l.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        if (deal is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Deal, deal.Id, deal.ResponsibleUserId);
        }
        else if (contact is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Contact, contact.Id, contact.ResponsibleUserId);
        }
        else if (lead is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Lead, lead.Id, lead.ResponsibleUserId);
        }
        else
        {
            assignee = await CrmEvents.AssigneeAsync(db, ctx.OrganizationId, ctx.UserId, ct);
            var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
            var created = CrmLead.Create(ctx.OrganizationId, number, new CrmLeadValues($"Звонок {CrmText.FormatPhone(phone)}",
                    await SourceAsync(ctx, "Звонок", ct), ResponsibleUserId: assignee, Phone: phone, Need: call.Comment),
                await CrmCommon.CurrencyAsync(db, ctx, ct), ctx.UserId, clock.UtcNow);
            db.CrmLeads.Add(created);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.CrmLeadCreated, nameof(CrmLead), created.Id, null, created.Describe(), "телефония");
            (target, targetId, newLead) = (CrmTarget.Lead, created.Id, true);
        }

        var activity = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Call, target, targetId, subject, call.Comment, null, assignee, ctx.UserId, clock.UtcNow,
            done: true, result: call.DurationSeconds is 0 ? "не дозвонились" : null, source: CrmActivitySource.Telephony, externalRef: externalId);
        db.CrmActivities.Add(activity);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmActivityCreated, nameof(CrmActivity), activity.Id, null, $"звонок {CrmText.MaskPhone(phone)}", "телефония");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CrmCallResult(target, targetId, activity.Id, newLead, false);
    }

    public static readonly IReadOnlyDictionary<string, string> Messengers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["whatsapp"] = "WhatsApp",
        ["telegram"] = "Telegram",
        ["max"] = "MAX",
    };

    /// <summary>
    /// Входящее сообщение мессенджера (D644): находит контакт (по телефону или нику мессенджера), его открытую сделку или открытый лид;
    /// незнакомый отправитель — новый лид «Сообщение в …» (название — из текста, умная вставка А1), ответственный — по правилу распределения.
    /// </summary>
    public async Task<CrmCallResult> RegisterMessageAsync(CrmMessageEvent msg, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var channel = msg.Channel is { } c && Messengers.TryGetValue(c.Trim(), out var name) ? name
            : throw new BusinessRuleException("crm.messenger", "Мессенджер: whatsapp, telegram или max.");
        var sender = CrmText.Require(msg.Sender, CrmText.MessengerMaxLength - channel.Length - 2, "Отправитель");
        var handle = $"{channel}: {sender}";
        var text = CrmText.Require(msg.Text, CrmActivity.TextMaxLength, "Текст");
        var phone = CrmText.NormalizePhone(msg.Phone);
        var externalId = msg.ExternalId is { } e ? CrmText.Optional($"{channel}:{e}", CrmActivity.ExternalRefMaxLength, "Внешний номер") : null;
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.intake.{ctx.OrganizationId}", ct);
        if (externalId is not null && await db.CrmActivities.AsNoTracking()
                .SingleOrDefaultAsync(a => a.OrganizationId == ctx.OrganizationId && a.Source == CrmActivitySource.Messenger && a.ExternalRef == externalId, ct) is { } dup)
        {
            return new CrmCallResult(dup.Target, dup.TargetId, dup.Id, false, true);
        }

        var contact = await db.CrmContacts.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.ErasedAtUtc == null
                && ((phone != null && (x.Phone == phone || x.Channels.Any(ch => ch.Kind == CrmChannelKind.Phone && ch.Value == phone)))
                    || x.Messenger == handle || x.Channels.Any(ch => ch.Kind == CrmChannelKind.Messenger && ch.Value == handle)))
            .OrderBy(x => x.IsArchived).ThenByDescending(x => x.Id).Select(x => new { x.Id, x.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var deal = contact is null ? null : await db.CrmDeals.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.ContactId == contact.Id && d.Outcome == CrmDealOutcome.Open)
            .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var lead = contact is not null ? null : await db.CrmLeads.AsNoTracking()
            .Where(l => l.OrganizationId == ctx.OrganizationId && (l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork)
                        && ((phone != null && l.Phone == phone) || l.Messenger == handle))
            .OrderByDescending(l => l.Id).Select(l => new { l.Id, l.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        CrmTarget target;
        long targetId, assignee;
        var newLead = false;
        if (deal is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Deal, deal.Id, deal.ResponsibleUserId);
        }
        else if (contact is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Contact, contact.Id, contact.ResponsibleUserId);
        }
        else if (lead is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Lead, lead.Id, lead.ResponsibleUserId);
        }
        else
        {
            assignee = await CrmEvents.AssigneeAsync(db, ctx.OrganizationId, ctx.UserId, ct);
            var parsed = CrmTextParser.Parse(text);
            var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
            var created = CrmLead.Create(ctx.OrganizationId, number, new CrmLeadValues(parsed.Title ?? $"Сообщение в {channel}", await SourceAsync(ctx, channel, ct), null,
                    assignee, msg.Name ?? parsed.ContactName, phone ?? parsed.Phone, parsed.Email, handle, parsed.CompanyName, parsed.Inn, text),
                await CrmCommon.CurrencyAsync(db, ctx, ct), ctx.UserId, clock.UtcNow);
            db.CrmLeads.Add(created);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.CrmLeadCreated, nameof(CrmLead), created.Id, null, created.Describe(), channel);
            (target, targetId, newLead) = (CrmTarget.Lead, created.Id, true);
        }

        var activity = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Message, target, targetId, $"Сообщение в {channel} от {msg.Name ?? sender}", text, null,
            assignee, ctx.UserId, clock.UtcNow, done: true, source: CrmActivitySource.Messenger,
            externalRef: externalId ?? $"{channel}:in:{Guid.NewGuid():N}"); // входящее всегда с внешним номером — по нему чаты отличают входящие от ответов
        activity.SetPeer($"{channel}:{sender}"); // D670: собеседник с мессенджером — id разных мессенджеров не смешиваются
        db.CrmActivities.Add(activity);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmActivityCreated, nameof(CrmActivity), activity.Id, null, $"сообщение {channel}", "мессенджер");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CrmCallResult(target, targetId, activity.Id, newLead, false);
    }

    /// <summary>
    /// Входящее письмо из ящика организации (D676): по адресу отправителя находит контакт (его открытую сделку) или открытый лид и пишет
    /// письмо в ленту; незнакомый отправитель — новый лид «Письмо: тема» (если включено), иначе письмо пропускается. Повтор (тот же
    /// Message-ID) не создаёт вторую запись. Возвращает null, если письмо пропущено.
    /// </summary>
    public async Task<CrmCallResult?> RegisterEmailAsync(CrmInboundEmail email, bool createLeads, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var from = CrmText.NormalizeEmail(email.FromEmail);
        if (from is null)
        {
            return null;
        }

        var subject = string.IsNullOrWhiteSpace(email.Subject) ? "(без темы)" : email.Subject.Trim();
        var text = string.IsNullOrWhiteSpace(email.Text) ? subject : email.Text.Trim();
        text = text.Length > CrmActivity.TextMaxLength ? text[..(CrmActivity.TextMaxLength - 1)] + "…" : text;
        var externalId = $"mail:{email.MessageId ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(from + subject + text)))[..32]}";
        externalId = externalId.Length > CrmActivity.ExternalRefMaxLength ? externalId[..CrmActivity.ExternalRefMaxLength] : externalId;
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.intake.{ctx.OrganizationId}", ct);
        if (await db.CrmActivities.AsNoTracking()
                .SingleOrDefaultAsync(a => a.OrganizationId == ctx.OrganizationId && a.Source == CrmActivitySource.Mail && a.ExternalRef == externalId, ct) is { } dup)
        {
            return new CrmCallResult(dup.Target, dup.TargetId, dup.Id, false, true);
        }

        var contact = await db.CrmContacts.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.ErasedAtUtc == null
                && (x.Email == from || x.Channels.Any(ch => ch.Kind == CrmChannelKind.Email && ch.Value == from)))
            .OrderBy(x => x.IsArchived).ThenByDescending(x => x.Id).Select(x => new { x.Id, x.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var deal = contact is null ? null : await db.CrmDeals.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.ContactId == contact.Id && d.Outcome == CrmDealOutcome.Open)
            .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        var lead = contact is not null ? null : await db.CrmLeads.AsNoTracking()
            .Where(l => l.OrganizationId == ctx.OrganizationId && (l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork) && l.Email == from)
            .OrderByDescending(l => l.Id).Select(l => new { l.Id, l.ResponsibleUserId }).FirstOrDefaultAsync(ct);
        CrmTarget target;
        long targetId, assignee;
        var newLead = false;
        if (deal is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Deal, deal.Id, deal.ResponsibleUserId);
        }
        else if (contact is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Contact, contact.Id, contact.ResponsibleUserId);
        }
        else if (lead is not null)
        {
            (target, targetId, assignee) = (CrmTarget.Lead, lead.Id, lead.ResponsibleUserId);
        }
        else if (!createLeads)
        {
            return null;
        }
        else
        {
            assignee = await CrmEvents.AssigneeAsync(db, ctx.OrganizationId, ctx.UserId, ct);
            var parsed = CrmTextParser.Parse(text);
            var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
            var title = parsed.Title ?? (subject.Length > CrmLead.TitleMaxLength - 8 ? subject[..(CrmLead.TitleMaxLength - 8)] : subject);
            var created = CrmLead.Create(ctx.OrganizationId, number, new CrmLeadValues(title, await SourceAsync(ctx, "Почта", ct), null, assignee,
                    Short(email.FromName, CrmLead.NameMaxLength) ?? parsed.ContactName, parsed.Phone, from, null, parsed.CompanyName, parsed.Inn,
                    text.Length > CrmLead.NeedMaxLength ? text[..CrmLead.NeedMaxLength] : text),
                await CrmCommon.CurrencyAsync(db, ctx, ct), ctx.UserId, clock.UtcNow);
            db.CrmLeads.Add(created);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.CrmLeadCreated, nameof(CrmLead), created.Id, null, created.Describe(), "входящее письмо");
            (target, targetId, newLead) = (CrmTarget.Lead, created.Id, true);
        }

        var activitySubject = $"Письмо от {email.FromName ?? CrmText.MaskEmail(from)}: {subject}";
        var activity = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Email, target, targetId,
            activitySubject.Length > CrmActivity.SubjectMaxLength ? activitySubject[..(CrmActivity.SubjectMaxLength - 1)] + "…" : activitySubject, text, null,
            assignee, ctx.UserId, clock.UtcNow, done: true, result: "входящее", source: CrmActivitySource.Mail, externalRef: externalId);
        db.CrmActivities.Add(activity);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmEmailReceived, nameof(CrmActivity), activity.Id, null, $"письмо от {CrmText.MaskEmail(from)}", "почта");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CrmCallResult(target, targetId, activity.Id, newLead, false);
    }

    private static string? Short(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();

    /// <summary>Источник по названию: действующий — его id, нет — новый (заявки с маркетплейсов приходят со своими именами).</summary>
    private async Task<long?> SourceAsync(AccessContext ctx, string? name, CancellationToken ct)
    {
        var n = CrmText.Optional(name, CrmReference.NameMaxLength, "Источник");
        if (n is null)
        {
            return null;
        }

        var id = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == CrmReferenceKind.Source && !r.IsArchived && r.Name == n)
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (id is not null)
        {
            return id;
        }

        var last = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == CrmReferenceKind.Source).MaxAsync(r => (int?)r.SortOrder, ct) ?? 0;
        var item = CrmReference.Create(ctx.OrganizationId, CrmReferenceKind.Source, n, last + 10);
        db.CrmReferences.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.CrmLeadsCreate) && !ctx.Permissions.Has(Permissions.CrmEdit))
        {
            await guard.DenyAsync(ctx, Permissions.CrmLeadsCreate, ct);
        }

        return ctx;
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason = null) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
