using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Printing;

/// <summary>Сторона документа в печатной форме. Kpp — только сверенный (D15).</summary>
public sealed record PrintPartyDto(string Name, string? Inn, string? Kpp, string? Address);

/// <summary>
/// Строка печатной формы: цена и стоимость без НДС, НДС, стоимость с НДС. Для УПД (D79): TnVedCode — графа 1б,
/// страна происхождения ввезённого товара и номер декларации — графы 10, 10а, 11 (у российского товара — прочерк).
/// </summary>
public sealed record PrintLineDto(
    int No, string Code, string Name, string UnitSymbol, string UnitCode, decimal Quantity, decimal Price, decimal? VatPercent,
    decimal AmountWithoutVat, decimal VatAmount, decimal Amount, string? TnVedCode = null, string? OriginCountryCode = null,
    string? OriginCountryName = null, string? CustomsDeclaration = null);

public sealed record InvoicePrintDto(
    string Number, DateOnly Date, DateOnly? DueDate, PrintPartyDto Seller, PrintRequisites Requisites, PrintPartyDto Buyer, string Basis,
    bool PricesIncludeVat, IReadOnlyList<PrintLineDto> Lines, decimal Total, decimal VatTotal, string TotalInWords, string CurrencyCode,
    bool Cancelled);

/// <summary>
/// УПД со статусом 1 (D68, D71). Number — порядковый номер счёта-фактуры (цифры номера отгрузки), ShipmentNumber — документ
/// об отгрузке (строка 5а). PaymentDocuments — строка 5: платёжные документы предоплаты до даты отгрузки. Warnings — чего
/// не хватает для правильного документа; на бумагу не выводятся.
/// </summary>
public sealed record UpdPrintDto(
    string Number, DateOnly Date, string ShipmentNumber, PrintPartyDto Seller, PrintRequisites Requisites, PrintPartyDto Buyer, string Basis,
    string Warehouse, IReadOnlyList<PrintLineDto> Lines, decimal TotalWithoutVat, decimal VatTotal, decimal Total, string TotalInWords, string Currency,
    bool Reversed, string? PaymentDocuments, IReadOnlyList<string> Warnings);

/// <summary>
/// Данные печатных форм (D68): счёт на оплату — по счёту покупателю; УПД (статус 1: счёт-фактура и передаточный документ) —
/// по проведённой отгрузке по заказу, цены — из заказа. Формы печатаются по-русски: это документы для российского учёта.
/// Права — как у денег продаж: «Продажи: просмотр» и «Цены и суммы».
/// </summary>
public sealed partial class PrintService(IKnitErpDbContext db, IAccessGuard guard)
{
    private static readonly Dictionary<string, string> CurrencyNumeric = new() { ["RUB"] = "643", ["KZT"] = "398", ["UZS"] = "860", ["BYN"] = "933" };

