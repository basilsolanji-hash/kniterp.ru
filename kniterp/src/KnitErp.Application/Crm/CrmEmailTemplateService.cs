using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmEmailTemplateDto(long Id, string Name, string Subject, string Body, bool IsArchived, byte[] RowVersion);

/// <summary>Письмо, собранное из шаблона для карточки: тема и текст с подставленными значениями.</summary>
public sealed record CrmRenderedEmail(string Subject, string Text);

/// <summary>
/// Шаблоны писем CRM (D674): справочник организации (правит crm.settings, пользуется crm.view), подстановка переменных по лиду, сделке
/// или контакту — только видимым пользователю. Значения берутся из карточки: имя и компания клиента, ответственный, сделка и сумма.
/// </summary>
public sealed class CrmEmailTemplateService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<CrmEmailTemplateDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        return await db.CrmEmailTemplates.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && (includeArchived || !t.IsArchived))
            .OrderBy(t => t.IsArchived).ThenBy(t => t.Name)
            .Select(t => new CrmEmailTemplateDto(t.Id, t.Name, t.Subject, t.Body, t.IsArchived, t.RowVersion)).ToListAsync(ct);
    }

    public async Task<long> CreateAsync(string? name, string? subject, string? body, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var t = CrmEmailTemplate.Create(ctx.OrganizationId, name, subject, body, ctx.UserId, clock.UtcNow);
        await EnsureUniqueAsync(ctx, t.Name, null, ct);
        db.CrmEmailTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        Audit(ctx, t.Id, null, $"шаблон письма «{t.Name}» создан");
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    public async Task UpdateAsync(long id, string? name, string? subject, string? body, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var t = await LoadAsync(ctx, id, ct);
        t.EnsureVersion(t.RowVersion, rowVersion);
        var before = $"«{t.Name}»: {t.Subject}";
        t.Update(name, subject, body);
        await EnsureUniqueAsync(ctx, t.Name, t.Id, ct);
        Audit(ctx, t.Id, before, $"«{t.Name}»: {t.Subject}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var t = await LoadAsync(ctx, id, ct);
        t.EnsureVersion(t.RowVersion, rowVersion);
        if (archived)
        {
            t.Archive();
        }
        else
        {
            t.Restore();
        }

        Audit(ctx, t.Id, null, $"шаблон письма «{t.Name}» {(archived ? "в архиве" : "восстановлен")}");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Шаблон, заполненный значениями карточки (лид, сделка или контакт видимы пользователю — иначе 404).</summary>
    public async Task<CrmRenderedEmail> RenderAsync(long templateId, CrmTarget target, long targetId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var t = await db.CrmEmailTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == templateId && x.OrganizationId == ctx.OrganizationId && !x.IsArchived, ct)
                ?? throw new NotFoundException("Шаблон письма");
        var values = await ValuesAsync(db, ctx, target, targetId, ct) ?? throw new NotFoundException("Объект");
        return new CrmRenderedEmail(CrmEmailTemplate.Render(t.Subject, values), CrmEmailTemplate.Render(t.Body, values));
    }

    /// <summary>Значения переменных по карточке; null — карточка не видна пользователю.</summary>
    internal static async Task<IReadOnlyDictionary<string, string?>?> ValuesAsync(IKnitErpDbContext db, AccessContext ctx, CrmTarget target, long id, CancellationToken ct)
    {
        string? name, company, deal = null, amount = null;
        long responsible;
        switch (target)
        {
            case CrmTarget.Lead:
                var lead = await CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => l.Id == id)
                    .Select(l => new { l.ContactName, l.CompanyName, l.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (lead is null)
                {
                    return null;
                }

                (name, company, responsible) = (lead.ContactName, lead.CompanyName, lead.ResponsibleUserId);
                break;
            case CrmTarget.Deal:
                var d = await (from x in CrmAccess.Deals(db, ctx).AsNoTracking().Where(x => x.Id == id)
                               join cp in db.Counterparties.AsNoTracking() on x.CounterpartyId equals cp.Id
                               join c in db.CrmContacts.AsNoTracking() on x.ContactId equals c.Id into cj
                               from c in cj.DefaultIfEmpty()
                               select new { x.Title, x.Amount, x.CurrencyCode, x.ResponsibleUserId, Company = cp.Name, Contact = c == null ? null : c.FullName })
                    .SingleOrDefaultAsync(ct);
                if (d is null)
                {
                    return null;
                }

                (name, company, responsible, deal) = (d.Contact, d.Company, d.ResponsibleUserId, d.Title);
                amount = $"{d.Amount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} {d.CurrencyCode}";
                break;
            default:
                var contact = await (from c in CrmAccess.Contacts(db, ctx).AsNoTracking().Where(c => c.Id == id)
                                     join cp in db.Counterparties.AsNoTracking() on c.CounterpartyId equals cp.Id into cpj
                                     from cp in cpj.DefaultIfEmpty()
                                     select new { c.FullName, Company = cp == null ? null : cp.Name, c.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (contact is null)
                {
                    return null;
                }

                (name, company, responsible) = (contact.FullName, contact.Company, contact.ResponsibleUserId);
                break;
        }

        var manager = await db.Users.AsNoTracking().Where(u => u.Id == responsible).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.ShortName).SingleOrDefaultAsync(ct);
        return new Dictionary<string, string?>
        {
            ["Имя"] = name, ["Компания"] = company, ["Менеджер"] = manager, ["Организация"] = org, ["Сделка"] = deal, ["Сумма"] = amount,
        };
    }

    /// <summary>Ответ на письмо — менеджеру, который его отправил: (почта, имя).</summary>
    internal static async Task<(string? Email, string? Name)> ReplyToAsync(IKnitErpDbContext db, long userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new ValueTuple<string?, string?>(u.Email, u.DisplayName)).SingleOrDefaultAsync(ct);

    /// <summary>Писем из карточек на пользователя в час (D678): защита почты сервера от рассылки спама через «Письмо клиенту».</summary>
    public const int EmailsPerUserPerHour = 100;

    internal static async Task EnsureEmailQuotaAsync(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-1);
        var sent = await db.CrmActivities.AsNoTracking().CountAsync(a => a.OrganizationId == ctx.OrganizationId && a.CreatedByUserId == ctx.UserId
            && a.Source == CrmActivitySource.Mail && a.ExternalRef == null && a.CreatedAtUtc >= since, ct);
        if (sent >= EmailsPerUserPerHour)
        {
            throw new BusinessRuleException("crm.mail.quota",
                $"Не больше {EmailsPerUserPerHour} писем в час от одного сотрудника. Для массовых писем — «Рассылки».");
        }
    }

    private async Task EnsureUniqueAsync(AccessContext ctx, string name, long? id, CancellationToken ct)
    {
        if (await db.CrmEmailTemplates.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.Name == name && t.Id != id, ct))
        {
            throw new BusinessRuleException("crm.template.duplicate", "Шаблон с таким названием уже есть.");
        }
    }

    private async Task<CrmEmailTemplate> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.CrmEmailTemplates.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Шаблон письма");

    private void Audit(AccessContext ctx, long id, string? before, string after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmEmailTemplate), id.ToString(),
            before, after, "Шаблоны писем", currentUser.CorrelationId));
}
