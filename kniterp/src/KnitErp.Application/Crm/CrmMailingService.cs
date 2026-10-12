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
    string? Tag, byte[] RowVersion);

public sealed record CrmMailingDto(long Id, string Subject, string Text, long? TagId, CrmMailingStatus Status, int Audience, int Recipients, int Delivered,
    byte[] RowVersion);

/// <summary>
/// Рассылки (D615, предложение В3): письмо контактам с согласием на рекламу (38-ФЗ ст. 18) и почтой; удалённые, архивные и без согласия
/// не попадают никогда. Каждое письмо — со ссылкой отписки (без входа). Кто, когда и кому отправил — в журнале (адреса не пишутся).
/// Право crm.mailing; почта сервера должна быть настроена. Отправка — сразу, до 2000 получателей (очередь — следующий шаг).
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
                          t == null ? null : t.Name, m.RowVersion)).Take(200).ToListAsync(ct);
    }

    public async Task<CrmMailingDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        var m = await db.CrmMailings.AsNoTracking().Include(x => x.Recipients).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Рассылка");
        return new CrmMailingDto(m.Id, m.Subject, m.Text, m.TagId, m.Status, await Audience(ctx, m.TagId).CountAsync(ct), m.Recipients.Count,
            m.Recipients.Count(r => r.Delivered), m.RowVersion);
    }

    /// <summary>Сколько контактов получит письмо: с согласием, почтой, не в архиве (и с тегом, если выбран).</summary>
    public async Task<int> AudienceCountAsync(long? tagId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        return await Audience(ctx, tagId).CountAsync(ct);
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

    /// <summary>Отправить: получатели фиксируются, каждому — письмо со ссылкой отписки; рассылка больше не меняется.</summary>
    public async Task<int> SendAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmMailing, ct);
        if (!mail.IsConfigured)
        {
            throw new BusinessRuleException("crm.mail.not_configured", "Почта сервера не настроена — обратитесь к администратору.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        var m = await LoadAsync(ctx, id, ct);
        m.EnsureVersion(m.RowVersion, rowVersion);
        var audience = await Audience(ctx, m.TagId).OrderBy(c => c.Id).Select(c => new { c.Id, c.Email, c.FullName, c.UnsubscribeToken })
            .Take(CrmMailing.MaxRecipients + 1).ToListAsync(ct);
        var sent = new List<(CrmMailingRecipient Recipient, string Email, string Name, Guid Token)>();
        foreach (var c in audience)
        {
            sent.Add((m.AddRecipient(c.Id, c.Email!), c.Email!, c.FullName, c.UnsubscribeToken));
        }

        m.MarkSent(clock.UtcNow);
        await db.SaveOrConflictAsync(ct);
        var delivered = 0;
        foreach (var (recipient, email, name, token) in sent)
        {
            var body = $"Здравствуйте, {name}!\n\n{m.Text}\n\n—\nВы получили это письмо, потому что дали согласие на рассылки. "
                       + $"Отписаться: {mail.PublicUrl}/account/crm-unsubscribe/{token:N}";
            var ok = await mail.SendAsync(email, m.Subject, body, ct);
            recipient.SetDelivered(ok);
            delivered += ok ? 1 : 0;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmMailingSent, nameof(CrmMailing), id.ToString(),
            null, $"«{m.Subject}»: получателей {sent.Count}, доставлено {delivered}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return delivered;
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

    private IQueryable<CrmContact> Audience(AccessContext ctx, long? tagId)
    {
        var q = db.CrmContacts.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.MarketingConsent && c.Email != null && !c.IsArchived
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