    public async Task<InvoicePrintDto> InvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var invoice = await db.CustomerInvoices.AsNoTracking().Include(i => i.Lines)
                          .SingleOrDefaultAsync(i => i.Id == invoiceId && i.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Счёт покупателю");
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var order = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == invoice.SalesOrderId, ct);
        var (entity, account) = await SellerAsync(ctx.OrganizationId, invoice.LegalEntityId, invoice.BankAccountId, ct);
        var items = await ItemsAsync(invoice.Lines.Select(l => l.ItemId), ct);
        var lines = invoice.Lines
            .Select(l => (Item: items[l.ItemId], l.Quantity, l.Price, l.VatPercent, l.Amount, l.VatAmount))
            .OrderBy(l => l.Item.Code)
            .Select((l, i) => new PrintLineDto(i + 1, l.Item.Code, l.Item.Name, l.Item.Symbol, l.Item.UnitCode, l.Quantity, l.Price, l.VatPercent,
                l.Amount - l.VatAmount, l.VatAmount, l.Amount))
            .ToList();
        return new InvoicePrintDto(invoice.Number, invoice.InvoiceDate, invoice.DueDate, Seller(entity), Requisites(entity, account),
            await BuyerAsync(invoice.CustomerId, ct), Basis(order), invoice.PricesIncludeVat, lines, invoice.Total, invoice.VatTotal,
            AmountInWords.Format(invoice.Total, org.CurrencyCode), org.CurrencyCode, invoice.Status == CustomerInvoiceStatus.Cancelled);
    }

    /// <summary>D1140 (сессия «Продажи»): XML УПД (формат ФНС 5.03) по проведённой отгрузке — для загрузки в оператора ЭДО.</summary>
    public async Task<UpdXmlFile> UpdXmlAsync(long stockDocumentId, IReadOnlyDictionary<int, IReadOnlyList<string>>? markingCodes, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var upd = await UpdAsync(stockDocumentId, ct);
        var ctx = await DemandAsync(ct);
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return UpdXml.Build(upd, TimeZoneInfo.ConvertTimeBySystemTimeZoneId(nowUtc, tz), Guid.NewGuid(), markingCodes);
    }

    public async Task<UpdPrintDto> UpdAsync(long stockDocumentId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
                      .SingleOrDefaultAsync(d => d.Id == stockDocumentId && d.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Документ");
        // D354 (сессия 4, Р1): УПД — и по акту оказания услуг.
        if (doc.Kind is not (StockOperationKind.Shipment or StockOperationKind.ServiceAct) || doc.SalesOrderId is null
            || doc.Status is not (StockDocumentStatus.Posted or StockDocumentStatus.Reversed))
        {
            throw new BusinessRuleException("print.upd.document", "УПД печатается по проведённой отгрузке или акту оказания услуг по заказу покупателя.");
        }

        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == doc.SalesOrderId, ct);
        var (entity, account) = await SellerAsync(ctx.OrganizationId, order.LegalEntityId, order.BankAccountId, ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == doc.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        var warnings = new List<string>();

        // D354 (сессия 4, Р1): проведённые отгрузка и акты заказа одной датой — один УПД (UpdMerge): строки товаров и услуг вместе,
        // номер — по отгрузке. Акт, вошедший в УПД отгрузки, печатается этим же УПД.
        var parts = new List<(string Number, IReadOnlyList<(long ItemId, decimal Quantity)> Lines)>();
        if (doc.Status == StockDocumentStatus.Posted)
        {
            var candidates = await UpdMerge.CandidatesAsync(db, ctx.OrganizationId, [order.Id], [doc.DocumentDate], ct);
            var hosts = UpdMerge.Hosts(candidates);
            var hostId = hosts.TryGetValue(doc.Id, out var h) ? h : doc.Id;
            var host = candidates.Single(c => c.Id == hostId);
            if (hostId != doc.Id)
            {
                warnings.Add($"Акт {doc.Number} входит в УПД отгрузки {host.Number} той же датой — печатается общий документ.");
            }

            parts.Add((host.Number, host.OrderLines));
            parts.AddRange(candidates.Where(c => hosts.GetValueOrDefault(c.Id) == hostId && c.Id != hostId).OrderBy(c => c.Id)
                .Select(c => (c.Number, c.OrderLines)));
        }
        else
        {
            parts.Add((doc.Number, doc.OrderLines()));
        }

        // D352 (сессия 4, Р2): комплект — одной строкой по цене заказа (код товара — код комплекта); компоненты видны в отгрузке.
        var ordered = parts.SelectMany(p => p.Lines).GroupBy(l => l.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList();
        var items = await ItemsAsync(ordered.Select(l => l.ItemId), ct);
        var lines = ordered
            .Select(l =>
            {
                var orderLine = order.Lines.Single(o => o.ItemId == l.ItemId);
                var (amount, vat) = Money.LineAmounts(l.Quantity, orderLine.NetPrice, orderLine.VatPercent, order.PricesIncludeVat);
                return (Item: items[l.ItemId], l.Quantity, orderLine.VatPercent, Amount: amount, Vat: vat);
            })
            .OrderBy(l => l.Item.IsService).ThenBy(l => l.Item.Code)
            // D354: у услуги «код вида товара» (графа 1б) и страна происхождения — прочерк.
            .Select((l, i) => new PrintLineDto(i + 1, l.Item.Code, l.Item.Name, l.Item.Symbol, l.Item.UnitCode, l.Quantity,
                Money.Round((l.Amount - l.Vat) / l.Quantity), l.VatPercent, l.Amount - l.Vat, l.Vat, l.Amount,
                l.Item.IsService ? NoValue : l.Item.TnVed,
                l.Item.Imported ? l.Item.CountryCode : null, l.Item.Imported ? l.Item.CountryName : null, l.Item.Imported ? l.Item.Declaration : null))
            .ToList();
        var total = lines.Sum(l => l.Amount);
        var country = Countries.Get(org.CountryCode);
        var buyer = await BuyerAsync(doc.CounterpartyId!.Value, ct);
        var buyerIsRussianCompany = await db.Counterparties.AsNoTracking().Where(c => c.Id == doc.CounterpartyId)
            .Select(c => c.CountryCode == Countries.Russia).SingleAsync(ct);

        // Строка 5: оплата (предоплата) до отгрузки — номер и дата платёжного документа покупателя.
        // D85: оплаты, разнесённые на заказ этой отгрузки.
        var prepayments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.OrganizationId == ctx.OrganizationId && p.Status == CustomerPaymentStatus.Posted && p.PaymentDate <= doc.DocumentDate
                        && db.CustomerPaymentAllocations.Any(a => a.PaymentId == p.Id && a.OrderId == order.Id && a.RemovedAtUtc == null))
            .OrderBy(p => p.PaymentDate).Select(p => new { p.DocumentNumber, p.PaymentDate }).ToListAsync(ct);
        var paymentDocuments = prepayments.Count == 0 ? null
            : string.Join("; ", prepayments.Select(p => $"№ {p.DocumentNumber ?? "—"} от {p.PaymentDate:dd.MM.yyyy}"));

        var seller = Seller(entity);
        if (org.CountryCode != Countries.Russia)
        {
            warnings.Add("Форма УПД — российская; для организации другой страны нужен документ по её законодательству.");
        }

        if (seller.Kpp is null && !entity.IsSoleProprietor && org.CountryCode == Countries.Russia)
        {
            warnings.Add($"У юрлица «{entity.ShortName}» не указан КПП — строка 2б неполная. Заполните его в разделе «Юрлица и счета».");
        }

        if (seller.Address is null)
        {
            warnings.Add($"Не указан адрес продавца (строка 2а) — заполните юридический адрес «{entity.ShortName}» в разделе «Юрлица и счета».");
        }

        if (lines.Any(l => l.OriginCountryCode is not null && l.CustomsDeclaration is null))
        {
            warnings.Add("У ввезённого товара не указан номер декларации на товары (графа 11) — заполните его в карточке номенклатуры.");
        }

        if (entity.IsSoleProprietor && entity.Ogrn is null)
        {
            warnings.Add("Для подписи индивидуального предпринимателя нужен ОГРНИП (реквизиты свидетельства о госрегистрации) — раздел «Юрлица и счета».");
        }

        if (entity.VatExempt && lines.Any(l => l.VatPercent is > 0))
        {
            warnings.Add($"«{entity.ShortName}» освобождено от НДС, а в заказе указан НДС. Проверьте ставки: нужно «без НДС».");
        }

        if (buyer.Inn is null && buyerIsRussianCompany)
        {
            warnings.Add("У покупателя не указан ИНН (строка 6б) — заполните его в карточке контрагента.");
        }

        if (buyer.Address is null)
        {
            warnings.Add("Не указан адрес покупателя (строка 6а) — заполните его в карточке контрагента.");
        }

        if (entity.DirectorName is null)
        {
            warnings.Add(entity.IsSoleProprietor
                ? "Не указаны ФИО предпринимателя для подписи — раздел «Юрлица и счета»."
                : $"Не указан руководитель «{entity.ShortName}» для подписи — раздел «Юрлица и счета».");
        }

        if (prepayments.Any(p => p.DocumentNumber is null))
        {
            warnings.Add("У оплаты до отгрузки не указан номер платёжного документа покупателя (строка 5).");
        }

        if (prepayments.Count > 0)
        {
            warnings.Add("Отгрузка в счёт предоплаты: в строке 5б указываются авансовые счета-фактуры, выставленные при получении оплаты. "
                         + "В knitERP авансовые счета-фактуры пока не оформляются — впишите их реквизиты из учётной программы.");
        }

        return new UpdPrintDto(VatInvoiceNumber(parts[0].Number), doc.DocumentDate, string.Join(", ", parts.Select(p => p.Number)), seller,
            Requisites(entity, account), buyer,
            Basis(order), warehouse, lines, lines.Sum(l => l.AmountWithoutVat), lines.Sum(l => l.VatAmount), total,
            AmountInWords.Format(total, org.CurrencyCode),
            $"{char.ToUpperInvariant(country.CurrencyName[0])}{country.CurrencyName[1..]}, {CurrencyNumeric.GetValueOrDefault(org.CurrencyCode, org.CurrencyCode)}",
            doc.Status == StockDocumentStatus.Reversed, paymentDocuments, warnings);
    }

    /// <summary>Прочерк в графе УПД (D354: «код вида товара» у услуги).</summary>
    public const string NoValue = "--";

    /// <summary>
    /// Порядковый номер счёта-фактуры (подп. «а» п. 1 Правил заполнения, ПП № 1137) — цифры номера отгрузки без префикса и ведущих нулей:
    /// «ОТ-000015» → «15». Нумерация отгрузок сквозная по организации и идёт по хронологии их оформления (D71).
    /// </summary>
    public static string VatInvoiceNumber(string shipmentNumber)
    {
        var digits = new string(shipmentNumber.SkipWhile(c => !char.IsAsciiDigit(c)).Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        return digits.Length == 0 ? shipmentNumber : digits;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    /// <summary>Юрлицо-продавец и счёт для оплаты: указанный или основной счёт юрлица (D78).</summary>
    private async Task<(LegalEntity Entity, LegalEntityAccount? Account)> SellerAsync(long organizationId, long entityId, long? accountId,
        CancellationToken ct)
    {
        var entity = await db.LegalEntities.AsNoTracking().SingleAsync(e => e.OrganizationId == organizationId && e.Id == entityId, ct);
        var account = await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.LegalEntityId == entityId && (accountId == null ? a.IsDefault : a.Id == accountId))
            .FirstOrDefaultAsync(ct);
        return (entity, account);
    }

    private static PrintPartyDto Seller(LegalEntity e) => new(e.Name, e.Inn, e.Kpp, e.LegalAddress);

    private static PrintRequisites Requisites(LegalEntity e, LegalEntityAccount? a) =>
        new(e.LegalAddress, a?.BankName, a?.Bic, a?.Account, a?.CorrAccount, e.DirectorName, e.AccountantName, e.DirectorPosition,
            e.IsSoleProprietor, e.Ogrn, e.VatExempt);

    private async Task<PrintPartyDto> BuyerAsync(long counterpartyId, CancellationToken ct) =>
        await db.Counterparties.AsNoTracking().Where(c => c.Id == counterpartyId)
            .Select(c => new PrintPartyDto(c.Name, c.Inn, c.Kpp, c.Address)).SingleAsync(ct);

    /// <summary>Основание: договор из заказа (если указан) и сам заказ.</summary>
    private static string Basis(SalesOrder order) =>
        (order.CustomerReference is { } reference ? reference + "; " : string.Empty) + $"заказ {order.Number} от {order.OrderDate:dd.MM.yyyy}";

    private sealed record PrintItem(
        string Code, string Name, string Symbol, string UnitCode, string? TnVed = null, string? CountryCode = null, string? CountryName = null,
        string? Declaration = null, bool IsService = false)
    {
        /// <summary>Ввезённый товар: страна происхождения указана и это не Россия (643). Услуга (D354) — не товар.</summary>
        public bool Imported => !IsService && CountryCode is not null && CountryCode != KnitErp.Domain.Catalog.ItemDetailRules.RussiaCode;
    }

    private async Task<Dictionary<long, PrintItem>> ItemsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => list.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new
            {
                i.Id, Item = new PrintItem(i.Code, i.Name, u.Symbol, u.Code, i.TnVedCode, i.OriginCountryCode, i.OriginCountryName, i.CustomsDeclaration,
                    i.Type == KnitErp.Domain.Catalog.ItemType.Service),
            })
            .ToDictionaryAsync(x => x.Id, x => x.Item, ct);
    }
}
