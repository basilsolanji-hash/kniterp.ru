using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public sealed record LabelTemplateDto(
    long Id, string Name, decimal WidthMm, decimal HeightMm, LabelSheet Sheet, byte Columns, byte Rows, decimal PaddingMm, long? PriceTypeId,
    IReadOnlyList<LabelElement> Elements, bool IsDefault, bool IsArchived, byte[] RowVersion);

public sealed record LabelTemplateCommand(
    string? Name, decimal WidthMm, decimal HeightMm, LabelSheet Sheet, byte Columns, byte Rows, decimal PaddingMm, long? PriceTypeId,
    IReadOnlyList<LabelElement> Elements);

/// <summary>Данные позиции для этикетки. Price — только с правом «Цены и суммы»; Barcode — первый EAN-13, иначе первый штрихкод, иначе код.</summary>
public sealed record LabelItemDto(
    long Id, string Name, string Code, string? Article, decimal? Price, string? Barcode, string Unit, string? Characteristics, string? Country,
    string? Description, string? Group);

public sealed record LabelPrintDto(LabelTemplateDto Template, string Organization, string Currency, IReadOnlyList<LabelItemDto> Items, DateOnly Date);

/// <summary>
/// Этикетки (D316): шаблоны организации — смотреть и печатать с правом «Справочники: просмотр», менять — «Справочники: изменение»;
/// цена на этикетке — только с правом «Цены и суммы». Печать: до 1000 позиций и до 5000 этикеток за раз.
/// </summary>
public sealed class LabelService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxItems = 1000;
    public const int MaxLabels = 5000;

    public async Task<IReadOnlyList<LabelTemplateDto>> TemplatesAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var rows = await db.LabelTemplates.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && (includeArchived || !t.IsArchived))
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<long> CreateAsync(LabelTemplateCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await RequirePriceTypeAsync(ctx, cmd.PriceTypeId, ct);
        if (await db.LabelTemplates.CountAsync(t => t.OrganizationId == ctx.OrganizationId, ct) >= LabelTemplate.MaxPerOrganization)
        {
            throw new BusinessRuleException("label.limit", "Не больше 30 шаблонов этикеток — отправьте ненужные в архив и измените их.");
        }

        var t = LabelTemplate.Create(ctx.OrganizationId, cmd.Name, cmd.WidthMm, cmd.HeightMm, cmd.Sheet, cmd.Columns, cmd.Rows, cmd.PaddingMm, cmd.PriceTypeId,
            cmd.Elements, clock.UtcNow);
        // Первый шаблон организации — основной.
        t.SetDefault(!await db.LabelTemplates.AnyAsync(x => x.OrganizationId == ctx.OrganizationId && !x.IsArchived, ct));
        db.LabelTemplates.Add(t);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, t.Id, null, $"{t.Name}, {Size(t)}", "Шаблон этикетки создан");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return t.Id;
    }

    /// <summary>Шаблон из образца: «Ценник 58×40», «Лист A4 3×8» и т. д.</summary>
    public Task<long> CreateFromPresetAsync(string key, CancellationToken ct = default)
    {
        var p = LabelTemplate.Presets.SingleOrDefault(x => x.Key == key) ?? throw new NotFoundException("Образец этикетки");
        return CreateAsync(new LabelTemplateCommand(p.Name, p.WidthMm, p.HeightMm, p.Sheet, p.Columns, p.Rows, 2, null, p.Elements), ct);
    }

    public async Task UpdateAsync(long id, LabelTemplateCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var t = await FindAsync(ctx, id, ct);
        t.EnsureVersion(t.RowVersion, rowVersion);
        await RequirePriceTypeAsync(ctx, cmd.PriceTypeId, ct);
        var before = $"{t.Name}, {Size(t)}";
        try
        {
            t.Set(cmd.Name, cmd.WidthMm, cmd.HeightMm, cmd.Sheet, cmd.Columns, cmd.Rows, cmd.PaddingMm, cmd.PriceTypeId, cmd.Elements, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, t.Id, before, $"{t.Name}, {Size(t)}", "Шаблон этикетки изменён");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetDefaultAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var t = await FindAsync(ctx, id, ct);
        if (t.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Шаблон в архиве.");
        }

        // Сначала снять отметку у прежнего, потом поставить: фильтрованный уникальный индекс не допускает двух основных даже внутри одной записи.
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var other in await db.LabelTemplates.Where(x => x.OrganizationId == ctx.OrganizationId && x.IsDefault && x.Id != id).ToListAsync(ct))
        {
            other.SetDefault(false);
        }

        await db.SaveOrConflictAsync(ct);
        t.SetDefault(true);
        Audit(ctx, t.Id, null, t.Name, "Основной шаблон этикетки");
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var t = await FindAsync(ctx, id, ct);
        t.EnsureVersion(t.RowVersion, rowVersion);
        t.SetArchived(archived);
        Audit(ctx, t.Id, null, t.Name, archived ? "Шаблон этикетки в архиве" : "Шаблон этикетки снова действует");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Данные для печати: шаблон (null — основной, иначе первый) и позиции в порядке ids.</summary>
    public async Task<LabelPrintDto> PrintAsync(IReadOnlyList<long> itemIds, long? templateId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var org = ctx.OrganizationId;
        var ids = itemIds.Distinct().Take(MaxItems + 1).ToList();
        if (ids.Count > MaxItems)
        {
            throw new BusinessRuleException("label.too_many", "За раз — этикетки не больше чем для 1000 позиций.");
        }

        var template = templateId is { } tid
            ? await db.LabelTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tid && t.OrganizationId == org, ct) ?? throw new NotFoundException("Шаблон этикетки")
            : await db.LabelTemplates.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived).OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id)
                .FirstOrDefaultAsync(ct);
        var dto = template is null ? PresetDto(LabelTemplate.Presets[0]) : ToDto(template);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => new { o.ShortName, o.CountryCode }).SingleAsync(ct);
        var items = await ItemsAsync(ctx, ids, dto.PriceTypeId, ct);
        var currency = organization.CountryCode switch { "KZ" => "₸", "UZ" => "сум", "BY" => "Br", _ => "₽" };
        return new LabelPrintDto(dto, organization.ShortName, currency, items, DateOnly.FromDateTime(clock.UtcNow));
    }

    /// <summary>Образец позиции для предпросмотра в конструкторе: найденная поиском или первая с штрихкодом; без позиций — пример.</summary>
    public async Task<LabelItemDto> SampleAsync(string? search, long? priceTypeId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var org = ctx.OrganizationId;
        var q = db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var u = search.Trim().ToUpperInvariant();
            q = q.Where(i => EF.Property<string>(i, ItemSearch.Column).Contains(u));
        }
        else
        {
            q = q.OrderByDescending(i => db.ItemBarcodes.Any(b => b.ItemId == i.Id)).ThenBy(i => i.Id);
        }

        var id = await q.Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        return id is { } found
            ? (await ItemsAsync(ctx, [found], priceTypeId, ct)).Single()
            : new LabelItemDto(0, "Свитер вязаный «Норвегия»", "СВ-0001", "KN-00042", ctx.Permissions.Has(Permissions.PriceView) ? 2490m : null,
                "2000000000015", "шт", "Цвет: синий, Размер: 48", "Россия", "Шерсть 50 %, акрил 50 %", "Свитеры");
    }

    private async Task<IReadOnlyList<LabelItemDto>> ItemsAsync(AccessContext ctx, IReadOnlyList<long> ids, long? priceTypeId, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var items = await (from i in db.Items.AsNoTracking()
                           join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                           where i.OrganizationId == org && ids.Contains(i.Id)
                           select new { i.Id, i.Name, i.Code, i.Article, i.Description, i.OriginCountryName, i.GroupId, i.ParentItemId, u.Symbol, i.Gtin })
            .ToListAsync(ct);
        if (items.Count != ids.Count)
        {
            throw new NotFoundException("Номенклатура");
        }

        var barcodes = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && ids.Contains(b.ItemId))
            .OrderBy(b => b.Type).ThenBy(b => b.Id).Select(b => new { b.ItemId, b.Code, b.Type }).ToListAsync(ct)).ToLookup(b => b.ItemId);
        var parents = items.Where(i => i.ParentItemId is not null).Select(i => i.ParentItemId!.Value).Distinct().ToList();
        var parentGroups = await db.Items.AsNoTracking().Where(i => parents.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.GroupId, ct);
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var values = (await (from v in db.ItemCharacteristicValues.AsNoTracking()
                             join c in db.Characteristics.AsNoTracking() on v.CharacteristicId equals c.Id
                             where v.OrganizationId == org && ids.Contains(v.ItemId)
                             orderby c.SortOrder
                             select new { v.ItemId, c.Name, v.Value }).ToListAsync(ct)).ToLookup(v => v.ItemId);
        Dictionary<long, decimal> prices = [];
        if (ctx.Permissions.Has(Permissions.PriceView))
        {
            var type = priceTypeId ?? await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && t.IsDefault).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct);
            prices = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == org && p.PriceTypeId == type && ids.Contains(p.ItemId))
                .ToDictionaryAsync(p => p.ItemId, p => p.Price, ct);
        }

        var byId = items.ToDictionary(i => i.Id);
        return ids.Select(id =>
        {
            var i = byId[id];
            var codes = barcodes[id].ToList();
            var barcode = codes.FirstOrDefault(b => b.Type == BarcodeType.Ean13)?.Code ?? codes.FirstOrDefault()?.Code ?? i.Gtin ?? i.Code;
            var groupId = i.ParentItemId is { } p ? parentGroups.GetValueOrDefault(p) ?? i.GroupId : i.GroupId;
            var chars = values[id].Any() ? string.Join(", ", values[id].Select(v => $"{v.Name}: {v.Value}")) : null;
            return new LabelItemDto(i.Id, i.Name, i.Code, i.Article, prices.TryGetValue(id, out var price) ? price : null, barcode, i.Symbol, chars,
                i.OriginCountryName, i.Description, groupId is { } g ? groups.GetValueOrDefault(g) : null);
        }).ToList();
    }

    private async Task RequirePriceTypeAsync(AccessContext ctx, long? priceTypeId, CancellationToken ct)
    {
        if (priceTypeId is { } p && !await db.PriceTypes.AnyAsync(t => t.Id == p && t.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Вид цены");
        }
    }

    private async Task<LabelTemplate> FindAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.LabelTemplates.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Шаблон этикетки");

    public static LabelTemplateDto PresetDto(LabelPreset p) =>
        new(0, p.Name, p.WidthMm, p.HeightMm, p.Sheet, p.Columns, p.Rows, 2, null, p.Elements, false, false, []);

    private static LabelTemplateDto ToDto(LabelTemplate t) =>
        new(t.Id, t.Name, t.WidthMm, t.HeightMm, t.Sheet, t.Columns, t.Rows, t.PaddingMm, t.PriceTypeId, t.Elements, t.IsDefault, t.IsArchived, t.RowVersion);

    private static string Size(LabelTemplate t) =>
        string.Create(CultureInfo.InvariantCulture, $"{t.WidthMm:0.#}×{t.HeightMm:0.#} мм, {(t.Sheet == LabelSheet.A4 ? $"A4 {t.Columns}×{t.Rows}" : "рулон")}");

    private void Audit(AccessContext ctx, long id, string? before, string after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(LabelTemplate),
            id.ToString(CultureInfo.InvariantCulture), before, after, reason, currentUser.CorrelationId));
}
