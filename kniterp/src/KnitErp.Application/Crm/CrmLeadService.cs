using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>Отбор лидов. Mine — только где я ответственный (у руководителя — переключатель «мои / все»).</summary>
public sealed record CrmLeadFilter(
    CrmLeadStatus? Status = null, string? Search = null, long? ResponsibleUserId = null, long? SourceId = null, bool Mine = false,
    long? TagId = null, bool OpenOnly = false, int Page = 0);

public sealed record CrmLeadRowDto(
    long Id, string Number, string Title, CrmLeadStatus Status, string? Source, string Responsible, string? ContactName, string? CompanyName,
    decimal? ExpectedAmount, string CurrencyCode, DateTime CreatedAtUtc)
{
    public string StatusName => CrmLead.StatusName(Status);
}

public sealed record CrmLeadDto(
    long Id, string Number, CrmLeadValues Values, CrmLeadStatus Status, long ResponsibleUserId, string Responsible, string? Source, string? Channel,
    string? RejectReason, string? RejectComment, long? MergedIntoLeadId, string? MergedIntoNumber, long? DealId, string? DealNumber,
    long? ContactId, string? Contact, long? CounterpartyId, string? Counterparty, string CreatedBy, DateTime CreatedAtUtc, DateTime? ClosedAtUtc,
    bool CanEdit, bool CanAll, bool CanSeeAmounts, byte[] RowVersion)
{
    public string StatusName => CrmLead.StatusName(Status);
    public bool IsClosed => Status is CrmLeadStatus.Qualified or CrmLeadStatus.Rejected;
}

/// <summary>
/// Квалификация (D605). PipelineId null — первая действующая воронка; CounterpartyId — выбрать контрагента вручную
/// (иначе ищется по ИНН, затем по точному названию; не найден — создаётся с ролью «покупатель»); DealTitle и Amount —
/// уточнить сделку (по умолчанию из лида).
/// </summary>
public sealed record CrmQualifyCommand(long? PipelineId = null, long? CounterpartyId = null, string? DealTitle = null, decimal? Amount = null);

public sealed record CrmQualifyResult(long DealId, long ContactId, long CounterpartyId, bool CounterpartyCreated, bool ContactCreated);

/// <summary>
/// Лиды (D604, D605): список, быстрый ввод, карточка, «в работу», отказ с причиной, подсказка дубликатов, объединение
/// (crm.all) и квалификация — одна транзакция: контакт + контрагент + сделка, лид остаётся в истории со ссылками.
/// Без crm.all видны только свои лиды; чужой — «не найдено».
/// </summary>
public sealed class CrmLeadService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IRequisitesLookup requisites)
{
    public async Task<CrmOptionsDto> OptionsAsync(CancellationToken ct = default) =>
        await CrmCommon.OptionsAsync(db, await guard.DemandAsync(Permissions.CrmView, ct), ct);

