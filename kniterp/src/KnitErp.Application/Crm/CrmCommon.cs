using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>Страница списка: строки и общее число найденных — для «показано 100 из 2 345».</summary>
public sealed record CrmPage<T>(IReadOnlyList<T> Rows, int Total, int Page, int PageSize);

/// <summary>
/// Списки для форм CRM: сотрудники, источники, причины, каналы и проекты базы, воронки с этапами, валюты. CanSeeAmounts — право
/// «Цены и суммы» (без него суммы сделок и деньги базы — «—»), CanAll — crm.all (все сделки, переназначение).
/// </summary>
public sealed record CrmOptionsDto(
    IReadOnlyList<LookupDto> Users, IReadOnlyList<LookupDto> Sources, IReadOnlyList<LookupDto> LossReasons, IReadOnlyList<LookupDto> Channels,
    IReadOnlyList<LookupDto> Projects, IReadOnlyList<CrmPipelineDto> Pipelines, IReadOnlyList<string> Currencies, string DefaultCurrency,
    bool CanEdit, bool CanAll, bool CanSeeAmounts, long UserId);

public enum CrmDuplicateKind : byte
{
    Lead = 1,
    Contact = 2,
    Counterparty = 3,
}

/// <summary>
/// Возможный дубликат (D605): что совпало и где. Телефон и почта в подсказке не показываются. LeadStatus — у лида;
/// Subtitle — должность контакта.
/// </summary>
public sealed record CrmDuplicateDto(
    CrmDuplicateKind Kind, long Id, string Title, string? Subtitle, string Href, bool ByPhone, bool ByEmail, bool ByInn, CrmLeadStatus? LeadStatus = null);

internal static class CrmCommon
{
    public const int PageSize = 100;

