using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record VatPeriodDto(DateOnly ValidFrom, DateOnly? ValidTo, decimal Percent);

/// <summary>CurrentPercent — процент на сегодня по времени организации; null — «Без НДС» или ещё не действует.</summary>
public sealed record VatRateDto(
    long Id, string Name, VatRateKind Kind, decimal? CurrentPercent, IReadOnlyList<VatPeriodDto> Periods, int ItemCount, bool IsArchived,
    byte[] RowVersion)
{
    public string KindName => VatRate.KindName(Kind);
    public string CurrentText => Kind == VatRateKind.Exempt ? "без НДС" : CurrentPercent is { } p ? $"{p:0.##}%" : "ещё не действует";
}

public sealed record VatRateListDto(IReadOnlyList<VatRateDto> Rates, string CountryName, bool CanEdit);

/// <summary>
/// Справочник ставок НДС организации (D60). Виды ставок с историей процентов по датам; новая организация получает ставки
/// своей страны. Права — как у справочников (D34).
/// </summary>
public sealed class VatRateService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<VatRateListDto> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, org.TimeZoneId));
        var rates = await db.VatRates.AsNoTracking().Include(r => r.Periods)
            .Where(r => r.OrganizationId == ctx.OrganizationId && (includeArchived || !r.IsArchived))
            .ToListAsync(ct);
        var usage = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.VatRateId != null && !i.IsArchived)
            .GroupBy(i => i.VatRateId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var rows = rates.OrderBy(r => r.Kind).ThenBy(r => r.Name).Select(r =>
        {
            var periods = r.Periods.OrderBy(p => p.ValidFrom).ToList();
            return new VatRateDto(r.Id, r.Name, r.Kind, r.PercentOn(today),
                periods.Select((p, i) => new VatPeriodDto(p.ValidFrom, i + 1 < periods.Count ? periods[i + 1].ValidFrom.AddDays(-1) : null, p.Percent)).ToList(),
                usage.GetValueOrDefault(r.Id), r.IsArchived, r.RowVersion);
        }).ToList();
        return new VatRateListDto(rows, KnitErp.Domain.Organizations.Countries.Get(org.CountryCode).Name, ctx.Permissions.Has(Permissions.CatalogEdit));
    }

    /// <summary>Новый вид ставки. Процент с даты обязателен, кроме «Без НДС».</summary>
    public async Task<long> CreateAsync(string name, VatRateKind kind, DateOnly? validFrom, decimal? percent, CancellationToken ct = default)
    {
        try
        {
            return await CreateAsyncCoreAsync(name, kind, validFrom, percent, ct);
        }
        catch
        {
            db.DiscardChanges(); // аудит каталога (D347): транзакция откатилась — несохранённое не должно уйти следующим сохранением вкладки
            throw;
        }
    }

    private async Task<long> CreateAsyncCoreAsync(string name, VatRateKind kind, DateOnly? validFrom, decimal? percent, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var rate = VatRate.Create(ctx.OrganizationId, name, kind);
        if (kind != VatRateKind.Exempt)
        {
            if (validFrom is not { } from || percent is not { } value)
            {
                throw new BusinessRuleException("vat.period_required", "Укажите процент и дату, с которой он действует.");
            }

            rate.AddPeriod(from, value);
        }

        await EnsureNameFreeAsync(ctx, rate.Name, null, ct);
        db.VatRates.Add(rate);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, rate.Id, null, Describe(rate), VatRate.KindName(kind));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rate.Id;
    }

    /// <summary>Новый процент с даты — например, когда закон меняет ставку со следующего года.</summary>
    public async Task AddPeriodAsync(long id, DateOnly validFrom, decimal percent, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var rate = await FindAsync(ctx, id, ct);
        rate.EnsureVersion(rate.RowVersion, rowVersion);
        var before = Describe(rate);
        rate.AddPeriod(validFrom, percent);
        Audit(ctx, AuditActions.CatalogChanged, id, before, Describe(rate), $"Ставка НДС: {percent:0.##}% с {validFrom:dd.MM.yyyy}");
        // Два одинаковых периода не сохранятся и при одновременной правке: уникальный индекс (вид, дата начала).
        await db.SaveOrConflictAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var rate = await FindAsync(ctx, id, ct);
        rate.EnsureVersion(rate.RowVersion, rowVersion);
        if (await db.Items.AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.VatRateId == id && !i.IsArchived, ct))
        {
            throw new BusinessRuleException("vat.in_use", $"Ставка «{rate.Name}» указана у действующей номенклатуры. Сначала смените ставку у позиций.");
        }

        rate.Archive();
        Audit(ctx, AuditActions.CatalogArchived, id, rate.Name, "в архиве", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogArchive, ct);
        var rate = await FindAsync(ctx, id, ct);
        rate.EnsureVersion(rate.RowVersion, rowVersion);
        await EnsureNameFreeAsync(ctx, rate.Name, id, ct);
        rate.Restore();
        Audit(ctx, AuditActions.CatalogRestored, id, "в архиве", rate.Name, null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Действующие ставки для выбора в карточке номенклатуры.</summary>
    public async Task<IReadOnlyList<VatRateDto>> ActiveAsync(CancellationToken ct = default) =>
        (await ListAsync(false, ct)).Rates;

    private async Task<VatRate> FindAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.VatRates.Include(r => r.Periods).SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Ставка НДС");

    private async Task EnsureNameFreeAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.VatRates.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Name == name && r.Id != exceptId, ct))
        {
            throw new BusinessRuleException("vat.name_taken", $"Ставка «{name}» уже есть.");
        }
    }

    private static string Describe(VatRate r) =>
        r.Kind == VatRateKind.Exempt
            ? $"{r.Name}: без НДС"
            : $"{r.Name}: " + string.Join(", ", r.Periods.OrderBy(p => p.ValidFrom).Select(p => $"{p.Percent:0.##}% с {p.ValidFrom:dd.MM.yyyy}"));

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(VatRate), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