    public async Task<CrmPage<CrmLeadRowDto>> ListAsync(CrmLeadFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var q = CrmAccess.Leads(db, ctx).AsNoTracking();
        if (filter.Mine)
        {
            q = q.Where(l => l.ResponsibleUserId == ctx.UserId);
        }

        if (filter.Status is { } status)
        {
            q = q.Where(l => l.Status == status);
        }
        else if (filter.OpenOnly)
        {
            q = q.Where(l => l.Status == CrmLeadStatus.New || l.Status == CrmLeadStatus.InWork);
        }

        if (filter.ResponsibleUserId is { } responsible)
        {
            q = q.Where(l => l.ResponsibleUserId == responsible);
        }

        if (filter.SourceId is { } source)
        {
            q = q.Where(l => l.SourceId == source);
        }

        if (filter.TagId is { } tagId)
        {
            q = q.Where(l => db.TagLinks.Any(t => t.TagId == tagId && t.Entity == TaggedEntity.CrmLead && t.EntityId == l.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            var phone = TryPhone(text);
            q = q.Where(l => l.Number.Contains(text) || l.Title.Contains(text) || (l.CompanyName != null && l.CompanyName.Contains(text))
                             || (l.ContactName != null && l.ContactName.Contains(text)) || l.Inn == text || l.Email == text.ToLower()
                             || (phone != null && l.Phone == phone));
        }

        var total = await q.CountAsync(ct);
        var (page, skip) = CrmCommon.Paging(filter.Page);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var rows = await (from l in q
                          join u in db.Users.AsNoTracking() on l.ResponsibleUserId equals u.Id
                          join s in db.CrmReferences.AsNoTracking() on l.SourceId equals s.Id into sj
                          from s in sj.DefaultIfEmpty()
                          orderby l.Id descending
                          select new CrmLeadRowDto(l.Id, l.Number, l.Title, l.Status, s == null ? null : s.Name, u.DisplayName, l.ContactName, l.CompanyName,
                              amounts ? l.ExpectedAmount : null, l.CurrencyCode, l.CreatedAtUtc))
            .Skip(skip).Take(CrmCommon.PageSize).ToListAsync(ct);
        return new CrmPage<CrmLeadRowDto>(rows, total, page, CrmCommon.PageSize);
    }

    public async Task<CrmLeadDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var l = await CrmAccess.Leads(db, ctx).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Лид");
        long[] userIds = [l.ResponsibleUserId, l.CreatedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        long?[] refIds = [l.SourceId, l.RejectReasonId];
        var refs = await db.CrmReferences.AsNoTracking().Where(r => refIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var channel = l.ChannelId is { } ch ? await db.Lookups.AsNoTracking().Where(x => x.Id == ch).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
        var merged = l.MergedIntoLeadId is { } m ? await db.CrmLeads.AsNoTracking().Where(x => x.Id == m).Select(x => x.Number).SingleOrDefaultAsync(ct) : null;
        var deal = l.DealId is { } d ? await db.CrmDeals.AsNoTracking().Where(x => x.Id == d).Select(x => x.Number).SingleOrDefaultAsync(ct) : null;
        var contact = l.ContactId is { } c ? await db.CrmContacts.AsNoTracking().Where(x => x.Id == c).Select(x => x.FullName).SingleOrDefaultAsync(ct) : null;
        var counterparty = l.CounterpartyId is { } cp ? await db.Counterparties.AsNoTracking().Where(x => x.Id == cp).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var values = new CrmLeadValues(l.Title, l.SourceId, l.ChannelId, l.ResponsibleUserId, l.ContactName, l.Phone, l.Email, l.Messenger, l.CompanyName,
            l.Inn, l.Need, amounts ? l.ExpectedAmount : null, l.CurrencyCode);
        return new CrmLeadDto(l.Id, l.Number, values, l.Status, l.ResponsibleUserId, users[l.ResponsibleUserId],
            Name(refs, l.SourceId), channel, Name(refs, l.RejectReasonId), l.RejectComment, l.MergedIntoLeadId, merged, l.DealId, deal,
            l.ContactId, contact, l.CounterpartyId, counterparty, users[l.CreatedByUserId], l.CreatedAtUtc, l.ClosedAtUtc,
            ctx.Permissions.Has(Permissions.CrmEdit), CrmAccess.SeesAll(ctx), amounts, l.RowVersion);
    }

    /// <summary>Быстрый ввод и полная форма: номер Л-000001 сквозной по организации.</summary>
    public async Task<long> CreateAsync(CrmLeadValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        values = WithoutHiddenAmount(ctx, values, null);
        await ValidateAsync(ctx, values, null, ct);
        var currency = await CrmCommon.CurrencyAsync(db, ctx, ct);
        CrmLead.Create(ctx.OrganizationId, "Л-0", values, currency, ctx.UserId, clock.UtcNow); // проверка полей до выдачи номера
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
        var lead = CrmLead.Create(ctx.OrganizationId, number, values, currency, ctx.UserId, clock.UtcNow);
        db.CrmLeads.Add(lead);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmLeadCreated, lead.Id, null, lead.Describe(), null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return lead.Id;
    }

    public async Task UpdateAsync(long id, CrmLeadValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, lead) = await LoadForEditAsync(id, rowVersion, ct);
        values = WithoutHiddenAmount(ctx, values, lead.ExpectedAmount);
        await ValidateAsync(ctx, values, lead, ct);
        var before = lead.Describe();
        try
        {
            lead.Update(values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmLeadChanged, id, before, lead.Describe(), null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task StartWorkAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, lead) = await LoadForEditAsync(id, rowVersion, ct);
        lead.StartWork();
        Audit(ctx, AuditActions.CrmLeadChanged, id, "Новый", "В работе", lead.Number);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RejectAsync(long id, long reasonId, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, lead) = await LoadForEditAsync(id, rowVersion, ct);
        await CrmCommon.EnsureReferenceAsync(db, ctx, reasonId, null, CrmReferenceKind.LossReason, ct);
        var before = CrmLead.StatusName(lead.Status);
        try
        {
            lead.Reject(reasonId, comment, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmLeadRejected, id, before, "Отказ", lead.RejectComment ?? lead.Number);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task ReopenAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, lead) = await LoadForEditAsync(id, rowVersion, ct);
        lead.Reopen();
        Audit(ctx, AuditActions.CrmLeadChanged, id, "Отказ", "В работе", lead.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Подсказка «похожие» при вводе лида: по телефону, почте и ИНН среди лидов, контактов и контрагентов (D605).</summary>
    public async Task<IReadOnlyList<CrmDuplicateDto>> DuplicatesAsync(string? phone, string? email, string? inn, long? exceptLeadId = null,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        try
        {
            return await CrmCommon.DuplicatesAsync(db, ctx, phone, email, inn, exceptLeadId, null, ct);
        }
        catch (BusinessRuleException)
        {
            return []; // номер ещё набирается — подсказки нет, ошибку покажет сохранение
        }
    }

    /// <summary>
    /// Объединение дубликатов (D605) — только с crm.all: <paramref name="duplicateId"/> закрывается отказом «Дубликат»
    /// со ссылкой на основной, пустые поля основного дополняются из него. Запись в журнал.
    /// </summary>
    public async Task MergeAsync(long duplicateId, long targetId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmAll, ct);
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var duplicate = await db.CrmLeads.SingleOrDefaultAsync(l => l.Id == duplicateId && l.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Лид");
        var target = await db.CrmLeads.SingleOrDefaultAsync(l => l.Id == targetId && l.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Лид");
        var reasonId = await DuplicateReasonAsync(ctx, ct);
        var before = target.Describe();
        try
        {
            duplicate.MergeInto(target, reasonId, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmLeadMerged, duplicateId, duplicate.Number, $"объединён с {target.Number}", null);
        Audit(ctx, AuditActions.CrmLeadChanged, targetId, before, target.Describe(), $"объединение с {duplicate.Number}");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Квалификация (D605) — одна транзакция: контакт (существующий с тем же телефоном или почтой либо новый), контрагент
    /// (выбранный, найденный по ИНН или точному названию, иначе новый «покупатель») и сделка на первом рабочем этапе воронки.
    /// Повторно квалифицировать нельзя.
    /// </summary>
    public async Task<CrmQualifyResult> QualifyAsync(long id, CrmQualifyCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.lead.{id}", ct);
        var lead = await CrmAccess.Leads(db, ctx).SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Лид");
        if (lead.Status == CrmLeadStatus.Qualified)
        {
            throw new BusinessRuleException("crm.lead.qualified", "Лид уже квалифицирован — по нему есть сделка.");
        }

        lead.EnsureOpen();
        if (lead.ContactName is null)
        {
            throw new BusinessRuleException("crm.lead.contact_required", "Для квалификации укажите контактное лицо лида.");
        }

        var stage = await FirstStageAsync(ctx, cmd.PipelineId, ct);
        var (counterparty, counterpartyCreated) = await CounterpartyForAsync(ctx, lead, cmd.CounterpartyId, ct);

        // D128: тот же телефон или почта ищутся только среди контактов, видимых пользователю (CrmAccess.Contacts): иначе новая сделка
        // привязала бы чужой контакт и открыла бы его менеджеру. Совпадение только с невидимым — новый контакт и отметка в журнале
        // без подробностей о чужом.
        var sameContact = (IQueryable<CrmContact> q) => q.Where(c => !c.IsArchived
                                                                     && ((lead.Phone != null && c.Phone == lead.Phone) || (lead.Email != null && c.Email == lead.Email)));
        var contact = await sameContact(CrmAccess.Contacts(db, ctx)).OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        var contactCreated = contact is null;
        if (contact is null)
        {
            var hiddenDuplicate = !CrmAccess.SeesAll(ctx)
                                  && await sameContact(db.CrmContacts.Where(c => c.OrganizationId == ctx.OrganizationId)).AnyAsync(ct);
            contact = CrmContact.Create(ctx.OrganizationId, new CrmContactValues(lead.ContactName, null, counterparty.Id, lead.Phone, lead.Email, lead.Messenger,
                $"Из лида {lead.Number}", lead.ResponsibleUserId), ctx.UserId, clock.UtcNow);
            db.CrmContacts.Add(contact);
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.CrmContactCreated, contact.Id, null, contact.Describe(),
                hiddenDuplicate ? $"квалификация {lead.Number}; возможный дубль у другого ответственного" : $"квалификация {lead.Number}",
                nameof(CrmContact));
        }

        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var values = new CrmDealValues(cmd.DealTitle ?? lead.Title, counterparty.Id, contact.Id,
            amounts ? cmd.Amount ?? lead.ExpectedAmount ?? 0 : lead.ExpectedAmount ?? 0, lead.CurrencyCode, null, lead.ResponsibleUserId, null, lead.ChannelId,
            lead.SourceId);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmDeal.NumberPrefix, ct);
        var (deal, entry) = CrmDeal.Create(ctx.OrganizationId, number, values, stage, lead.CurrencyCode, ctx.UserId, clock.UtcNow, lead.Id);
        db.CrmDeals.Add(deal);
        await db.SaveChangesAsync(ct);
        entry.AttachTo(deal);
        db.CrmDealStageHistory.Add(entry);
        lead.MarkQualified(contact.Id, counterparty.Id, deal.Id, clock.UtcNow);
        Audit(ctx, AuditActions.CrmDealCreated, deal.Id, null, deal.Describe(), $"из лида {lead.Number}", nameof(CrmDeal));
        Audit(ctx, AuditActions.CrmLeadQualified, lead.Id, CrmLead.StatusName(CrmLeadStatus.InWork), $"сделка {deal.Number}, контрагент {counterparty.Name}",
            lead.Number);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
        return new CrmQualifyResult(deal.Id, contact.Id, counterparty.Id, counterpartyCreated, contactCreated);
    }

    private async Task<CrmStage> FirstStageAsync(AccessContext ctx, long? pipelineId, CancellationToken ct)
    {
        var pipeline = pipelineId is { } pid
            ? await db.CrmPipelines.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pid && p.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Воронка")
            : await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
                  .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).FirstAsync(ct);
        pipeline.EnsureActive();
        return await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == pipeline.Id && !s.IsArchived && s.Kind == CrmStageKind.Open)
                   .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).FirstOrDefaultAsync(ct)
               ?? throw new BusinessRuleException("crm.stage.last_open", "В воронке нет рабочих этапов.");
    }

    /// <summary>Контрагент для квалификации: выбранный, по ИНН, по точному названию или новый «покупатель» (D605).</summary>
    private async Task<(Counterparty Counterparty, bool Created)> CounterpartyForAsync(AccessContext ctx, CrmLead lead, long? chosenId, CancellationToken ct)
    {
        Counterparty? found = null;
        if (chosenId is { } cid)
        {
            found = await db.Counterparties.SingleOrDefaultAsync(c => c.Id == cid && c.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Контрагент");
        }
        else if (lead.Inn is { } inn)
        {
            found = await db.Counterparties.Where(c => c.OrganizationId == ctx.OrganizationId && c.Inn == inn).OrderBy(c => c.IsArchived).ThenBy(c => c.Id)
                .FirstOrDefaultAsync(ct);
        }

        var name = lead.CompanyName ?? lead.ContactName!;
        found ??= await db.Counterparties.Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.Name == name).OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (found is not null)
        {
            if (found.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Контрагент «{found.Name}» в архиве. Верните его из архива или выберите другого.");
            }

            if (!found.IsCustomer)
            {
                // Найденный поставщик становится и покупателем: иначе по сделке не оформить заказ покупателя.
                var changes = found.Update(found.Name, found.Inn, found.Kpp, found.IsSupplier, true, found.Comment);
                foreach (var change in changes)
                {
                    Audit(ctx, AuditActions.CatalogChanged, found.Id, change.Before, change.After, change.Field, nameof(Counterparty));
                }
            }

            return (found, false);
        }

        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
        // А2 (D662): реквизиты нового покупателя — из ЕГРЮЛ/ЕГРИП по ИНН лида, если справочник подключён; недоступен — как ввёл менеджер.
        string? kpp = null, address = null;
        if (lead.Inn is { } leadInn && requisites.IsConfigured && country == KnitErp.Domain.Organizations.Countries.Russia
            && (KnitErp.Domain.Organizations.RussianRequisites.IsValidLegalEntityInn(leadInn) || KnitErp.Domain.Organizations.RussianRequisites.IsValidPersonInn(leadInn)))
        {
            try
            {
                if (await requisites.FindByInnAsync(leadInn, ct) is { } r)
                {
                    name = r.ShortName.Length <= Counterparty.NameMaxLength ? r.ShortName : name;
                    kpp = r.Kpp;
                    address = r.Address;
                }
            }
            catch (ExternalServiceUnavailableException)
            {
            }
        }

        var created = Counterparty.Create(ctx.OrganizationId, name, lead.Inn, kpp, false, true, $"Создан из лида {lead.Number}", country);
        if (address is not null)
        {
            created.SetAddress(address);
        }

        db.Counterparties.Add(created);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, created.Id, null, created.Inn is null ? created.Name : $"{created.Name}, ИНН {created.Inn}",
            $"квалификация {lead.Number}", nameof(Counterparty));
        return (created, true);
    }

    private async Task<long> DuplicateReasonAsync(AccessContext ctx, CancellationToken ct)
    {
        const string name = "Дубликат";
        var id = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == CrmReferenceKind.LossReason && r.Name == name)
            .OrderBy(r => r.IsArchived).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing)
        {
            return existing;
        }

        var reason = CrmReference.Create(ctx.OrganizationId, CrmReferenceKind.LossReason, name, 1000);
        db.CrmReferences.Add(reason);
        await db.SaveChangesAsync(ct);
        return reason.Id;
    }