    public static async Task<CrmOptionsDto> OptionsAsync(IKnitErpDbContext db, AccessContext ctx, CancellationToken ct)
    {
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var users = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.Status == MembershipStatus.Active)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.DisplayName).Select(u => new LookupDto(u.Id, u.DisplayName)).ToListAsync(ct);
        var refs = await db.CrmReferences.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name).Select(r => new { r.Kind, Dto = new LookupDto(r.Id, r.Name) }).ToListAsync(ct);
        var lookups = await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && !l.IsArchived)
            .OrderBy(l => l.Name).Select(l => new { l.Kind, Dto = new LookupDto(l.Id, l.Name) }).ToListAsync(ct);
        var currency = await CurrencyAsync(db, ctx, ct);
        return new CrmOptionsDto(users,
            refs.Where(r => r.Kind == CrmReferenceKind.Source).Select(r => r.Dto).ToList(),
            refs.Where(r => r.Kind == CrmReferenceKind.LossReason).Select(r => r.Dto).ToList(),
            lookups.Where(l => l.Kind == LookupKind.SalesChannel).Select(l => l.Dto).ToList(),
            lookups.Where(l => l.Kind == LookupKind.Project).Select(l => l.Dto).ToList(),
            await CrmSettingsService.PipelinesAsync(db, ctx.OrganizationId, includeArchived: false, ct),
            CrmCurrency.Codes, currency,
            ctx.Permissions.Has(Permissions.CrmEdit), CrmAccess.SeesAll(ctx), ctx.Permissions.Has(Permissions.PriceView), ctx.UserId);
    }

    public static async Task<string> CurrencyAsync(IKnitErpDbContext db, AccessContext ctx, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CurrencyCode).SingleAsync(ct);

    /// <summary>Ответственный — активный сотрудник организации. Без crm.all назначить можно только себя (D600).</summary>
    public static async Task EnsureResponsibleAsync(IKnitErpDbContext db, AccessContext ctx, long? userId, long? current, CancellationToken ct)
    {
        if (userId is not { } uid || uid == current)
        {
            return;
        }

        if (uid != ctx.UserId && !CrmAccess.SeesAll(ctx))
        {
            throw new BusinessRuleException("crm.responsible.other", "Назначить ответственным другого сотрудника может только руководитель продаж.");
        }

        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == uid && m.Status == MembershipStatus.Active, ct))
        {
            throw new NotFoundException("Сотрудник");
        }
    }

    /// <summary>Запись справочника CRM своей организации нужного вида; новая ссылка — только на действующую.</summary>
    public static async Task EnsureReferenceAsync(IKnitErpDbContext db, AccessContext ctx, long? id, long? current, CrmReferenceKind kind, CancellationToken ct)
    {
        if (id is not { } rid || rid == current)
        {
            return;
        }

        var item = await db.CrmReferences.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rid && r.OrganizationId == ctx.OrganizationId, ct);
        if (item is null || item.Kind != kind)
        {
            throw new NotFoundException(CrmReference.KindName(kind));
        }

        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"«{item.Name}» в архиве.");
        }
    }

    /// <summary>Проект или канал продаж базы (D77): действующий, своей организации.</summary>
    public static async Task EnsureLookupAsync(IKnitErpDbContext db, AccessContext ctx, long? id, long? current, LookupKind kind, CancellationToken ct)
    {
        if (id is not { } lid || lid == current)
        {
            return;
        }

        var lookup = await db.Lookups.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lid && l.OrganizationId == ctx.OrganizationId, ct);
        if (lookup is null || lookup.Kind != kind)
        {
            throw new NotFoundException(Lookup.KindName(kind));
        }

        if (lookup.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"«{lookup.Name}» в архиве.");
        }
    }

    public static async Task<Counterparty> CounterpartyAsync(IKnitErpDbContext db, AccessContext ctx, long id, long? current, CancellationToken ct)
    {
        var c = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Контрагент");
        if (c.IsArchived && id != current)
        {
            throw new BusinessRuleException("catalog.archived", $"Контрагент «{c.Name}» в архиве.");
        }

        return c;
    }

    /// <summary>Возможные дубликаты по телефону, почте и ИНН среди лидов и контактов организации (D605).</summary>
    public static async Task<IReadOnlyList<CrmDuplicateDto>> DuplicatesAsync(
        IKnitErpDbContext db, AccessContext ctx, string? phone, string? email, string? inn, long? exceptLeadId, long? exceptContactId, CancellationToken ct)
    {
        var p = CrmText.NormalizePhone(phone);
        var e = CrmText.NormalizeEmail(email);
        var i = CrmText.NormalizeInn(inn);
        if (p is null && e is null && i is null)
        {
            return [];
        }

        var org = ctx.OrganizationId;
        var leads = await db.CrmLeads.AsNoTracking()
            .Where(l => l.OrganizationId == org && l.Id != exceptLeadId && l.MergedIntoLeadId == null
                        && ((p != null && l.Phone == p) || (e != null && l.Email == e) || (i != null && l.Inn == i)))
            .OrderByDescending(l => l.Id).Take(10)
            .Select(l => new { l.Id, l.Number, l.Title, l.Status, l.Phone, l.Email, l.Inn }).ToListAsync(ct);
        var contacts = await db.CrmContacts.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Id != exceptContactId && ((p != null && c.Phone == p) || (e != null && c.Email == e)))
            .OrderByDescending(c => c.Id).Take(10)
            .Select(c => new { c.Id, c.FullName, c.Position, c.Phone, c.Email }).ToListAsync(ct);
        var counterparties = i is null ? [] : await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Inn == i).Take(5).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return leads.Select(l => new CrmDuplicateDto(CrmDuplicateKind.Lead, l.Id, $"{l.Number} {l.Title}", null, $"crm/leads/{l.Id}",
                p != null && l.Phone == p, e != null && l.Email == e, i != null && l.Inn == i, l.Status))
            .Concat(contacts.Select(c => new CrmDuplicateDto(CrmDuplicateKind.Contact, c.Id, c.FullName, c.Position, $"crm/contacts/{c.Id}",
                p != null && c.Phone == p, e != null && c.Email == e, false)))
            .Concat(counterparties.Select(c => new CrmDuplicateDto(CrmDuplicateKind.Counterparty, c.Id, c.Name, null, $"counterparties/{c.Id}", false, false, true)))
            .ToList();
    }

    public static (int Page, int Skip) Paging(int page) => (Math.Max(0, page), Math.Max(0, page) * PageSize);
}
