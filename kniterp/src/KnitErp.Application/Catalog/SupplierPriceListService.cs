using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public enum PriceListAction : byte
{
    Update = 1,
    Unchanged = 2,
    NotFound = 3,
    Error = 4,
}

/// <summary>Строка прайс-листа: что в файле, с какой позицией сопоставлена и как (артикул поставщика или штрихкод), что будет.</summary>
public sealed record PriceListRowDto(
    int RowNumber, string? Article, string? Name, string? Barcode, decimal? FilePrice, long? ItemId, string? ItemCode, string? ItemName, string? MatchedBy,
    decimal? OldPurchasePrice, decimal? NewPurchasePrice, PriceListAction Action, string? Error);

public sealed record PriceListPlanDto(long SupplierId, bool PricesIncludeVat, IReadOnlyList<PriceListRowDto> Rows)
{
    public int ToUpdate => Rows.Count(r => r.Action == PriceListAction.Update);
    public int NotFound => Rows.Count(r => r.Action == PriceListAction.NotFound);
    public int Errors => Rows.Count(r => r.Action == PriceListAction.Error);
}

public sealed record PriceListResult(int Updated, int Linked, int AutoPrices);

/// <summary>Коды ошибок строки прайс-листа (текст — на экране, на языке интерфейса).</summary>
public static class PriceListErrors
{
    public const string Price = "price";
    public const string Bundle = "bundle";
    public const string Duplicate = "duplicate";
}

