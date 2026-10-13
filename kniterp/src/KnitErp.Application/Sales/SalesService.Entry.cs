using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Повтор заказа (А3): новый черновик и что не перенесено (архивные позиции, без цены), чего не хватает на складе.</summary>
public sealed record RepeatOrderResult(long OrderId, IReadOnlyList<string> Skipped, IReadOnlyList<string> Short);

/// <summary>Строка вставки из Excel или буфера (А4): распознанная позиция и количество или причина ошибки.</summary>
public sealed record PasteLineDto(int Row, string Text, long? ItemId, string? Code, string? Name, decimal? Quantity, decimal? Price, string? Error);

/// <summary>Ячейка сетки «размер × цвет»: модификация, остаток (null — нет складских прав), цена по правилам подстановки.</summary>
public sealed record SalesGridCellDto(string Row, string Col, long ItemId, string Code, decimal? Stock, decimal? Price);

public sealed record SalesGridDto(long ParentId, string Code, string Name, string? RowAxis, string? ColAxis, IReadOnlyList<string> Rows,
    IReadOnlyList<string> Cols, IReadOnlyList<SalesGridCellDto> Cells);

/// <summary>Позиция в подборе: остаток и резерв на складе заказа (null — нет складских прав), ожидаемый выпуск производства, цена.</summary>
public sealed record SalesPickItemDto(
    long ItemId, string Code, string Name, string? Article, string UnitSymbol, decimal? Stock, decimal? Reserved, decimal? Expected, DateOnly? ExpectedDate,
    decimal? Price, bool HasModifications);

/// <summary>Значения по умолчанию нового заказа (А5): из соглашения покупателя и его последнего заказа.</summary>
public sealed record SalesOrderDefaultsDto(
    long? WarehouseId, long? LegalEntityId, string? CustomerReference, long? ResponsibleUserId, string? Agreement, string? PaymentTerms);

/// <summary>Расчётная дата отгрузки: Date null — нет данных (не хватает товара и нет производства); Late — позже обещанной.</summary>
public sealed record ShipEstimateDto(DateOnly? Date, bool Late, string Basis);

public sealed partial class SalesService
{
    public const int MaxPasteRows = SalesOrder.MaxLines;

    /// <summary>
    /// А3 (D1130): «Создать по образцу» — новый черновик того же покупателя, склада и юрлица с теми же позициями и количествами;
    /// цены и скидки — актуальные по правилам (соглашение → вид цены клиента → основной, D1102, D1106). Архивные позиции и позиции без
    /// цены пропускаются — перечень в ответе; чего не хватает на складе (при складских правах) — тоже.
    /// </summary>
    public async Task<RepeatOrderResult> RepeatOrderAsync(long sourceId, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var source = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                         .SingleOrDefaultAsync(o => o.Id == sourceId && o.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Заказ покупателя");
        var today = await TodayAsync(ctx, ct);
        var header = await ValidateHeaderAsync(ctx,
            new SalesOrderHeader(today, source.CustomerId, source.WarehouseId, null, source.CustomerReference, source.PricesIncludeVat, null, source.LegalEntityId),
            null, ct);
        var itemIds = source.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new { i.Code, i.IsArchived }, ct);
        var quotes = await SalesPricing.QuoteAsync(db, ctx.OrganizationId, source.CustomerId, today, source.PricesIncludeVat, itemIds,
            (await SalesPricing.AgreementForAsync(db, ctx.OrganizationId, source.CustomerId, today, ct))?.Id, ct);
        var skipped = new List<string>();

        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SalesOrder.NumberPrefix, ct);
        var order = SalesOrder.Create(ctx.OrganizationId, number, header, ctx.UserId, clock.UtcNow);
        order.SetAgreement(quotes.AgreementId);
        foreach (var line in source.Lines)
        {
            var item = items[line.ItemId];
            if (item.IsArchived)
            {
                skipped.Add($"{item.Code}: в архиве");
                continue;
            }

            if (!quotes.Items.TryGetValue(line.ItemId, out var q) || q.Price is not { } price)
            {
                skipped.Add(q?.NoRate == true ? $"{item.Code}: нет курса {q.Conversion}" : $"{item.Code}: нет цены"); // D151
                continue;
            }

            order.SetLine(line.ItemId, line.Quantity, price, q.VatPercent, line.ManualDiscountPercent, q.Conversion);
        }

