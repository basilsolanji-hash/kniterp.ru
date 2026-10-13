using System.Globalization;
using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Printing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Изображение для печатной формы (D1120); DataUri — для HTML-формы.</summary>
public sealed record SalesPrintImage(string ContentType, byte[] Content)
{
    public string DataUri => $"data:{ContentType};base64,{Convert.ToBase64String(Content)}";
}

/// <summary>
/// Оформление печатной формы по настройкам юрлица (D1120, D1121): логотип, печать, факсимиле подписей и строка QR для оплаты
/// (ГОСТ Р 56042-2014). null — не выводить.
/// </summary>
public sealed record SalesPrintDecorations(
    SalesPrintImage? Logo = null, SalesPrintImage? Stamp = null, SalesPrintImage? DirectorSignature = null, SalesPrintImage? AccountantSignature = null,
    string? PaymentQr = null)
{
    public static readonly SalesPrintDecorations None = new();
}

/// <summary>Печатная форма «Заказ покупателя» (D1121): цены — со скидкой, суммы — как в заказе.</summary>
public sealed record SalesOrderPrintDto(
    string Number, DateOnly Date, DateOnly? ShipDate, PrintPartyDto Seller, PrintRequisites Requisites, PrintPartyDto Buyer, string? DeliveryAddress,
    string Warehouse, bool PricesIncludeVat, IReadOnlyList<PrintLineDto> Lines, decimal Total, decimal VatTotal, string TotalInWords, string CurrencyCode,
    string Status, bool Cancelled, string? Comment, string? CustomerReference);

/// <summary>Данные для QR-кода оплаты по ГОСТ Р 56042-2014 (обязательные реквизиты — получатель, счёт, банк, БИК, корр. счёт).</summary>
public sealed record PaymentQrData(
    string? Name, string? PersonalAcc, string? BankName, string? Bic, string? CorrespAcc, string? PayeeInn, string? Kpp, string? Purpose, decimal Sum);

/// <summary>
/// Строка QR-кода платежа по ГОСТ Р 56042-2014 (D1121): «ST00012|Name=…|PersonalAcc=…|BankName=…|BIC=…|CorrespAcc=…|PayeeINN=…|KPP=…|Purpose=…|Sum=копейки».
/// ST0001 — формат, 2 — кодировка UTF-8. Разделитель «|» внутри значений недопустим — заменяется пробелом. Без обязательного реквизита — null:
/// QR, который банк не поймёт, хуже, чем никакой.
/// </summary>
public static class PaymentQr
{
    public static string? Build(PaymentQrData d)
    {
        var name = Clean(d.Name);
        var account = Digits(d.PersonalAcc);
        var bank = Clean(d.BankName);
        var bic = Digits(d.Bic);
        var corr = Digits(d.CorrespAcc);
        if (name is null || account is not { Length: 20 } || bank is null || bic is not { Length: 9 } || corr is not { Length: 20 } || d.Sum <= 0)
        {
            return null;
        }

        var sb = new StringBuilder("ST00012");
        Add(sb, "Name", name);
        Add(sb, "PersonalAcc", account);
        Add(sb, "BankName", bank);
        Add(sb, "BIC", bic);
        Add(sb, "CorrespAcc", corr);
        Add(sb, "PayeeINN", Digits(d.PayeeInn));
        Add(sb, "KPP", Digits(d.Kpp));
        Add(sb, "Purpose", Clean(d.Purpose) is { } p ? p.Length > 210 ? p[..210] : p : null);
        Add(sb, "Sum", decimal.Round(d.Sum * 100m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void Add(StringBuilder sb, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append('|').Append(key).Append('=').Append(value);
        }
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = new string(value.Select(c => c == '|' || char.IsControl(c) ? ' ' : c).ToArray());
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Digits(string? value) =>
        value is null ? null : new string(value.Where(char.IsAsciiDigit).ToArray()) is { Length: > 0 } s ? s : null;
}

/// <summary>
/// Печать документов продаж (D1120, D1121): форма «Заказ покупателя» и оформление счёта, УПД и заказа по настройкам юрлица-продавца.
/// Права — как у печатных форм продаж: «Продажи: просмотр» и «Цены и суммы»; чужая организация — 404.
/// </summary>
public sealed class SalesDocumentPrintService(IKnitErpDbContext db, IAccessGuard guard)
{
    public async Task<SalesOrderPrintDto> OrderAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                        .SingleOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var entity = await db.LegalEntities.AsNoTracking().SingleAsync(e => e.OrganizationId == ctx.OrganizationId && e.Id == order.LegalEntityId, ct);
        var account = await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == entity.Id && (order.BankAccountId == null ? a.IsDefault : a.Id == order.BankAccountId))
            .FirstOrDefaultAsync(ct);
        var itemIds = order.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, UnitCode = u.Code })
            .ToDictionaryAsync(x => x.Id, ct);
        var buyer = await db.Counterparties.AsNoTracking().Where(c => c.Id == order.CustomerId && c.OrganizationId == ctx.OrganizationId)
            .Select(c => new PrintPartyDto(c.Name, c.Inn, c.Kpp, c.Address)).SingleAsync(ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == order.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        var lines = order.Lines.OrderBy(l => items[l.ItemId].Code)
            .Select((l, i) => new PrintLineDto(i + 1, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Symbol, items[l.ItemId].UnitCode,
                l.Quantity, Money.Round(l.NetPrice), l.VatPercent, l.Amount - l.VatAmount, l.VatAmount, l.Amount))
            .ToList();
        return new SalesOrderPrintDto(order.Number, order.OrderDate, order.ShipDate, new PrintPartyDto(entity.Name, entity.Inn, entity.Kpp, entity.LegalAddress),
            new PrintRequisites(entity.LegalAddress, account?.BankName, account?.Bic, account?.Account, account?.CorrAccount, entity.DirectorName,
                entity.AccountantName, entity.DirectorPosition, entity.IsSoleProprietor, entity.Ogrn, entity.VatExempt),
            buyer, order.DeliveryAddress, warehouse, order.PricesIncludeVat, lines, order.Total, order.VatTotal,
            AmountInWords.Format(order.Total, org.CurrencyCode), org.CurrencyCode, SalesOrder.StatusName(order.Status),
            order.Status == SalesOrderStatus.Cancelled, order.Comment, order.CustomerReference);
    }

