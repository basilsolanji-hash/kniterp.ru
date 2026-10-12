using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Printing;
using KnitErp.Application.Structure;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record SalesReturnReasonDto(long Id, string Name, bool IsArchived, byte[] RowVersion);

public sealed record ReturnLineDto(long ItemId, string Code, string Name, string UnitSymbol, decimal Quantity, ReturnQuality Quality, decimal DefectQuantity)
{
    public string QualityName => SalesReturnDetails.QualityName(Quality);
}

public sealed record ReturnDetailsInput(long? ReasonId, string? Comment, long? QuarantineWarehouseId, long? WriteOffReasonId);

/// <summary>
/// Возврат от покупателя в карточке документа (D1153): причина, качество по строкам, склад карантина и причина списания брака;
/// после проведения — ссылки на перемещение в карантин и списание. CanEdit — черновик и кладовщик склада возврата.
/// </summary>
public sealed record ReturnPanelDto(
    long DocumentId, bool Draft, long? ReasonId, string? Reason, string? Comment, long? QuarantineWarehouseId, string? QuarantineWarehouse,
    long? WriteOffReasonId, string? WriteOffReason, IReadOnlyList<ReturnLineDto> Lines, IReadOnlyList<LookupDto> Reasons,
    IReadOnlyList<LookupWarehouseDto> Warehouses, IReadOnlyList<LookupDto> WriteOffReasons, bool CanEdit,
    long? TransferDocumentId, string? TransferDocumentNumber, long? WriteOffDocumentId, string? WriteOffDocumentNumber, bool CanPrintCorrection,
    byte[]? RowVersion);

/// <summary>Что показывает карточка складского документа от продаж: коды маркировки и сведения возврата.</summary>
public sealed record StockDocumentSalesDto(DocumentMarkingDto? Marking, ReturnPanelDto? Return);

/// <summary>Строка корректировочного счёта-фактуры: «до» и «после» изменения, уменьшение (возврат) — по графам формы УКД.</summary>
public sealed record CorrectionLineDto(
    int No, string Code, string Name, string UnitCode, string UnitSymbol, decimal QuantityBefore, decimal QuantityAfter, decimal PriceBefore,
    decimal PriceAfter, decimal AmountBefore, decimal AmountAfter, decimal? VatPercent, decimal VatBefore, decimal VatAfter, decimal TotalBefore,
    decimal TotalAfter)
{
    public decimal AmountDecrease => AmountBefore - AmountAfter;
    public decimal VatDecrease => VatBefore - VatAfter;
    public decimal TotalDecrease => TotalBefore - TotalAfter;
}

/// <summary>
/// Корректировочный УПД (УКД, статус 1) к возврату по заказу (D1155): номер — цифры номера возврата, к счетам-фактурам отгрузок заказа.
/// Реквизиты — юрлицо заказа, как у УПД.
/// </summary>
public sealed record CorrectionPrintDto(
    string Number, DateOnly Date, string ReturnNumber, string Shipments, PrintPartyDto Seller, PrintRequisites Requisites, PrintPartyDto Buyer, string Basis,
    string Currency, IReadOnlyList<CorrectionLineDto> Lines, bool Reversed, IReadOnlyList<string> Warnings)
{
    public decimal AmountDecrease => Lines.Sum(l => l.AmountDecrease);
    public decimal VatDecrease => Lines.Sum(l => l.VatDecrease);
    public decimal TotalDecrease => Lines.Sum(l => l.TotalDecrease);
}

public sealed record RefundCommand(DateOnly Date, decimal Amount, long? MoneyAccountId, string? Comment);

public sealed record CustomerRefundDto(long MoneyOperationId, string Number, DateOnly Date, decimal Amount, CustomerRefundStatus Status, string? CancelReason,
    byte[] RowVersion);

public sealed record ShipPickListRefDto(long Id, string Number, PickListStatus Status, bool PartiallyPicked)
{
    public string StatusName => SalesPickList.StatusName(Status);
}

public sealed record ShipGroupRefDto(long Id, string Number, ShipmentGroupStatus Status, string? Delivery, string? TrackingNumber)
{
    public string StatusName => SalesShipmentGroup.StatusName(Status);
}

