using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record CounterpartyDto(
    long Id, string Name, string? Inn, string? Kpp, bool IsSupplier, bool IsCustomer, string? Comment, bool IsArchived, byte[] RowVersion,
    string CountryCode = KnitErp.Domain.Organizations.Countries.Russia, string? Address = null)
{
    public string RolesText => Counterparty.RolesText(IsSupplier, IsCustomer);
    public KnitErp.Domain.Organizations.CountryInfo Country => KnitErp.Domain.Organizations.Countries.Get(CountryCode);
}

/// <summary>
/// CountryCode — страна регистрации (D60); null — страна организации при создании, прежняя при изменении.
/// Address — адрес для печатных форм; null — не менять (загрузка из Excel адреса не знает), пустая строка — стереть.
/// </summary>
public sealed record CounterpartyCommand(
    string Name, string? Inn, string? Kpp, bool IsSupplier, bool IsCustomer, string? Comment, string? CountryCode = null, string? Address = null);

public sealed record CounterpartyFilter(bool? Suppliers = null, bool? Customers = null, string? Search = null, bool IncludeArchived = false, long? TagId = null);

public sealed record CounterpartyListDto(IReadOnlyList<CounterpartyDto> Counterparties, bool CanEdit, bool CanArchive);

/// <summary>Контрагенты — справочник с правами номенклатуры (допущение D34).</summary>
public sealed class CounterpartyService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<CounterpartyListDto> ListAsync(CounterpartyFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var q = db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId);
        if (filter.TagId is { } tagId)
        {
            // D104: отбор по тегу — в запросе, чтобы находились и объекты за пределами первых строк списка.
            q = q.Where(c => db.TagLinks.Any(l => l.TagId == tagId && l.Entity == KnitErp.Domain.Tagging.TaggedEntity.Counterparty && l.EntityId == c.Id));
        }

        if (!filter.IncludeArchived)
        {
            q = q.Where(c => !c.IsArchived);
        }

        if (filter.Suppliers == true)
        {
            q = q.Where(c => c.IsSupplier);
        }

        if (filter.Customers == true)
        {
            q = q.Where(c => c.IsCustomer);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(c => c.Name.Contains(s) || (c.Inn != null && c.Inn.Contains(s)));
        }

        var rows = await q.OrderBy(c => c.Name).Take(2000)
            .Select(c => new CounterpartyDto(c.Id, c.Name, c.Inn, c.Kpp, c.IsSupplier, c.IsCustomer, c.Comment, c.IsArchived, c.RowVersion, c.CountryCode, c.Address))
            .ToListAsync(ct);
        return new CounterpartyListDto(rows, ctx.Permissions.Has(Permissions.CatalogEdit), ctx.Permissions.Has(Permissions.CatalogArchive));
    }

    public async Task<long> CreateAsync(CounterpartyCommand cmd, CancellationToken ct = default)
    {
        try
        {
            return await CreateAsyncCoreAsync(cmd, ct);
        }
        catch
        {
            db.DiscardChanges(); // аудит каталога (D347): транзакция откатилась — несохранённое не должно уйти следующим сохранением вкладки
            throw;
        }
    }

    private async Task<long> CreateAsyncCoreAsync(CounterpartyCommand cmd, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var country = cmd.CountryCode ?? await db.Organizations.Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
        var c = Counterparty.Create(ctx.OrganizationId, cmd.Name, cmd.Inn, cmd.Kpp, cmd.IsSupplier, cmd.IsCustomer, cmd.Comment, country);
        c.SetAddress(cmd.Address);
        await EnsureInnFreeAsync(ctx, c.CountryCode, c.Inn, c.Kpp, null, ct);
        db.Counterparties.Add(c);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, c.Id, null, c.Inn is null ? c.Name : $"{c.Name}, ИНН {c.Inn}", c.RolesText());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return c.Id;
    }

    public async Task UpdateAsync(long id, CounterpartyCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var c = await FindAsync(ctx, id, ct);
        c.EnsureVersion(c.RowVersion, rowVersion);
        var changes = c.Update(cmd.Name, cmd.Inn, cmd.Kpp, cmd.IsSupplier, cmd.IsCustomer, cmd.Comment, cmd.CountryCode).ToList();
        if (cmd.Address is not null && c.SetAddress(cmd.Address) is { } addressChange)
        {
            changes.Add(addressChange);
        }

        await EnsureInnFreeAsync(ctx, c.CountryCode, c.Inn, c.Kpp, id, ct);
        foreach (var change in changes)
        {
            Audit(ctx, AuditActions.CatalogChanged, id, change.Before, change.After, change.Field);
        }

        await db.SaveOrConflictAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var c = await FindAsync(ctx, id, ct);
        c.EnsureVersion(c.RowVersion, rowVersion);
        c.Archive();
        Audit(ctx, AuditActions.CatalogArchived, id, c.Name, "в архиве", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var c = await FindAsync(ctx, id, ct);
        c.EnsureVersion(c.RowVersion, rowVersion);
        await EnsureInnFreeAsync(ctx, c.CountryCode, c.Inn, c.Kpp, id, ct);
        c.Restore();
        Audit(ctx, AuditActions.CatalogRestored, id, "в архиве", c.Name, null);
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<Counterparty> FindAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Counterparties.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Контрагент");

    private async Task EnsureInnFreeAsync(AccessContext ctx, string country, string? inn, string? kpp, long? exceptId, CancellationToken ct)
    {
        if (inn is null)
        {
            return;
        }

        var existing = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.CountryCode == country && c.Inn == inn && c.Kpp == kpp && c.Id != exceptId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            throw new BusinessRuleException("catalog.counterparty.duplicate",
                $"Контрагент с ИНН {inn}{(kpp is null ? "" : $" и КПП {kpp}")} уже есть: «{existing}».");
        }
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Counterparty), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}

internal static class CounterpartyText
{
    public static string RolesText(this Counterparty c) => Counterparty.RolesText(c.IsSupplier, c.IsCustomer);
}
