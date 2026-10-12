using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Строка списка соглашений. ValidToday — действует сегодня (по времени организации).</summary>
public sealed record SalesAgreementRowDto(
    long Id, string Number, string Name, SalesAgreementScope Scope, long? CounterpartyId, string? Customer, long? TagId, string? Tag,
    long? PriceTypeId, string? PriceType, decimal DiscountPercent, DateOnly ValidFrom, DateOnly? ValidTo, decimal? PrepaymentPercent, int? PaymentDays,
    decimal? CreditLimit, long? WarehouseId, string? Warehouse, long? ContractId, string? Contract, string? Comment, SalesAgreementStatus Status,
    bool ValidToday, byte[] RowVersion)
{
    public string ScopeName => SalesAgreement.ScopeName(Scope);
    public string Target => Scope switch
    {
        SalesAgreementScope.Customer => Customer ?? string.Empty,
        SalesAgreementScope.Group => Tag ?? string.Empty,
        _ => SalesAgreement.ScopeName(Scope),
    };
}

/// <summary>Запись истории соглашения — из журнала аудита.</summary>
public sealed record SalesAgreementHistoryDto(DateTime AtUtc, string User, string Action, string? Before, string? After);

public sealed record SalesAgreementDto(SalesAgreementRowDto Agreement, IReadOnlyList<SalesAgreementHistoryDto> History);

public sealed record SalesContractOptionDto(long Id, long CounterpartyId, string Name);

public sealed record SalesAgreementOptionsDto(
    IReadOnlyList<LookupDto> Customers, IReadOnlyList<LookupDto> Tags, IReadOnlyList<LookupDto> PriceTypes, IReadOnlyList<LookupDto> Warehouses,
    IReadOnlyList<SalesContractOptionDto> Contracts, IReadOnlyList<LookupDto> Users);

/// <summary>Условия продаж покупателя (вкладка карточки контрагента): вид цены клиента, менеджер, действующее соглашение.</summary>
public sealed record CustomerSalesTermsDto(
    long CounterpartyId, string Customer, long? PriceTypeId, string? PriceType, long? ManagerUserId, string? Manager,
    SalesAgreementRowDto? Current, IReadOnlyList<SalesAgreementRowDto> Agreements, bool CanEdit, byte[]? RowVersion);

/// <summary>Настройки продаж роли (D1103).</summary>
public sealed record SalesRoleSettingsDto(
    long RoleId, string Role, bool IsSystem, decimal? MaxDiscountPercent, long? MinPriceTypeId, string? MinPriceType, SalesOrderVisibility Visibility)
{
    public string VisibilityName => SalesRoleSettings.VisibilityName(Visibility);
}