/// <summary>
/// Отгрузка и возвраты в карточке заказа (D1150–D1154): задания на сборку, отгрузки с логистикой, переплата после возврата и аванс
/// покупателя (суммы — только с правом «Цены и суммы»), возвраты денег.
/// </summary>
public sealed record SalesOrderShippingDto(
    IReadOnlyList<ShipPickListRefDto> PickLists, IReadOnlyList<ShipGroupRefDto> Groups, decimal? Overpaid, decimal? CustomerAdvance,
    IReadOnlyList<CustomerRefundDto> Refunds, IReadOnlyList<MoneyAccountOptionDto> Accounts, bool CanPick, bool CanShip, bool CanMoney);

public sealed partial class SalesService
{
    // ---------- Причины возврата ----------

    public async Task<IReadOnlyList<SalesReturnReasonDto>> ReturnReasonsAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await SeedReturnReasonsAsync(ctx, ct);
        return await db.SalesReturnReasons.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && (includeArchived || !r.IsArchived))
            .OrderBy(r => r.IsArchived).ThenBy(r => r.Name).Select(r => new SalesReturnReasonDto(r.Id, r.Name, r.IsArchived, r.RowVersion)).ToListAsync(ct);
    }

    public async Task<long> CreateReturnReasonAsync(string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        if (await db.SalesReturnReasons.CountAsync(r => r.OrganizationId == ctx.OrganizationId, ct) >= SalesReturnReason.MaxReasons)
        {
            throw new BusinessRuleException("sales.return_reason.too_many", $"Причин возврата не больше {SalesReturnReason.MaxReasons}.");
        }

        var reason = SalesReturnReason.Create(ctx.OrganizationId, name);
        if (await db.SalesReturnReasons.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Name == reason.Name, ct))
        {
            throw new BusinessRuleException("sales.return_reason.duplicate", $"Причина «{reason.Name}» уже есть.");
        }

        db.SalesReturnReasons.Add(reason);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesReturnReason), reason.Id, null, reason.Name, "причина возврата");
        await db.SaveChangesAsync(ct);
        return reason.Id;
    }

    public async Task SetReturnReasonArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var reason = await db.SalesReturnReasons.SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Причина возврата");
        reason.EnsureVersion(reason.RowVersion, rowVersion);
        reason.SetArchived(archived);
        Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesReturnReason), id, archived ? "действует" : "в архиве",
            archived ? "в архиве" : "действует", reason.Name);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Пустой справочник причин заполняется типовыми при первом обращении того, кто может их выбирать.</summary>
    private async Task SeedReturnReasonsAsync(AccessContext ctx, CancellationToken ct)
    {
        if (await db.SalesReturnReasons.AnyAsync(r => r.OrganizationId == ctx.OrganizationId, ct))
        {
            return;
        }

        foreach (var name in SalesReturnReason.Defaults)
        {
            db.SalesReturnReasons.Add(SalesReturnReason.Create(ctx.OrganizationId, name));
        }

        Audit(ctx, AuditActions.SalesShippingSettingsChanged, nameof(SalesReturnReason), ctx.OrganizationId, null, string.Join(", ", SalesReturnReason.Defaults),
            "типовые причины возврата");
        await db.SaveChangesAsync(ct);
    }

    // ---------- Карточка складского документа ----------

    /// <summary>Данные продаж для карточки складского документа — одним вызовом (у вкладки один DbContext).</summary>
    public async Task<StockDocumentSalesDto> StockDocumentPanelAsync(long documentId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var kind = await db.StockDocuments.AsNoTracking().Where(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId)
            .Select(d => (StockOperationKind?)d.Kind).SingleOrDefaultAsync(ct);
        if (kind is not (StockOperationKind.Shipment or StockOperationKind.CustomerReturn))
        {
            return new StockDocumentSalesDto(null, null);
        }

        return new StockDocumentSalesDto(await MarkingAsync(documentId, ct),
            kind == StockOperationKind.CustomerReturn ? await ReturnPanelAsync(ctx, documentId, ct) : null);
    }

    private async Task<ReturnPanelDto> ReturnPanelAsync(AccessContext ctx, long documentId, CancellationToken ct)
    {
        var doc = await VisibleDocumentAsync(ctx, documentId, ct);
        var details = await db.SalesReturnDetails.AsNoTracking().Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.StockDocumentId == documentId, ct);
        var draft = doc.Status == StockDocumentStatus.Draft;
        var canEdit = draft && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId);
        if (canEdit || ctx.Permissions.Has(Permissions.SalesEdit))
        {
            await SeedReturnReasonsAsync(ctx, ct);
        }

        var org = ctx.OrganizationId;
        var reasons = await db.SalesReturnReasons.AsNoTracking().Where(r => r.OrganizationId == org && (!r.IsArchived || r.Id == details!.ReasonId))
            .OrderBy(r => r.Name).Select(r => new LookupDto(r.Id, r.Name)).ToListAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == org && w.Id != doc.WarehouseId && (!w.IsArchived || w.Id == details!.QuarantineWarehouseId))
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
        var writeOffReasons = await db.OperationReasons.AsNoTracking()
            .Where(r => r.OrganizationId == org && r.Kind == StockOperationKind.WriteOff && (!r.IsArchived || r.Id == details!.WriteOffReasonId))
            .OrderBy(r => r.Name).Select(r => new LookupDto(r.Id, r.Name)).ToListAsync(ct);
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol }).ToDictionaryAsync(x => x.Id, ct);
        var lines = doc.Lines.Select(l =>
            {
                var q = details?.Lines.FirstOrDefault(x => x.ItemId == l.ItemId);
                return new ReturnLineDto(l.ItemId, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Symbol, l.Quantity,
                    q?.Quality ?? ReturnQuality.Good, q?.Quantity ?? 0);
            })
            .OrderBy(l => l.Code).ToList();
        long?[] followUps = [details?.TransferDocumentId, details?.WriteOffDocumentId];
        var numbers = await db.StockDocuments.AsNoTracking().Where(d => followUps.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Number, ct);
        return new ReturnPanelDto(documentId, draft, details?.ReasonId, reasons.FirstOrDefault(r => r.Id == details?.ReasonId)?.Name, details?.Comment,
            details?.QuarantineWarehouseId, warehouses.FirstOrDefault(w => w.Id == details?.QuarantineWarehouseId)?.Name,
            details?.WriteOffReasonId, writeOffReasons.FirstOrDefault(r => r.Id == details?.WriteOffReasonId)?.Name, lines, reasons, warehouses,
            writeOffReasons, canEdit, details?.TransferDocumentId, details?.TransferDocumentId is { } t ? numbers[t] : null,
            details?.WriteOffDocumentId, details?.WriteOffDocumentId is { } w ? numbers[w] : null,
            doc.SalesOrderId is not null && doc.Status is StockDocumentStatus.Posted or StockDocumentStatus.Reversed
                                         && ctx.Permissions.Has(Permissions.SalesView) && ctx.Permissions.Has(Permissions.PriceView),
            details?.RowVersion);
    }

    /// <summary>Причина возврата, склад карантина и причина списания брака (D1153) — пока возврат черновик; кладовщик склада возврата.</summary>
    public async Task SetReturnDetailsAsync(long documentId, ReturnDetailsInput input, byte[]? rowVersion, CancellationToken ct = default)
    {
        var (ctx, doc, details) = await ReturnDraftAsync(documentId, rowVersion, ct);
        var org = ctx.OrganizationId;
        if (input.ReasonId is { } rid && rid != details.ReasonId
            && !await db.SalesReturnReasons.AnyAsync(r => r.Id == rid && r.OrganizationId == org && !r.IsArchived, ct))
        {
            throw new NotFoundException("Причина возврата");
        }

        if (input.QuarantineWarehouseId is { } wid && wid != details.QuarantineWarehouseId)
        {
            var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == wid && x.OrganizationId == org, ct) ?? throw new NotFoundException("Склад");
            if (w.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Склад «{w.Name}» в архиве.");
            }

            if (wid == doc.WarehouseId)
            {
                throw new BusinessRuleException("sales.return.quarantine_same", "Склад карантина должен отличаться от склада возврата.");
            }
        }

        if (input.WriteOffReasonId is { } wr && wr != details.WriteOffReasonId)
        {
            var r = await db.OperationReasons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == wr && x.OrganizationId == org, ct)
                    ?? throw new NotFoundException("Причина");
            if (r.IsArchived || r.Kind != StockOperationKind.WriteOff)
            {
                throw new BusinessRuleException("stock.document.reason_kind", $"Причина «{r.Name}» относится к другому виду операций или в архиве.");
            }
        }

        var before = $"{details.ReasonId}/{details.QuarantineWarehouseId}/{details.WriteOffReasonId}/{details.Comment}";
        details.SetHeader(input.ReasonId, input.Comment, input.QuarantineWarehouseId, input.WriteOffReasonId);
        Audit(ctx, AuditActions.SalesReturnChanged, nameof(StockDocument), documentId, before,
            $"{details.ReasonId}/{details.QuarantineWarehouseId}/{details.WriteOffReasonId}/{details.Comment}", doc.Number);
        await SaveDetailsAsync(details, ct);
    }

    /// <summary>Качество строки возврата: годен (остаётся на складе заказа) или брак — в карантин или в списание, количество брака.</summary>
    public async Task SetReturnQualityAsync(long documentId, long itemId, ReturnQuality quality, decimal quantity, byte[]? rowVersion,
        CancellationToken ct = default)
    {
        var (ctx, doc, details) = await ReturnDraftAsync(documentId, rowVersion, ct);
        var line = doc.Lines.FirstOrDefault(l => l.ItemId == itemId) ?? throw new BusinessRuleException("stock.document.line_missing", "Такой строки в документе нет.");
        if (quantity > line.Quantity)
        {
            throw new BusinessRuleException("sales.return.quality_exceeds",
                $"Брака {Quantities.Format(quantity)} больше, чем в строке возврата ({Quantities.Format(line.Quantity)}).");
        }

        details.SetLine(itemId, quality, quality == ReturnQuality.Good ? 0 : quantity);
        var code = await db.Items.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.Code).SingleAsync(ct);
        Audit(ctx, AuditActions.SalesReturnChanged, nameof(StockDocument), documentId, null,
            $"{code}: {SalesReturnDetails.QualityName(quality)} {Quantities.Format(quantity)}", doc.Number);
        await SaveDetailsAsync(details, ct);
    }

    private async Task SaveDetailsAsync(SalesReturnDetails details, CancellationToken ct)
    {
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<(AccessContext, StockDocument, SalesReturnDetails)> ReturnDraftAsync(long documentId, byte[]? rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.WarehouseDocumentCreate, ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == documentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        if (!WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, doc.WarehouseId))
        {
            throw new NotFoundException("Документ");
        }

        if (doc.Kind != StockOperationKind.CustomerReturn)
        {
            throw new BusinessRuleException("sales.return.kind", "Причина и качество указываются в возврате от покупателя.");
        }

        if (doc.Status != StockDocumentStatus.Draft)
        {
            throw new BusinessRuleException("stock.document.status", "Документ уже не черновик — изменения запрещены.");
        }

        var details = await db.SalesReturnDetails.Include(d => d.Lines)
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.StockDocumentId == documentId, ct);
        if (details is null)
        {
            details = SalesReturnDetails.Create(ctx.OrganizationId, documentId);
            db.SalesReturnDetails.Add(details);
        }
        else if (rowVersion is not null)
        {
            details.EnsureVersion(details.RowVersion, rowVersion);
        }

        return (ctx, doc, details);
    }

    // ---------- Корректировочный УПД ----------

    /// <summary>
    /// Данные УКД к проведённому возврату по заказу (D1155): по каждой позиции — количество и стоимость по отгрузкам заказа «до»
    /// (отгружено за вычетом прежних возвратов) и «после» этого возврата, по ценам заказа. Права — как у УПД.
    /// </summary>
    public async Task<CorrectionPrintDto> CorrectionAsync(long returnDocumentId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var org = ctx.OrganizationId;
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == returnDocumentId && d.OrganizationId == org, ct)
                  ?? throw new NotFoundException("Документ");
        if (doc.Kind != StockOperationKind.CustomerReturn || doc.SalesOrderId is not { } orderId
            || doc.Status is not (StockDocumentStatus.Posted or StockDocumentStatus.Reversed))
        {
            throw new BusinessRuleException("sales.correction.document", "Корректировочный УПД печатается по проведённому возврату по заказу покупателя.");
        }

        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId, ct);
        var entity = await db.LegalEntities.AsNoTracking().SingleAsync(e => e.Id == order.LegalEntityId, ct);
        var account = await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.LegalEntityId == entity.Id && (order.BankAccountId == null ? a.IsDefault : a.Id == order.BankAccountId))
            .FirstOrDefaultAsync(ct);
        var buyer = await db.Counterparties.AsNoTracking().Where(c => c.Id == order.CustomerId)
            .Select(c => new PrintPartyDto(c.Name, c.Inn, c.Kpp, c.Address)).SingleAsync(ct);
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId == orderId && d.Id != doc.Id
                        && d.Status == StockDocumentStatus.Posted)
            .Select(d => new { d.Id, d.Number, d.Kind, d.DocumentDate, d.PostedAtUtc, Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList() })
            .ToListAsync(ct);
        var shipments = moved.Where(d => d.Kind == StockOperationKind.Shipment).OrderBy(d => d.DocumentDate).ThenBy(d => d.Id).ToList();
        // «До» — отгружено минус возвраты, проведённые раньше этого.
        var earlierReturns = moved.Where(d => d.Kind == StockOperationKind.CustomerReturn && d.PostedAtUtc < doc.PostedAtUtc).ToList();
        var itemIds = doc.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, UnitCode = u.Code })
            .ToDictionaryAsync(x => x.Id, ct);
        var lines = doc.Lines.OrderBy(l => items[l.ItemId].Code).Select((l, i) =>
        {
            var orderLine = order.Lines.Single(o => o.ItemId == l.ItemId);
            var before = shipments.SelectMany(d => d.Lines).Where(x => x.ItemId == l.ItemId).Sum(x => x.Quantity)
                         - earlierReturns.SelectMany(d => d.Lines).Where(x => x.ItemId == l.ItemId).Sum(x => x.Quantity);
            var after = Math.Max(0, before - l.Quantity);
            var (amountBefore, vatBefore) = Money.LineAmounts(before, orderLine.NetPrice, orderLine.VatPercent, order.PricesIncludeVat);
            var (amountAfter, vatAfter) = Money.LineAmounts(after, orderLine.NetPrice, orderLine.VatPercent, order.PricesIncludeVat);
            var price = before == 0 ? 0 : Money.Round((amountBefore - vatBefore) / before);
            return new CorrectionLineDto(i + 1, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].UnitCode, items[l.ItemId].Symbol, before, after,
                price, after == 0 ? price : Money.Round((amountAfter - vatAfter) / after), amountBefore - vatBefore, amountAfter - vatAfter,
                orderLine.VatPercent, vatBefore, vatAfter, amountBefore, amountAfter);
        }).ToList();
        var org2 = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == org, ct);
        var country = Countries.Get(org2.CountryCode);
        var warnings = new List<string>();
        if (entity.Kpp is null && !entity.IsSoleProprietor && org2.CountryCode == Countries.Russia)
        {
            warnings.Add($"У юрлица «{entity.ShortName}» не указан КПП — заполните его в разделе «Юрлица и счета».");
        }

        if (buyer.Address is null)
        {
            warnings.Add("Не указан адрес покупателя — заполните его в карточке контрагента.");
        }

        if (shipments.Count == 0)
        {
            warnings.Add("По заказу нет проведённых отгрузок — корректировать нечего.");
        }

        var requisites = new PrintRequisites(entity.LegalAddress, account?.BankName, account?.Bic, account?.Account, account?.CorrAccount, entity.DirectorName,
            entity.AccountantName, entity.DirectorPosition, entity.IsSoleProprietor, entity.Ogrn, entity.VatExempt);
        return new CorrectionPrintDto(PrintService.VatInvoiceNumber(doc.Number), doc.DocumentDate, doc.Number,
            string.Join("; ", shipments.Select(s => $"№ {PrintService.VatInvoiceNumber(s.Number)} от {s.DocumentDate:dd.MM.yyyy}")),
            new PrintPartyDto(entity.Name, entity.Inn, entity.Kpp, entity.LegalAddress), requisites, buyer,
            (order.CustomerReference is { } reference ? reference + "; " : string.Empty) + $"заказ {order.Number} от {order.OrderDate:dd.MM.yyyy}; возврат {doc.Number}",
            $"{char.ToUpperInvariant(country.CurrencyName[0])}{country.CurrencyName[1..]}, {org2.CurrencyCode}", lines,
            doc.Status == StockDocumentStatus.Reversed, warnings);
    }

    // ---------- Отгрузка и деньги в карточке заказа ----------

    public async Task<SalesOrderShippingDto> ShippingPanelAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var picks = await db.SalesPickLists.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.Lines.Any(l => l.SalesOrderId == orderId))
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.Number, p.Status, Partial = p.Lines.Any(l => l.Picked > 0) }).ToListAsync(ct);
        var groups = await (from g in db.SalesShipmentGroups.AsNoTracking()
                            where g.OrganizationId == ctx.OrganizationId && g.Documents.Any(d => d.SalesOrderId == orderId)
                            join m in db.SalesDeliveryMethods.AsNoTracking() on g.DeliveryMethodId equals m.Id into mj
                            from m in mj.DefaultIfEmpty()
                            orderby g.Id
                            select new ShipGroupRefDto(g.Id, g.Number, g.Status, m == null ? null : m.Name, g.TrackingNumber)).ToListAsync(ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        decimal? overpaid = null, advance = null;
        IReadOnlyList<CustomerRefundDto> refunds = [];
        IReadOnlyList<MoneyAccountOptionDto> accounts = [];
        if (prices)
        {
            overpaid = await OverpaidAsync(ctx, order, ct);
            advance = (await FreeAdvancesAsync(ctx, order.CustomerId, ct)).Sum(a => a.Free);
            refunds = await (from r in db.CustomerRefunds.AsNoTracking()
                             where r.OrganizationId == ctx.OrganizationId && r.SalesOrderId == orderId
                             join op in db.MoneyOperations.AsNoTracking() on r.MoneyOperationId equals op.Id
                             group new { r, op } by new { op.Id, op.Number, op.OperationDate, op.Status, op.CancelReason, op.RowVersion } into g
                             orderby g.Key.Id
                             select new CustomerRefundDto(g.Key.Id, g.Key.Number, g.Key.OperationDate, g.Sum(x => x.r.Amount),
                                 g.Key.Status == MoneyOperationStatus.Posted ? CustomerRefundStatus.Posted : CustomerRefundStatus.Cancelled, g.Key.CancelReason,
                                 g.Key.RowVersion)).ToListAsync(ct);
            accounts = (await MoneyService.OptionsAsync(db, ctx.OrganizationId, ct)).Where(a => a.LegalEntityId == order.LegalEntityId).ToList();
        }

        var open = order.Status == SalesOrderStatus.Confirmed;
        return new SalesOrderShippingDto(picks.Select(p => new ShipPickListRefDto(p.Id, p.Number, p.Status, p.Status == PickListStatus.Open && p.Partial)).ToList(),
            groups, overpaid, advance, refunds, accounts,
            open && (ctx.Permissions.Has(Permissions.SalesEdit) || CanWorkAt(ctx, order.WarehouseId)),
            open && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, order.WarehouseId),
            prices && ctx.Permissions.Has(Permissions.SalesEdit));
    }

    /// <summary>
    /// Переплата по заказу после возврата: разнесено оплат на заказ больше, чем стоит то, что покупатель оставил себе
    /// (сумма заказа минус возвращённое по ценам заказа).
    /// </summary>
    private async Task<decimal> OverpaidAsync(AccessContext ctx, SalesOrder order, CancellationToken ct)
    {
        var returned = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == order.Id && d.Kind == StockOperationKind.CustomerReturn
                        && d.Status == StockDocumentStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => new { l.ItemId, l.Quantity })).ToListAsync(ct);
        if (returned.Count == 0)
        {
            return 0;
        }

        var returnedValue = Money.Round(returned.Sum(l => l.Quantity * order.UnitCostWithVat(l.ItemId)));
        var paid = (await PaidByOrder.SalesAsync(db, ctx.OrganizationId, [order.Id], ct)).GetValueOrDefault(order.Id);
        return Math.Max(0, paid - Math.Max(0, order.Total - returnedValue));
    }

    /// <summary>Свободный аванс по оплатам покупателя: сумма оплаты − действующие разноски − возвраты денег.</summary>
    private async Task<List<(long PaymentId, long? AccountId, DateOnly Date, decimal Free)>> FreeAdvancesAsync(AccessContext ctx, long customerId, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var payments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.CustomerId == customerId && p.Status == CustomerPaymentStatus.Posted)
            .Select(p => new
            {
                p.Id, p.MoneyAccountId, p.PaymentDate, p.Amount,
                Allocated = db.CustomerPaymentAllocations.Where(a => a.PaymentId == p.Id && a.RemovedAtUtc == null).Sum(a => (decimal?)a.Amount) ?? 0,
                Refunded = db.CustomerRefunds.Where(r => r.PaymentId == p.Id && r.Status == CustomerRefundStatus.Posted).Sum(r => (decimal?)r.Amount) ?? 0,
            })
            .ToListAsync(ct);
        return payments.Select(p => (p.Id, p.MoneyAccountId, p.PaymentDate, Free: p.Amount - p.Allocated - p.Refunded)).Where(p => p.Free > 0)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).ToList();
    }

    /// <summary>
    /// Зачёт в аванс (D1154): переплата по заказу после возврата снимается с разносок этого заказа (сначала поздние) — она становится
    /// авансом покупателя: её можно зачесть в другой заказ («Зачесть аванс») или вернуть деньгами. Возвращает переведённую сумму.
    /// </summary>
    public async Task<decimal> ReleaseOverpaymentAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            await db.LockAsync($"kniterp.allocation.customer.{ctx.OrganizationId}.{order.CustomerId}", ct);
            var released = await ReleaseCoreAsync(ctx, order, ct);
            if (released <= 0)
            {
                throw new BusinessRuleException("sales.refund.no_overpayment", "Переплаты по заказу нет — переводить в аванс нечего.");
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return released;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<decimal> ReleaseCoreAsync(AccessContext ctx, SalesOrder order, CancellationToken ct)
    {
        var need = await OverpaidAsync(ctx, order, ct);
        if (need <= 0)
        {
            return 0;
        }

        var released = 0m;
        var allocations = await db.CustomerPaymentAllocations
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.OrderId == order.Id && a.RemovedAtUtc == null).OrderByDescending(a => a.Id).ToListAsync(ct);
        foreach (var a in allocations)
        {
            if (need <= 0)
            {
                break;
            }

            var take = Math.Min(need, a.Amount);
            a.Remove(ctx.UserId, clock.UtcNow);
            await db.SaveChangesAsync(ct); // снятие — раньше новой строки: уникальный индекс действующих разносок
            if (a.Amount - take > 0)
            {
                db.CustomerPaymentAllocations.Add(CustomerPaymentAllocation.Create(ctx.OrganizationId, a.PaymentId, order.Id, a.Amount - take, ctx.UserId, clock.UtcNow));
            }

            need -= take;
            released += take;
        }

        Audit(ctx, AuditActions.SalesAdvanceReleased, nameof(SalesOrder), order.Id, null, $"{released:0.00}", $"{order.Number}: переплата после возврата — аванс покупателя");
        return released;
    }

    /// <summary>
    /// Возврат денег покупателю (D1154): переплата по заказу сначала переводится в аванс, затем из аванса покупателя (оплаты от старых
    /// к новым) выдаются деньги — денежной операцией «выдача» со счёта или из кассы (РКО). Не больше свободного аванса.
    /// </summary>
    public async Task<long> RefundAsync(long orderId, RefundCommand cmd, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        if (cmd.Amount <= 0 || Money.Round(cmd.Amount) != cmd.Amount)
        {
            throw new BusinessRuleException("sales.refund.amount", "Сумма возврата — больше нуля, до копеек.");
        }

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            await db.LockAsync($"kniterp.allocation.customer.{ctx.OrganizationId}.{order.CustomerId}", ct);
            await ReleaseCoreAsync(ctx, order, ct);
            await db.SaveChangesAsync(ct);
            var free = await FreeAdvancesAsync(ctx, order.CustomerId, ct);
            if (cmd.Amount > free.Sum(f => f.Free))
            {
                throw new BusinessRuleException("sales.refund.exceeds", $"Вернуть можно не больше аванса покупателя: {free.Sum(f => f.Free):0.00}.");
            }

            var account = await MoneyService.ResolveAsync(db, ctx.OrganizationId, cmd.MoneyAccountId ?? free.Select(f => f.AccountId).FirstOrDefault(f => f != null),
                              order.LegalEntityId, order.BankAccountId, ct)
                          ?? throw new BusinessRuleException("sales.refund.account", "Укажите счёт или кассу, откуда возвращаются деньги.");
            var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == order.CustomerId).Select(c => c.Name).SingleAsync(ct);
            var lastReturn = await db.StockDocuments.AsNoTracking()
                .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == orderId && d.Kind == StockOperationKind.CustomerReturn && d.Status == StockDocumentStatus.Posted)
                .OrderByDescending(d => d.Id).Select(d => new { d.Id, d.Number }).FirstOrDefaultAsync(ct);
            var operationId = await new MoneyOperationService(db, guard, currentUser, clock).CreateAsync(new MoneyOperationCommand(
                cmd.Date, MoneyOperationKind.Expense, account, null, cmd.Amount, customer,
                $"Возврат денег покупателю по заказу {order.Number}{(lastReturn is null ? "" : ", возврат " + lastReturn.Number)}", cmd.Comment), ct);
            var left = cmd.Amount;
            foreach (var f in free)
            {
                if (left <= 0)
                {
                    break;
                }

                var part = Math.Min(left, f.Free);
                db.CustomerRefunds.Add(CustomerRefund.Create(ctx.OrganizationId, cmd.Date, order.CustomerId, f.PaymentId, orderId, lastReturn?.Id, operationId, part,
                    ctx.UserId, clock.UtcNow));
                left -= part;
            }

            Audit(ctx, AuditActions.CustomerRefundCreated, nameof(SalesOrder), orderId, null, $"{cmd.Amount:0.00}", $"{order.Number}: {customer}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return operationId;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Отмена возврата денег: отменяется денежная операция (с причиной), сумма снова в авансе покупателя.</summary>
    public async Task CancelRefundAsync(long moneyOperationId, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var refunds = await db.CustomerRefunds.Where(r => r.OrganizationId == ctx.OrganizationId && r.MoneyOperationId == moneyOperationId).ToListAsync(ct);
            if (refunds.Count == 0)
            {
                throw new NotFoundException("Возврат денег");
            }

            await new MoneyOperationService(db, guard, currentUser, clock).CancelAsync(moneyOperationId, reason, rowVersion, ct);
            foreach (var r in refunds)
            {
                r.Cancel(ctx.UserId, reason, clock.UtcNow);
            }

            Audit(ctx, AuditActions.CustomerRefundCancelled, nameof(CustomerRefund), moneyOperationId, $"{refunds.Sum(r => r.Amount):0.00}", "отменён", reason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }
}

/// <summary>Возвраты денег покупателям (D1154) в расчётах: оплата считается уменьшенной на возвращённое.</summary>
internal static class SalesRefunds
{
    /// <summary>Вычесть возвраты денег из оплат по покупателям (paid: покупатель → оплачено), на дату asOf.</summary>
    public static async Task SubtractAsync(IKnitErpDbContext db, long org, IReadOnlyCollection<long>? customers, DateOnly? asOf, Dictionary<long, decimal> paid,
        CancellationToken ct)
    {
        var q = db.CustomerRefunds.AsNoTracking().Where(r => r.OrganizationId == org && r.Status == CustomerRefundStatus.Posted && (asOf == null || r.RefundDate <= asOf));
        if (customers is not null)
        {
            q = q.Where(r => customers.Contains(r.CustomerId));
        }

        foreach (var x in await q.GroupBy(r => r.CustomerId).Select(g => new { g.Key, Sum = g.Sum(r => r.Amount) }).ToListAsync(ct))
        {
            paid[x.Key] = paid.GetValueOrDefault(x.Key) - x.Sum;
        }
    }

    /// <summary>Строки акта сверки: возврат денег — дебет (долг покупателя растёт на выданное).</summary>
    public static async Task<IReadOnlyList<SettlementRowDto>> SettlementRowsAsync(IKnitErpDbContext db, long org, long customerId, long entityId, DateOnly to,
        CancellationToken ct) =>
        (await (from r in db.CustomerRefunds.AsNoTracking()
                where r.OrganizationId == org && r.CustomerId == customerId && r.Status == CustomerRefundStatus.Posted && r.RefundDate <= to
                join op in db.MoneyOperations.AsNoTracking() on r.MoneyOperationId equals op.Id
                join a in db.LegalEntityAccounts.AsNoTracking() on op.AccountId equals a.Id
                where a.LegalEntityId == entityId
                group r by new { op.Number, op.OperationDate } into g
                select new { g.Key.Number, g.Key.OperationDate, Sum = g.Sum(r => r.Amount) }).ToListAsync(ct))
        .Select(x => new SettlementRowDto(x.OperationDate, $"Возврат денег покупателю, {x.Number}", x.Sum, 0, 2)).ToList();
}
