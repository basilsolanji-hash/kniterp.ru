using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// Крючки отгрузки и возврата в проведении складских документов (D1108, D1151, D1153). Вызываются из
/// <see cref="StockDocumentService"/> внутри его транзакции; права на проведение он уже проверил.
/// </summary>
internal static class SalesShippingHooks
{
    /// <summary>Перед проведением: коды маркировки по маркируемым строкам и проверка брака возврата.</summary>
    public static async Task BeforePostAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, CancellationToken ct)
    {
        if (doc.Kind is not (StockOperationKind.Shipment or StockOperationKind.CustomerReturn))
        {
            return;
        }

        if (doc.Kind == StockOperationKind.Shipment && doc.SalesOrderId is { } orderId)
        {
            await EnsureWithinOrderAsync(db, ctx, doc, orderId, ct);
        }

        await SalesMarkingRules.EnsureCompleteAsync(db, ctx, doc, ct);
        if (doc.Kind == StockOperationKind.CustomerReturn
            && await db.SalesReturnDetails.Include(d => d.Lines).SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.StockDocumentId == doc.Id, ct)
                is { } details)
        {
            details.Validate(doc.WarehouseId, doc.Lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity))); // компонент может быть в нескольких комплектах (D352)
        }
    }

    /// <summary>
    /// После проведения (движения уже сохранены): коды отгрузки становятся проданными, коды возврата освобождаются; брак возврата
    /// перемещается в карантин и списывается — новыми документами в той же транзакции; консолидированная отгрузка, все документы
    /// которой проведены, отмечается отгруженной.
    /// </summary>
    public static async Task AfterPostAsync(
        IKnitErpDbContext db, AccessContext ctx, StockDocumentService documents, StockDocument doc, DateTime nowUtc, string? correlationId,
        CancellationToken ct)
    {
        if (doc.Kind is not (StockOperationKind.Shipment or StockOperationKind.CustomerReturn))
        {
            return;
        }

        var codes = await db.SalesMarkingCodes.Where(c => c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == doc.Id).ToListAsync(ct);
        if (doc.Kind == StockOperationKind.Shipment)
        {
            foreach (var code in codes)
            {
                code.MarkSold(nowUtc);
            }

            await SalesMarkingRules.SaveSoldAsync(db, ct);
            var link = await db.SalesShipmentGroupDocuments.AsNoTracking()
                .SingleOrDefaultAsync(l => l.OrganizationId == ctx.OrganizationId && l.StockDocumentId == doc.Id, ct);
            if (link is not null)
            {
                var group = await db.SalesShipmentGroups.Include(g => g.Documents).SingleAsync(g => g.Id == link.GroupId, ct);
                var ids = group.Documents.Select(d => d.StockDocumentId).ToList();
                if (group.Status == ShipmentGroupStatus.Draft
                    && !await db.StockDocuments.AnyAsync(d => ids.Contains(d.Id) && d.Id != doc.Id && d.Status != StockDocumentStatus.Posted, ct))
                {
                    group.MarkPosted(ctx.UserId, nowUtc);
                    db.AuditEntries.Add(AuditEntry.Create(nowUtc, ctx.OrganizationId, ctx.UserId, AuditActions.SalesShipmentGroupChanged,
                        nameof(SalesShipmentGroup), group.Id.ToString(), "Черновик", "Отгружено", group.Number, correlationId));
                    await db.SaveChangesAsync(ct);
                }
            }

            return;
        }

        // Возврат: проданные коды этих товаров снова свободны.
        foreach (var code in codes)
        {
            var sold = await db.SalesMarkingCodes.SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.IsSold
                                                                           && c.Gtin == code.Gtin && c.Serial == code.Serial, ct);
            sold?.Release(doc.Id, nowUtc);
        }

        await db.SaveChangesAsync(ct);
        await ProcessDefectsAsync(db, ctx, documents, doc, correlationId, nowUtc, ct);
    }

    /// <summary>
    /// Перед сторно (D1159, D1153): отгрузку заказа, по которому уже проведён возврат, не сторнируют — исправление отдельным документом;
    /// возврат с оформленным браком сторнируется только после сторно перемещения в карантин и списания.
    /// </summary>
    public static async Task BeforeReverseAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, CancellationToken ct)
    {
        if (doc.Kind == StockOperationKind.Shipment && doc.SalesOrderId is { } orderId
            && await db.StockDocuments.AnyAsync(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == orderId
                                                     && d.Kind == StockOperationKind.CustomerReturn && d.Status == StockDocumentStatus.Posted, ct))
        {
            throw new BusinessRuleException("sales.shipment.reverse_after_return",
                "По заказу уже оформлен возврат от покупателя — сторно отгрузки запрещено. Исправляйте отдельным документом возврата.");
        }

        if (doc.Kind != StockOperationKind.CustomerReturn)
        {
            return;
        }

        var details = await db.SalesReturnDetails.AsNoTracking()
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.StockDocumentId == doc.Id, ct);
        long?[] followUps = [details?.TransferDocumentId, details?.WriteOffDocumentId];
        if (await db.StockDocuments.AnyAsync(d => followUps.Contains(d.Id) && d.Status == StockDocumentStatus.Posted, ct))
        {
            throw new BusinessRuleException("sales.return.defects_posted",
                "По возврату оформлен брак — сначала сторнируйте перемещение в карантин и списание брака, затем сам возврат.");
        }
    }

    /// <summary>Сторно: отгрузки — её коды освобождаются; возврата — освобождённые им коды снова проданы.</summary>
    public static async Task AfterReverseAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, DateTime nowUtc, CancellationToken ct)
    {
        if (doc.Kind == StockOperationKind.Shipment)
        {
            foreach (var code in await db.SalesMarkingCodes.Where(c => c.OrganizationId == ctx.OrganizationId && c.StockDocumentId == doc.Id && c.IsSold)
                         .ToListAsync(ct))
            {
                code.Release(doc.Id, nowUtc);
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        if (doc.Kind != StockOperationKind.CustomerReturn)
        {
            return;
        }

        foreach (var code in await db.SalesMarkingCodes.Where(c => c.OrganizationId == ctx.OrganizationId && c.ReleasedByDocumentId == doc.Id && !c.IsSold)
                     .ToListAsync(ct))
        {
            code.MarkSold(nowUtc);
        }

        await SalesMarkingRules.SaveSoldAsync(db, ct);
    }

    /// <summary>
    /// D1159: отгрузка по заказу — не больше остатка заказа (заказано − отгружено + возвращено по проведённым документам).
    /// Под блокировкой заказа: две отгрузки одновременно не превысят заказ.
    /// </summary>
    private static async Task EnsureWithinOrderAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, long orderId, CancellationToken ct)
    {
        await db.LockAsync($"kniterp.order.sales.{orderId}", ct);
        var ordered = await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId)
            .SelectMany(o => o.Lines).GroupBy(l => l.ItemId).Select(g => new { g.Key, Sum = g.Sum(l => l.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        // D352 (каталог): строки комплекта — в комплектах, как в заказе (BundleLines.InOrderTerms).
        var raw = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == orderId && d.Status == StockDocumentStatus.Posted && d.Id != doc.Id)
            .SelectMany(d => d.Lines.Select(l => new { d.Id, d.Kind, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity })).ToListAsync(ct);
        var moved = BundleLines.InOrderTerms(raw.Select(m => ((m.Id, m.Kind), m.ItemId, m.Quantity, m.BundleItemId, m.BundleQuantity)));
        var over = doc.OrderLines().GroupBy(l => l.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity),
                Left: ordered.GetValueOrDefault(g.Key)
                      - moved.Where(m => m.ItemId == g.Key).Sum(m => m.Key.Kind == StockOperationKind.Shipment ? m.Quantity : m.Key.Kind == StockOperationKind.CustomerReturn ? -m.Quantity : 0)))
            .Where(x => x.Quantity > x.Left).ToList();
        if (over.Count > 0)
        {
            var ids = over.Select(x => x.ItemId).ToList();
            var codes = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
            throw new BusinessRuleException("sales.shipment.exceeds_order",
                "Отгрузка больше остатка заказа — " + string.Join("; ", over.Take(10).Select(x =>
                    $"{codes[x.ItemId]}: отгружается {Quantities.Format(x.Quantity)}, осталось {Quantities.Format(Math.Max(x.Left, 0))}")) + ".");
        }
    }

    /// <summary>Брак возврата: перемещение в карантин и списание — документами склада, проведёнными сразу (D1153).</summary>
    private static async Task ProcessDefectsAsync(
        IKnitErpDbContext db, AccessContext ctx, StockDocumentService documents, StockDocument doc, string? correlationId, DateTime nowUtc, CancellationToken ct)
    {
        var details = await db.SalesReturnDetails.Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.StockDocumentId == doc.Id, ct);
        if (details is null || details.Lines.Count == 0)
        {
            return;
        }

        long? transferId = null, writeOffId = null;
        var quarantine = details.Lines.Where(l => l.Quality == ReturnQuality.Quarantine).Select(l => (l.ItemId, l.Quantity)).ToList();
        if (quarantine.Count > 0)
        {
            var reason = await db.OperationReasons.AsNoTracking()
                             .Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == StockOperationKind.Transfer && !r.IsArchived && !r.RequiresComment)
                             .OrderBy(r => r.Id).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct)
                         ?? throw new BusinessRuleException("sales.return.transfer_reason",
                             "Нет причины перемещения без обязательного пояснения — добавьте её в справочнике причин склада.");
            transferId = await documents.CreateWithLinesAsync(StockOperationKind.Transfer,
                new StockDocumentHeader(doc.WarehouseId, details.QuarantineWarehouseId, null, reason, doc.DocumentDate,
                    $"Брак по возврату {doc.Number} — в карантин"), quarantine, ct);
            await PostAsync(db, documents, transferId.Value, ct);
        }

        var writeOff = details.Lines.Where(l => l.Quality == ReturnQuality.WriteOff).Select(l => (l.ItemId, l.Quantity)).ToList();
        if (writeOff.Count > 0)
        {
            writeOffId = await documents.CreateWithLinesAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(doc.WarehouseId, null, null, details.WriteOffReasonId, doc.DocumentDate, $"Брак по возврату {doc.Number}"),
                writeOff, ct);
            await PostAsync(db, documents, writeOffId.Value, ct);
        }

        details.AttachDocuments(transferId, writeOffId);
        db.AuditEntries.Add(AuditEntry.Create(nowUtc, ctx.OrganizationId, ctx.UserId, AuditActions.SalesReturnChanged, nameof(StockDocument),
            doc.Id.ToString(), null, $"брак: в карантин {quarantine.Count} поз., списано {writeOff.Count} поз.", doc.Number, correlationId));
        await db.SaveChangesAsync(ct);
    }

    private static async Task PostAsync(IKnitErpDbContext db, StockDocumentService documents, long id, CancellationToken ct)
    {
        var version = await db.StockDocuments.AsNoTracking().Where(d => d.Id == id).Select(d => d.RowVersion).SingleAsync(ct);
        await documents.PostAsync(id, version, ct);
    }
}

