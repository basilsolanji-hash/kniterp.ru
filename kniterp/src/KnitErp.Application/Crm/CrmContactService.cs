using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmContactRowDto(
    long Id, string FullName, string? Position, long? CounterpartyId, string? Counterparty, string? Phone, string? Email, string Responsible, bool IsArchived);

public sealed record CrmChannelDto(long Id, CrmChannelKind Kind, string Value, string? Label);

public sealed record CrmContactDto(
    long Id, CrmContactValues Values, string? Counterparty, string Responsible, bool IsArchived, IReadOnlyList<CrmDealRowDto> Deals,
    DateTime CreatedAtUtc, bool CanEdit, byte[] RowVersion, IReadOnlyList<CrmChannelDto>? Channels = null, CrmConsentValues? Consent = null,
    bool IsErased = false, bool CanErase = false, bool CanExport = false);

/// <summary>
/// Контакты (D606, этап 1): список, карточка со сделками, создание и правка, архив, подсказка дубликатов по телефону и почте.
/// Без crm.all видны свои контакты и основные контакты своих сделок. Телефоны и почты в журнал — только маской (D612).
/// Согласия 152-ФЗ, несколько телефонов и выгрузка — этап 2 (D610–D619).
/// </summary>
public sealed class CrmContactService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ISpreadsheetFormat spreadsheet)
{
    public async Task<CrmPage<CrmContactRowDto>> ListAsync(string? search, long? counterpartyId = null, bool includeArchived = false, int page = 0,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var q = CrmAccess.Contacts(db, ctx).AsNoTracking();
        if (!includeArchived)
        {
            q = q.Where(c => !c.IsArchived);
        }

        if (counterpartyId is { } cp)
        {
            q = q.Where(c => c.CounterpartyId == cp);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            var mail = text.ToLowerInvariant();
            string? phone = null;
            try
            {
                phone = text.Count(char.IsAsciiDigit) >= 10 ? CrmText.NormalizePhone(text) : null;
            }
            catch (BusinessRuleException)
            {
            }

            q = q.Where(c => c.FullName.Contains(text) || (c.Position != null && c.Position.Contains(text)) || c.Email == mail || (phone != null && c.Phone == phone)
                             || db.Counterparties.Any(x => x.Id == c.CounterpartyId && x.Name.Contains(text)));
        }

        var total = await q.CountAsync(ct);
        var (p, skip) = CrmCommon.Paging(page);
        var rows = await (from c in q
                          join u in db.Users.AsNoTracking() on c.ResponsibleUserId equals u.Id
                          join k in db.Counterparties.AsNoTracking() on c.CounterpartyId equals k.Id into kj
                          from k in kj.DefaultIfEmpty()
                          orderby c.FullName, c.Id
                          select new CrmContactRowDto(c.Id, c.FullName, c.Position, c.CounterpartyId, k == null ? null : k.Name, c.Phone, c.Email, u.DisplayName,
                              c.IsArchived))
            .Skip(skip).Take(CrmCommon.PageSize).ToListAsync(ct);
        return new CrmPage<CrmContactRowDto>(rows, total, p, CrmCommon.PageSize);
    }