        await SalesDiscounts.ApplyAsync(db, ctx.OrganizationId, order, ct);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesOrderCreated, nameof(SalesOrder), order.Id, null, $"{order.Number} по образцу {source.Number}",
            skipped.Count == 0 ? null : "не перенесено: " + string.Join(", ", skipped));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var shortages = new List<string>();
        var stock = await StockByItemAsync(ctx, order.WarehouseId, order.Lines.Select(l => l.ItemId).ToList(), ct);
        if (stock is not null)
        {
            foreach (var line in order.Lines.Where(l => stock.GetValueOrDefault(l.ItemId) < l.Quantity))
            {
                shortages.Add($"{items[line.ItemId].Code}: {Quantities.Format(line.Quantity - Math.Max(0, stock.GetValueOrDefault(line.ItemId)))}");
            }
        }

        return new RepeatOrderResult(order.Id, skipped, shortages);
    }

    /// <summary>
    /// А4 (D1131): разбор строк из Excel или буфера: «артикул/код/штрихкод ⇥ количество», «… ⇥ количество ⇥ цена»,
    /// «… ⇥ размер ⇥ цвет ⇥ количество [⇥ цена]» (разделитель — табуляция или «;»). Первая строка без числа количества — заголовок.
    /// Позиция с модификациями и размером/цветом — нужная модификация. Ничего не сохраняет.
    /// </summary>
    public async Task<IReadOnlyList<PasteLineDto>> PreviewPasteAsync(long orderId, string? text, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        await LoadOrderAsync(ctx, orderId, ct);
        return await ParsePasteAsync(ctx, text, ct);
    }

    /// <summary>Добавить в черновик верные строки вставки (цена — указанная или по правилам); возвращает все строки с ошибками и итогом.</summary>
    public async Task<IReadOnlyList<PasteLineDto>> AddPastedLinesAsync(long orderId, string? text, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(orderId, rowVersion, ct);
        var rows = await ParsePasteAsync(ctx, text, ct);
        var good = rows.Where(r => r.Error is null).ToList();
        if (good.Count == 0)
        {
            return rows;
        }

        var quotes = await SalesPricing.QuoteAsync(db, ctx.OrganizationId, order.CustomerId, order.OrderDate, order.PricesIncludeVat,
            good.Select(r => r.ItemId!.Value).Distinct().ToList(), order.AgreementId, ct);
        var result = new List<PasteLineDto>();
        foreach (var r in rows)
        {
            if (r.Error is not null)
            {
                result.Add(r);
                continue;
            }

            var q = quotes.Items[r.ItemId!.Value];
            if ((r.Price ?? q.Price) is not { } price)
            {
                result.Add(r with { Error = "нет цены — укажите её в строке" });
                continue;
            }

            var existing = order.Lines.FirstOrDefault(l => l.ItemId == r.ItemId);
            if (existing is null && order.Lines.Count >= SalesOrder.MaxLines)
            {
                result.Add(r with { Error = $"в заказе не больше {SalesOrder.MaxLines} строк" });
                continue;
            }

            order.SetLine(r.ItemId.Value, (existing?.Quantity ?? 0) + r.Quantity!.Value, price, existing?.VatPercent ?? q.VatPercent,
                existing?.ManualDiscountPercent ?? 0, r.Price is null ? q.Conversion : null);
            result.Add(r with { Price = price });
        }

        await SalesDiscounts.ApplyAsync(db, ctx.OrganizationId, order, ct);
        var added = result.Count(r => r.Error is null);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), orderId, null, $"вставлено строк: {added}", $"{order.Number}: вставка из Excel");
        await db.SaveOrConflictAsync(ct);
        return result;
    }

    /// <summary>А4: строки листа Excel как текст для вставки (колонки — через табуляцию).</summary>
    public static string SheetToText(IReadOnlyList<IReadOnlyList<string>> rows) =>
        string.Join('\n', rows.Take(MaxPasteRows + 1).Select(r => string.Join('\t', r)));

    private async Task<IReadOnlyList<PasteLineDto>> ParsePasteAsync(AccessContext ctx, string? text, CancellationToken ct)
    {
        var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n')
            .Select((t, i) => (Row: i + 1, Text: t.Trim())).Where(l => l.Text.Length > 0).ToList();
        if (lines.Count == 0)
        {
            throw new BusinessRuleException("sales.paste.empty", "Вставьте строки: артикул или штрихкод и количество через табуляцию или «;».");
        }

        if (lines.Count > MaxPasteRows + 1)
        {
            throw new BusinessRuleException("sales.paste.too_many", $"За один раз — не больше {MaxPasteRows} строк.");
        }

        var parsed = lines.Select(l => (l.Row, l.Text, Cells: l.Text.Split(l.Text.Contains('\t') ? '\t' : ';').Select(c => c.Trim()).ToArray())).ToList();
        // Заголовок: в первой строке нет числа ни в одной колонке, кроме первой.
        if (parsed.Count > 0 && parsed[0].Cells.Skip(1).All(c => !Number(c, out _)))
        {
            parsed.RemoveAt(0);
        }

        var codes = parsed.Select(p => p.Cells[0]).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var org = ctx.OrganizationId;
        var byCode = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && (codes.Contains(i.Code) || (i.Article != null && codes.Contains(i.Article))))
            .Select(i => new { i.Id, i.Code, i.Name, i.Article, i.IsArchived, i.ParentItemId }).ToListAsync(ct);
        var byBarcode = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && codes.Contains(b.Code))
            .Join(db.Items.AsNoTracking(), b => b.ItemId, i => i.Id, (b, i) => new { b.Code, i.Id, ICode = i.Code, i.Name, i.IsArchived, i.ParentItemId })
            .ToListAsync(ct);
        var parentIds = byCode.Select(i => i.Id).Concat(byBarcode.Select(b => b.Id)).Distinct().ToList();
        var mods = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && i.ParentItemId != null && parentIds.Contains(i.ParentItemId.Value) && !i.IsArchived)
            .Select(i => new { i.Id, i.Code, i.Name, Parent = i.ParentItemId!.Value }).ToListAsync(ct);
        var modIds = mods.Select(m => m.Id).ToList();
        var values = (await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == org && modIds.Contains(v.ItemId))
                .Select(v => new { v.ItemId, v.Value }).ToListAsync(ct))
            .ToLookup(v => v.ItemId, v => v.Value.ToLowerInvariant());

        var result = new List<PasteLineDto>();
        foreach (var (row, rowText, cells) in parsed)
        {
            PasteLineDto Fail(string error) => new(row, rowText, null, null, null, null, null, error);
            var code = cells[0];
            if (code.Length == 0)
            {
                result.Add(Fail("нет артикула или кода"));
                continue;
            }

            // Колонки: [код, кол-во], [код, кол-во, цена], [код, размер, кол-во], [код, размер, цвет, кол-во, (цена)].
            string[] attrs;
            string qtyText;
            string? priceText = null;
            switch (cells.Length)
            {
                case 1:
                    result.Add(Fail("нет количества"));
                    continue;
                case 2:
                    (attrs, qtyText) = ([], cells[1]);
                    break;
                case 3 when Number(cells[1], out _) && Number(cells[2], out _):
                    (attrs, qtyText, priceText) = ([], cells[1], cells[2]);
                    break;
                case 3:
                    (attrs, qtyText) = ([cells[1]], cells[2]);
                    break;
                default:
                    (attrs, qtyText, priceText) = ([cells[1], cells[2]], cells[3], cells.Length > 4 ? cells[4] : null);
                    break;
            }

            if (!Number(qtyText, out var qty) || qty <= 0)
            {
                result.Add(Fail($"количество «{qtyText}» — не число больше нуля"));
                continue;
            }

            decimal? price = null;
            if (!string.IsNullOrWhiteSpace(priceText))
            {
                if (!Number(priceText, out var p) || p < 0)
                {
                    result.Add(Fail($"цена «{priceText}» — не число"));
                    continue;
                }

                price = p;
            }

            var found = byCode.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase))
                        ?? byCode.FirstOrDefault(i => string.Equals(i.Article, code, StringComparison.OrdinalIgnoreCase));
            var item = found is not null ? (found.Id, found.Code, found.Name, found.IsArchived)
                : byBarcode.FirstOrDefault(b => b.Code == code) is { } bc ? (bc.Id, bc.ICode, bc.Name, bc.IsArchived)
                : ((long Id, string Code, string Name, bool IsArchived)?)null;
            if (item is not { } it)
            {
                result.Add(Fail($"позиция «{code}» не найдена"));
                continue;
            }

            if (it.IsArchived)
            {
                result.Add(Fail($"позиция {it.Code} в архиве"));
                continue;
            }

            var wanted = attrs.Where(a => a.Length > 0).Select(a => a.ToLowerInvariant()).ToList();
            var own = mods.Where(m => m.Parent == it.Id).ToList();
            if (wanted.Count > 0 || own.Count > 0)
            {
                if (own.Count == 0)
                {
                    result.Add(Fail($"у позиции {it.Code} нет модификаций (размер, цвет)"));
                    continue;
                }

                var matches = own.Where(m => wanted.All(w => values[m.Id].Contains(w))).ToList();
                if (wanted.Count == 0 || matches.Count != 1)
                {
                    result.Add(Fail(matches.Count == 0 ? $"у {it.Code} нет модификации «{string.Join(", ", attrs)}»"
                        : $"у {it.Code} несколько модификаций — укажите размер и цвет"));
                    continue;
                }

                it = (matches[0].Id, matches[0].Code, matches[0].Name, false);
            }

            result.Add(new PasteLineDto(row, rowText, it.Id, it.Code, it.Name, qty, price, null));
        }

        return result;

        static bool Number(string text, out decimal value) =>
            decimal.TryParse(text.Replace(" ", string.Empty).Replace(' ', ' ').Replace(" ", string.Empty).Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Сетка «размер × цвет» модели (D1132): оси — характеристики модификаций (по названиям «Размер»/«Цвет», иначе первые две).</summary>
    public async Task<SalesGridDto> GridAsync(long orderId, long parentItemId, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var org = ctx.OrganizationId;
        var parent = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == parentItemId && i.OrganizationId == org, ct)
                     ?? throw new NotFoundException("Номенклатура");
        var mods = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && i.ParentItemId == parentItemId && !i.IsArchived)
            .Select(i => new { i.Id, i.Code }).ToListAsync(ct);
        if (mods.Count == 0)
        {
            throw new BusinessRuleException("sales.grid.no_variants", $"У позиции {parent.Code} нет модификаций — добавьте её обычной строкой.");
        }

        var ids = mods.Select(m => m.Id).ToList();
        var values = await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == org && ids.Contains(v.ItemId))
            .Select(v => new { v.ItemId, v.CharacteristicId, v.Value }).ToListAsync(ct);
        var axes = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == org && values.Select(v => v.CharacteristicId).Contains(c.Id))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var size = axes.FirstOrDefault(a => a.Name.Contains("азмер", StringComparison.OrdinalIgnoreCase));
        var color = axes.FirstOrDefault(a => a.Name.Contains("вет", StringComparison.OrdinalIgnoreCase) && a.Id != size?.Id);
        var colAxis = size ?? axes.FirstOrDefault();
        var rowAxis = color ?? axes.FirstOrDefault(a => a.Id != colAxis?.Id);
        string Value(long itemId, long? axis) => axis is { } a ? values.FirstOrDefault(v => v.ItemId == itemId && v.CharacteristicId == a)?.Value ?? "—" : "";

        var stock = await StockByItemAsync(ctx, order.WarehouseId, ids, ct);
        var quotes = await SalesPricing.QuoteAsync(db, org, order.CustomerId, order.OrderDate, order.PricesIncludeVat, ids, order.AgreementId, ct);
        var cells = mods.Select(m => new SalesGridCellDto(Value(m.Id, rowAxis?.Id), Value(m.Id, colAxis?.Id), m.Id, m.Code,
            stock?.GetValueOrDefault(m.Id), quotes.Items.GetValueOrDefault(m.Id)?.Price)).ToList();
        return new SalesGridDto(parent.Id, parent.Code, parent.Name, rowAxis?.Name, colAxis?.Name,
            cells.Select(c => c.Row).Distinct().OrderBy(SortKey).ToList(), cells.Select(c => c.Col).Distinct().OrderBy(SortKey).ToList(), cells);

        // Размеры «44, 46, 48» — по числу, остальное — по алфавиту.
        static string SortKey(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString("000000.00", CultureInfo.InvariantCulture) : "~" + v;
    }

    /// <summary>Добавить ячейки сетки одной операцией: itemId → количество (нулевые пропускаются); цена — по правилам подстановки.</summary>
    public async Task<int> AddGridAsync(long orderId, IReadOnlyDictionary<long, decimal> quantities, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(orderId, rowVersion, ct);
        var wanted = quantities.Where(q => q.Value > 0).ToDictionary(q => q.Key, q => q.Value);
        if (wanted.Count == 0)
        {
            throw new BusinessRuleException("sales.grid.empty", "Укажите количество хотя бы в одной ячейке.");
        }

        var ids = wanted.Keys.ToList();
        if (ids.Count > Variants.MaxPerBatch)
        {
            throw new BusinessRuleException("sales.paste.too_many", $"За один раз — не больше {Variants.MaxPerBatch} строк.");
        }

        var items = new List<EntryItemDto>();
        foreach (var id in ids)
        {
            items.Add(await StockEntry.ActiveItems(db, ctx.OrganizationId, id).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Номенклатура"));
        }

        var quotes = await SalesPricing.QuoteAsync(db, ctx.OrganizationId, order.CustomerId, order.OrderDate, order.PricesIncludeVat, ids, order.AgreementId, ct);
        foreach (var item in items)
        {
            StockEntry.EnsurePrecision(item, wanted[item.Id]);
            var q = quotes.Items[item.Id];
            var price = q.Price ?? throw (q.NoRate
                ? new BusinessRuleException("currency.rate.missing", $"Цена позиции {item.Code} — в {q.Conversion}, а курса этой валюты нет: введите курс в «Курсах валют».")
                : new BusinessRuleException("sales.price.missing", $"У позиции {item.Code} нет цены ни в одном виде цены — введите цену вручную."));
            var existing = order.Lines.FirstOrDefault(l => l.ItemId == item.Id);
            order.SetLine(item.Id, (existing?.Quantity ?? 0) + wanted[item.Id], price, existing?.VatPercent ?? q.VatPercent, existing?.ManualDiscountPercent ?? 0,
                q.Conversion);
        }

        await SalesDiscounts.ApplyAsync(db, ctx.OrganizationId, order, ct);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), orderId, null, $"по сетке добавлено строк: {items.Count}", $"{order.Number}: сетка");
        await db.SaveOrConflictAsync(ct);
        return items.Count;
    }

    /// <summary>
    /// Подбор позиций (D1133): поиск по коду, названию, артикулу, штрихкоду; остаток и резерв — на складе заказа и только при складских
    /// правах (D118); ожидаемый выпуск — незавершённые производственные заказы позиции; цена — по правилам подстановки.
    /// </summary>
    public async Task<IReadOnlyList<SalesPickItemDto>> PickItemsAsync(long orderId, string? search, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var org = ctx.OrganizationId;
        var text = (search ?? string.Empty).Trim();
        var q = db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived);
        if (text.Length > 0)
        {
            q = q.Where(i => i.Code.Contains(text) || i.Name.Contains(text) || (i.Article != null && i.Article.Contains(text))
                             || db.ItemBarcodes.Any(b => b.ItemId == i.Id && b.Code == text));
        }

        var items = await q.OrderBy(i => i.Code).Take(30)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, i.Article, u.Symbol })
            .ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var parents = await db.Items.AsNoTracking().Where(i => i.ParentItemId != null && ids.Contains(i.ParentItemId.Value)).Select(i => i.ParentItemId!.Value)
            .Distinct().ToListAsync(ct);
        var stock = await StockByItemAsync(ctx, order.WarehouseId, ids, ct);
        var reserved = stock is null ? null : await Reservations.ByWarehouseItemAsync(db, org, [order.WarehouseId], ids, order.Id, ct);
        var expected = await ExpectedOutputAsync(org, ids, ct);
        var quotes = await SalesPricing.QuoteAsync(db, org, order.CustomerId, order.OrderDate, order.PricesIncludeVat, ids, order.AgreementId, ct);
        return items.Select(i => new SalesPickItemDto(i.Id, i.Code, i.Name, i.Article, i.Symbol, stock?.GetValueOrDefault(i.Id),
            reserved?.GetValueOrDefault((order.WarehouseId, i.Id)), expected.GetValueOrDefault(i.Id).Quantity is var e and > 0 ? e : null,
            expected.GetValueOrDefault(i.Id).Date, quotes.Items.GetValueOrDefault(i.Id)?.Price, parents.Contains(i.Id))).ToList();
    }

    /// <summary>
    /// А5 (D1134): значения по умолчанию нового заказа — склад и договор из действующего соглашения, иначе из последнего заказа покупателя;
    /// юрлицо — из последнего заказа; ответственный — закреплённый менеджер покупателя.
    /// </summary>
    public async Task<SalesOrderDefaultsDto> DefaultsForCustomerAsync(long customerId, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var org = ctx.OrganizationId;
        if (!await db.Counterparties.AnyAsync(c => c.Id == customerId && c.OrganizationId == org, ct))
        {
            throw new NotFoundException("Контрагент");
        }

        var agreement = await SalesPricing.AgreementForAsync(db, org, customerId, await TodayAsync(ctx, ct), ct);
        var last = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.CustomerId == customerId && o.Status != SalesOrderStatus.Cancelled)
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .Select(o => new { o.WarehouseId, o.LegalEntityId, o.CustomerReference }).FirstOrDefaultAsync(ct);
        string? contract = null;
        if (agreement?.ContractId is { } kid)
        {
            contract = await db.CounterpartyContracts.AsNoTracking().Where(k => k.Id == kid && k.OrganizationId == org)
                .Select(k => "Договор № " + k.Number + " от " + k.ContractDate.ToString()).SingleOrDefaultAsync(ct);
            if (await db.CounterpartyContracts.AsNoTracking().SingleOrDefaultAsync(k => k.Id == kid && k.OrganizationId == org, ct) is { } k2)
            {
                contract = $"Договор № {k2.Number} от {k2.ContractDate:dd.MM.yyyy}";
            }
        }
        else if (await db.CounterpartyContracts.AsNoTracking()
                     .Where(k => k.OrganizationId == org && k.CounterpartyId == customerId && k.Status == ContractStatus.Active && k.Kind == ContractKind.Sale)
                     .OrderByDescending(k => k.ContractDate).FirstOrDefaultAsync(ct) is { } active)
        {
            contract = $"Договор № {active.Number} от {active.ContractDate:dd.MM.yyyy}";
        }

        var manager = await db.CustomerSalesProfiles.AsNoTracking().Where(p => p.OrganizationId == org && p.CounterpartyId == customerId)
            .Select(p => p.ManagerUserId).SingleOrDefaultAsync(ct);
        var terms = agreement is null ? null
            : string.Join(", ", new[]
            {
                agreement.PrepaymentPercent is > 0 ? $"предоплата {agreement.PrepaymentPercent:0.##}%" : null,
                agreement.PaymentDays is { } d ? $"отсрочка {d} дн." : null,
            }.Where(x => x is not null));
        return new SalesOrderDefaultsDto(agreement?.WarehouseId ?? last?.WarehouseId, last?.LegalEntityId, contract ?? last?.CustomerReference, manager,
            agreement is null ? null : $"{agreement.Number} «{agreement.Name}»", string.IsNullOrEmpty(terms) ? null : terms);
    }

    /// <summary>
    /// Расчётная дата отгрузки (D1135): всё есть на складе — сегодня; не хватает — самая поздняя из дат производства недостающих позиций;
    /// нет производства — нет данных. Late — позже обещанной клиенту даты. Без складских прав — нет данных.
    /// </summary>
    public async Task<ShipEstimateDto> ShipEstimateAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var ids = order.Lines.Select(l => l.ItemId).ToList();
        var stock = await StockByItemAsync(ctx, order.WarehouseId, ids, ct);
        if (stock is null)
        {
            return new ShipEstimateDto(null, false, "нет прав на остатки склада");
        }

        var moved = await MovedAsync(ctx, [orderId], ct);
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, ids, ct);
        var reserved = await Reservations.ByWarehouseItemAsync(db, ctx.OrganizationId, [order.WarehouseId], ids, order.Id, ct);
        var shortIds = order.Lines.Where(l => !services.Contains(l.ItemId))
            .Where(l => l.Quantity - Net(moved, orderId, l.ItemId) > stock.GetValueOrDefault(l.ItemId) - reserved.GetValueOrDefault((order.WarehouseId, l.ItemId)))
            .Select(l => l.ItemId).ToList();
        var today = await TodayAsync(ctx, ct);
        if (shortIds.Count == 0)
        {
            return new ShipEstimateDto(today, order.ShipDate is { } p && today > p, "всё есть на складе");
        }

        var expected = await ExpectedOutputAsync(ctx.OrganizationId, shortIds, ct);
        if (shortIds.Any(id => expected.GetValueOrDefault(id).Date is null))
        {
            return new ShipEstimateDto(null, order.ShipDate is not null, "не хватает товара, производство не запланировано");
        }

        var date = shortIds.Max(id => expected[id].Date!.Value);
        return new ShipEstimateDto(date, order.ShipDate is { } promised && date > promised, "по сроку производства");
    }

    /// <summary>Незавершённые производственные заказы по позициям: количество и самый поздний срок (допущение D1133: выпуск без учёта частичного).</summary>
    private async Task<Dictionary<long, (decimal Quantity, DateOnly? Date)>> ExpectedOutputAsync(long org, IReadOnlyCollection<long> itemIds, CancellationToken ct) =>
        (await db.ProductionOrders.AsNoTracking()
            .Where(p => p.OrganizationId == org && itemIds.Contains(p.ItemId)
                        && (p.Status == ProductionOrderStatus.Draft || p.Status == ProductionOrderStatus.Released || p.Status == ProductionOrderStatus.PartiallyDone))
            .GroupBy(p => p.ItemId).Select(g => new { g.Key, Quantity = g.Sum(p => p.Quantity), Date = g.Max(p => p.DueDate) }).ToListAsync(ct))
        .ToDictionary(x => x.Key, x => (x.Quantity, x.Date));

    /// <summary>Остаток позиций на складе — только через видимые движения (D118); без складских прав или склад вне области — null.</summary>
    private async Task<Dictionary<long, decimal>?> StockByItemAsync(AccessContext ctx, long warehouseId, IReadOnlyCollection<long> itemIds, CancellationToken ct)
    {
        var movements = WarehouseScope.VisibleMovements(db, ctx);
        if (movements is null || (WarehouseScope.Visible(ctx, WarehouseScope.StockViews) is { } visible && !visible.Contains(warehouseId)))
        {
            return null;
        }

        return await movements.Where(m => m.WarehouseId == warehouseId && itemIds.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
    }
}
