using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Exchange;

/// <summary>Что попало в файл: для экрана и журнала.</summary>
public sealed record EnterpriseDataSummary(
    int Items, int Counterparties, int Warehouses, int Organizations, IReadOnlyDictionary<string, int> Documents, int SkippedReversed)
{
    public int DocumentsTotal => Documents.Values.Sum();
}

public sealed record EnterpriseDataFile(byte[] Xml, string FileName, EnterpriseDataSummary Summary);

/// <summary>
/// Выгрузка для 1С в универсальном формате обмена EnterpriseData 1.8 (D105) — его загружают «Бухгалтерия предприятия 3.0»,
/// «Управление торговлей 11», «Комплексная автоматизация 2» штатной обработкой «Загрузка данных из файла» / «Выгрузка и загрузка
/// данных EnterpriseData». В файл идут проведённые складские документы за период (сторнированные — нет) и справочники, на которые
/// они ссылаются: организации (свои юрлица), контрагенты, склады, номенклатура. Ссылки — постоянные GUID, вычисленные из
/// организации, вида и номера записи: повторная выгрузка того же документа в 1С обновляет его, а не создаёт дубль.
/// Суммы поступлений и реализаций — из строк заказа (цена с НДС пропорционально количеству документа).
/// </summary>
public sealed class EnterpriseDataExportService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const string FormatVersion = "1.8";
    public const int MaxDays = 366;
    private static readonly XNamespace Msg = "http://www.1c.ru/SSL/Exchange/Message";
    private static readonly XNamespace Ed = "http://v8.1c.ru/edi/edi_stnd/EnterpriseData/" + FormatVersion;
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Виды складских документов → объекты EnterpriseData.</summary>
    public static string DocumentType(StockOperationKind kind, bool byOrder) => kind switch
    {
        StockOperationKind.Receipt => byOrder ? "Документ.ПоступлениеТоваровУслуг" : "Документ.ОприходованиеТоваров",
        StockOperationKind.Shipment => "Документ.РеализацияТоваровУслуг",
        StockOperationKind.Transfer => "Документ.ПеремещениеТоваров",
        StockOperationKind.WriteOff => "Документ.СписаниеТоваров",
        StockOperationKind.ReturnToSupplier => "Документ.ВозвратТоваровПоставщику",
        StockOperationKind.CustomerReturn => "Документ.ВозвратТоваровОтПокупателя",
        // D354 (сессия 4, Р1): акты услуг — те же документы реализации и поступления, строки — в табличной части «Услуги».
        StockOperationKind.ServiceAct => "Документ.РеализацияТоваровУслуг",
        StockOperationKind.SupplierServiceAct => "Документ.ПоступлениеТоваровУслуг",
        _ => throw new BusinessRuleException("exchange.kind", "Этот вид документа не выгружается."),
    };

    /// <summary>
    /// Постоянный GUID записи для 1С: UUID версии 5 (RFC 9562) от «организация:вид:номер записи». Один и тот же объект
    /// всегда получает одну и ту же ссылку.
    /// </summary>
    public static Guid StableId(long organizationId, string kind, long id)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"kniterp:{organizationId}:{kind}:{id}"));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static string VatRate(decimal? percent) => percent switch
    {
        null => "БезНДС",
        0m => "НДС0",
        10m => "НДС10",
        18m => "НДС18",
        20m => "НДС20",
        _ => "НДС" + percent.Value.ToString("0.##", Inv),
    };

    public async Task<EnterpriseDataFile> ExportAsync(DateOnly from, DateOnly to, long? legalEntityId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        // Выгрузка содержит справочники, складские движения и суммы — нужны все три права.
        foreach (var permission in new[] { Permissions.CatalogView, Permissions.WarehouseReportView, Permissions.PriceView })
        {
            if (!ctx.Permissions.Has(permission))
            {
                await guard.DenyAsync(ctx, permission, ct);
            }
        }

        if (to < from || to.DayNumber - from.DayNumber > MaxDays)
        {
            throw new BusinessRuleException("exchange.period", $"Период — не длиннее {MaxDays} дней, начало не позже конца.");
        }

        var org = ctx.OrganizationId;
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org).ToListAsync(ct);
        if (entities.Count == 0)
        {
            throw new BusinessRuleException("exchange.no_legal_entity", "Заполните реквизиты юрлица организации — без них 1С не примет документы.");
        }

        if (legalEntityId is { } le && entities.All(e => e.Id != le))
        {
            throw new NotFoundException("Юрлицо");
        }

        var defaultEntity = entities.FirstOrDefault(e => e.IsDefault) ?? entities[0];
        var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseReportView);
        var docs = await db.StockDocuments.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.OrganizationId == org && d.DocumentDate >= from && d.DocumentDate <= to
                        && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Reversed)
                        && (visible == null || visible.Contains(d.WarehouseId)))
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.Id).ToListAsync(ct);
        var skipped = docs.Count(d => d.Status == StockDocumentStatus.Reversed);
        docs = docs.Where(d => d.Status == StockDocumentStatus.Posted).ToList();

        var purchaseIds = docs.Where(d => d.PurchaseOrderId != null).Select(d => d.PurchaseOrderId!.Value).Distinct().ToList();
        var salesIds = docs.Where(d => d.SalesOrderId != null).Select(d => d.SalesOrderId!.Value).Distinct().ToList();
        var purchases = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).Where(o => purchaseIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var sales = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => salesIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        // Документ относится к юрлицу своего заказа; без заказа — к основному.
        long EntityOf(StockDocument d) =>
            d.PurchaseOrderId is { } p ? purchases[p].LegalEntityId : d.SalesOrderId is { } s ? sales[s].LegalEntityId : defaultEntity.Id;
        if (legalEntityId is { } only)
        {
            docs = docs.Where(d => EntityOf(d) == only).ToList();
        }

        // D352 (сессия 4, Р2): цены документа по заказу покупателя — из строк заказа (комплект — строкой заказа).
        static IReadOnlyList<(long ItemId, decimal Quantity)> Goods(StockDocument d) => d.SalesOrderId is not null ? d.OrderLines() : d.ItemTotals();
        // D356 (сессия 4): в файл идут фактически ушедшие позиции — комплект компонентами (склад 1С ведётся по ним).
        var itemIds = docs.SelectMany(d => d.ItemTotals()).Select(l => l.ItemId).Distinct().ToList();
        var componentIds = docs.SelectMany(d => d.Lines).Where(l => l.BundleItemId != null).Select(l => l.ItemId).Distinct().ToList();
        var salePrices = componentIds.Count == 0
            ? []
            : await (await KnitErp.Application.Finance.PriceConversion.TodayAsync(db, org, clock.UtcNow, ct)).ToOrganizationAsync( // D151: веса — в одной валюте
                (await (from pr in db.ItemPrices.AsNoTracking()
                        join t in db.PriceTypes.AsNoTracking() on pr.PriceTypeId equals t.Id
                        where pr.OrganizationId == org && t.IsDefault && componentIds.Contains(pr.ItemId)
                        select new { pr.ItemId, pr.Price, pr.CurrencyCode }).ToListAsync(ct)).Select(r => (r.ItemId, r.Price, r.CurrencyCode)), ct);
        var items = await (from i in db.Items.AsNoTracking()
                           join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                           where itemIds.Contains(i.Id)
                           select new { i.Id, i.Code, i.Name, i.Article, i.Description, UnitId = u.Id, UnitCode = u.Code, u.Symbol, UnitName = u.Name })
            .ToDictionaryAsync(x => x.Id, ct);
        var counterpartyIds = docs.Where(d => d.CounterpartyId != null).Select(d => d.CounterpartyId!.Value).Distinct().ToList();
        var counterparties = await db.Counterparties.AsNoTracking().Where(c => counterpartyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var warehouseIds = docs.Where(d => !StockOperationKinds.IsServiceAct(d.Kind)) // D354: склад акта в файл не идёт
            .SelectMany(d => new[] { d.WarehouseId, d.TargetWarehouseId ?? d.WarehouseId }).Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var usedEntities = docs.Select(EntityOf).Distinct().Select(id => entities.Single(e => e.Id == id)).ToList();

        XElement Ref(string kind, long id) => new(Ed + "Ссылка", StableId(org, kind, id).ToString());
        XElement OrgRef(LegalEntity e) => new(Ed + "Организация", Ref("org", e.Id), new XElement(Ed + "Наименование", e.ShortName),
            new XElement(Ed + "НаименованиеСокращенное", e.ShortName), new XElement(Ed + "НаименованиеПолное", e.Name), new XElement(Ed + "ИНН", e.Inn),
            e.Kpp is null ? null : new XElement(Ed + "КПП", e.Kpp),
            new XElement(Ed + "ЮридическоеФизическоеЛицо", e.IsSoleProprietor ? "ФизическоеЛицо" : "ЮридическоеЛицо"));
        XElement CounterpartyRef(string element, KnitErp.Domain.Catalog.Counterparty c) => new(Ed + element, Ref("cp", c.Id),
            new XElement(Ed + "Наименование", c.Name), new XElement(Ed + "НаименованиеПолное", c.Name),
            c.Inn is null ? null : new XElement(Ed + "ИНН", c.Inn), c.Kpp is null ? null : new XElement(Ed + "КПП", c.Kpp),
            new XElement(Ed + "ЮридическоеФизическоеЛицо", c.Kpp is null && c.Inn is { Length: 12 } ? "ФизическоеЛицо" : "ЮридическоеЛицо"));
        XElement WarehouseRef(string element, long id) => new(Ed + element, Ref("wh", id), new XElement(Ed + "Наименование", warehouses[id].Name),
            new XElement(Ed + "ТипСклада", "Оптовый"));
        XElement UnitRef(long itemId) => new(Ed + "ЕдиницаИзмерения", Ref("unit", items[itemId].UnitId),
            new XElement(Ed + "ДанныеКлассификатора", new XElement(Ed + "Код", items[itemId].UnitCode), new XElement(Ed + "Наименование", items[itemId].Symbol)));
        XElement ItemRef(long itemId) => new(Ed + "Номенклатура", Ref("item", itemId), new XElement(Ed + "НаименованиеПолное", items[itemId].Name),
            new XElement(Ed + "КодВПрограмме", items[itemId].Code), new XElement(Ed + "Наименование", items[itemId].Name),
            items[itemId].Article is { } a ? new XElement(Ed + "Артикул", a) : null);
        XElement Currency() => new(Ed + "Валюта", new XElement(Ed + "Ссылка", StableId(org, "currency", 643).ToString()),
            new XElement(Ed + "ДанныеКлассификатора", new XElement(Ed + "Код", "643"), new XElement(Ed + "Наименование", "руб.")));

        var body = new XElement(Ed + "Body");
        foreach (var e in usedEntities)
        {
            body.Add(new XElement(Ed + "Справочник.Организации", new XElement(Ed + "КлючевыеСвойства", OrgRef(e).Elements())));
        }

        foreach (var c in counterparties.Values.OrderBy(c => c.Name))
        {
            body.Add(new XElement(Ed + "Справочник.Контрагенты", new XElement(Ed + "КлючевыеСвойства", CounterpartyRef("x", c).Elements())));
        }

        foreach (var w in warehouses.Values.OrderBy(w => w.Name))
        {
            body.Add(new XElement(Ed + "Справочник.Склады", new XElement(Ed + "КлючевыеСвойства", WarehouseRef("x", w.Id).Elements())));
        }

        foreach (var i in items.Values.OrderBy(i => i.Code))
        {
            body.Add(new XElement(Ed + "Справочник.Номенклатура", new XElement(Ed + "КлючевыеСвойства", ItemRef(i.Id).Elements()), UnitRef(i.Id),
                i.Description is { } d ? new XElement(Ed + "Описание", d) : null));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in docs)
        {
            var type = DocumentType(d.Kind, d.PurchaseOrderId is not null);
            counts[type] = counts.GetValueOrDefault(type) + 1;
            var entity = entities.Single(e => e.Id == EntityOf(d));
            var element = new XElement(Ed + type,
                new XElement(Ed + "КлючевыеСвойства", Ref("doc", d.Id),
                    new XElement(Ed + "Дата", d.DocumentDate.ToDateTime(new TimeOnly(12, 0)).ToString("yyyy-MM-ddTHH:mm:ss", Inv)),
                    new XElement(Ed + "Номер", d.Number), OrgRef(entity)));

            // Цены — из строк заказа: доля суммы строки заказа по количеству документа.
            IReadOnlyList<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)> Priced(
                IEnumerable<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)> orderLines) =>
                Goods(d).Select(l =>
                {
                    var match = orderLines.Where(o => o.ItemId == l.ItemId).ToList();
                    var qty = match.Sum(o => o.Quantity);
                    if (qty == 0)
                    {
                        return (l.ItemId, l.Quantity, 0m, 0m, (decimal?)null);
                    }

                    var amount = decimal.Round(match.Sum(o => o.Amount) * l.Quantity / qty, 2);
                    var vat = decimal.Round(match.Sum(o => o.Vat) * l.Quantity / qty, 2);
                    return (l.ItemId, l.Quantity, amount, vat, match[0].VatPercent);
                }).ToList();

            IReadOnlyList<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)>? priced = null;
            if (d.PurchaseOrderId is { } po)
            {
                priced = Priced(purchases[po].Lines.Select(o => (o.ItemId, o.Quantity, o.Amount, o.VatAmount, o.VatPercent)));
            }
            else if (d.SalesOrderId is { } so)
            {
                priced = Priced(sales[so].Lines.Select(o => (o.ItemId, o.Quantity, o.Amount, o.VatAmount, o.VatPercent)));
            }

            if (priced is not null && d.SalesOrderId is not null)
            {
                priced = BundleComponents(d, priced, salePrices);
            }

            if (priced is not null)
            {
                element.Add(Currency(), new XElement(Ed + "Сумма", Money(priced.Sum(p => p.Amount))), new XElement(Ed + "ЦенаВключаетНДС", "true"));
            }

            if (d.CounterpartyId is { } cid)
            {
                element.Add(CounterpartyRef("Контрагент", counterparties[cid]));
            }

            var act = StockOperationKinds.IsServiceAct(d.Kind);
            if (d.Kind == StockOperationKind.Transfer)
            {
                element.Add(WarehouseRef("СкладОтправитель", d.WarehouseId), WarehouseRef("СкладПолучатель", d.TargetWarehouseId!.Value));
            }
            else if (!act)
            {
                element.Add(WarehouseRef("Склад", d.WarehouseId)); // D354: у акта услуг склада нет
            }

            if (d.Comment is { } comment)
            {
                element.Add(new XElement(Ed + "Комментарий", comment));
            }

            var goods = new XElement(Ed + (act ? "Услуги" : "Товары"));
            var rows = priced?.Select(p => (p.ItemId, p.Quantity)).ToList() ?? Goods(d).ToList();
            foreach (var l in rows.OrderBy(l => items[l.ItemId].Code))
            {
                var row = new XElement(Ed + "Строка", ItemRef(l.ItemId), UnitRef(l.ItemId), new XElement(Ed + "Количество", Qty(l.Quantity)));
                if (priced?.FirstOrDefault(p => p.ItemId == l.ItemId) is { Quantity: > 0 } p)
                {
                    row.Add(new XElement(Ed + "Цена", Money(p.Quantity == 0 ? 0 : decimal.Round(p.Amount / p.Quantity, 2))),
                        new XElement(Ed + "Сумма", Money(p.Amount)), new XElement(Ed + "СтавкаНДС", VatRate(p.VatPercent)),
                        new XElement(Ed + "СуммаНДС", Money(p.Vat)));
                }

                goods.Add(row);
            }

            element.Add(goods);
            body.Add(element);
        }

        var now = clock.UtcNow;
        var message = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("Message",
                new XAttribute(XNamespace.Xmlns + "msg", Msg), new XAttribute(XNamespace.Xmlns + "xs", Xs), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XElement(Msg + "Header",
                    new XElement(Msg + "Format", Ed.NamespaceName),
                    new XElement(Msg + "CreationDate", now.ToString("yyyy-MM-ddTHH:mm:ss", Inv)),
                    new XElement(Msg + "AvailableVersion", FormatVersion)),
                body));
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, Async = false };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            message.Save(writer);
        }

        var summary = new EnterpriseDataSummary(items.Count, counterparties.Count, warehouses.Count, usedEntities.Count, counts, skipped);
        db.AuditEntries.Add(AuditEntry.Create(now, org, ctx.UserId, AuditActions.ExchangeExported, "EnterpriseData", $"{from:yyyyMMdd}-{to:yyyyMMdd}",
            null, $"документов: {summary.DocumentsTotal}, номенклатуры: {summary.Items}", $"EnterpriseData {FormatVersion}, {from:dd.MM.yyyy}–{to:dd.MM.yyyy}",
            currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return new EnterpriseDataFile(buffer.ToArray(), $"knitERP-EnterpriseData-{from:yyyyMMdd}-{to:yyyyMMdd}.xml", summary);
    }

    /// <summary>
    /// D356 (сессия 4): строка комплекта → строки его компонентов из документа. Сумма и НДС комплекта делятся по весу «количество ×
    /// цена продажи основного вида» (нет цены хоть у одного — по количеству); остаток округления — последнему, итог не меняется.
    /// Одна позиция отдельно и в комплекте — одной строкой файла.
    /// </summary>
    internal static IReadOnlyList<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)> BundleComponents(
        StockDocument d, IReadOnlyList<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)> priced,
        IReadOnlyDictionary<long, decimal> salePrices)
    {
        var result = new List<(long ItemId, decimal Quantity, decimal Amount, decimal Vat, decimal? VatPercent)>();
        foreach (var p in priced)
        {
            var parts = d.Lines.Where(l => l.BundleItemId == p.ItemId).GroupBy(l => l.ItemId)
                .Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity))).OrderBy(c => c.ItemId).ToList();
            if (parts.Count == 0)
            {
                result.Add(p);
                continue;
            }

            var byPrice = parts.All(c => salePrices.GetValueOrDefault(c.ItemId) > 0);
            var weights = parts.Select(c => c.Quantity * (byPrice ? salePrices[c.ItemId] : 1)).ToList();
            var total = weights.Sum();
            decimal amountLeft = p.Amount, vatLeft = p.Vat;
            for (var i = 0; i < parts.Count; i++)
            {
                var last = i == parts.Count - 1;
                var amount = last ? amountLeft : decimal.Round(p.Amount * weights[i] / total, 2);
                var vat = last ? vatLeft : decimal.Round(p.Vat * weights[i] / total, 2);
                amountLeft -= amount;
                vatLeft -= vat;
                result.Add((parts[i].ItemId, parts[i].Quantity, amount, vat, p.VatPercent));
            }
        }

        return result.GroupBy(r => r.ItemId)
            .Select(g => (g.Key, g.Sum(r => r.Quantity), g.Sum(r => r.Amount), g.Sum(r => r.Vat), g.First().VatPercent)).ToList();
    }

    private static string Money(decimal value) => value.ToString("0.00", Inv);

    private static string Qty(decimal value) => value.ToString("0.######", Inv);
}
