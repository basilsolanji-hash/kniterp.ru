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

public sealed record CrmContactDto(
    long Id, CrmContactValues Values, string? Counterparty, string Responsible, bool IsArchived, IReadOnlyList<CrmDealRowDto> Deals,
    DateTime CreatedAtUtc, bool CanEdit, byte[] RowVersion);

/// <summary>
/// Контакты (D606, этап 1): список, карточка со сделками, создание и правка, архив, подсказка дубликатов по телефону и почте.
/// Без crm.all видны свои контакты и основные контакты своих сделок. Телефоны и почты в журнал — только маской (D612).
/// Согласия 152-ФЗ, несколько телефонов и выгрузка — этап 2 (D610–D619).
/// </summary>
public sealed class CrmContactService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
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
        var c = await CrmAccess.Contacts(db, ctx).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Контакт");
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
        return new CrmContactDto(c.Id, new CrmContactValues(c.FullName, c.Position, c.CounterpartyId, c.Phone, c.Email, c.Messenger, c.Comment, c.ResponsibleUserId),
            counterparty, responsible, c.IsArchived, deals, c.CreatedAtUtc, ctx.Permissions.Has(Permissions.CrmEdit), c.RowVersion);
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
        var c = await CrmAccess.Contacts(db, ctx).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Контакт");
        return (ctx, c.EnsureVersion(c.RowVersion, rowVersion));
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CrmContact), id.ToString(),
            before, after, null, currentUser.CorrelationId));
}
