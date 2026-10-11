using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>
/// Шапка карточки контрагента и какие разделы открыть пользователю. Разделы заказов и документов загружает страница
/// сервисами своих модулей (с их проверкой прав и области складов); флаги здесь — только чтобы не вызывать заведомо закрытое.
/// </summary>
public sealed record CounterpartyCardDto(
    CounterpartyDto Counterparty, bool CanEdit, bool CanEditContracts, bool CanSeePrices, bool CanSeeSales, bool CanSeePurchases,
    bool CanSeeStockDocuments);

public sealed record ContractFileDto(long Id, string FileName, int SizeBytes, DateTime UploadedAtUtc, string UploadedBy);

/// <summary>AmountLimit — null без права «Цены и суммы».</summary>
public sealed record ContractDto(
    long Id, string Number, DateOnly ContractDate, ContractKind Kind, DateOnly? ValidFrom, DateOnly? ValidTo, string? Subject,
    int? PaymentDays, decimal? AmountLimit, string? Comment, ContractStatus Status, byte[] RowVersion, IReadOnlyList<ContractFileDto> Files)
{
    public string KindName => CounterpartyContract.KindName(Kind);
    public string StatusName => CounterpartyContract.StatusName(Status);

    /// <summary>Срок вышел, а договор не закрыт — подсказка закрыть или продлить.</summary>
    public bool ExpiredOn(DateOnly today) => Status == ContractStatus.Active && ValidTo is { } to && to < today;
}

