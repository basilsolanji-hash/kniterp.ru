using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

public sealed record TechCardRowDto(
    long Id, long ItemId, string ItemCode, string ItemName, int Version, TechCardStatus Status, decimal OutputQuantity, string UnitSymbol,
    int LineCount, string CreatedBy, DateTime? ActivatedAtUtc)
{
    public string StatusName => TechCard.StatusName(Status);
}

public sealed record TechCardLineDto(long ItemId, string Code, string Name, string UnitSymbol, decimal Quantity, decimal WastePercent)
{
    /// <summary>Норма с отходом на партию.</summary>
    public decimal Gross => Quantity * (1 + WastePercent / 100m);
}

public sealed record TechCardDto(
    long Id, long ItemId, string ItemCode, string ItemName, string UnitSymbol, int Version, TechCardStatus Status, decimal OutputQuantity,
    string? Comment, string CreatedBy, DateTime CreatedAtUtc, string? ActivatedBy, DateTime? ActivatedAtUtc,
    IReadOnlyList<TechCardLineDto> Lines, long? ActiveVersionId, bool CanEdit, byte[] RowVersion, IReadOnlyList<ProductionStage>? Route = null)
{
    public string StatusName => TechCard.StatusName(Status);
}

/// <summary>Потребность в материале на выпуск; Available — остаток на выбранном складе или по всем видимым (null — нет права на отчёты).</summary>
public sealed record RequirementRowDto(string Code, string Name, string UnitSymbol, decimal Net, decimal Gross, decimal? Available)
{
    public decimal? Shortage => Available is { } a && a < Gross ? Gross - a : null;
}

public sealed record ProductOptionDto(long Id, string Code, string Name, string UnitSymbol, byte Precision, ItemType Type);

