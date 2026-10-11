using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Organizations;

public sealed record LegalEntityAccountDto(
    long Id, string BankName, string Bic, string Account, string? CorrAccount, bool IsDefault, bool IsArchived, byte[] RowVersion,
    MoneyAccountKind Kind = MoneyAccountKind.Bank, decimal OpeningBalance = 0, DateOnly? OpeningDate = null, decimal? CashLimit = null)
{
    public bool IsCash => Kind == MoneyAccountKind.Cash;

    /// <summary>«Сбербанк, р/с …0001» или «Касса «Цех»» — для выбора в заказе и оплате.</summary>
    public string Label => KnitErp.Application.Finance.MoneyService.Label(Kind, BankName, Account);
}

public sealed record LegalEntityDto(
    long Id, LegalEntityKind Kind, string Name, string ShortName, string Inn, string? Kpp, string? Ogrn, string? LegalAddress,
    string? DirectorPosition, string? DirectorName, string? AccountantName, bool VatExempt, bool IsDefault, bool IsArchived, int Orders,
    IReadOnlyList<LegalEntityAccountDto> Accounts, byte[] RowVersion)
{
    public LegalEntityData Data => new(Kind, Name, ShortName, Inn, Kpp, Ogrn, LegalAddress, DirectorPosition, DirectorName, AccountantName, VatExempt);
}

public sealed record LegalEntityAccountOptionDto(long Id, string Label, bool IsDefault);

/// <summary>Действующее юрлицо и его действующие счета — для выбора в заказе.</summary>
public sealed record LegalEntityOptionDto(
    long Id, string ShortName, LegalEntityKind Kind, bool VatExempt, bool IsDefault, IReadOnlyList<LegalEntityAccountOptionDto> Accounts);

public sealed record LegalEntityAccountCommand(string? BankName, string? Bic, string? Account, string? CorrAccount);

