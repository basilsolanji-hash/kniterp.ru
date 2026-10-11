using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Строка листа «Состав комплектов» и итог её проверки (D308).</summary>
public sealed record BundleImportRow(int RowNumber, string BundleCode, string ComponentCode, string QuantityText, IReadOnlyList<string> Errors);

/// <summary>
/// Excel каталога (D308, сессия № 4): столбцы «Предмет расчёта», «Маркировка», «GTIN», «Минимальная цена», «Упаковки» и лист
/// «Состав комплектов». Столбца или листа нет — значения не меняются: старые файлы загружаются как раньше.
/// </summary>
public sealed partial class ItemExchangeService
{
    public const string PaymentSubjectColumn = "Предмет расчёта";
    public const string MarkingColumn = "Маркировка";
    public const string GtinColumn = "GTIN";
    public const string MinPriceColumn = "Минимальная цена";
    public const string PacksColumn = "Упаковки";
    public const string BundleSheet = "Состав комплектов";
    public static readonly IReadOnlyList<string> BundleColumns = ["Комплект (код)", "Компонент (код)", "Количество"];
    private const string NotMarked = "нет";

    /// <summary>Разобранные торговые столбцы строки: null у части — столбца нет. Packs — единица, коэффициент, штрихкод.</summary>
    private sealed record TradeSpec(ItemTradeDetails? Trade, IReadOnlyList<(long UnitId, decimal Factor, string? Barcode)>? Packs, bool Changed);

    /// <summary>Справочники для торговых столбцов: единицы, текущие упаковки и чьи штрихкоды и GTIN уже заняты.</summary>
    private sealed record CatalogLookups(
        IReadOnlyList<UnitOfMeasure> Units, IReadOnlyDictionary<long, byte> Precision, ILookup<long, ItemPack> Packs,
        IReadOnlyDictionary<string, long> BarcodeOwners, IReadOnlyDictionary<string, long> GtinOwners, Dictionary<string, int> FileCodes);