/// <summary>
/// Технологические карты изделий (D63): нормы расхода материалов на партию изделия, версии и ввод в действие.
/// Права — как у справочников: смотреть — «Справочники: просмотр», править — «Справочники: изменение».
/// </summary>
public sealed class TechCardService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Что может быть изделием карты: готовая продукция и полуфабрикаты.</summary>
    public static readonly IReadOnlyList<ItemType> ProductTypes = [ItemType.Finished, ItemType.SemiFinished];

    public async Task<IReadOnlyList<TechCardRowDto>> ListAsync(string? search = null, bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var q = from c in db.TechCards.AsNoTracking()
                where c.OrganizationId == ctx.OrganizationId && (includeArchived || c.Status != TechCardStatus.Archived)
                join i in db.Items.AsNoTracking() on c.ItemId equals i.Id
                join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                join a in db.Users.AsNoTracking() on c.CreatedByUserId equals a.Id
                select new { c, i.Code, i.Name, u.Symbol, Author = a.DisplayName };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            q = q.Where(x => x.Code.Contains(text) || x.Name.Contains(text));
        }

        return await q.OrderBy(x => x.Name).ThenByDescending(x => x.c.Version).Take(2000)
            .Select(x => new TechCardRowDto(x.c.Id, x.c.ItemId, x.Code, x.Name, x.c.Version, x.c.Status, x.c.OutputQuantity, x.Symbol,
                x.c.Lines.Count, x.Author, x.c.ActivatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<TechCardDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var card = await db.TechCards.AsNoTracking().Include(c => c.Lines)
                       .SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Техкарта");
        var itemIds = card.Lines.Select(l => l.ItemId).Append(card.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol })
            .ToDictionaryAsync(x => x.Id, ct);
        var userIds = new[] { card.CreatedByUserId, card.ActivatedByUserId ?? 0 };
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var activeId = await db.TechCards.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.ItemId == card.ItemId && c.Status == TechCardStatus.Active)
            .Select(c => (long?)c.Id).SingleOrDefaultAsync(ct);
        var product = items[card.ItemId];
        return new TechCardDto(card.Id, card.ItemId, product.Code, product.Name, product.Symbol, card.Version, card.Status, card.OutputQuantity,
            card.Comment, users.GetValueOrDefault(card.CreatedByUserId) ?? "—", card.CreatedAtUtc,
            card.ActivatedByUserId is { } by ? users.GetValueOrDefault(by) : null, card.ActivatedAtUtc,
            card.Lines.Select(l => items[l.ItemId]).Zip(card.Lines)
                .Select(x => new TechCardLineDto(x.Second.ItemId, x.First.Code, x.First.Name, x.First.Symbol, x.Second.Quantity, x.Second.WastePercent))
                .OrderBy(l => l.Code).ToList(),
            activeId, ctx.Permissions.Has(Permissions.CatalogEdit) && card.Status == TechCardStatus.Draft, card.RowVersion, card.RouteValue.Stages);
    }

    /// <summary>Изделия для новой карты: действующая готовая продукция и полуфабрикаты.</summary>
    public async Task<IReadOnlyList<ProductOptionDto>> ProductsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        return await Options(ctx, productsOnly: true).ToListAsync(ct);
    }

    /// <summary>Материалы для строк: любая действующая позиция.</summary>
    public async Task<IReadOnlyList<ProductOptionDto>> MaterialsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        return await Options(ctx).ToListAsync(ct);
    }

    /// <summary>Новая карта-черновик изделия; версия — следующая после последней.</summary>
    public async Task<long> CreateAsync(long productItemId, decimal outputQuantity, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var product = await Options(ctx, id: productItemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Позиция");
        if (!ProductTypes.Contains(product.Type))
        {
            throw new BusinessRuleException("techcard.product_type", "Техкарта составляется на готовую продукцию или полуфабрикат.");
        }

        EnsurePrecision(product, outputQuantity);
        var card = TechCard.Create(ctx.OrganizationId, productItemId, await NextVersionAsync(ctx, productItemId, ct), outputQuantity, comment,
            ctx.UserId, clock.UtcNow);
        db.TechCards.Add(card);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, card.Id, null, $"{product.Code} {product.Name}, версия {card.Version}", "Техкарта");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return card.Id;
    }

    /// <summary>Новая версия-черновик копией карты — так меняют действующие нормы.</summary>
    public async Task<long> CopyAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var source = await FindAsync(ctx, id, ct);
        var copy = source.CopyAsVersion(await NextVersionAsync(ctx, source.ItemId, ct), ctx.UserId, clock.UtcNow);
        db.TechCards.Add(copy);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveOrConflictAsync(ct);
        Audit(ctx, AuditActions.CatalogCreated, copy.Id, null, $"Версия {copy.Version} копией версии {source.Version}", "Техкарта");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return copy.Id;
    }

    public async Task UpdateHeaderAsync(long id, decimal outputQuantity, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, card) = await LoadForEditAsync(id, rowVersion, ct);
        var product = await Options(ctx, includeArchived: true, id: card.ItemId).SingleAsync(ct);
        EnsurePrecision(product, outputQuantity);
        var before = $"на {card.OutputQuantity:0.######} {product.UnitSymbol}";
        card.SetHeader(outputQuantity, comment);
        Audit(ctx, AuditActions.CatalogChanged, id, before, $"на {card.OutputQuantity:0.######} {product.UnitSymbol}", "Техкарта: партия");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Норма материала на партию: добавить или изменить.</summary>
    public async Task SetLineAsync(long id, long materialItemId, decimal quantity, decimal wastePercent, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, card) = await LoadForEditAsync(id, rowVersion, ct);
        var material = await Options(ctx, id: materialItemId).SingleOrDefaultAsync(ct)
                       ?? throw new BusinessRuleException("techcard.material_missing", "Материал не найден или в архиве.");
        // Многоуровневые карты (D90, дополнение): полуфабрикат не может содержать само изделие ни на каком уровне.
        var graph = (await db.TechCards.AsNoTracking()
                .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status != TechCardStatus.Archived)
                .SelectMany(c => c.Lines.Select(l => new { c.ItemId, Material = l.ItemId })).ToListAsync(ct))
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<long>)g.Select(x => x.Material).Distinct().ToList());
        if (BillOfMaterials.Contains(materialItemId, card.ItemId, id => graph.GetValueOrDefault(id) ?? []))
        {
            throw new BusinessRuleException("techcard.cycle", $"«{material.Name}» сам содержит это изделие в своём составе — получился бы замкнутый круг.");
        }

        var before = card.Lines.FirstOrDefault(l => l.ItemId == materialItemId) is { } old ? Norm(old.Quantity, old.WastePercent, material) : null;
        card.SetLine(materialItemId, quantity, wastePercent);
        Audit(ctx, AuditActions.CatalogChanged, id, before, Norm(quantity, wastePercent, material), $"Техкарта: {material.Code} {material.Name}");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Маршрут изделия (D401): этапы из стандартного набора, вязание и контроль качества обязательны; только у черновика.</summary>
    public async Task SetRouteAsync(long id, IReadOnlyCollection<ProductionStage> stages, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, card) = await LoadForEditAsync(id, rowVersion, ct);
        var before = card.RouteValue.Describe();
        card.SetRoute(ProductionRoute.Of(stages));
        Audit(ctx, AuditActions.CatalogChanged, id, before, card.RouteValue.Describe(), "Техкарта: маршрут");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveLineAsync(long id, long materialItemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, card) = await LoadForEditAsync(id, rowVersion, ct);
        card.RemoveLine(materialItemId);
        Audit(ctx, AuditActions.CatalogChanged, id, $"материал №{materialItemId}", "удалён", "Техкарта");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Ввод в действие; прежняя действующая версия изделия уходит в архив в той же транзакции.</summary>
    public async Task ActivateAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, card) = await LoadForEditAsync(id, rowVersion, ct);
        var product = await db.Items.AsNoTracking().SingleAsync(i => i.Id == card.ItemId, ct);
        if (product.IsArchived)
        {
            throw new BusinessRuleException("techcard.product_archived", $"Изделие «{product.Name}» в архиве.");
        }

        var materialIds = card.Lines.Select(l => l.ItemId).ToList();
        if (await db.Items.AnyAsync(i => materialIds.Contains(i.Id) && i.IsArchived, ct))
        {
            throw new BusinessRuleException("techcard.material_archived", "В карте есть материал из архива — замените его.");
        }

        var previous = await db.TechCards
            .SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.ItemId == card.ItemId && c.Status == TechCardStatus.Active, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (previous is not null)
        {
            previous.Archive();
            Audit(ctx, AuditActions.CatalogArchived, previous.Id, $"версия {previous.Version} действует", "в архиве",
                $"Техкарта: заменена версией {card.Version}");
            // Сначала архив прежней — иначе уникальный индекс «одна действующая на изделие» не даст сохранить новую.
            await db.SaveOrConflictAsync(ct);
        }

        card.Activate(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.TechCardActivated, id, "черновик", $"версия {card.Version} действует", $"{product.Code} {product.Name}");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var card = await FindAsync(ctx, id, ct);
        card.EnsureVersion(card.RowVersion, rowVersion);
        var before = TechCard.StatusName(card.Status);
        card.Archive();
        Audit(ctx, AuditActions.CatalogArchived, id, before, "в архиве", "Техкарта");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Сколько материалов нужно на выпуск <paramref name="quantity"/> изделий и хватает ли их на складе.
    /// Остаток показывается только тем, кому доступны отчёты склада, и в пределах их складов.
    /// </summary>
    /// <param name="explode">
    /// По уровням (D90, дополнение): полуфабрикаты с действующей техкартой раскрываются до материалов их карт — потребность в пряже
    /// на изделие через все уровни; без раскрытия полуфабрикат — обычная строка (его берут со склада).
    /// </param>
    public async Task<IReadOnlyList<RequirementRowDto>> RequirementAsync(
        long id, decimal quantity, long? warehouseId = null, bool explode = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var card = await db.TechCards.AsNoTracking().Include(c => c.Lines)
                       .SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Техкарта");
        IReadOnlyList<(long ItemId, decimal Net, decimal Gross)> need;
        List<long> ids;
        if (explode)
        {
            var active = await db.TechCards.AsNoTracking().Include(c => c.Lines)
                .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status == TechCardStatus.Active && c.ItemId != card.ItemId)
                .ToDictionaryAsync(c => c.ItemId, ct);
            var precisions = await Options(ctx, includeArchived: true).ToDictionaryAsync(o => o.Id, o => (int)o.Precision, ct);
            var totals = BillOfMaterials.Explode(card, quantity, active, itemId => precisions.GetValueOrDefault(itemId, TechCard.NormPrecision));
            need = totals.Select(t => (t.Key, t.Value, t.Value)).ToList();
            ids = totals.Keys.ToList();
        }
        else
        {
            ids = card.Lines.Select(l => l.ItemId).ToList();
            var precisions = await Options(ctx, includeArchived: true, ids: ids).ToDictionaryAsync(o => o.Id, o => (int)o.Precision, ct);
            need = card.Requirement(quantity, itemId => precisions[itemId]);
        }

        var materials = await Options(ctx, includeArchived: true, ids: ids).ToDictionaryAsync(o => o.Id, ct);

        Dictionary<long, decimal>? stock = null;
        if (ctx.Permissions.Has(Permissions.WarehouseReportView))
        {
            var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseReportView);
            var moves = db.StockMovements.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && ids.Contains(m.ItemId) && (visible == null || visible.Contains(m.WarehouseId)));
            if (warehouseId is { } wh)
            {
                moves = moves.Where(m => m.WarehouseId == wh);
            }

            stock = await moves.GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        }

        return need.Select(n =>
        {
            var m = materials[n.ItemId];
            return new RequirementRowDto(m.Code, m.Name, m.UnitSymbol, n.Net, n.Gross, stock is null ? null : stock.GetValueOrDefault(n.ItemId));
        }).OrderBy(r => r.Code).ToList();
    }

    /// <summary>Позиции для выбора; фильтры — до проекции, чтобы запрос переводился в SQL.</summary>
    private IQueryable<ProductOptionDto> Options(
        AccessContext ctx, bool includeArchived = false, long? id = null, IReadOnlyCollection<long>? ids = null, bool productsOnly = false)
    {
        var q = db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId);
        if (!includeArchived)
        {
            q = q.Where(i => !i.IsArchived);
        }

        if (id is { } one)
        {
            q = q.Where(i => i.Id == one);
        }

        if (ids is not null)
        {
            q = q.Where(i => ids.Contains(i.Id));
        }

        if (productsOnly)
        {
            q = q.Where(i => i.Type == ItemType.Finished || i.Type == ItemType.SemiFinished);
        }

        return q.OrderBy(i => i.Code)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new ProductOptionDto(i.Id, i.Code, i.Name, u.Symbol, u.Precision, i.Type));
    }

    private async Task<int> NextVersionAsync(AccessContext ctx, long itemId, CancellationToken ct) =>
        (await db.TechCards.Where(c => c.OrganizationId == ctx.OrganizationId && c.ItemId == itemId).MaxAsync(c => (int?)c.Version, ct) ?? 0) + 1;

    private async Task<TechCard> FindAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.TechCards.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Техкарта");

    private async Task<(AccessContext, TechCard)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var card = await FindAsync(ctx, id, ct);
        card.EnsureVersion(card.RowVersion, rowVersion);
        return (ctx, card);
    }

    private static void EnsurePrecision(ProductOptionDto product, decimal quantity) =>
        StockEntry.EnsurePrecision(new EntryItemDto(product.Id, product.Code, product.Name, product.UnitSymbol, product.Precision), quantity);

    private static string Norm(decimal quantity, decimal waste, ProductOptionDto m) =>
        waste == 0 ? $"{quantity:0.######} {m.UnitSymbol}" : $"{quantity:0.######} {m.UnitSymbol} + {waste:0.##}% отхода";

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(TechCard), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