    /// <summary>Логотип, печать, подписи и QR — по настройкам юрлица-продавца документа (заказ, счёт или УПД по отгрузке).</summary>
    public async Task<SalesPrintDecorations> DecorationsAsync(SalesMailDocument document, long id, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var org = ctx.OrganizationId;
        long entityId;
        PaymentQrData? qr = null;
        switch (document)
        {
            case SalesMailDocument.Order:
                entityId = await db.SalesOrders.AsNoTracking().Where(o => o.Id == id && o.OrganizationId == org).Select(o => (long?)o.LegalEntityId)
                               .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Заказ покупателя");
                break;
            case SalesMailDocument.Invoice:
                var invoice = await db.CustomerInvoices.AsNoTracking().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id && i.OrganizationId == org, ct)
                              ?? throw new NotFoundException("Счёт покупателю");
                entityId = invoice.LegalEntityId;
                var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CurrencyCode).SingleAsync(ct);
                if (currency == "RUB" && invoice.Status == CustomerInvoiceStatus.Issued)
                {
                    var e = await db.LegalEntities.AsNoTracking().SingleAsync(x => x.OrganizationId == org && x.Id == entityId, ct);
                    var a = await db.LegalEntityAccounts.AsNoTracking()
                        .Where(x => x.OrganizationId == org && x.LegalEntityId == entityId && (invoice.BankAccountId == null ? x.IsDefault : x.Id == invoice.BankAccountId))
                        .FirstOrDefaultAsync(ct);
                    var vat = invoice.VatTotal > 0 ? $", в т.ч. НДС {invoice.VatTotal.ToString("0.00", CultureInfo.InvariantCulture)}" : ", без НДС";
                    qr = new PaymentQrData(e.Name, a?.Account, a?.BankName, a?.Bic, a?.CorrAccount, e.Inn, e.Kpp,
                        $"Оплата по счёту № {invoice.Number} от {invoice.InvoiceDate:dd.MM.yyyy}{vat}", invoice.Total);
                }

