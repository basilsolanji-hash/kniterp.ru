using System.Security.Cryptography;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Purchasing;

public enum UpdMatch : byte
{
    None = 0,
    SupplierArticle = 1,
    Barcode = 2,
    ExternalCode = 3,
    ItemCode = 4,
    SupplierName = 5,
    Bound = 6,
}

public sealed record SupplierUpdLineDto(
    int No, string Name, string? Code, string? Barcode, string? Unit, decimal Quantity, decimal? Price, decimal? Amount, decimal? VatPercent,
    long? ItemId, string? ItemCode, string? ItemName, UpdMatch MatchedBy, bool InOrder, decimal? OrderPrice);

/// <summary>Позиция выбранного заказа — к ней можно привязать несопоставленную строку.</summary>
public sealed record UpdItemOptionDto(long Id, string Code, string Name);

public sealed record UpdOrderOptionDto(long Id, string Number, DateOnly Date, decimal Total);

public sealed record SupplierUpdPreviewDto(
    string FileHash, string Number, DateOnly Date, string SellerName, string SellerInn, string BuyerName, string BuyerInn, decimal? Total,
    long? SupplierId, string? Supplier, long? LegalEntityId, IReadOnlyList<UpdOrderOptionDto> Orders, long? OrderId,
    IReadOnlyList<SupplierUpdLineDto> Lines, IReadOnlyList<string> Problems, string? ImportedReceipt, bool CanSaveBindings,
    IReadOnlyList<UpdItemOptionDto> OrderItems)
{
    public int Unmatched => Lines.Count(l => l.ItemId is null);
}

/// <summary>Решения пользователя по строкам: привязать к позиции (ItemId) или пропустить (null).</summary>
public sealed record UpdLineDecision(int No, long? ItemId);

public sealed record SupplierUpdImportResult(long ReceiptId, string ReceiptNumber, int Lines, int Skipped, int BindingsSaved);

/// <summary>
/// D149 (А7): загрузка XML УПД поставщика → черновик поступления по заказу поставщику. Поставщик — по ИНН продавца (контрагент-поставщик
/// своей организации), покупатель — ИНН своего юрлица заказа: не совпало — отказ. Позиции сопоставляются по артикулу поставщика (D309),
/// штрихкоду (D79), коду внешней связи (D310), коду или артикулу позиции и наименованию у поставщика; несопоставленные пользователь
/// привязывает или пропускает, привязка запоминается артикулом поставщика (с правом «Справочники: изменение»). Черновик создаётся обычным
/// поступлением по заказу (права склада, D129 при проведении); проводит человек. Один файл — одна загрузка (SHA-256), повтор — отказ
/// с номером поступления. Файл не хранится.
/// </summary>
public sealed class SupplierUpdImportService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, PurchaseService purchases, StockDocumentService documents)
{
    public async Task<SupplierUpdPreviewDto> PreviewAsync(byte[] content, long? orderId, IReadOnlyCollection<UpdLineDecision>? decisions = null,
        CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var upd = SupplierUpdXml.Parse(content);
        return await PreviewCoreAsync(ctx, upd, Hash(content), orderId, decisions ?? [], ct);
    }