/// <summary>
/// Соглашения с покупателями, условия продаж клиента и ограничения цен по ролям (D1101–D1103). Смотреть — «Продажи: просмотр»
/// и «Цены и суммы», менять — «Продажи: соглашения и ограничения цен» и «Цены и суммы». Соглашение не удаляется — уходит в архив;
/// каждое изменение — в журнале аудита (это и есть история соглашения).
/// </summary>
public sealed class SalesAgreementService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxAgreements = 5000;

    public async Task<IReadOnlyList<SalesAgreementRowDto>> ListAsync(bool includeArchived = false, long? counterpartyId = null, string? search = null,
        CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var q = db.SalesAgreements.AsNoTracking().Where(a => a.OrganizationId == ctx.OrganizationId);
        if (!includeArchived)
        {
            q = q.Where(a => a.Status == SalesAgreementStatus.Active);
        }

        if (counterpartyId is { } cid)
        {
            q = q.Where(a => a.CounterpartyId == cid);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            q = q.Where(a => a.Number.Contains(text) || a.Name.Contains(text)
                             || db.Counterparties.Any(c => c.Id == a.CounterpartyId && (c.Name.Contains(text) || c.Inn == text)));
        }

        var list = await q.OrderBy(a => a.Status).ThenByDescending(a => a.ValidFrom).ThenByDescending(a => a.Id).Take(MaxAgreements).ToListAsync(ct);
        return await RowsAsync(ctx, list, ct);
    }

    public async Task<SalesAgreementDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var agreement = await db.SalesAgreements.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Соглашение");
        var key = id.ToString();
        var history = await db.AuditEntries.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.EntityType == nameof(SalesAgreement) && e.EntityId == key)
            .OrderByDescending(e => e.Id).Take(200)
            .Join(db.Users.AsNoTracking(), e => e.ActorUserId, u => u.Id, (e, u) => new SalesAgreementHistoryDto(e.OccurredAtUtc, u.DisplayName, e.Action, e.Before, e.After))
            .ToListAsync(ct);
        return new SalesAgreementDto((await RowsAsync(ctx, [agreement], ct)).Single(), history);
    }

    public async Task<SalesAgreementOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var org = ctx.OrganizationId;
        var customers = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && c.IsCustomer && !c.IsArchived)
            .OrderBy(c => c.Name).Select(c => new LookupDto(c.Id, c.Name)).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived).OrderBy(t => t.Name)
            .Select(t => new LookupDto(t.Id, t.Name)).ToListAsync(ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LookupDto(t.Id, t.Name)).ToListAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == org && !w.IsArchived).OrderBy(w => w.Name)
            .Select(w => new LookupDto(w.Id, w.Name)).ToListAsync(ct);
        var contracts = (await db.CounterpartyContracts.AsNoTracking()
                .Where(c => c.OrganizationId == org && c.Status == ContractStatus.Active && (c.Kind == ContractKind.Sale || c.Kind == ContractKind.Commission))
                .OrderBy(c => c.ContractDate).Select(c => new { c.Id, c.CounterpartyId, c.Number, c.ContractDate }).ToListAsync(ct))
            .Select(c => new SalesContractOptionDto(c.Id, c.CounterpartyId, $"№ {c.Number} от {c.ContractDate:dd.MM.yyyy}")).ToList();
        var users = await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == org && m.Status == MembershipStatus.Active)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u).OrderBy(u => u.DisplayName)
            .Select(u => new LookupDto(u.Id, u.DisplayName)).ToListAsync(ct);
        return new SalesAgreementOptionsDto(customers, tags, types, warehouses, contracts, users);
    }

    public async Task<long> CreateAsync(SalesAgreementTerms terms, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        await ValidateAsync(ctx, terms, null, ct);
        var count = await db.SalesAgreements.CountAsync(a => a.OrganizationId == ctx.OrganizationId, ct);
        if (count >= MaxAgreements)
        {
            throw new BusinessRuleException("sales.agreement.too_many", $"Соглашений не больше {MaxAgreements}.");
        }

        // Проверка данных — до выдачи номера: отказ после счётчика оставил бы его в контексте вкладки.
        SalesAgreement.Create(ctx.OrganizationId, "—", terms, ctx.UserId, clock.UtcNow);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SalesAgreement.NumberPrefix, ct);
        var agreement = SalesAgreement.Create(ctx.OrganizationId, number, terms, ctx.UserId, clock.UtcNow);
        db.SalesAgreements.Add(agreement);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesAgreementCreated, agreement.Id, null, await DescribeAsync(ctx, agreement, ct), $"{number} «{agreement.Name}»");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return agreement.Id;
    }

    public async Task UpdateAsync(long id, SalesAgreementTerms terms, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var agreement = await LoadAsync(ctx, id, ct);
        agreement.EnsureVersion(agreement.RowVersion, rowVersion);
        await ValidateAsync(ctx, terms, agreement, ct);
        var before = await DescribeAsync(ctx, agreement, ct);
        var changes = agreement.Update(terms);
        if (changes.Count == 0)
        {
            return;
        }

        Audit(ctx, AuditActions.SalesAgreementChanged, id, before, await DescribeAsync(ctx, agreement, ct),
            $"{agreement.Number}: {string.Join(", ", changes.Select(c => c.Field))}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var agreement = await LoadAsync(ctx, id, ct);
        agreement.EnsureVersion(agreement.RowVersion, rowVersion);
        if ((agreement.Status == SalesAgreementStatus.Archived) == archived)
        {
            return;
        }

        agreement.SetArchived(archived);
        Audit(ctx, AuditActions.SalesAgreementChanged, id, archived ? "действует" : "в архиве", archived ? "в архиве" : "действует", agreement.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Условия продаж покупателя: вид цены клиента, менеджер, соглашения (вкладка карточки контрагента).</summary>
    public async Task<CustomerSalesTermsDto> CustomerTermsAsync(long counterpartyId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var customer = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == counterpartyId && c.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Контрагент");
        var profile = await db.CustomerSalesProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.CounterpartyId == counterpartyId, ct);
        string? type = null, manager = null;
        if (profile?.PriceTypeId is { } pt)
        {
            type = await db.PriceTypes.AsNoTracking().Where(t => t.Id == pt).Select(t => t.Name).SingleAsync(ct);
        }

        if (profile?.ManagerUserId is { } mu)
        {
            manager = await db.Users.AsNoTracking().Where(u => u.Id == mu).Select(u => u.DisplayName).SingleAsync(ct);
        }

        var current = await SalesPricing.AgreementForAsync(db, ctx.OrganizationId, counterpartyId, await TodayAsync(ctx, ct), ct);
        var own = await db.SalesAgreements.AsNoTracking().Where(a => a.OrganizationId == ctx.OrganizationId && a.CounterpartyId == counterpartyId)
            .OrderBy(a => a.Status).ThenByDescending(a => a.ValidFrom).ToListAsync(ct);
        var all = own.Concat(current is not null && own.All(a => a.Id != current.Id) ? [current] : []).ToList();
        var rows = await RowsAsync(ctx, all, ct);
        return new CustomerSalesTermsDto(counterpartyId, customer.Name, profile?.PriceTypeId, type, profile?.ManagerUserId, manager,
            current is null ? null : rows.Single(r => r.Id == current.Id), rows.Where(r => r.CounterpartyId == counterpartyId).ToList(),
            ctx.Permissions.Has(Permissions.SalesAgreementEdit), profile?.RowVersion);
    }

    public async Task SetCustomerTermsAsync(long counterpartyId, long? priceTypeId, long? managerUserId, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var customer = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == counterpartyId && c.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Контрагент");
        var profile = await db.CustomerSalesProfiles.SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.CounterpartyId == counterpartyId, ct);
        if (profile is not null && rowVersion is not null)
        {
            profile.EnsureVersion(profile.RowVersion, rowVersion);
        }

        if (priceTypeId != profile?.PriceTypeId)
        {
            await EnsurePriceTypeAsync(ctx, priceTypeId, ct);
        }

        if (managerUserId is { } uid && uid != profile?.ManagerUserId
            && !await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == uid && m.Status == MembershipStatus.Active, ct))
        {
            throw new NotFoundException("Сотрудник");
        }

        var before = profile is null ? "—" : $"вид цены {profile.PriceTypeId?.ToString() ?? "—"}, менеджер {profile.ManagerUserId?.ToString() ?? "—"}";
        if (profile is null)
        {
            profile = CustomerSalesProfile.Create(ctx.OrganizationId, counterpartyId);
            profile.Set(priceTypeId, managerUserId);
            db.CustomerSalesProfiles.Add(profile);
        }
        else
        {
            profile.Set(priceTypeId, managerUserId);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.SalesCustomerProfileChanged, nameof(Counterparty),
            counterpartyId.ToString(), before, $"вид цены {priceTypeId?.ToString() ?? "—"}, менеджер {managerUserId?.ToString() ?? "—"}", customer.Name,
            currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Настройки продаж ролей организации: максимальная скидка, вид цены — граница, видимость заказов.</summary>
    public async Task<IReadOnlyList<SalesRoleSettingsDto>> RoleSettingsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var settings = await db.SalesRoleSettings.AsNoTracking().Where(s => s.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(s => s.RoleId, ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var roles = await db.Roles.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived).OrderByDescending(r => r.IsSystem)
            .ThenBy(r => r.Name).ToListAsync(ct);
        return roles.Select(r =>
        {
            var s = settings.GetValueOrDefault(r.Id);
            return new SalesRoleSettingsDto(r.Id, r.IsSystem ? SystemRoles.NameOf(r.Code) : r.Name, r.IsSystem, s?.MaxDiscountPercent, s?.MinPriceTypeId,
                s?.MinPriceTypeId is { } t ? types.GetValueOrDefault(t) : null, s?.Visibility ?? SalesOrderVisibility.All);
        }).ToList();
    }

    public async Task SetRoleSettingsAsync(long roleId, decimal? maxDiscountPercent, long? minPriceTypeId, SalesOrderVisibility visibility,
        CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId && r.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Роль");
        await EnsurePriceTypeAsync(ctx, minPriceTypeId, ct);
        var settings = await db.SalesRoleSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId && s.RoleId == roleId, ct);
        var before = settings is null ? "без ограничений" : Describe(settings);
        if (settings is null)
        {
            // Создать при первом сохранении: проверки — до добавления в контекст (иначе повтор вставил бы вторую запись).
            var created = SalesRoleSettings.Create(ctx.OrganizationId, roleId);
            created.Set(maxDiscountPercent, minPriceTypeId, visibility);
            db.SalesRoleSettings.Add(created);
            settings = created;
        }
        else
        {
            settings.Set(maxDiscountPercent, minPriceTypeId, visibility);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.SalesRoleSettingsChanged, nameof(Role),
            roleId.ToString(), before, Describe(settings), role.IsSystem ? SystemRoles.NameOf(role.Code) : role.Name, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);

        static string Describe(SalesRoleSettings s) =>
            $"скидка до {s.MaxDiscountPercent?.ToString("0.##") ?? "—"}%, граница — вид цены {s.MinPriceTypeId?.ToString() ?? "—"}, заказы: {SalesRoleSettings.VisibilityName(s.Visibility)}";
    }

    /// <summary>Цены позиций для покупателя на дату (подсказка формы и подбор). Нужны продажи и цены.</summary>
    public async Task<PriceQuoteSetDto> QuoteAsync(long customerId, DateOnly date, bool docIncludesVat, IReadOnlyCollection<long>? itemIds = null,
        long? agreementId = null, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        if (!await db.Counterparties.AnyAsync(c => c.Id == customerId && c.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Контрагент");
        }

        return await SalesPricing.QuoteAsync(db, ctx.OrganizationId, customerId, date, docIncludesVat, itemIds, agreementId, ct);
    }

    private async Task ValidateAsync(AccessContext ctx, SalesAgreementTerms t, SalesAgreement? current, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        if (t.CounterpartyId is { } cid && cid != current?.CounterpartyId)
        {
            var c = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid && x.OrganizationId == org, ct)
                    ?? throw new NotFoundException("Контрагент");
            if (c.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Контрагент «{c.Name}» в архиве.");
            }

            if (!c.IsCustomer)
            {
                throw new BusinessRuleException("stock.document.not_customer", $"«{c.Name}» не отмечен как покупатель.");
            }
        }

        if (t.TagId is { } tid && tid != current?.TagId && !await db.Tags.AnyAsync(x => x.Id == tid && x.OrganizationId == org && !x.IsArchived, ct))
        {
            throw new NotFoundException("Тег");
        }

        if (t.PriceTypeId != current?.PriceTypeId)
        {
            await EnsurePriceTypeAsync(ctx, t.PriceTypeId, ct);
        }

        if (t.WarehouseId is { } wid && wid != current?.WarehouseId && !await db.Warehouses.AnyAsync(w => w.Id == wid && w.OrganizationId == org && !w.IsArchived, ct))
        {
            throw new NotFoundException("Склад");
        }

        if (t.ContractId is { } kid)
        {
            var contract = await db.CounterpartyContracts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == kid && x.OrganizationId == org, ct)
                           ?? throw new NotFoundException("Договор");
            if (t.CounterpartyId is null || contract.CounterpartyId != t.CounterpartyId)
            {
                throw new BusinessRuleException("sales.agreement.contract", "Договор — только того же покупателя, для которого соглашение.");
            }
        }
    }

    private async Task EnsurePriceTypeAsync(AccessContext ctx, long? priceTypeId, CancellationToken ct)
    {
        if (priceTypeId is not { } id)
        {
            return;
        }

        var type = await db.PriceTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Вид цены");
        if (type.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Вид цены «{type.Name}» в архиве.");
        }
    }

    private async Task<IReadOnlyList<SalesAgreementRowDto>> RowsAsync(AccessContext ctx, IReadOnlyList<SalesAgreement> list, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var customerIds = list.Where(a => a.CounterpartyId is not null).Select(a => a.CounterpartyId!.Value).Distinct().ToList();
        var customers = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var tagIds = list.Where(a => a.TagId is not null).Select(a => a.TagId!.Value).Distinct().ToList();
        var tags = await db.Tags.AsNoTracking().Where(t => t.OrganizationId == org && tagIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == org).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var contractIds = list.Where(a => a.ContractId is not null).Select(a => a.ContractId!.Value).Distinct().ToList();
        var contracts = (await db.CounterpartyContracts.AsNoTracking().Where(c => c.OrganizationId == org && contractIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Number, c.ContractDate }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => $"№ {c.Number} от {c.ContractDate:dd.MM.yyyy}");
        var today = await TodayAsync(ctx, ct);
        return list.Select(a => new SalesAgreementRowDto(a.Id, a.Number, a.Name, a.Scope, a.CounterpartyId,
            a.CounterpartyId is { } c ? customers.GetValueOrDefault(c) : null, a.TagId, a.TagId is { } t ? tags.GetValueOrDefault(t) : null,
            a.PriceTypeId, a.PriceTypeId is { } p ? types.GetValueOrDefault(p) : null, a.DiscountPercent, a.ValidFrom, a.ValidTo, a.PrepaymentPercent,
            a.PaymentDays, a.CreditLimit, a.WarehouseId, a.WarehouseId is { } w ? warehouses.GetValueOrDefault(w) : null, a.ContractId,
            a.ContractId is { } k ? contracts.GetValueOrDefault(k) : null, a.Comment, a.Status, a.IsValidOn(today), a.RowVersion)).ToList();
    }

    private async Task<string> DescribeAsync(AccessContext ctx, SalesAgreement a, CancellationToken ct)
    {
        var row = (await RowsAsync(ctx, [a], ct)).Single();
        return $"{row.Target}; вид цены {row.PriceType ?? "—"}; скидка {a.DiscountPercent:0.##}%; {a.ValidFrom:dd.MM.yyyy}–{a.ValidTo?.ToString("dd.MM.yyyy") ?? "бессрочно"}; "
               + $"предоплата {a.PrepaymentPercent?.ToString("0.##") ?? "—"}%; отсрочка {a.PaymentDays?.ToString() ?? "—"} дн.; лимит {a.CreditLimit?.ToString("0.00") ?? "—"}; "
               + $"склад {row.Warehouse ?? "—"}; договор {row.Contract ?? "—"}";
    }

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private async Task<SalesAgreement> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.SalesAgreements.SingleOrDefaultAsync(a => a.Id == id && a.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Соглашение");

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesAgreementEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(SalesAgreement), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