    private async Task<CatalogLookups> CatalogLookupsAsync(long org, IReadOnlyList<ItemImportRow> raw, IReadOnlyDictionary<string, Item> existing,
        CancellationToken ct)
    {
        var units = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org && !u.IsArchived).ToListAsync(ct);
        var precision = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org).ToDictionaryAsync(u => u.Id, u => u.Precision, ct);
        var ids = existing.Values.Select(i => i.Id).ToList();
        var packs = (await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.ItemId)).ToListAsync(ct)).ToLookup(p => p.ItemId);
        var packTexts = raw.Select(r => r.Extra?.GetValueOrDefault(PacksColumn)).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var barcodes = packTexts.SelectMany(t => t!.Split(';')).Select(p => p.Split('/') is { Length: 2 } parts ? parts[1].Trim() : null)
            .Where(b => !string.IsNullOrEmpty(b)).Cast<string>().Distinct().ToList();
        var owners = new Dictionary<string, long>();
        if (barcodes.Count > 0)
        {
            foreach (var b in await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && barcodes.Contains(b.Code)).Select(b => new { b.Code, b.ItemId })
                         .ToListAsync(ct))
            {
                owners.TryAdd(b.Code, b.ItemId);
            }

            foreach (var p in await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org && p.Barcode != null && barcodes.Contains(p.Barcode))
                         .Select(p => new { p.Barcode, p.ItemId }).ToListAsync(ct))
            {
                owners.TryAdd(p.Barcode!, p.ItemId);
            }
        }

        var gtins = raw.Select(r => r.Extra?.GetValueOrDefault(GtinColumn)).Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => TryGtin(g!)).Where(g => g is not null).Cast<string>().Distinct().ToList();
        var gtinOwners = gtins.Count == 0
            ? new Dictionary<string, long>()
            : await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && i.Gtin != null && gtins.Contains(i.Gtin))
                .ToDictionaryAsync(i => i.Gtin!, i => i.Id, ct);
        return new CatalogLookups(units, precision, packs, owners, gtinOwners, []);
    }

    /// <summary>Торговые столбцы строки по правилам карточки; ошибки — в список строки. null — ни одного торгового столбца нет.</summary>
    private TradeSpec? ParseTrade(IReadOnlyDictionary<string, string> extra, Item? current, Item candidate, CatalogLookups lookups, List<string> errors,
        bool canSeePrices, int rowNumber)
    {
        bool Has(string column) => extra.ContainsKey(column);
        if (!Has(PaymentSubjectColumn) && !Has(MarkingColumn) && !Has(GtinColumn) && !Has(MinPriceColumn) && !Has(PacksColumn))
        {
            return null;
        }

        var before = current?.Trade ?? new ItemTradeDetails(ItemTypes.DefaultPaymentSubject(candidate.Type), false, TrackingType.None, null, null);
        var trade = before;
        if (Has(PaymentSubjectColumn))
        {
            var text = extra[PaymentSubjectColumn].Trim();
            var subject = Enum.GetValues<PaymentSubject>().Cast<PaymentSubject?>()
                .FirstOrDefault(s => string.Equals(TradeRules.PaymentSubjectName(s!.Value), text, StringComparison.OrdinalIgnoreCase));
            if (text.Length > 0 && subject is null)
            {
                errors.Add($"Предмет расчёта «{text}»: укажите «Товар», «Подакцизный товар» или «Услуга».");
            }

            trade = trade with { PaymentSubject = subject ?? ItemTypes.DefaultPaymentSubject(candidate.Type) };
        }

        if (Has(MarkingColumn))
        {
            var text = extra[MarkingColumn].Trim();
            var tracking = text.Length == 0 || string.Equals(text, NotMarked, StringComparison.OrdinalIgnoreCase)
                ? TrackingType.None
                : Marking.All.Cast<TrackingType?>().FirstOrDefault(t => string.Equals(Marking.Name(t!.Value), text, StringComparison.OrdinalIgnoreCase)
                                                                        || string.Equals(Marking.MoySkladCode(t.Value), text, StringComparison.OrdinalIgnoreCase));
            if (tracking is null)
            {
                errors.Add($"Маркировка «{text}»: укажите вид продукции как на листе «Допустимые значения» или «нет».");
            }

            trade = trade with { IsMarked = tracking is not (null or TrackingType.None), TrackingType = tracking ?? TrackingType.None };
        }

        if (Has(GtinColumn))
        {
            trade = trade with { Gtin = extra[GtinColumn].Trim() is { Length: > 0 } g ? g : null };
        }

        if (Has(MinPriceColumn))
        {
            if (!canSeePrices)
            {
                errors.Add("Минимальную цену загружает только тот, у кого есть право «Цены и суммы».");
            }

            try
            {
                trade = trade with { MinPrice = Decimals.Parse(extra[MinPriceColumn], MinPriceColumn) };
            }
            catch (BusinessRuleException ex)
            {
                errors.Add(ex.Message);
            }
        }

        try
        {
            trade = TradeRules.Normalize(candidate.Type, trade);
            if (trade.Gtin is { } gtin)
            {
                if (lookups.GtinOwners.TryGetValue(gtin, out var owner) && owner != current?.Id)
                {
                    errors.Add($"GTIN {gtin} уже у другой позиции.");
                }

                if (lookups.FileCodes.TryGetValue("gtin:" + gtin, out var otherRow) && otherRow != rowNumber)
                {
                    errors.Add($"GTIN {gtin} повторяется в строке {otherRow}.");
                }

                lookups.FileCodes.TryAdd("gtin:" + gtin, rowNumber);
            }
        }
        catch (BusinessRuleException ex)
        {
            errors.Add(ex.Message);
        }

        IReadOnlyList<(long UnitId, decimal Factor, string? Barcode)>? packs = null;
        var packsChanged = false;
        if (Has(PacksColumn))
        {
            packs = ParsePacks(extra[PacksColumn], current, candidate, lookups, errors, rowNumber);
            var now = current is null ? [] : lookups.Packs[current.Id].Select(p => (p.UnitId, p.Factor, p.Barcode)).OrderBy(p => p.UnitId).ToList();
            packsChanged = !now.SequenceEqual(packs.OrderBy(p => p.UnitId));
        }

        return new TradeSpec(trade, packs, trade != before || packsChanged);
    }

    /// <summary>«упак = 12; кор = 48 / 4600000000008»: единица (обозначение или наименование), коэффициент, необязательный штрихкод.</summary>
    private static List<(long UnitId, decimal Factor, string? Barcode)> ParsePacks(string text, Item? current, Item candidate, CatalogLookups lookups,
        List<string> errors, int rowNumber)
    {
        var result = new List<(long UnitId, decimal Factor, string? Barcode)>();
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && !ItemTypes.IsStocked(candidate.Type))
        {
            errors.Add("Упаковки бывают только у товаров — не у услуг и комплектов.");
            return result;
        }

        if (parts.Length > ItemPack.MaxPerItem)
        {
            errors.Add($"У позиции не больше {ItemPack.MaxPerItem} упаковок.");
            return result;
        }

        foreach (var part in parts)
        {
            var eq = part.Split('=', 2, StringSplitOptions.TrimEntries);
            var right = eq.Length == 2 ? eq[1].Split('/', 2, StringSplitOptions.TrimEntries) : [];
            var unit = eq.Length == 2 ? FindUnit(lookups.Units, eq[0]) : null;
            if (eq.Length != 2 || unit is null)
            {
                errors.Add($"Упаковка «{part}»: пишите «единица = количество» или «единица = количество / штрихкод», единица — из справочника.");
                continue;
            }

            try
            {
                if (unit.Id == candidate.UnitId)
                {
                    throw new BusinessRuleException("catalog.pack.same_unit", "Единица упаковки совпадает с основной — выберите другую (коробка, мешок, упаковка).");
                }

                var factor = Packs.CheckFactor(Decimals.Parse(right[0], PacksColumn) ?? 0, lookups.Precision.GetValueOrDefault(candidate.UnitId));
                var barcode = right.Length == 2 ? Packs.NormalizeBarcode(right[1]) : null;
                if (barcode is not null)
                {
                    if (lookups.BarcodeOwners.TryGetValue(barcode, out var owner)
                        && !(owner == current?.Id && lookups.Packs[owner].Any(p => p.Barcode == barcode)))
                    {
                        errors.Add($"Штрихкод упаковки {barcode} уже занят.");
                    }

                    if (lookups.FileCodes.TryGetValue("pack:" + barcode, out var otherRow) && otherRow != rowNumber)
                    {
                        errors.Add($"Штрихкод упаковки {barcode} повторяется в строке {otherRow}.");
                    }

                    lookups.FileCodes.TryAdd("pack:" + barcode, rowNumber);
                }

                if (result.Any(r => r.UnitId == unit.Id))
                {
                    errors.Add($"Упаковка «{unit.Symbol}» указана дважды.");
                    continue;
                }

                result.Add((unit.Id, factor, barcode));
            }
            catch (BusinessRuleException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return result;
    }

    /// <summary>Применение торговых столбцов: поля позиции и замена упаковок (как в файле). Журнал — по изменённым полям.</summary>
    private async Task ApplyTradeAsync(AccessContext ctx, Item item, TradeSpec? spec, CancellationToken ct)
    {
        if (spec is null)
        {
            return;
        }

        var id = item.Id.ToString();
        if (spec.Trade is { } trade)
        {
            foreach (var c in item.SetTrade(trade))
            {
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, c.Before, c.After, $"Импорт: {c.Field}");
            }
        }

        if (spec.Packs is { } packs)
        {
            var current = await db.ItemPacks.Where(p => p.ItemId == item.Id).ToListAsync(ct);
            var precision = await db.Units.AsNoTracking().Where(u => u.Id == item.UnitId).Select(u => u.Precision).SingleAsync(ct);
            var removed = current.Where(p => packs.All(n => n.UnitId != p.UnitId)).ToList();
            db.ItemPacks.RemoveRange(removed);
            await db.SaveChangesAsync(ct); // освобождает штрихкоды убранных упаковок до вставки новых
            foreach (var (unitId, factor, barcode) in packs)
            {
                if (current.FirstOrDefault(p => p.UnitId == unitId) is { } pack)
                {
                    pack.Set(factor, precision, barcode);
                }
                else
                {
                    db.ItemPacks.Add(ItemPack.Create(ctx.OrganizationId, item, unitId, factor, precision, barcode));
                }
            }

            if (removed.Count > 0 || packs.Count > 0)
            {
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, null, PacksText(packs, await UnitSymbolsAsync(ct)), "Импорт: упаковки");
            }
        }
    }

    private async Task<Dictionary<long, string>> UnitSymbolsAsync(CancellationToken ct) =>
        await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Symbol, ct);

    private static string PacksText(IEnumerable<(long UnitId, decimal Factor, string? Barcode)> packs, IReadOnlyDictionary<long, string> symbols) =>
        string.Join("; ", packs.OrderBy(p => p.Factor).Select(p =>
            $"{symbols.GetValueOrDefault(p.UnitId, "?")} = {p.Factor.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"))}{(p.Barcode is { } b ? " / " + b : "")}"));

    /// <summary>Значения торговых столбцов для выгрузки.</summary>
    private static void ExportTrade(Dictionary<string, string> cells, Item item, ILookup<long, ItemPack> packs, IReadOnlyDictionary<long, string> symbols,
        bool prices)
    {
        cells[PaymentSubjectColumn] = TradeRules.PaymentSubjectName(item.PaymentSubject);
        cells[MarkingColumn] = item.IsMarked ? Marking.Name(item.TrackingType) : NotMarked;
        cells[GtinColumn] = item.Gtin ?? "";
        cells[MinPriceColumn] = prices ? Number(item.MinPrice) : "";
        cells[PacksColumn] = PacksText(packs[item.Id].Select(p => (p.UnitId, p.Factor, p.Barcode)), symbols);
    }

    private static string? TryGtin(string text)
    {
        try
        {
            return Marking.NormalizeGtin(text);
        }
        catch (BusinessRuleException)
        {
            return null;
        }
    }

    // ---------- Лист «Состав комплектов» ----------

    /// <summary>
    /// Проверка листа: комплект — позиция типа «Комплекты» (из базы или из этого же файла), компонент — товар или модификация, количество
    /// по точности единицы компонента, пара не повторяется. Для каждого комплекта из листа состав заменяется целиком.
    /// </summary>
    private async Task<IReadOnlyList<BundleImportRow>> PlanBundlesAsync(AccessContext ctx, IReadOnlyList<IReadOnlyList<string>>? sheet,
        IReadOnlyList<ItemImportRow> itemRows, CancellationToken ct)
    {
        if (sheet is null || sheet.Count <= 1 || sheet.Skip(1).All(r => r.All(c => c.Length == 0)))
        {
            return [];
        }

        if (!ctx.Permissions.Has(Permissions.BundleEdit))
        {
            throw new BusinessRuleException("import.bundles_denied",
                $"Лист «{BundleSheet}»: состав комплектов загружает только тот, у кого есть право «Номенклатура: состав комплектов».");
        }

        for (var c = 0; c < BundleColumns.Count; c++)
        {
            var actual = c < sheet[0].Count ? sheet[0][c].Trim() : "";
            if (!string.Equals(actual, BundleColumns[c], StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("import.header", $"Лист «{BundleSheet}»: столбец {c + 1} должен называться «{BundleColumns[c]}», а в файле «{actual}».");
            }
        }

        var org = ctx.OrganizationId;
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Code, i.Type, i.IsArchived, u.Precision })
            .ToDictionaryAsync(i => i.Code, ct);
        var units = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org && !u.IsArchived).ToListAsync(ct);
        // Тип и точность из файла важнее базы: позиция могла быть создана или изменена этим же файлом.
        var fromFile = itemRows.Where(r => r.Action != ImportAction.Error)
            .Select(r => (Code: Item.NormalizeCode(r.Code), Type: ParseType(r.TypeText), Unit: FindUnit(units, r.UnitText)))
            .Where(r => r.Type is not null && r.Unit is not null)
            .ToDictionary(r => r.Code, r => (Type: r.Type!.Value, r.Unit!.Precision));
        (ItemType Type, byte Precision, bool Archived)? Find(string code) =>
            fromFile.TryGetValue(code, out var f) ? (f.Type, f.Precision, false)
            : items.TryGetValue(code, out var i) ? (i.Type, i.Precision, i.IsArchived) : null;

        var result = new List<BundleImportRow>();
        var pairs = new Dictionary<(string, string), int>();
        for (var r = 1; r < sheet.Count; r++)
        {
            var cells = sheet[r];
            var bundle = Item.NormalizeCode(Cell(cells, 0));
            var component = Item.NormalizeCode(Cell(cells, 1));
            var qtyText = Cell(cells, 2);
            if (bundle.Length == 0 && component.Length == 0 && qtyText.Length == 0)
            {
                continue;
            }

            var errors = new List<string>();
            var b = Find(bundle);
            if (b is null)
            {
                errors.Add($"Комплекта {bundle} нет ни в номенклатуре, ни в файле.");
            }
            else if (b.Value.Type != ItemType.Bundle)
            {
                errors.Add($"{bundle} — не комплект: тип должен быть «{ItemTypes.Name(ItemType.Bundle)}».");
            }

            var c = Find(component);
            if (c is null)
            {
                errors.Add($"Компонента {component} нет ни в номенклатуре, ни в файле.");
            }
            else if (!ItemTypes.IsStocked(c.Value.Type))
            {
                errors.Add($"{component} — услуга или комплект: в состав входят только товары.");
            }
            else if (c.Value.Archived)
            {
                errors.Add($"Позиция {component} в архиве.");
            }

            try
            {
                var qty = Decimals.Parse(qtyText, BundleColumns[2]) ?? 0;
                BundleRules.CheckQuantity(qty, c?.Precision ?? UnitOfMeasure.MaxPrecision);
            }
            catch (BusinessRuleException ex)
            {
                errors.Add(ex.Message);
            }

            if (pairs.TryGetValue((bundle, component), out var first))
            {
                errors.Add($"Пара {bundle} — {component} уже есть в строке {first}.");
            }
            else
            {
                pairs[(bundle, component)] = r + 1;
            }

            result.Add(new BundleImportRow(r + 1, bundle, component, qtyText, errors));
        }

        foreach (var g in result.GroupBy(x => x.BundleCode).Where(g => g.Count() > BundleComponent.MaxPerBundle))
        {
            var row = g.First();
            result[result.IndexOf(row)] = row with { Errors = [.. row.Errors, $"В комплекте не больше {BundleComponent.MaxPerBundle} компонентов."] };
        }

        return result;
    }

    /// <summary>Замена состава комплектов из листа (после создания позиций). Возвращает число комплектов с изменённым составом.</summary>
    private async Task<int> ApplyBundlesAsync(AccessContext ctx, IReadOnlyList<BundleImportRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var org = ctx.OrganizationId;
        var codes = rows.SelectMany(r => new[] { r.BundleCode, r.ComponentCode }).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && codes.Contains(i.Code))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, u.Precision }).ToDictionaryAsync(i => i.Code, ct);
        var changed = 0;
        foreach (var group in rows.GroupBy(r => r.BundleCode))
        {
            var bundleId = items[group.Key].Id;
            var wanted = group.Select((r, i) => (ItemId: items[r.ComponentCode].Id, Quantity: Decimals.Parse(r.QuantityText, BundleColumns[2])!.Value,
                Precision: items[r.ComponentCode].Precision, Order: i)).ToList();
            var current = await db.BundleComponents.Where(c => c.OrganizationId == org && c.BundleItemId == bundleId).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
                .ToListAsync(ct);
            if (current.Select(c => (c.ComponentItemId, c.Quantity)).SequenceEqual(wanted.Select(w => (w.ItemId, w.Quantity))))
            {
                continue;
            }

            db.BundleComponents.RemoveRange(current);
            await db.SaveChangesAsync(ct);
            foreach (var w in wanted)
            {
                db.BundleComponents.Add(BundleComponent.Create(org, bundleId, w.ItemId, w.Quantity, w.Precision, w.Order));
            }

            Audit(ctx, AuditActions.CatalogChanged, nameof(Item), bundleId.ToString(), $"компонентов: {current.Count}",
                string.Join("; ", group.Select(r => $"{r.ComponentCode} × {r.QuantityText}")), "Импорт: состав комплекта");
            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Текущий состав комплектов для выгрузки и шаблона.</summary>
    private async Task<List<IReadOnlyList<string>>> ExportBundlesAsync(long org, CancellationToken ct) =>
        (await (from c in db.BundleComponents.AsNoTracking()
                join b in db.Items.AsNoTracking() on c.BundleItemId equals b.Id
                join i in db.Items.AsNoTracking() on c.ComponentItemId equals i.Id
                where c.OrganizationId == org && !b.IsArchived
                orderby b.Code, c.SortOrder, c.Id
                select new { Bundle = b.Code, Component = i.Code, c.Quantity }).ToListAsync(ct))
        .Select(x => (IReadOnlyList<string>)[x.Bundle, x.Component, Number(x.Quantity)]).ToList();
}