/// <summary>
/// Прайс-лист поставщика (D323): Excel поставщика → закупочные цены позиций. Заголовок ищется в первых 30 строках: столбец артикула
/// («Артикул», «Код», «Арт.») и цены («Цена…»), необязательные — наименование и штрихкод. Сопоставление: артикул поставщика в
/// «Поставщиках позиции», затем штрихкод позиции; найденное по штрихкоду запоминается как артикул поставщика. Цены с НДС пересчитываются
/// без НДС по ставке позиции. После загрузки — автоцены по правилам (D322). Право: «Справочники: изменение» и «Цены и суммы».
/// </summary>
public sealed class SupplierPriceListService(IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRows = 20_000;
    private static readonly string[] ArticleHeaders = ["артикул", "арт.", "арт", "код", "код товара", "article", "sku"];
    private static readonly string[] PriceHeaders = ["цена", "price", "стоимость"];
    private static readonly string[] NameHeaders = ["наименование", "название", "товар", "name"];
    private static readonly string[] BarcodeHeaders = ["штрихкод", "штрих-код", "ean", "barcode", "шк"];

    public async Task<PriceListPlanDto> PreviewAsync(long supplierId, Stream file, bool pricesIncludeVat, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        await RequireSupplierAsync(org, supplierId, ct);
        var rows = spreadsheet.ReadFirstSheet(file, MaxRows + 30);
        var (headerIndex, art, price, name, barcode) = FindHeader(rows);

        var links = await db.ItemSuppliers.AsNoTracking().Where(s => s.OrganizationId == org && s.SupplierId == supplierId && s.SupplierArticle != null)
            .Select(s => new { s.SupplierArticle, s.ItemId }).ToListAsync(ct);
        var byArticle = links.GroupBy(l => l.SupplierArticle!.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ItemId,
            StringComparer.OrdinalIgnoreCase);
        var byBarcode = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org).ToDictionaryAsync(b => b.Code, b => b.ItemId, ct);
        var items = await (from i in db.Items.AsNoTracking()
                           where i.OrganizationId == org && !i.IsArchived
                           select new { i.Id, i.Code, i.Name, i.PurchasePrice, i.VatRateId, i.Type }).ToDictionaryAsync(i => i.Id, ct);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var rates = (await db.VatRates.AsNoTracking().Include(r => r.Periods).Where(r => r.OrganizationId == org).ToListAsync(ct)).ToDictionary(r => r.Id, r => r.PercentOn(today));

        var result = new List<PriceListRowDto>();
        var seen = new HashSet<long>();
        for (var r = headerIndex + 1; r < rows.Count && result.Count < MaxRows; r++)
        {
            string? Cell(int? c) => c is { } i && i < rows[r].Count && !string.IsNullOrWhiteSpace(rows[r][i]) ? rows[r][i].Trim() : null;
            var article = Cell(art);
            var text = Cell(price);
            var title = Cell(name);
            var code = Cell(barcode)?.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (article is null && code is null && text is null)
            {
                continue; // пустая строка или разделитель раздела
            }

            var line = r + 1;
            if (!TryPrice(text, out var filePrice))
            {
                result.Add(new PriceListRowDto(line, article, title, code, null, null, null, null, null, null, null, PriceListAction.Error,
                    PriceListErrors.Price));
                continue;
            }

            long? itemId = null;
            string? matchedBy = null;
            if (article is not null && byArticle.TryGetValue(article, out var a))
            {
                (itemId, matchedBy) = (a, "артикул поставщика");
            }
            else if (code is not null && byBarcode.TryGetValue(code, out var b))
            {
                (itemId, matchedBy) = (b, "штрихкод");
            }

            if (itemId is not { } id || !items.TryGetValue(id, out var item))
            {
                result.Add(new PriceListRowDto(line, article, title, code, filePrice, null, null, null, null, null, null, PriceListAction.NotFound, null));
                continue;
            }

            if (item.Type == ItemType.Bundle)
            {
                result.Add(new PriceListRowDto(line, article, title, code, filePrice, id, item.Code, item.Name, matchedBy, null, null, PriceListAction.Error,
                    PriceListErrors.Bundle));
                continue;
            }

            if (!seen.Add(id))
            {
                result.Add(new PriceListRowDto(line, article, title, code, filePrice, id, item.Code, item.Name, matchedBy, item.PurchasePrice, null, PriceListAction.Error,
                    PriceListErrors.Duplicate));
                continue;
            }

            var percent = pricesIncludeVat && item.VatRateId is { } v ? rates.GetValueOrDefault(v) ?? 0 : 0;
            var newPrice = decimal.Round(filePrice / (1 + percent / 100m), 2, MidpointRounding.AwayFromZero);
            result.Add(new PriceListRowDto(line, article, title, code, filePrice, id, item.Code, item.Name, matchedBy, item.PurchasePrice, newPrice,
                item.PurchasePrice == newPrice ? PriceListAction.Unchanged : PriceListAction.Update, null));
        }

        return new PriceListPlanDto(supplierId, pricesIncludeVat, result);
    }

    /// <summary>Применить план предпросмотра: закупочные цены, запоминание артикула поставщика, автоцены — одной транзакцией.</summary>
    public async Task<PriceListResult> ApplyAsync(PriceListPlanDto plan, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        var supplier = await RequireSupplierAsync(org, plan.SupplierId, ct);
        var rows = plan.Rows.Where(r => r.Action == PriceListAction.Update && r.ItemId is not null && r.NewPurchasePrice is > 0).ToList();
        var ids = rows.Select(r => r.ItemId!.Value).ToList();
        var updated = 0;
        var linked = 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var items = await db.Items.Where(i => i.OrganizationId == org && ids.Contains(i.Id) && !i.IsArchived).ToDictionaryAsync(i => i.Id, ct);
            var existingLinks = await db.ItemSuppliers.Where(s => s.OrganizationId == org && s.SupplierId == plan.SupplierId && ids.Contains(s.ItemId))
                .ToDictionaryAsync(s => s.ItemId, ct);
            var linkCounts = await db.ItemSuppliers.AsNoTracking().Where(s => s.OrganizationId == org && ids.Contains(s.ItemId))
                .GroupBy(s => s.ItemId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            // D348: «есть ли основной поставщик» — одним запросом на весь прайс, а не запросом на строку (20 000 строк — 20 000 запросов).
            var withMain = (await db.ItemSuppliers.AsNoTracking().Where(s => s.OrganizationId == org && s.IsMain && ids.Contains(s.ItemId))
                .Select(s => s.ItemId).Distinct().ToListAsync(ct)).ToHashSet();
            foreach (var r in rows)
            {
                if (!items.TryGetValue(r.ItemId!.Value, out var item))
                {
                    continue;
                }

                foreach (var c in item.SetDetails(item.Details with { PurchasePrice = r.NewPurchasePrice }))
                {
                    db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), item.Id.ToString(CultureInfo.InvariantCulture),
                        c.Before, c.After, $"Прайс-лист «{supplier}»: {c.Field}", currentUser.CorrelationId));
                    updated++;
                }

                // Найдено по штрихкоду — запомнить артикул поставщика: следующий прайс сопоставится по нему.
                // Ревью: длинный артикул (не поместится в справочник) и предел поставщиков позиции — связь не создаётся, цена обновляется;
                // длинное наименование обрезается. Раньше одна такая строка отменяла весь прайс.
                if (r.Article is { Length: <= ItemSupplier.ArticleMaxLength } && item.Type != ItemType.Bundle && !existingLinks.ContainsKey(item.Id)
                    && linkCounts.GetValueOrDefault(item.Id) < ItemSupplier.MaxPerItem)
                {
                    var supplierName = r.Name is { Length: > ItemSupplier.NameMaxLength } longName ? longName[..ItemSupplier.NameMaxLength] : r.Name;
                    var link = ItemSupplier.Create(org, item, plan.SupplierId, r.Article, supplierName, withMain.Add(item.Id));
                    linkCounts[item.Id] = linkCounts.GetValueOrDefault(item.Id) + 1;
                    db.ItemSuppliers.Add(link);
                    existingLinks[item.Id] = link;
                    linked++;
                }
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogImported, "SupplierPriceList", plan.SupplierId.ToString(CultureInfo.InvariantCulture),
                null, $"строк {plan.Rows.Count}: закупочных цен изменено {updated}, связано артикулов {linked}, не найдено {plan.NotFound}",
                $"Прайс-лист «{supplier}»" + (plan.PricesIncludeVat ? " (цены с НДС)" : ""), currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            var auto = await AutoPrices.ApplyAsync(db, org, ids, ctx.UserId, clock.UtcNow, ct);
            await tx.CommitAsync(ct);
            return new PriceListResult(updated, linked, auto);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private static (int Header, int Article, int Price, int? Name, int? Barcode) FindHeader(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        static int? Find(IReadOnlyList<string> row, string[] names) =>
            row.Select((v, i) => (V: (v ?? string.Empty).Trim().ToLowerInvariant(), I: i))
                .Where(x => names.Any(n => x.V == n || x.V.StartsWith(n + " ", StringComparison.Ordinal) || x.V.StartsWith(n + ",", StringComparison.Ordinal)))
                .Select(x => (int?)x.I).FirstOrDefault();

        for (var r = 0; r < Math.Min(30, rows.Count); r++)
        {
            var row = rows[r];
            if (Find(row, ArticleHeaders) is { } a && Find(row, PriceHeaders) is { } p)
            {
                return (r, a, p, Find(row, NameHeaders), Find(row, BarcodeHeaders));
            }
        }

        throw new BusinessRuleException("catalog.price_list.header",
            "Не найден заголовок: нужны столбцы «Артикул» (или «Код») и «Цена» в первых 30 строках листа.");
    }

    private static bool TryPrice(string? text, out decimal price)
    {
        price = 0;
        var t = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("₽", string.Empty, StringComparison.Ordinal).Replace("руб.", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out price) && price > 0 && price < 1_000_000_000;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct); // закупочные цены видны только с правом «Цены и суммы»
        return ctx;
    }

    private async Task<string> RequireSupplierAsync(long org, long supplierId, CancellationToken ct) =>
        await db.Counterparties.AsNoTracking().Where(c => c.Id == supplierId && c.OrganizationId == org && c.IsSupplier).Select(c => c.Name).SingleOrDefaultAsync(ct)
        ?? throw new NotFoundException("Поставщик");
}