/// <summary>Правила кодов маркировки (D1108): полнота перед проведением и запрет повторной продажи.</summary>
internal static class SalesMarkingRules
{
    /// <summary>Маркируемые позиции документа: товар со склада с признаком маркировки.</summary>
    public static async Task<Dictionary<long, (string Code, string? Gtin)>> MarkedItemsAsync(IKnitErpDbContext db, long org, IEnumerable<long> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id) && i.IsMarked
                                                        && i.Type != ItemType.Service && i.Type != ItemType.Bundle)
            .ToDictionaryAsync(i => i.Id, i => (i.Code, i.Gtin), ct);
    }

    public static string Lock(long org) => $"kniterp.sales.marking.{org}";

    /// <summary>
    /// Кодов по каждой маркируемой строке — ровно её количество, GTIN кода — GTIN позиции. В отгрузке код ещё не продан; в возврате
    /// по заказу — продан именно по этому заказу (в возврате без заказа — в организации).
    /// </summary>
    public static async Task EnsureCompleteAsync(IKnitErpDbContext db, AccessContext ctx, StockDocument doc, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var marked = await MarkedItemsAsync(db, org, doc.Lines.Select(l => l.ItemId), ct);
        var codes = await db.SalesMarkingCodes.AsNoTracking().Where(c => c.OrganizationId == org && c.StockDocumentId == doc.Id).ToListAsync(ct);
        if (marked.Count == 0 && codes.Count == 0)
        {
            return;
        }

        await db.LockAsync(Lock(org), ct);
        var problems = new List<string>();
        foreach (var line in doc.Lines.Where(l => marked.ContainsKey(l.ItemId)))
        {
            var (code, gtin) = marked[line.ItemId];
            var count = codes.Count(c => c.ItemId == line.ItemId);
            if (count != line.Quantity)
            {
                problems.Add($"{code}: кодов {count} из {Quantities.Format(line.Quantity)}");
            }

            if (gtin is null)
            {
                problems.Add($"{code}: у маркируемой позиции не указан GTIN");
            }
            else if (codes.Any(c => c.ItemId == line.ItemId && c.Gtin != gtin))
            {
                problems.Add($"{code}: GTIN кода не совпадает с GTIN позиции {gtin}");
            }
        }

        if (codes.Any(c => !marked.ContainsKey(c.ItemId) || doc.Lines.All(l => l.ItemId != c.ItemId)))
        {
            problems.Add("есть коды позиций, которых нет в строках или которые не маркируются — удалите их");
        }

        if (problems.Count > 0)
        {
            throw new BusinessRuleException("sales.marking.incomplete",
                $"Коды маркировки «Честный ЗНАК» не готовы — {string.Join("; ", problems.Take(10))}. Отсканируйте коды для каждой единицы товара.");
        }

        var serials = codes.Select(c => c.Serial).Distinct().ToList();
        var sold = (await db.SalesMarkingCodes.AsNoTracking()
                .Where(c => c.OrganizationId == org && c.IsSold && serials.Contains(c.Serial))
                .Select(c => new { c.Gtin, c.Serial, c.StockDocumentId }).ToListAsync(ct))
            .Where(s => codes.Any(c => c.Gtin == s.Gtin && c.Serial == s.Serial)).ToList();
        if (doc.Kind == StockOperationKind.Shipment)
        {
            if (sold.Count > 0)
            {
                throw new BusinessRuleException("sales.marking.sold_twice",
                    $"Коды уже проданы другими отгрузками: {string.Join(", ", sold.Take(5).Select(s => "01" + s.Gtin + "21" + s.Serial))}. Один код продаётся один раз.");
            }

            return;
        }

        var shipments = doc.SalesOrderId is { } orderId
            ? await db.StockDocuments.AsNoTracking().Where(d => d.OrganizationId == org && d.SalesOrderId == orderId && d.Kind == StockOperationKind.Shipment)
                .Select(d => d.Id).ToListAsync(ct)
            : null;
        var missing = codes.Where(c => !sold.Any(s => s.Gtin == c.Gtin && s.Serial == c.Serial
                                                        && (shipments is null || shipments.Contains(s.StockDocumentId)))).ToList();
        if (missing.Count > 0)
        {
            throw new BusinessRuleException("sales.marking.not_sold",
                $"Эти коды не отгружались покупателю по этому заказу: {string.Join(", ", missing.Take(5).Select(c => c.Cis))}.");
        }
    }

    /// <summary>Сохранение проданных кодов: нарушение UNIQUE проданных кодов в SQL Server — понятный отказ, а не ошибка базы.</summary>
    public static async Task SaveSoldAsync(IKnitErpDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.GetBaseException().Message.Contains("ux_sales_marking_codes_sold", StringComparison.Ordinal))
        {
            throw new BusinessRuleException("sales.marking.sold_twice", "Код маркировки уже продан другой отгрузкой — один код продаётся один раз.");
        }
    }
}
