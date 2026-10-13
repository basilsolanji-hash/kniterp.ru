using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Отбор прайс-листа: вид цены (нет — основной), группа с подгруппами, только в наличии (на складе или на всех видимых), поиск.</summary>
public sealed record PriceListFilter(long? PriceTypeId = null, long? GroupId = null, bool InStockOnly = false, long? WarehouseId = null, string? Search = null);

/// <summary>Строка прайс-листа. Stock — только при складских правах (D118); PhotoId — первое фото позиции, только с правом «Каталог: просмотр».</summary>
public sealed record PriceListRowDto(long ItemId, string? Group, string Code, string? Article, string Name, string Unit, decimal Price, decimal? Stock, long? PhotoId);

public sealed record PriceListDto(
    string PriceType, bool IncludesVat, string CurrencyCode, DateOnly Date, string Organization, bool StockShown, bool PhotosShown, bool Truncated,
    IReadOnlyList<PriceListRowDto> Rows);

public sealed record PriceListOptionsDto(
    IReadOnlyList<(long Id, string Name, bool IsDefault)> PriceTypes, IReadOnlyList<(long Id, string Name)> Groups,
    IReadOnlyList<(long Id, string Name)> Warehouses, bool CanSeeStock);

/// <summary>
/// D1142 (ТЗ §2): прайс-лист по виду цены и отбору — просмотр, печать, PDF и Excel. Позиции — неархивные, с ценой в выбранном виде
/// цены. Наличие — только через видимые движения складов (D118): без складских прав отбор «в наличии» недоступен, колонки остатка нет.
/// Ссылки на фото — только с правом «Каталог: просмотр» (фото отдаются по тому же праву). Права: «Продажи: просмотр» и «Цены и суммы».
/// </summary>
public sealed class SalesPriceListService(IKnitErpDbContext db, IAccessGuard guard, IClock clock)
{
    public const int MaxRows = 5000;

