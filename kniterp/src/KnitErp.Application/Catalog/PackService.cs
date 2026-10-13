using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record ItemPackDto(long Id, long ItemId, long UnitId, string UnitName, string UnitSymbol, decimal Factor, string? Barcode, byte[] RowVersion);

/// <summary>Результат сканирования: позиция и сколько основных единиц даёт один скан (1 — штрихкод позиции, иначе — упаковки).</summary>
public sealed record ScanResultDto(long ItemId, string Code, string Name, decimal Quantity, string? PackUnit);

/// <summary>
/// Упаковки позиции (D307): смотреть — «Справочники: просмотр», менять — «Справочники: изменение». Штрихкод упаковки уникален
/// вместе со штрихкодами позиций — сканер находит одно. Пересчёт количества — <see cref="Packs.ToBase"/>.
/// </summary>
public sealed class PackService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<ItemPackDto>> ListAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        await FindItemAsync(ctx, itemId, ct);
        return await ListInternalAsync(db, ctx.OrganizationId, [itemId], ct);
    }

    /// <summary>
    /// D353 (сессия 4, Р4): упаковки всех позиций организации — один запрос при открытии документа, чтобы ввод «N упаковок» и скан
    /// штрихкода упаковки в строках работали без запросов на каждую позицию. Без права на справочники — пусто (ввод в основных единицах).
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<ItemPackDto>>> AllAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.CatalogView))
        {
            return new Dictionary<long, IReadOnlyList<ItemPackDto>>();
        }

        var packs = await (from p in db.ItemPacks.AsNoTracking()
                           join u in db.Units.AsNoTracking() on p.UnitId equals u.Id
                           where p.OrganizationId == ctx.OrganizationId
                           orderby p.ItemId, p.Factor
                           select new ItemPackDto(p.Id, p.ItemId, p.UnitId, u.Name, u.Symbol, p.Factor, p.Barcode, p.RowVersion)).ToListAsync(ct);
        return packs.GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => (IReadOnlyList<ItemPackDto>)g.ToList());
    }

    internal static async Task<List<ItemPackDto>> ListInternalAsync(IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long> itemIds,
        CancellationToken ct) =>
        await (from p in db.ItemPacks.AsNoTracking()
               join u in db.Units.AsNoTracking() on p.UnitId equals u.Id
               where p.OrganizationId == organizationId && itemIds.Contains(p.ItemId)
               orderby p.Factor
               select new ItemPackDto(p.Id, p.ItemId, p.UnitId, u.Name, u.Symbol, p.Factor, p.Barcode, p.RowVersion)).ToListAsync(ct);

    /// <summary>Добавить упаковку или изменить коэффициент и штрихкод упаковки той же единицы.</summary>
    public async Task<long> SetAsync(long itemId, long unitId, decimal factor, string? barcode, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var item = await FindItemAsync(ctx, itemId, ct);
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Позиция в архиве. Сначала верните её из архива.");
        }

        var unit = await db.Units.AsNoTracking().SingleOrDefaultAsync(u => u.Id == unitId && u.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Единица измерения");
        if (unit.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Единица «{unit.Name}» в архиве.");
        }

        var precision = await db.Units.AsNoTracking().Where(u => u.Id == item.UnitId).Select(u => u.Precision).SingleAsync(ct);
        var packs = await db.ItemPacks.Where(p => p.OrganizationId == ctx.OrganizationId && p.ItemId == itemId).ToListAsync(ct);
        var pack = packs.FirstOrDefault(p => p.UnitId == unitId);
        // Все проверки — до изменения сущностей: у вкладки Blazor один DbContext, отклонённая правка не должна остаться в нём.
        Packs.CheckFactor(factor, precision);
        if (Packs.NormalizeBarcode(barcode) is { } normalized)
        {
            await EnsureBarcodeFreeAsync(db, ctx.OrganizationId, normalized, pack?.Id, ct);
        }

        string? before = pack is null ? null : $"{unit.Symbol} = {Quantities.Format(pack.Factor)}{(pack.Barcode is { } b ? $", {b}" : "")}";
        if (pack is null)
        {
            if (packs.Count >= ItemPack.MaxPerItem)
            {
                throw new BusinessRuleException("catalog.pack.too_many", $"У позиции не больше {ItemPack.MaxPerItem} упаковок.");
            }

            pack = ItemPack.Create(ctx.OrganizationId, item, unitId, factor, precision, barcode);
            db.ItemPacks.Add(pack);
        }
        else
        {
            pack.Set(factor, precision, barcode);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), itemId.ToString(),
            before, $"{unit.Symbol} = {Quantities.Format(pack.Factor)}{(pack.Barcode is { } bc ? $", {bc}" : "")}", "Упаковка", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        return pack.Id;
    }

    public async Task RemoveAsync(long packId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var pack = await db.ItemPacks.SingleOrDefaultAsync(p => p.Id == packId && p.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Упаковка");
        pack.EnsureVersion(pack.RowVersion, rowVersion);
        var symbol = await db.Units.AsNoTracking().Where(u => u.Id == pack.UnitId).Select(u => u.Symbol).SingleAsync(ct);
        db.ItemPacks.Remove(pack);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), pack.ItemId.ToString(),
            $"{symbol} = {Quantities.Format(pack.Factor)}", "убрана", "Упаковка", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Скан: штрихкод позиции — 1 основная единица, штрихкод упаковки — коэффициент упаковки; null — не найдено.</summary>
    public async Task<ScanResultDto?> ScanAsync(string? code, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var c = (code ?? string.Empty).Trim();
        if (c.Length == 0)
        {
            return null;
        }

        var item = await (from b in db.ItemBarcodes.AsNoTracking()
                          join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                          where b.OrganizationId == ctx.OrganizationId && b.Code == c
                          select new ScanResultDto(i.Id, i.Code, i.Name, 1m, null)).FirstOrDefaultAsync(ct);
        return item ?? await (from p in db.ItemPacks.AsNoTracking()
                              join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                              join u in db.Units.AsNoTracking() on p.UnitId equals u.Id
                              where p.OrganizationId == ctx.OrganizationId && p.Barcode == c
                              select new ScanResultDto(i.Id, i.Code, i.Name, p.Factor, u.Symbol)).FirstOrDefaultAsync(ct);
    }

    /// <summary>Штрихкод не занят ни позицией, ни другой упаковкой.</summary>
    internal static async Task EnsureBarcodeFreeAsync(IKnitErpDbContext db, long organizationId, string code, long? exceptPackId, CancellationToken ct)
    {
        var owner = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == organizationId && b.Code == code)
                        .Join(db.Items.AsNoTracking(), b => b.ItemId, i => i.Id, (b, i) => i.Code).FirstOrDefaultAsync(ct)
                    ?? await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.Barcode == code && p.Id != exceptPackId)
                        .Join(db.Items.AsNoTracking(), p => p.ItemId, i => i.Id, (p, i) => i.Code).FirstOrDefaultAsync(ct);
        if (owner is not null)
        {
            throw new BusinessRuleException("catalog.barcode.duplicate", $"Штрихкод {code} уже у позиции {owner}.");
        }
    }

    private async Task<Item> FindItemAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Номенклатура");
}