    public async Task<CrmContactDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var c = await CrmAccess.Contacts(db, ctx).AsNoTracking().Include(x => x.Channels).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Контакт");
        var counterparty = c.CounterpartyId is { } cp ? await db.Counterparties.AsNoTracking().Where(x => x.Id == cp).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
        var responsible = await db.Users.AsNoTracking().Where(u => u.Id == c.ResponsibleUserId).Select(u => u.DisplayName).SingleAsync(ct);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var deals = await (from d in CrmAccess.Deals(db, ctx).AsNoTracking()
                           join k in db.Counterparties.AsNoTracking() on d.CounterpartyId equals k.Id
                           join u in db.Users.AsNoTracking() on d.ResponsibleUserId equals u.Id
                           join s in db.CrmStages.AsNoTracking() on d.StageId equals s.Id
                           join p in db.CrmPipelines.AsNoTracking() on d.PipelineId equals p.Id
                           where d.ContactId == id
                           orderby d.Id descending
                           select new CrmDealRowDto(d.Id, d.Number, d.Title, k.Name, p.Name, s.Name, s.Color, d.Outcome, amounts ? d.Amount : null, d.CurrencyCode,
                               d.Probability, d.ExpectedCloseDate, u.DisplayName, d.CreatedAtUtc))
            .Take(100).ToListAsync(ct);
        return new CrmContactDto(c.Id, new CrmContactValues(c.FullName, c.Position, c.CounterpartyId, c.Phone, c.Email, c.Messenger, c.Comment, c.ResponsibleUserId, c.BirthDate),
            counterparty, responsible, c.IsArchived, deals, c.CreatedAtUtc, ctx.Permissions.Has(Permissions.CrmEdit), c.RowVersion,
            c.Channels.OrderBy(x => x.Kind).ThenBy(x => x.Id).Select(x => new CrmChannelDto(x.Id, x.Kind, x.Value, x.Label)).ToList(),
            new CrmConsentValues(c.ProcessingBasis, c.BasisDate, c.BasisNote, c.MarketingConsent, c.MarketingConsentDate), c.IsErased,
            ctx.Permissions.Has(Permissions.CrmAll) && !c.IsErased, ctx.Permissions.Has(Permissions.CrmExport));
    }

    public async Task<long> CreateAsync(CrmContactValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await ValidateAsync(ctx, values, null, ct);
        var contact = CrmContact.Create(ctx.OrganizationId, values, ctx.UserId, clock.UtcNow);
        db.CrmContacts.Add(contact);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmContactCreated, contact.Id, null, contact.Describe());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return contact.Id;
    }

    public async Task UpdateAsync(long id, CrmContactValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        await ValidateAsync(ctx, values, contact, ct);
        var before = contact.Describe();
        try
        {
            contact.Update(values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmContactChanged, id, before, contact.Describe());
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        if (archived)
        {
            contact.Archive();
        }
        else
        {
            contact.Restore();
        }

        Audit(ctx, AuditActions.CrmContactChanged, id, archived ? null : "в архиве", archived ? "в архиве" : "действует");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<IReadOnlyList<CrmDuplicateDto>> DuplicatesAsync(string? phone, string? email, long? exceptContactId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        try
        {
            return await CrmCommon.DuplicatesAsync(db, ctx, phone, email, null, null, exceptContactId, ct);
        }
        catch (BusinessRuleException)
        {
            return [];
        }
    }

    public async Task AddChannelAsync(long id, CrmChannelKind kind, string? value, string? label, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        try
        {
            contact.AddChannel(kind, value, label);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        var added = contact.Channels[^1];
        Audit(ctx, AuditActions.CrmContactChanged, id, null, $"доп. {Masked(added.Kind, added.Value)}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveChannelAsync(long id, long channelId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        var ch = contact.Channels.FirstOrDefault(x => x.Id == channelId);
        contact.RemoveChannel(channelId);
        Audit(ctx, AuditActions.CrmContactChanged, id, ch is null ? null : $"доп. {Masked(ch.Kind, ch.Value)}", "удалён");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task MakePrimaryAsync(long id, long channelId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        var before = contact.Describe();
        contact.MakePrimary(channelId);
        Audit(ctx, AuditActions.CrmContactChanged, id, before, contact.Describe());
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Основание обработки ПДн и согласие на рассылки (D611, D612) — с датами; изменение — в журнал.</summary>
    public async Task SetConsentAsync(long id, CrmConsentValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, contact) = await LoadForEditAsync(id, rowVersion, ct);
        var today = CrmEvents.Today(clock.UtcNow, await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct));
        string change;
        try
        {
            change = contact.SetConsent(values, today);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmContactConsent, nameof(CrmContact), id.ToString(),
            null, change, null, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Удаление ПДн по запросу (D613), только crm.all: ФИО, телефоны, почты и мессенджер контакта и связанных с ним лидов заменяются;
    /// сделки, суммы и история остаются. Запись в журнал без самих данных.
    /// </summary>
    public async Task ErasePersonalDataAsync(long id, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmAll, ct);
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        var why = CrmText.Require(reason, 300, "Основание (запрос субъекта)");
        await using var tx = await db.BeginTransactionAsync(ct);
        var contact = await db.CrmContacts.Include(x => x.Channels).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Контакт");
        var phones = contact.Channels.Where(x => x.Kind == CrmChannelKind.Phone).Select(x => x.Value).Append(contact.Phone).OfType<string>().ToList();
        var mails = contact.Channels.Where(x => x.Kind == CrmChannelKind.Email).Select(x => x.Value).Append(contact.Email).OfType<string>().ToList();
        contact.ErasePersonalData(clock.UtcNow);
        var leads = await db.CrmLeads.Where(l => l.OrganizationId == ctx.OrganizationId
                                                 && (l.ContactId == id || (l.Phone != null && phones.Contains(l.Phone)) || (l.Email != null && mails.Contains(l.Email))))
            .ToListAsync(ct);
        foreach (var lead in leads)
        {
            lead.ErasePersonalData();
        }

        // D677: переписка с клиентом (мессенджеры и почта) по контакту, его лидам и сделкам обезличивается; адреса в рассылках стираются.
        var leadIds = leads.Select(l => l.Id).ToList();
        var dealIds = await db.CrmDeals.Where(d => d.OrganizationId == ctx.OrganizationId && d.ContactId == id).Select(d => d.Id).ToListAsync(ct);
        var talk = await db.CrmActivities.Where(a => a.OrganizationId == ctx.OrganizationId
                && (a.Source == CrmActivitySource.Messenger || a.Source == CrmActivitySource.Mail || a.Kind == CrmActivityKind.Email || a.Kind == CrmActivityKind.Message)
                && ((a.Target == CrmTarget.Contact && a.TargetId == id) || (a.Target == CrmTarget.Lead && leadIds.Contains(a.TargetId))
                    || (a.Target == CrmTarget.Deal && dealIds.Contains(a.TargetId))))
            .ToListAsync(ct);
        foreach (var a in talk)
        {
            a.ErasePersonalData();
        }

        var recipients = await db.CrmMailings.Where(m => m.OrganizationId == ctx.OrganizationId).SelectMany(m => m.Recipients).Where(r => r.ContactId == id).ToListAsync(ct);
        foreach (var r in recipients)
        {
            r.ErasePersonalData();
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmContactErased, nameof(CrmContact), id.ToString(),
            null, $"ПДн удалены, лидов затронуто: {leads.Count}, сообщений и писем: {talk.Count}", why, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Выгрузка контактов в Excel (D614) — только с crm.export, факт выгрузки — в журнал. forMailing — только контакты с согласием
    /// на рекламные рассылки и почтой (38-ФЗ); иначе — все видимые, кроме удалённых.
    /// </summary>
    public async Task<byte[]> ExportAsync(bool forMailing, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmExport, ct);
        var q = CrmAccess.Contacts(db, ctx).AsNoTracking().Where(c => c.ErasedAtUtc == null);
        if (forMailing)
        {
            q = q.Where(c => c.MarketingConsent && c.Email != null && !c.IsArchived);
        }

        var rows = await (from c in q
                          join k in db.Counterparties.AsNoTracking() on c.CounterpartyId equals k.Id into kj
                          from k in kj.DefaultIfEmpty()
                          orderby c.FullName
                          select new { c.FullName, c.Position, Company = k == null ? null : k.Name, c.Phone, c.Email, c.Messenger, c.ProcessingBasis, c.MarketingConsent,
                              c.MarketingConsentDate, c.IsArchived })
            .Take(20_000).ToListAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmExported, nameof(CrmContact), null,
            null, $"контактов: {rows.Count}", forMailing ? "для рассылки (с согласием)" : "все контакты", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return spreadsheet.Write([new SheetData("Контакты",
            ["ФИО", "Должность", "Компания", "Телефон", "Почта", "Мессенджер", "Основание обработки", "Согласие на рассылки", "Архив"],
            rows.Select(r => (IReadOnlyList<string>)[r.FullName, r.Position ?? "", r.Company ?? "", CrmText.FormatPhone(r.Phone) ?? "", r.Email ?? "", r.Messenger ?? "",
                CrmContact.BasisName(r.ProcessingBasis), r.MarketingConsent ? $"да, {r.MarketingConsentDate:dd.MM.yyyy}" : "нет", r.IsArchived ? "да" : ""]).ToList())]);
    }

    private static string Masked(CrmChannelKind kind, string value) => kind switch
    {
        CrmChannelKind.Phone => "тел. " + CrmText.MaskPhone(value),
        CrmChannelKind.Email => "почта " + CrmText.MaskEmail(value),
        _ => "мессенджер",
    };

    private async Task ValidateAsync(AccessContext ctx, CrmContactValues v, CrmContact? current, CancellationToken ct)
    {
        if (v.CounterpartyId is { } cp)
        {
            await CrmCommon.CounterpartyAsync(db, ctx, cp, current?.CounterpartyId, ct);
        }

        await CrmCommon.EnsureResponsibleAsync(db, ctx, v.ResponsibleUserId, current?.ResponsibleUserId ?? ctx.UserId, ct);
    }

    private async Task<(AccessContext, CrmContact)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var c = await CrmAccess.Contacts(db, ctx).Include(x => x.Channels).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Контакт");
        return (ctx, c.EnsureVersion(c.RowVersion, rowVersion));
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CrmContact), id.ToString(),
            before, after, null, currentUser.CorrelationId));
}