/// <summary>
/// Карточка контрагента и его договоры (D400). Просмотр — право «Справочники: просмотр», договоры меняются с правом изменять справочники,
/// лимит суммы виден только с правом «Цены и суммы». Чужая организация — «не найдено».
/// </summary>
public sealed class CounterpartyCardService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private static readonly string[] StockViewPermissions =
        [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    public async Task<CounterpartyCardDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var c = await db.Counterparties.AsNoTracking()
            .Where(x => x.Id == id && x.OrganizationId == ctx.OrganizationId)
            .Select(x => new CounterpartyDto(x.Id, x.Name, x.Inn, x.Kpp, x.IsSupplier, x.IsCustomer, x.Comment, x.IsArchived, x.RowVersion, x.CountryCode, x.Address))
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Контрагент");
        var p = ctx.Permissions;
        return new CounterpartyCardDto(c, p.Has(Permissions.CatalogEdit), p.Has(Permissions.CatalogEdit) && !c.IsArchived, p.Has(Permissions.PriceView),
            p.Has(Permissions.SalesView), p.Has(Permissions.PurchaseView), StockViewPermissions.Any(p.Has));
    }

    public async Task<IReadOnlyList<ContractDto>> ContractsAsync(long counterpartyId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        await EnsureCounterpartyAsync(ctx, counterpartyId, ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var contracts = await db.CounterpartyContracts.AsNoTracking()
            .Where(x => x.OrganizationId == ctx.OrganizationId && x.CounterpartyId == counterpartyId)
            .OrderBy(x => x.Status).ThenByDescending(x => x.ContractDate).ThenByDescending(x => x.Id)
            .ToListAsync(ct);
        var ids = contracts.Select(x => x.Id).ToList();
        var files = await db.CounterpartyContractFiles.AsNoTracking()
            .Where(f => f.OrganizationId == ctx.OrganizationId && ids.Contains(f.ContractId))
            .OrderByDescending(f => f.UploadedAtUtc)
            .Select(f => new
            {
                f.ContractId,
                Dto = new ContractFileDto(f.Id, f.FileName, f.SizeBytes, f.UploadedAtUtc,
                    db.Users.Where(u => u.Id == f.UploadedByUserId).Select(u => u.DisplayName).FirstOrDefault() ?? "—"),
            })
            .ToListAsync(ct);
        var byContract = files.ToLookup(f => f.ContractId, f => f.Dto);
        return contracts.Select(x => new ContractDto(x.Id, x.Number, x.ContractDate, x.Kind, x.ValidFrom, x.ValidTo, x.Subject, x.PaymentDays,
            prices ? x.AmountLimit : null, x.Comment, x.Status, x.RowVersion, byContract[x.Id].ToList())).ToList();
    }

    public async Task<long> AddContractAsync(long counterpartyId, ContractTerms terms, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var counterparty = await EnsureCounterpartyAsync(ctx, counterpartyId, ct);
        if (counterparty.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Контрагент в архиве. Сначала верните его из архива.");
        }

        terms = WithLimitAllowed(ctx, terms, null);
        var contract = CounterpartyContract.Create(ctx.OrganizationId, counterpartyId, terms, clock.UtcNow, ctx.UserId);
        await EnsureNumberFreeAsync(ctx, counterpartyId, contract.Number, null, ct);
        db.CounterpartyContracts.Add(contract);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ContractCreated, contract.Id, null, $"№ {contract.Number} от {contract.ContractDate:dd.MM.yyyy}", counterparty.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return contract.Id;
    }

    public async Task UpdateContractAsync(long contractId, ContractTerms terms, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var contract = await FindContractAsync(ctx, contractId, ct);
        contract.EnsureVersion(contract.RowVersion, rowVersion);
        terms = WithLimitAllowed(ctx, terms, contract.AmountLimit);
        var changes = contract.Update(terms);
        await EnsureNumberFreeAsync(ctx, contract.CounterpartyId, contract.Number, contract.Id, ct);
        foreach (var change in changes)
        {
            Audit(ctx, AuditActions.ContractChanged, contract.Id, change.Before, change.After, change.Field);
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task CloseContractAsync(long contractId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var contract = await FindContractAsync(ctx, contractId, ct);
        contract.EnsureVersion(contract.RowVersion, rowVersion);
        contract.Close();
        Audit(ctx, AuditActions.ContractChanged, contract.Id, "Действует", "Закрыт", "Статус");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task ReopenContractAsync(long contractId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var contract = await FindContractAsync(ctx, contractId, ct);
        contract.EnsureVersion(contract.RowVersion, rowVersion);
        contract.Reopen();
        Audit(ctx, AuditActions.ContractChanged, contract.Id, "Закрыт", "Действует", "Статус");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<long> AddFileAsync(long contractId, string? fileName, string? contentType, byte[] content, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var contract = await FindContractAsync(ctx, contractId, ct);
        if (contract.Status == ContractStatus.Closed)
        {
            throw new BusinessRuleException("contract.closed", "Договор закрыт. Сначала откройте его снова.");
        }

        var file = CounterpartyContractFile.Create(ctx.OrganizationId, contract.Id, fileName, contentType, content, clock.UtcNow, ctx.UserId);
        db.CounterpartyContractFiles.Add(file);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ContractFileAdded, contract.Id, null, $"{file.FileName}, {file.SizeBytes / 1024 + 1} КБ", $"№ {contract.Number}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return file.Id;
    }

    /// <summary>Скачивание скана: право просмотра справочников, своя организация. Просмотр пишется в журнал — договор может быть коммерческой тайной.</summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> GetFileAsync(long fileId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var file = await db.CounterpartyContractFiles.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == fileId && f.OrganizationId == ctx.OrganizationId, ct)
            ?? throw new NotFoundException("Файл договора");
        Audit(ctx, AuditActions.ContractFileViewed, file.ContractId, null, file.FileName, null);
        await db.SaveChangesAsync(ct);
        return (file.FileName, file.ContentType, file.Content);
    }

    /// <summary>Без права «Цены и суммы» лимит не виден и не меняется: сохраняется прежнее значение.</summary>
    private static ContractTerms WithLimitAllowed(AccessContext ctx, ContractTerms terms, decimal? current) =>
        ctx.Permissions.Has(Permissions.PriceView) ? terms : terms with { AmountLimit = current };

    private async Task<Counterparty> EnsureCounterpartyAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Контрагент");

    private async Task<CounterpartyContract> FindContractAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.CounterpartyContracts.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Договор");

    private async Task EnsureNumberFreeAsync(AccessContext ctx, long counterpartyId, string number, long? exceptId, CancellationToken ct)
    {
        var taken = await db.CounterpartyContracts.AsNoTracking()
            .AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.CounterpartyId == counterpartyId && c.Number == number && c.Id != exceptId, ct);
        if (taken)
        {
            throw new BusinessRuleException("contract.duplicate", $"Договор № {number} с этим контрагентом уже есть.");
        }
    }

    private void Audit(AccessContext ctx, string action, long contractId, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CounterpartyContract), contractId.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