    private async Task ValidateAsync(AccessContext ctx, CrmLeadValues v, CrmLead? current, CancellationToken ct)
    {
        await CrmCommon.EnsureReferenceAsync(db, ctx, v.SourceId, current?.SourceId, CrmReferenceKind.Source, ct);
        await CrmCommon.EnsureLookupAsync(db, ctx, v.ChannelId, current?.ChannelId, LookupKind.SalesChannel, ct);
        await CrmCommon.EnsureResponsibleAsync(db, ctx, v.ResponsibleUserId, current?.ResponsibleUserId ?? ctx.UserId, ct);
    }

    /// <summary>Без права «Цены и суммы» сумма не видна — и не меняется: сохраняется прежняя.</summary>
    private static CrmLeadValues WithoutHiddenAmount(AccessContext ctx, CrmLeadValues v, decimal? current) =>
        ctx.Permissions.Has(Permissions.PriceView) ? v : v with { ExpectedAmount = current };

    private async Task<(AccessContext, CrmLead)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var lead = await CrmAccess.Leads(db, ctx).SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Лид");
        return (ctx, lead.EnsureVersion(lead.RowVersion, rowVersion));
    }

    private static string? TryPhone(string text)
    {
        try
        {
            return text.Count(char.IsAsciiDigit) >= 10 ? CrmText.NormalizePhone(text) : null;
        }
        catch (BusinessRuleException)
        {
            return null;
        }
    }

    private static string? Name(Dictionary<long, string> map, long? id) => id is { } v && map.TryGetValue(v, out var n) ? n : null;

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason, string entity = nameof(CrmLead)) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
