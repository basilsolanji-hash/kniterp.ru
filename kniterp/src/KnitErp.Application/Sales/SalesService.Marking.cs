using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record MarkingCodeDto(long Id, long ItemId, string Gtin, string Serial, bool IsSold)
{
    public string Cis => "01" + Gtin + "21" + Serial;
}

/// <summary>Маркируемая строка документа: сколько кодов нужно (количество строки) и какие отсканированы.</summary>
public sealed record MarkingLineDto(long ItemId, string Code, string Name, string? Gtin, decimal Quantity, IReadOnlyList<MarkingCodeDto> Codes)
{
    public bool Complete => Codes.Count == Quantity;
}

/// <summary>Коды маркировки складского документа (D1108). CanEdit — черновик и право «коды маркировки» в области склада.</summary>
public sealed record DocumentMarkingDto(long DocumentId, StockOperationKind Kind, bool CanEdit, bool Posted, IReadOnlyList<MarkingLineDto> Lines)
{
    public bool Complete => Lines.All(l => l.Complete);
}

public sealed partial class SalesService
{
    /// <summary>
    /// Коды маркировки отгрузки или возврата (D1108); null — у документа нет маркируемых строк и кодов. Смотреть — кто видит
    /// документ (складские права в области склада) или продажи.
    /// </summary>
    public async Task<DocumentMarkingDto?> MarkingAsync(long documentId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var doc = await VisibleDocumentAsync(ctx, documentId, ct);
        if (doc.Kind is not (StockOperationKind.Shipment or StockOperationKind.CustomerReturn))
        {
            return null;
        }

        var marked = await SalesMarkingRules.MarkedItemsAsync(db, ctx.OrganizationId, doc.Lines.Select(l => l.ItemId), ct);
        var codes = await db.SalesMarkingCodes.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == documentId)
            .OrderBy(c => c.Id).Select(c => new MarkingCodeDto(c.Id, c.ItemId, c.Gtin, c.Serial, c.IsSold)).ToListAsync(ct);
        if (marked.Count == 0 && codes.Count == 0)
        {
            return null;
        }