                break;
            case SalesMailDocument.Upd:
                var orderId = await db.StockDocuments.AsNoTracking()
                                  .Where(d => d.Id == id && d.OrganizationId == org && d.Kind == StockOperationKind.Shipment).Select(d => d.SalesOrderId)
                                  .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Документ");
                entityId = await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId && o.OrganizationId == org).Select(o => o.LegalEntityId).SingleAsync(ct);
                break;
            default:
                throw new NotFoundException("Документ");
        }

        var flags = (await db.SalesDocumentSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == org && s.LegalEntityId == entityId, ct))?.Flags
                    ?? SalesDocumentFlags.Default;
        var (logo, stamps) = document switch
        {
            SalesMailDocument.Order => (flags.LogoOnOrder, flags.StampOnOrder),
            SalesMailDocument.Invoice => (flags.LogoOnInvoice, flags.StampOnInvoice),
            _ => (false, flags.StampOnUpd),
        };
        var kinds = new List<SalesDocumentImageKind>();
        if (logo)
        {
            kinds.Add(SalesDocumentImageKind.Logo);
        }

        if (stamps)
        {
            kinds.AddRange([SalesDocumentImageKind.Stamp, SalesDocumentImageKind.DirectorSignature, SalesDocumentImageKind.AccountantSignature]);
        }

        var images = kinds.Count == 0
            ? new Dictionary<SalesDocumentImageKind, SalesPrintImage>()
            : (await db.SalesDocumentImages.AsNoTracking()
                .Where(i => i.OrganizationId == org && i.LegalEntityId == entityId && kinds.Contains(i.Kind)).ToListAsync(ct))
            .ToDictionary(i => i.Kind, i => new SalesPrintImage(i.ContentType, i.Content));
        return new SalesPrintDecorations(images.GetValueOrDefault(SalesDocumentImageKind.Logo), images.GetValueOrDefault(SalesDocumentImageKind.Stamp),
            images.GetValueOrDefault(SalesDocumentImageKind.DirectorSignature), images.GetValueOrDefault(SalesDocumentImageKind.AccountantSignature),
            flags.QrOnInvoice && qr is not null ? PaymentQr.Build(qr) : null);
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }
}

/// <summary>Формирование PDF документа продаж (D1123): данные — те же, что у печатной формы, с теми же правами.</summary>
public interface ISalesPdfRenderer
{
    byte[] Invoice(InvoicePrintDto doc, SalesPrintDecorations decorations);

    byte[] Upd(UpdPrintDto doc, SalesPrintDecorations decorations);

    byte[] Order(SalesOrderPrintDto doc, SalesPrintDecorations decorations);

    /// <summary>D1142: прайс-лист — группы подзаголовками, остаток при складских правах.</summary>
    byte[] PriceList(PriceListDto list);
}

public static class SalesDocumentPdf
{
    /// <summary>PDF документа и имя файла. Права и организация — у переданного guard (пользователь или отправитель писем).</summary>
    public static async Task<EmailAttachment> BuildAsync(IKnitErpDbContext db, IAccessGuard guard, ISalesPdfRenderer renderer, SalesMailDocument document,
        long id, CancellationToken ct)
    {
        var print = new PrintService(db, guard);
        var extra = new SalesDocumentPrintService(db, guard);
        switch (document)
        {
            case SalesMailDocument.Order:
            {
                var doc = await extra.OrderAsync(id, ct);
                return new EmailAttachment(FileName("Заказ", doc.Number), "application/pdf", renderer.Order(doc, await extra.DecorationsAsync(document, id, ct)));
            }

            case SalesMailDocument.Invoice:
            {
                var doc = await print.InvoiceAsync(id, ct);
                return new EmailAttachment(FileName("Счёт", doc.Number), "application/pdf", renderer.Invoice(doc, await extra.DecorationsAsync(document, id, ct)));
            }

            case SalesMailDocument.Upd:
            {
                var doc = await print.UpdAsync(id, ct);
                return new EmailAttachment(FileName("УПД", doc.ShipmentNumber), "application/pdf", renderer.Upd(doc, await extra.DecorationsAsync(document, id, ct)));
            }

            default:
                throw new NotFoundException("Документ");
        }
    }

    private static string FileName(string kind, string number)
    {
        var safe = new string(number.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '-' : c).ToArray());
        return $"{kind} {safe}.pdf";
    }
}
