using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Tagging;
using KnitErp.Domain.Access;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Видимость объектов CRM (D600). С правом crm.all — все лиды, сделки и контакты организации. Только с crm.view — свои:
/// где пользователь ответственный или наблюдатель сделки; контакт — свой или основной контакт видимой сделки.
/// Чужое — «не найдено» (404), как объект чужой организации. Фильтры — в запросе, до проекции.
/// </summary>
public static class CrmAccess
{
    public static bool SeesAll(AccessContext ctx) => ctx.Permissions.Has(Permissions.CrmAll);

    public static IQueryable<CrmLead> Leads(IKnitErpDbContext db, AccessContext ctx)
    {
        var q = db.CrmLeads.Where(l => l.OrganizationId == ctx.OrganizationId);
        return SeesAll(ctx) ? q : q.Where(l => l.ResponsibleUserId == ctx.UserId);
    }

    public static IQueryable<CrmDeal> Deals(IKnitErpDbContext db, AccessContext ctx)
    {
        var q = db.CrmDeals.Where(d => d.OrganizationId == ctx.OrganizationId);
        return SeesAll(ctx) ? q : q.Where(d => d.ResponsibleUserId == ctx.UserId || d.Participants.Any(p => p.UserId == ctx.UserId));
    }

    public static IQueryable<CrmContact> Contacts(IKnitErpDbContext db, AccessContext ctx)
    {
        var q = db.CrmContacts.Where(c => c.OrganizationId == ctx.OrganizationId);
        if (SeesAll(ctx))
        {
            return q;
        }

        var deals = Deals(db, ctx);
        return q.Where(c => c.ResponsibleUserId == ctx.UserId || deals.Any(d => d.ContactId == c.Id));
    }
}

/// <summary>Лиды, сделки и контакты в тегах и обсуждениях (D601): название, подпись и ссылка — только видимые пользователю.</summary>
public static class CrmVisibility
{
    public static async Task<IReadOnlyList<TaggedObjectDto>> DescribeAsync(
        IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.CrmView))
        {
            return [];
        }

        switch (entity)
        {
            case TaggedEntity.CrmLead:
                return (await CrmAccess.Leads(db, ctx).AsNoTracking().Where(l => ids.Contains(l.Id))
                        .Select(l => new { l.Id, l.Number, l.Title, l.CompanyName }).ToListAsync(ct))
                    .Select(l => new TaggedObjectDto(entity, l.Id, $"{l.Number} {l.Title}", l.CompanyName, $"crm/leads/{l.Id}")).ToList();
            case TaggedEntity.CrmDeal:
                return (await (from d in CrmAccess.Deals(db, ctx).AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                               where ids.Contains(d.Id)
                               select new { d.Id, d.Number, d.Title, c.Name }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"{d.Number} {d.Title}", d.Name, $"crm/deals/{d.Id}")).ToList();
            case TaggedEntity.CrmContact:
                // Только ФИО и должность: телефоны и почты в тегах и обсуждениях не показываются.
                return (await CrmAccess.Contacts(db, ctx).AsNoTracking().Where(c => ids.Contains(c.Id))
                        .Select(c => new { c.Id, c.FullName, c.Position }).ToListAsync(ct))
                    .Select(c => new TaggedObjectDto(entity, c.Id, c.FullName, c.Position, $"crm/contacts/{c.Id}")).ToList();
            default:
                return [];
        }
    }
}