        var itemIds = doc.Lines.Select(l => l.ItemId).Union(codes.Select(c => c.ItemId)).ToList();
        var names = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.Code, i.Name, i.Gtin), ct);
        var lines = doc.Lines.Where(l => marked.ContainsKey(l.ItemId))
            .Select(l => new MarkingLineDto(l.ItemId, names[l.ItemId].Code, names[l.ItemId].Name, names[l.ItemId].Gtin, l.Quantity,
                codes.Where(c => c.ItemId == l.ItemId).ToList()))
            .Concat(codes.Where(c => !marked.ContainsKey(c.ItemId) || doc.Lines.All(l => l.ItemId != c.ItemId)).GroupBy(c => c.ItemId)
                .Select(g => new MarkingLineDto(g.Key, names[g.Key].Code, names[g.Key].Name, names[g.Key].Gtin, 0, g.ToList())))
            .OrderBy(l => l.Code).ToList();
        return new DocumentMarkingDto(doc.Id, doc.Kind, doc.Status == StockDocumentStatus.Draft && CanMark(ctx, doc.WarehouseId),
            doc.Status == StockDocumentStatus.Posted, lines);
    }

    /// <summary>
    /// Сканирование кода DataMatrix в черновик отгрузки или возврата (D1108): GTIN кода — GTIN маркируемой позиции документа,
    /// кодов не больше количества строки, код не повторяется; в отгрузке — ещё не продан, в возврате — продан по этому заказу.
    /// Возвращает код позиции, к которой добавлен код.
    /// </summary>
    public async Task<string> ScanMarkingCodeAsync(long documentId, string? raw, CancellationToken ct = default)
    {
        var (ctx, doc) = await MarkingDraftAsync(documentId, ct);
        var parts = MarkingCodes.Parse(raw);
        var marked = await SalesMarkingRules.MarkedItemsAsync(db, ctx.OrganizationId, doc.Lines.Select(l => l.ItemId), ct);
        if (marked.Count == 0)
        {
            throw new BusinessRuleException("sales.marking.not_marked", "В документе нет маркируемых позиций — коды не нужны.");
        }

        var itemId = marked.Where(m => m.Value.Gtin == parts.Gtin).Select(m => (long?)m.Key).FirstOrDefault()
                     ?? throw new BusinessRuleException("sales.marking.gtin_mismatch",
                         $"GTIN кода {parts.Gtin} не совпадает с GTIN ни одной маркируемой позиции документа.");
        var line = doc.Lines.Single(l => l.ItemId == itemId);
        var existing = await db.SalesMarkingCodes.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == documentId)
            .Select(c => new { c.ItemId, c.Gtin, c.Serial }).ToListAsync(ct);
        if (existing.Any(c => c.Gtin == parts.Gtin && c.Serial == parts.Serial))
        {
            throw new BusinessRuleException("sales.marking.duplicate", "Этот код уже отсканирован в документе.");
        }

        if (existing.Count(c => c.ItemId == itemId) >= line.Quantity)
        {
            throw new BusinessRuleException("sales.marking.enough",
                $"По позиции {marked[itemId].Code} уже отсканировано {Quantities.Format(line.Quantity)} кодов — столько, сколько в строке.");
        }

        var sold = await db.SalesMarkingCodes.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.IsSold && c.Gtin == parts.Gtin && c.Serial == parts.Serial)
            .Select(c => (long?)c.StockDocumentId).FirstOrDefaultAsync(ct);
        if (doc.Kind == StockOperationKind.Shipment && sold is { } soldIn)
        {
            var number = await db.StockDocuments.AsNoTracking().Where(d => d.Id == soldIn).Select(d => d.Number).SingleAsync(ct);
            throw new BusinessRuleException("sales.marking.sold_twice", $"Код уже продан — отгрузка {number}. Один код продаётся один раз.");
        }

        if (doc.Kind == StockOperationKind.CustomerReturn
            && (sold is null || (doc.SalesOrderId is { } orderId
                                 && !await db.StockDocuments.AnyAsync(d => d.Id == sold && d.SalesOrderId == orderId, ct))))
        {
            throw new BusinessRuleException("sales.marking.not_sold", "Этот код не отгружался покупателю по этому заказу — его нельзя принять в возврат.");
        }

        db.SalesMarkingCodes.Add(SalesMarkingCode.Create(ctx.OrganizationId, documentId, itemId, parts, ctx.UserId, clock.UtcNow));
        Audit(ctx, AuditActions.SalesMarkingChanged, nameof(StockDocument), documentId, null, parts.Cis, $"{doc.Number}: {marked[itemId].Code}");
        await db.SaveChangesAsync(ct);
        return marked[itemId].Code;
    }

    public async Task RemoveMarkingCodeAsync(long documentId, long codeId, CancellationToken ct = default)
    {
        var (ctx, doc) = await MarkingDraftAsync(documentId, ct);
        var code = await db.SalesMarkingCodes.SingleOrDefaultAsync(c => c.Id == codeId && c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == documentId, ct)
                   ?? throw new NotFoundException("Код маркировки");
        db.SalesMarkingCodes.Remove(code);
        Audit(ctx, AuditActions.SalesMarkingChanged, nameof(StockDocument), documentId, code.Cis, "код удалён", doc.Number);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Коды маркировки отгрузки по строкам — для XML УПД (сведения о маркировке в строке): ключ — № строки так, как её нумерует УПД
    /// (строки по коду позиции, с 1), значение — коды идентификации (КИ) этой строки. Строк без кодов в словаре нет. У проведённой
    /// отгрузки — проданные по ней коды; у черновика — отсканированные.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> CodesForShipmentAsync(long stockDocumentId, CancellationToken ct = default)
    {
        var (doc, codes) = await ShipmentCodesAsync(stockDocumentId, ct);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var order = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => new { i.Id, i.Code }).ToListAsync(ct);
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var no = 0;
        foreach (var item in order.OrderBy(i => i.Code))
        {
            no++;
            var list = codes.Where(c => c.ItemId == item.Id).Select(c => c.Cis).ToList();
            if (list.Count > 0)
            {
                result[no] = list;
            }
        }

        return result;
    }

    /// <summary>Коды отгрузки списком (для экрана и выгрузки CSV).</summary>
    private async Task<(StockDocument Doc, IReadOnlyList<MarkingCodeDto> Codes)> ShipmentCodesAsync(long shipmentId, CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        var doc = await VisibleDocumentAsync(ctx, shipmentId, ct);
        if (doc.Kind != StockOperationKind.Shipment)
        {
            throw new BusinessRuleException("sales.marking.not_shipment", "Коды выгружаются по отгрузке.");
        }

        var codes = await db.SalesMarkingCodes.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == shipmentId && (c.IsSold || doc.Status == StockDocumentStatus.Draft))
            .OrderBy(c => c.ItemId).ThenBy(c => c.Id)
            .Select(c => new MarkingCodeDto(c.Id, c.ItemId, c.Gtin, c.Serial, c.IsSold)).ToListAsync(ct);
        return (doc, codes);
    }

    /// <summary>
    /// CSV для вывода из оборота / передачи по ЭДО (D1108): одна строка — один код идентификации, разделитель «;», UTF-8.
    /// Колонки: КИ, GTIN, серийный номер, код позиции, наименование, документ, дата. Без цен. API «Честного ЗНАКа» не вызывается.
    /// </summary>
    public async Task<(byte[] Content, string FileName)> MarkingCsvAsync(long shipmentId, CancellationToken ct = default)
    {
        var (doc, codes) = await ShipmentCodesAsync(shipmentId, ct);
        if (doc.Status != StockDocumentStatus.Posted)
        {
            throw new BusinessRuleException("sales.marking.not_posted", "Выгрузка кодов — по проведённой отгрузке.");
        }

        var itemIds = codes.Select(c => c.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.Code, i.Name), ct);
        var sb = new StringBuilder();
        sb.Append("КИ;GTIN;Серийный номер;Код позиции;Наименование;Документ;Дата\r\n");
        foreach (var c in codes)
        {
            sb.Append(Csv(c.Cis)).Append(';').Append(c.Gtin).Append(';').Append(Csv(c.Serial)).Append(';')
                .Append(Csv(items[c.ItemId].Code)).Append(';').Append(Csv(items[c.ItemId].Name)).Append(';')
                .Append(Csv(doc.Number)).Append(';').Append(doc.DocumentDate.ToString("dd.MM.yyyy")).Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return (bytes, $"marking-{doc.Number}.csv");

        // Значения в кавычках, если в них разделитель или кавычка; формулы Excel («=», «+», «-», «@») экранируются апострофом.
        static string Csv(string value)
        {
            var v = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
            return v.Contains(';') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }

    private static bool CanMark(AccessContext ctx, long warehouseId) => WarehouseScope.Covers(ctx, Permissions.SalesMarkingEdit, warehouseId);

    /// <summary>Черновик отгрузки или возврата, который пользователь вправе сканировать: право маркировки в области склада.</summary>
    private async Task<(AccessContext, StockDocument)> MarkingDraftAsync(long documentId, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesMarkingEdit, ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!CanMark(ctx, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        if (doc.Kind is not (StockOperationKind.Shipment or StockOperationKind.CustomerReturn))
        {
            throw new BusinessRuleException("sales.marking.kind", "Коды маркировки сканируются в отгрузку и возврат от покупателя.");
        }

        if (doc.Status != StockDocumentStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Документ уже не черновик — изменения запрещены.");
        }

        return (ctx, doc);
    }

    /// <summary>Складской документ, который пользователь видит: складские права в области склада или продажи (документ по заказу).</summary>
    private async Task<StockDocument> VisibleDocumentAsync(AccessContext ctx, long documentId, CancellationToken ct)
    {
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        var byWarehouse = WarehouseScope.StockViews.Append(Permissions.SalesMarkingEdit).Any(p => WarehouseScope.Covers(ctx, p, doc.WarehouseId));
        if (byWarehouse || (doc.SalesOrderId is not null && ctx.Permissions.Has(Permissions.SalesView)))
        {
            return doc;
        }

        if (!WarehouseScope.StockViews.Any(ctx.Permissions.Has) && !ctx.Permissions.Has(Permissions.SalesView))
        {
            await guard.DenyAsync(ctx, Permissions.SalesView, ct);
        }

        throw new NotFoundException("Документ");
    }
}