    public async Task<PriceListOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.IsDefault }).ToListAsync(ct);
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org).Select(g => new { g.Id, g.ParentId, g.Name }).ToListAsync(ct);
        var byId = groups.ToDictionary(g => g.Id);
        string Path(long id)
        {
            var parts = new List<string>();
            for (long? g = id; g is { } gid && byId.TryGetValue(gid, out var x) && parts.Count < 6; g = x.ParentId)
            {
                parts.Insert(0, x.Name);
            }

            return string.Join(" / ", parts);
        }

        var canStock = WarehouseScope.VisibleMovements(db, ctx) is not null;
        var visible = WarehouseScope.Visible(ctx, WarehouseScope.StockViews);
        var warehouses = canStock
            ? await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == org && !w.IsArchived && (visible == null || visible.Contains(w.Id)))
                .OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync(ct)
            : [];
        return new PriceListOptionsDto(types.Select(t => (t.Id, t.Name, t.IsDefault)).ToList(),
            groups.Select(g => (g.Id, Path(g.Id))).OrderBy(g => g.Item2, StringComparer.CurrentCulture).ToList(),
            warehouses.Select(w => (w.Id, w.Name)).ToList(), canStock);
    }

    public async Task<PriceListDto> BuildAsync(PriceListFilter filter, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        var type = await db.PriceTypes.AsNoTracking()
                       .Where(t => t.OrganizationId == org && !t.IsArchived && (filter.PriceTypeId == null ? t.IsDefault : t.Id == filter.PriceTypeId))
                       .OrderBy(t => t.SortOrder).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Вид цены");
        var movements = WarehouseScope.VisibleMovements(db, ctx);
        if (filter.InStockOnly && movements is null)
        {
            throw new BusinessRuleException("sales.price_list.stock", "Отбор «только в наличии» доступен с правами на остатки склада.");
        }

        if (filter.WarehouseId is { } wid && movements is not null)
        {
            if (WarehouseScope.Visible(ctx, WarehouseScope.StockViews) is { } visible && !visible.Contains(wid))
            {
                throw new NotFoundException("Склад");
            }

            movements = movements.Where(m => m.WarehouseId == wid);
        }

        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org).Select(g => new { g.Id, g.ParentId, g.Name }).ToListAsync(ct);
        HashSet<long>? groupIds = null;
        if (filter.GroupId is { } gid0)
        {
            if (groups.All(g => g.Id != gid0))
            {
                throw new NotFoundException("Группа");
            }

            // Группа с подгруппами (как у акций, D1106).
            groupIds = [gid0];
            for (var added = true; added;)
            {
                added = false;
                foreach (var g in groups.Where(g => g.ParentId is { } p && groupIds.Contains(p) && !groupIds.Contains(g.Id)))
                {
                    added |= groupIds.Add(g.Id);
                }
            }
        }

        var q = from i in db.Items.AsNoTracking()
                join p in db.ItemPrices.AsNoTracking() on i.Id equals p.ItemId
                join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                where i.OrganizationId == org && !i.IsArchived && p.OrganizationId == org && p.PriceTypeId == type.Id
                select new { i.Id, i.GroupId, i.Code, i.Article, i.Name, Unit = u.Symbol, p.Price };
        if (groupIds is not null)
        {
            var ids = groupIds.ToList();
            q = q.Where(x => x.GroupId != null && ids.Contains(x.GroupId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            q = q.Where(x => x.Code.Contains(text) || x.Name.Contains(text) || (x.Article != null && x.Article.Contains(text)));
        }

        Dictionary<long, decimal>? stock = null;
        if (movements is not null)
        {
            var stockQuery = movements.GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).Where(x => x.Sum > 0);
            if (filter.InStockOnly)
            {
                q = q.Where(x => stockQuery.Any(s => s.Key == x.Id));
            }

            var itemIds = q.Select(x => x.Id);
            stock = await stockQuery.Where(s => itemIds.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Sum, ct);
        }

        var rows = await q.OrderBy(x => x.Code).Take(MaxRows + 1).ToListAsync(ct);
        var truncated = rows.Count > MaxRows;
        rows = rows.Take(MaxRows).ToList();
        var photos = ctx.Permissions.Has(Permissions.CatalogView);
        Dictionary<long, long> photoIds = [];
        if (photos && rows.Count > 0)
        {
            var ids = rows.Select(r => r.Id).ToList();
            photoIds = (await db.ItemPhotos.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.ItemId))
                    .Select(p => new { p.ItemId, p.Id, p.SortOrder }).ToListAsync(ct))
                .GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).First().Id);
        }

        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var orgRow = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => new { o.ShortName, o.CurrencyCode, o.TimeZoneId }).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, orgRow.TimeZoneId));
        return new PriceListDto(type.Name, type.IncludesVat, orgRow.CurrencyCode, today, orgRow.ShortName, stock is not null, photos, truncated,
            rows.Select(r => new PriceListRowDto(r.Id, r.GroupId is { } g ? groupName.GetValueOrDefault(g) : null, r.Code, r.Article, r.Name, r.Unit, r.Price,
                    stock is null ? null : stock.GetValueOrDefault(r.Id), photos ? photoIds.GetValueOrDefault(r.Id) is var ph and > 0 ? ph : null : null))
                .OrderBy(r => r.Group ?? "￿", StringComparer.CurrentCulture).ThenBy(r => r.Code, StringComparer.Ordinal).ToList());
    }

    /// <summary>Excel прайс-листа; photoUrl — абсолютная ссылка на фото (открывается после входа с правом каталога).</summary>
    public async Task<byte[]> ExcelAsync(PriceListFilter filter, ISpreadsheetFormat spreadsheet, Func<long, string> photoUrl, CancellationToken ct = default)
    {
        var list = await BuildAsync(filter, ct);
        static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        var header = new List<string> { "Группа", "Код", "Артикул", "Наименование", "Ед.", $"Цена, {list.CurrencyCode}" };
        if (list.StockShown)
        {
            header.Add("В наличии");
        }

        if (list.PhotosShown)
        {
            header.Add("Фото");
        }

        var rows = list.Rows.Select(r =>
        {
            var row = new List<string> { r.Group ?? "", r.Code, r.Article ?? "", r.Name, r.Unit, N(r.Price) };
            if (list.StockShown)
            {
                row.Add(N(r.Stock ?? 0));
            }

            if (list.PhotosShown)
            {
                row.Add(r.PhotoId is { } id ? photoUrl(id) : "");
            }

            return (IReadOnlyList<string>)row;
        }).ToList();
        var numeric = new HashSet<int> { 5 };
        if (list.StockShown)
        {
            numeric.Add(6);
        }

        return spreadsheet.Write([new SheetData("Прайс-лист", header, rows, numeric)]);
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }
}