    public async Task<SupplierUpdImportResult> ImportAsync(byte[] content, string? fileName, long orderId, IReadOnlyCollection<UpdLineDecision> decisions,
        bool saveBindings, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var upd = SupplierUpdXml.Parse(content);
        var hash = Hash(content);
        var preview = await PreviewCoreAsync(ctx, upd, hash, orderId, decisions, ct);
        if (preview.ImportedReceipt is { } done)
        {
            throw new BusinessRuleException("purchase.upd.duplicate", $"Этот файл УПД уже загружен: поступление {done}.");
        }

        if (preview.Problems.Count > 0)
        {
            throw new BusinessRuleException("purchase.upd.problems", string.Join(" ", preview.Problems));
        }

        var skipped = decisions.Where(d => d.ItemId is null).Select(d => d.No).ToHashSet();
        var unresolved = preview.Lines.Where(l => l.ItemId is null && !skipped.Contains(l.No)).Select(l => l.No).ToList();
        if (unresolved.Count > 0)
        {
            throw new BusinessRuleException("purchase.upd.unmatched", $"Привяжите к позициям или пропустите строки УПД: {string.Join(", ", unresolved)}.");
        }

        var taken = preview.Lines.Where(l => l.ItemId is not null && !skipped.Contains(l.No)).ToList();
        var outside = taken.Where(l => !l.InOrder).Select(l => l.No).ToList();
        if (outside.Count > 0)
        {
            throw new BusinessRuleException("purchase.upd.not_in_order",
                $"Строк УПД {string.Join(", ", outside)} нет в заказе — пропустите их или добавьте позиции в заказ (лишнее оформляется поступлением без заказа).");
        }

        if (taken.Count == 0)
        {
            throw new BusinessRuleException("purchase.upd.nothing", "Все строки УПД пропущены — поступление не из чего создать.");
        }

        if (await db.StockDocuments.AsNoTracking().AnyAsync(d => d.OrganizationId == ctx.OrganizationId && d.PurchaseOrderId == orderId
                                                              && d.Kind == StockOperationKind.Receipt && d.Status == StockDocumentStatus.Draft, ct))
        {
            throw new BusinessRuleException("purchase.upd.draft_exists", "По заказу уже есть черновик поступления — проведите или удалите его, затем загрузите УПД.");
        }

        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct);
        try
        {
            // Всё — одной транзакцией: отказ на любом шаге не оставляет полупустого черновика; тот же файл в двух вкладках — по очереди.
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.upd.{ctx.OrganizationId}.{hash}", ct);
            if (await db.SupplierUpdImports.AsNoTracking().AnyAsync(i => i.OrganizationId == ctx.OrganizationId && i.FileHash == hash, ct))
            {
                throw new BusinessRuleException("purchase.upd.duplicate", "Этот файл УПД уже загружен.");
            }

            var receiptId = await purchases.CreateReceiptAsync(orderId, ct);
            // Количества — по УПД (сумма строк по позиции); позиции заказа, которых нет в УПД, из черновика убираются.
            var quantities = taken.GroupBy(l => l.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            var receipt = await documents.GetAsync(receiptId, ct);
            foreach (var line in receipt.Lines.Where(l => !quantities.ContainsKey(l.ItemId)).ToList())
            {
                await documents.RemoveLineAsync(receiptId, line.ItemId, (await documents.GetAsync(receiptId, ct)).RowVersion, ct);
            }

            foreach (var (itemId, quantity) in quantities)
            {
                await documents.SetLineAsync(receiptId, itemId, quantity, (await documents.GetAsync(receiptId, ct)).RowVersion, ct);
            }

            var head = await documents.GetAsync(receiptId, ct);
            await documents.UpdateHeaderAsync(receiptId, new StockDocumentHeader(head.WarehouseId, head.TargetWarehouseId, head.CounterpartyId, head.ReasonId,
                head.DocumentDate, Clip($"По заказу {order.Number}, УПД поставщика № {upd.Number} от {upd.Date:dd.MM.yyyy}", StockDocument.CommentMaxLength),
                head.PurchaseOrderId, head.SalesOrderId), head.RowVersion, ct);

            var saved = saveBindings && preview.CanSaveBindings
                ? await SaveBindingsAsync(ctx, preview.SupplierId!.Value, upd, decisions.Where(d => d.ItemId is not null), ct)
                : 0;
            db.SupplierUpdImports.Add(SupplierUpdImport.Create(ctx.OrganizationId, hash, fileName, upd.SellerInn, upd.Number, upd.Date, orderId, receiptId,
                taken.Count, ctx.UserId, clock.UtcNow));
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.SupplierUpdImported, nameof(StockDocument),
                receiptId.ToString(), null, $"{taken.Count} строк, пропущено {skipped.Count}", $"{order.Number}: УПД № {upd.Number} от {upd.Date:dd.MM.yyyy}",
                currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            var number = (await documents.GetAsync(receiptId, ct)).Number;
            await tx.CommitAsync(ct);
            return new SupplierUpdImportResult(receiptId, number, taken.Count, skipped.Count, saved);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<SupplierUpdPreviewDto> PreviewCoreAsync(AccessContext ctx, SupplierUpd upd, string hash, long? orderId,
        IReadOnlyCollection<UpdLineDecision> decisions, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var problems = new List<string>();
        var imported = await db.SupplierUpdImports.AsNoTracking().Where(i => i.OrganizationId == org && i.FileHash == hash)
            .Join(db.StockDocuments.AsNoTracking(), i => i.ReceiptDocumentId, d => d.Id, (i, d) => d.Number).FirstOrDefaultAsync(ct);
        var suppliers = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && c.Inn == upd.SellerInn && c.IsSupplier && !c.IsArchived)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var supplier = suppliers.Count == 1 ? suppliers[0] : null;
        if (suppliers.Count == 0)
        {
            problems.Add($"Поставщика с ИНН {upd.SellerInn} нет — создайте его (реквизиты заполнятся по ИНН) и загрузите файл снова.");
        }
        else if (suppliers.Count > 1)
        {
            problems.Add($"С ИНН {upd.SellerInn} несколько поставщиков — оставьте одного (остальных — в архив).");
        }

        var entity = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && e.Inn == upd.BuyerInn && !e.IsArchived)
            .Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            problems.Add($"Покупатель в УПД (ИНН {upd.BuyerInn}) — не ваше юрлицо: файл прислан не вам.");
        }