/// <summary>
/// Свои юрлица и расчётные счета (D78), как «Юр. лица» в МойСклад: ООО и ИП одной фабрики. Смотреть — «Организация: просмотр»,
/// менять — «Организация: изменение». Список для выбора в заказе — с правом «Продажи: просмотр».
/// </summary>
public sealed class LegalEntityService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<LegalEntityDto>> ListAsync(bool includeArchived = true, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationView, ct);
        var orders = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId)
            .GroupBy(o => o.LegalEntityId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var accounts = (await db.LegalEntityAccounts.AsNoTracking().Where(a => a.OrganizationId == ctx.OrganizationId)
                .OrderBy(a => a.IsArchived).ThenByDescending(a => a.IsDefault).ThenBy(a => a.Id).ToListAsync(ct))
            .ToLookup(a => a.LegalEntityId);
        var entities = await db.LegalEntities.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && (includeArchived || !e.IsArchived))
            .OrderBy(e => e.IsArchived).ThenByDescending(e => e.IsDefault).ThenBy(e => e.ShortName).ToListAsync(ct);
        return entities.Select(e => new LegalEntityDto(e.Id, e.Kind, e.Name, e.ShortName, e.Inn, e.Kpp, e.Ogrn, e.LegalAddress, e.DirectorPosition,
                e.DirectorName, e.AccountantName, e.VatExempt, e.IsDefault, e.IsArchived, orders.GetValueOrDefault(e.Id),
                accounts[e.Id].Select(a => new LegalEntityAccountDto(a.Id, a.BankName, a.Bic, a.Account, a.CorrAccount, a.IsDefault, a.IsArchived,
                    a.RowVersion, a.Kind, a.OpeningBalance, a.OpeningDate, a.CashLimit)).ToList(), e.RowVersion))
            .ToList();
    }

    /// <summary>Действующие юрлица со счетами — для заказа; основное первым.</summary>
    public async Task<IReadOnlyList<LegalEntityOptionDto>> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        return await OptionsAsync(db, ctx.OrganizationId, ct);
    }

    internal static async Task<IReadOnlyList<LegalEntityOptionDto>> OptionsAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct)
    {
        // Счёт на оплату — только расчётный счёт, кассы сюда не попадают (D80).
        var accounts = (await db.LegalEntityAccounts.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId && !a.IsArchived && a.Kind == MoneyAccountKind.Bank)
                .OrderByDescending(a => a.IsDefault).ThenBy(a => a.Id).ToListAsync(ct))
            .ToLookup(a => a.LegalEntityId);
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == organizationId && !e.IsArchived)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName).ToListAsync(ct);
        return entities.Select(e => new LegalEntityOptionDto(e.Id, e.ShortName, e.Kind, e.VatExempt, e.IsDefault,
            accounts[e.Id].Select(a => new LegalEntityAccountOptionDto(a.Id,
                new LegalEntityAccountDto(a.Id, a.BankName, a.Bic, a.Account, a.CorrAccount, a.IsDefault, a.IsArchived, a.RowVersion).Label,
                a.IsDefault)).ToList())).ToList();
    }

    public async Task<long> CreateAsync(LegalEntityData data, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var country = await CountryAsync(ctx, ct);
        if (await db.LegalEntities.CountAsync(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived, ct) >= LegalEntity.MaxEntities)
        {
            throw new BusinessRuleException("legal_entity.too_many", $"Действующих юрлиц не больше {LegalEntity.MaxEntities}.");
        }

        var hasDefault = await db.LegalEntities.AnyAsync(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault, ct);
        var entity = LegalEntity.Create(ctx.OrganizationId, country, data, isDefault: !hasDefault);
        await EnsureUniqueAsync(ctx, entity.Inn, entity.Kpp, null, ct);
        db.LegalEntities.Add(entity);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(LegalEntity), entity.Id, null,
            $"{entity.ShortName}, ИНН {entity.Inn}{(entity.Kpp is null ? "" : ", КПП " + entity.Kpp)}", LegalEntity.KindName(entity.Kind));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return entity.Id;
    }

    public async Task UpdateAsync(long id, LegalEntityData data, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, entity) = await LoadAsync(id, rowVersion, ct);
        // Вид (юрлицо / ИП) не меняется, если по нему уже есть заказы: документы выписаны от его имени.
        if (data.Kind != entity.Kind && await db.SalesOrders.AnyAsync(o => o.OrganizationId == ctx.OrganizationId && o.LegalEntityId == id, ct))
        {
            throw new BusinessRuleException("legal_entity.kind_used", "По юрлицу уже есть заказы — вид менять нельзя. Добавьте новое юрлицо.");
        }

        var changes = entity.Update(await CountryAsync(ctx, ct), data);
        await EnsureUniqueAsync(ctx, entity.Inn, entity.Kpp, id, ct);
        foreach (var c in changes)
        {
            Audit(ctx, AuditActions.OrganizationRequisitesChanged, nameof(LegalEntity), id, c.Before, c.After, $"{entity.ShortName}: {c.Field}");
        }

        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Основное юрлицо подставляется в новый заказ. Прежнее основное перестаёт им быть.</summary>
    public async Task MakeDefaultAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, entity) = await LoadAsync(id, rowVersion, ct);
        if (entity.IsDefault)
        {
            return;
        }

        var current = await db.LegalEntities.Where(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault).ToListAsync(ct);
        foreach (var e in current)
        {
            e.SetDefault(false);
        }

        // Сначала снимается прежнее основное: уникальный индекс «одно основное» проверяется на каждой строке.
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        entity.SetDefault(true);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntity), id, current.FirstOrDefault()?.ShortName, entity.ShortName, "Основное юрлицо");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, entity) = await LoadAsync(id, rowVersion, ct);
        if (!archived)
        {
            await EnsureUniqueAsync(ctx, entity.Inn, entity.Kpp, id, ct);
        }

        entity.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(LegalEntity), id,
            archived ? entity.ShortName : "в архиве", archived ? "в архиве" : entity.ShortName, "Юрлицо");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Новый расчётный счёт юрлица; первый счёт становится основным.</summary>
    public async Task<long> AddAccountAsync(long legalEntityId, LegalEntityAccountCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await db.LegalEntities.SingleOrDefaultAsync(e => e.Id == legalEntityId && e.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Юрлицо");
        entity.EnsureActive();
        var accounts = db.LegalEntityAccounts.Where(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == legalEntityId && !a.IsArchived);
        await EnsureRoomAsync(accounts, ct);
        accounts = accounts.Where(a => a.Kind == MoneyAccountKind.Bank);
        var account = LegalEntityAccount.Create(ctx.OrganizationId, legalEntityId, await CountryAsync(ctx, ct), cmd.BankName, cmd.Bic, cmd.Account,
            cmd.CorrAccount, isDefault: !await accounts.AnyAsync(a => a.IsDefault, ct));
        await EnsureAccountFreeAsync(legalEntityId, account.Account, null, ct);
        db.LegalEntityAccounts.Add(account);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(LegalEntityAccount), account.Id, null, $"{account.BankName}, р/с {account.Account}",
            $"Расчётный счёт: {entity.ShortName}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return account.Id;
    }

    /// <summary>Новая касса юрлица (D80): наличные деньги, только название.</summary>
    public async Task<long> AddCashAsync(long legalEntityId, string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await db.LegalEntities.SingleOrDefaultAsync(e => e.Id == legalEntityId && e.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Юрлицо");
        entity.EnsureActive();
        await EnsureRoomAsync(db.LegalEntityAccounts.Where(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == legalEntityId && !a.IsArchived), ct);
        var cash = LegalEntityAccount.CreateCash(ctx.OrganizationId, legalEntityId, name);
        if (await db.LegalEntityAccounts.AnyAsync(a => a.LegalEntityId == legalEntityId && !a.IsArchived && a.Kind == MoneyAccountKind.Cash
                                                       && a.BankName == cash.BankName, ct))
        {
            throw new BusinessRuleException("money.cash_duplicate", $"Касса «{cash.BankName}» у этого юрлица уже есть.");
        }

        db.LegalEntityAccounts.Add(cash);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, nameof(LegalEntityAccount), cash.Id, null, $"Касса «{cash.BankName}»", $"Касса: {entity.ShortName}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return cash.Id;
    }

    public async Task RenameCashAsync(long id, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, cash) = await LoadAccountAsync(id, rowVersion, ct);
        var before = cash.BankName;
        cash.RenameCash(name);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntityAccount), id, before, cash.BankName, "Касса");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Начальный остаток счёта или кассы на дату (D80) — с этой даты knitERP считает движения. Влияет на остатки денег,
    /// поэтому дата не может попасть в закрытый период.
    /// </summary>
    public async Task SetOpeningAsync(long id, DateOnly? date, decimal amount, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, account) = await LoadAccountAsync(id, rowVersion, ct);
        foreach (var d in new[] { account.OpeningDate, date })
        {
            if (d is { } day)
            {
                await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, day, ct);
            }
        }

        var before = $"{account.OpeningBalance:0.00} на {account.OpeningDate:dd.MM.yyyy}";
        account.SetOpening(date, amount);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntityAccount), id, before, $"{account.OpeningBalance:0.00} на {account.OpeningDate:dd.MM.yyyy}",
            "Начальный остаток");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Лимит остатка кассы (D87); null — лимит не установлен (малое предприятие и ИП вправе не устанавливать).</summary>
    public async Task SetCashLimitAsync(long id, decimal? limit, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, account) = await LoadAccountAsync(id, rowVersion, ct);
        var before = account.CashLimit is { } b ? $"{b:0.00}" : "не установлен";
        account.SetCashLimit(limit);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntityAccount), id, before, account.CashLimit is { } a ? $"{a:0.00}" : "не установлен",
            "Лимит остатка кассы");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task UpdateAccountAsync(long id, LegalEntityAccountCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, account) = await LoadAccountAsync(id, rowVersion, ct);
        var before = $"{account.BankName}, БИК {account.Bic}, р/с {account.Account}, к/с {account.CorrAccount ?? "—"}";
        account.Set(await CountryAsync(ctx, ct), cmd.BankName, cmd.Bic, cmd.Account, cmd.CorrAccount);
        await EnsureAccountFreeAsync(account.LegalEntityId, account.Account, id, ct);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntityAccount), id, before,
            $"{account.BankName}, БИК {account.Bic}, р/с {account.Account}, к/с {account.CorrAccount ?? "—"}", "Расчётный счёт");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task MakeDefaultAccountAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, account) = await LoadAccountAsync(id, rowVersion, ct);
        if (account.IsDefault)
        {
            return;
        }

        account.EnsureCanBeDefault();
        var current = await db.LegalEntityAccounts.Where(a => a.LegalEntityId == account.LegalEntityId && a.IsDefault).ToListAsync(ct);
        foreach (var a in current)
        {
            a.SetDefault(false);
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        account.SetDefault(true);
        Audit(ctx, AuditActions.CatalogChanged, nameof(LegalEntityAccount), id, current.FirstOrDefault()?.Account, account.Account, "Основной расчётный счёт");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Счёт в архив: в выписанных счетах он остаётся, в новых заказах его не выбрать. Основным становится другой счёт, если есть.</summary>
    public async Task SetAccountArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, account) = await LoadAccountAsync(id, rowVersion, ct);
        if (!archived && !account.IsCash)
        {
            await EnsureAccountFreeAsync(account.LegalEntityId, account.Account, id, ct);
        }

        var wasDefault = account.IsDefault;
        account.SetArchived(archived);
        Audit(ctx, archived ? AuditActions.CatalogArchived : AuditActions.CatalogRestored, nameof(LegalEntityAccount), id,
            archived ? account.BankName + " " + account.Account : "в архиве", archived ? "в архиве" : account.BankName + " " + account.Account,
            account.IsCash ? "Касса" : "Расчётный счёт");
        await db.SaveOrConflictAsync(ct);
        var hasDefault = await db.LegalEntityAccounts.AnyAsync(a => a.LegalEntityId == account.LegalEntityId && a.IsDefault, ct);
        if ((wasDefault || !archived) && !hasDefault)
        {
            var next = await db.LegalEntityAccounts.Where(a => a.LegalEntityId == account.LegalEntityId && !a.IsArchived && a.Kind == MoneyAccountKind.Bank)
                .OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            next?.SetDefault(true);
            await db.SaveOrConflictAsync(ct);
        }
    }

    /// <summary>Основное юрлицо новой организации — из её реквизитов (D78).</summary>
    internal static void SeedDefault(IKnitErpDbContext db, Organization org) =>
        db.LegalEntities.Add(LegalEntity.Create(org.Id, org.CountryCode, new LegalEntityData(LegalEntityKind.Company, org.FullName, org.ShortName,
            org.Inn, org.PrintableKpp, null, org.ActualAddress, null, null, null, false), isDefault: true));

    private async Task<string> CountryAsync(AccessContext ctx, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);

    private async Task<(AccessContext, LegalEntity)> LoadAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await db.LegalEntities.SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Юрлицо");
        return (ctx, entity.EnsureVersion(entity.RowVersion, rowVersion));
    }

    private async Task<(AccessContext, LegalEntityAccount)> LoadAccountAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var account = await db.LegalEntityAccounts.SingleOrDefaultAsync(a => a.Id == id && a.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Расчётный счёт");
        return (ctx, account.EnsureVersion(account.RowVersion, rowVersion));
    }

    private async Task EnsureUniqueAsync(AccessContext ctx, string inn, string? kpp, long? exceptId, CancellationToken ct)
    {
        if (await db.LegalEntities.AnyAsync(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived && e.Inn == inn && e.Kpp == kpp
                                                && e.Id != exceptId, ct))
        {
            throw new BusinessRuleException("legal_entity.duplicate", $"Юрлицо с ИНН {inn}{(kpp is null ? "" : " и КПП " + kpp)} уже есть.");
        }
    }

    private static async Task EnsureRoomAsync(IQueryable<LegalEntityAccount> active, CancellationToken ct)
    {
        if (await active.CountAsync(ct) >= LegalEntityAccount.MaxAccounts)
        {
            throw new BusinessRuleException("legal_entity.accounts_too_many", $"Действующих счетов и касс у юрлица не больше {LegalEntityAccount.MaxAccounts}.");
        }
    }

    private async Task EnsureAccountFreeAsync(long legalEntityId, string account, long? exceptId, CancellationToken ct)
    {
        if (await db.LegalEntityAccounts.AnyAsync(a => a.LegalEntityId == legalEntityId && !a.IsArchived && a.Kind == MoneyAccountKind.Bank && a.Account == account
                                                    && a.Id != exceptId, ct))
        {
            throw new BusinessRuleException("legal_entity.account_duplicate", $"Счёт {account} у этого юрлица уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