        if (upd.CurrencyCode is { } okv && okv != "643")
        {
            problems.Add($"УПД в валюте (код {okv}) — загрузка только рублёвых УПД.");
        }

        var orders = supplier is null || entity is null
            ? []
            : await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.OrganizationId == org && o.SupplierId == supplier.Id && o.LegalEntityId == entity && o.Status == PurchaseOrderStatus.Confirmed)
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Take(50)
                .Select(o => new UpdOrderOptionDto(o.Id, o.Number, o.OrderDate, o.Lines.Sum(l => l.Amount))).ToListAsync(ct);
        var chosen = orderId is { } oid ? orders.FirstOrDefault(o => o.Id == oid) : orders.Count == 1 ? orders[0] : null;
        if (orderId is not null && chosen is null && supplier is not null && entity is not null)
        {
            problems.Add("Выбранный заказ — не этого поставщика и юрлица или не подтверждён.");
        }
        else if (supplier is not null && entity is not null && orders.Count == 0)
        {
            problems.Add("У поставщика нет подтверждённых заказов на это юрлицо — создайте заказ поставщику.");
        }

        var orderLines = chosen is null
            ? []
            : await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == chosen.Id).SelectMany(o => o.Lines)
                .Join(db.Items.AsNoTracking(), l => l.ItemId, i => i.Id, (l, i) => new { l.ItemId, l.Price, i.Code, i.Name }).ToListAsync(ct);
        var matches = supplier is null ? [] : await MatchAsync(org, supplier.Id, upd.Lines, ct);
        var bound = decisions.Where(d => d.ItemId is not null).ToDictionary(d => d.No, d => d.ItemId!.Value);
        var boundIds = bound.Values.Distinct().ToList();
        var boundItems = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && boundIds.Contains(i.Id) && !i.IsArchived)
            .ToDictionaryAsync(i => i.Id, i => (i.Code, i.Name), ct);
        var lines = upd.Lines.Select(l =>
        {
            (long Id, string Code, string Name, UpdMatch By)? m = bound.TryGetValue(l.No, out var b) && boundItems.TryGetValue(b, out var bi)
                ? (b, bi.Code, bi.Name, UpdMatch.Bound)
                : matches.TryGetValue(l.No, out var found) ? found : null;
            var orderLine = m is { } mm ? orderLines.FirstOrDefault(o => o.ItemId == mm.Id) : null;
            return new SupplierUpdLineDto(l.No, l.Name, l.Code, l.Barcode, l.Unit, l.Quantity, l.Price, l.Amount, l.VatPercent, m?.Id, m?.Code, m?.Name,
                m?.By ?? UpdMatch.None, orderLine is not null, orderLine?.Price);
        }).ToList();
        return new SupplierUpdPreviewDto(hash, upd.Number, upd.Date, upd.SellerName, upd.SellerInn, upd.BuyerName, upd.BuyerInn, upd.Total, supplier?.Id,
            supplier?.Name, entity, orders, chosen?.Id, lines, problems, imported, supplier is not null && ctx.Permissions.Has(Permissions.CatalogEdit),
            orderLines.DistinctBy(l => l.ItemId).OrderBy(l => l.Code).Select(l => new UpdItemOptionDto(l.ItemId, l.Code, l.Name)).ToList());
    }

    /// <summary>Сопоставление строк: артикул поставщика → штрихкод → код внешней связи → код или артикул позиции → наименование у поставщика.</summary>
    private async Task<Dictionary<int, (long Id, string Code, string Name, UpdMatch By)>> MatchAsync(long org, long supplierId,
        IReadOnlyList<SupplierUpdLine> lines, CancellationToken ct)
    {
        var codes = lines.Select(l => l.Code).OfType<string>().Distinct().ToList();
        var barcodes = lines.Select(l => l.Barcode).Concat(lines.Select(l => l.Code)).OfType<string>().Distinct().ToList();
        var names = lines.Select(l => l.Name).Distinct().ToList();
        var active = db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived);
        var byArticle = await db.ItemSuppliers.AsNoTracking().Where(s => s.OrganizationId == org && s.SupplierId == supplierId && s.SupplierArticle != null
                                                                          && codes.Contains(s.SupplierArticle))
            .Join(active, s => s.ItemId, i => i.Id, (s, i) => new { Key = s.SupplierArticle!, i.Id, i.Code, i.Name }).ToListAsync(ct);
        var byName = await db.ItemSuppliers.AsNoTracking().Where(s => s.OrganizationId == org && s.SupplierId == supplierId && s.SupplierItemName != null
                                                                       && names.Contains(s.SupplierItemName))
            .Join(active, s => s.ItemId, i => i.Id, (s, i) => new { Key = s.SupplierItemName!, i.Id, i.Code, i.Name }).ToListAsync(ct);
        var byBarcode = await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && barcodes.Contains(b.Code))
            .Join(active, b => b.ItemId, i => i.Id, (b, i) => new { Key = b.Code, i.Id, i.Code, i.Name }).ToListAsync(ct);
        var byExternal = await db.ExternalLinks.AsNoTracking().Where(x => x.OrganizationId == org && x.ObjectType == ExternalObjectType.Item
                                                                          && x.ExternalCode != null && codes.Contains(x.ExternalCode))
            .Join(active, x => x.LocalId, i => i.Id, (x, i) => new { Key = x.ExternalCode!, i.Id, i.Code, i.Name }).ToListAsync(ct);
        var byCode = await active.Where(i => codes.Contains(i.Code) || (i.Article != null && codes.Contains(i.Article)))
            .Select(i => new { i.Id, i.Code, i.Name, i.Article }).ToListAsync(ct);

        var result = new Dictionary<int, (long, string, string, UpdMatch)>();
        foreach (var l in lines)
        {
            // Однозначное совпадение — одна позиция; несколько позиций с тем же ключом — пусть решит человек.
            (long, string, string, UpdMatch)? One<T>(IEnumerable<T> found, Func<T, (long, string, string)> pick, UpdMatch by)
            {
                var list = found.Select(pick).Distinct().ToList();
                return list.Count == 1 ? (list[0].Item1, list[0].Item2, list[0].Item3, by) : null;
            }

            var m = (l.Code is { } c ? One(byArticle.Where(x => string.Equals(x.Key, c, StringComparison.OrdinalIgnoreCase)), x => (x.Id, x.Code, x.Name), UpdMatch.SupplierArticle) : null)
                    ?? One(byBarcode.Where(x => x.Key == l.Barcode || x.Key == l.Code), x => (x.Id, x.Code, x.Name), UpdMatch.Barcode)
                    ?? (l.Code is { } e ? One(byExternal.Where(x => x.Key == e), x => (x.Id, x.Code, x.Name), UpdMatch.ExternalCode) : null)
                    ?? (l.Code is { } k ? One(byCode.Where(x => string.Equals(x.Code, k, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(x.Article, k, StringComparison.OrdinalIgnoreCase)), x => (x.Id, x.Code, x.Name), UpdMatch.ItemCode) : null)
                    ?? One(byName.Where(x => x.Key == l.Name), x => (x.Id, x.Code, x.Name), UpdMatch.SupplierName);
            if (m is { } found)
            {
                result[l.No] = found;
            }
        }

        return result;
    }

    /// <summary>Запомнить привязку: у позиции появляется (или дополняется) поставщик с его артикулом и наименованием из УПД.</summary>
    private async Task<int> SaveBindingsAsync(AccessContext ctx, long supplierId, SupplierUpd upd, IEnumerable<UpdLineDecision> bound, CancellationToken ct)
    {
        var saved = 0;
        foreach (var d in bound)
        {
            var line = upd.Lines.FirstOrDefault(l => l.No == d.No);
            var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == d.ItemId && i.OrganizationId == ctx.OrganizationId, ct);
            if (line is null || item is null || item.Type == ItemType.Bundle)
            {
                continue;
            }

            var article = line.Code is { Length: <= ItemSupplier.ArticleMaxLength } c ? c : null;
            var name = Clip(line.Name, ItemSupplier.NameMaxLength);
            var links = await db.ItemSuppliers.Where(s => s.OrganizationId == ctx.OrganizationId && s.ItemId == item.Id).ToListAsync(ct);
            var link = links.FirstOrDefault(s => s.SupplierId == supplierId);
            if (link is null)
            {
                if (links.Count >= ItemSupplier.MaxPerItem)
                {
                    continue;
                }

                db.ItemSuppliers.Add(ItemSupplier.Create(ctx.OrganizationId, item, supplierId, article, name, links.Count == 0));
            }
            else if (link.SupplierArticle is null && article is not null || link.SupplierItemName is null)
            {
                link.Set(link.SupplierArticle ?? article, link.SupplierItemName ?? name, link.IsMain);
            }
            else
            {
                continue;
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), item.Id.ToString(), null,
                $"поставщик: артикул {article ?? "—"}", "Привязка из УПД поставщика", currentUser.CorrelationId));
            saved++;
        }

        return saved;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PurchaseView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}
