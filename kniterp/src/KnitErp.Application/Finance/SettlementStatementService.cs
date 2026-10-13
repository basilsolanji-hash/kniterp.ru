using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

/// <summary>Строка акта: Debit — увеличивает долг контрагента перед нами (отгрузка, оплата поставщику), Credit — уменьшает.</summary>
/// <summary>Rank — порядок в пределах дня: документы, затем возвраты, затем оплаты.</summary>
public sealed record SettlementRowDto(DateOnly Date, string Document, decimal Debit, decimal Credit, int Rank = 0);

/// <summary>
/// Акт сверки взаимных расчётов за период. Opening и Closing — сальдо по нашим данным: больше нуля — долг контрагента перед
/// юрлицом, меньше — долг юрлица перед контрагентом.
/// </summary>
public sealed record SettlementStatementDto(
    long LegalEntityId, string Entity, string EntityShort, string? EntityInn, string? EntityKpp, string? DirectorPosition, string? DirectorName,
    string? AccountantName, long CounterpartyId, string Counterparty, string? CounterpartyInn, string? CounterpartyKpp, DateOnly From, DateOnly To,
    string CurrencyCode, decimal Opening, IReadOnlyList<SettlementRowDto> Rows)
{
    public decimal DebitTotal => Rows.Sum(r => r.Debit);
    public decimal CreditTotal => Rows.Sum(r => r.Credit);
    public decimal Closing => Opening + DebitTotal - CreditTotal;
}

public sealed record SettlementOptionsDto(
    IReadOnlyList<KnitErp.Application.Structure.LookupDto> Counterparties, IReadOnlyList<KnitErp.Application.Structure.LookupDto> LegalEntities);

/// <summary>
/// Акт сверки (D85) по контрагенту и своему юрлицу. Источники — те же, что в расчётах с покупателями и поставщиками:
/// проведённые отгрузки, возвраты и поступления по заказам (по ценам заказа с НДС, по каждому документу с округлением до копеек)
/// и действующие оплаты. Юрлицо документа — юрлицо заказа; юрлицо оплаты — юрлицо её счёта или кассы, без счёта — заказа,
/// без заказа — основное. Форма акта законом не утверждена — состав по обычаю делового оборота: наши данные заполнены,
/// графы контрагента — для его отметок.
/// </summary>
public sealed class SettlementStatementService(IKnitErpDbContext db, IAccessGuard guard)
{
    public const int MaxDays = 3 * 366;

    /// <summary>Выбор для акта: контрагенты, чьи расчёты пользователю видны (покупатели — с правом продаж, поставщики — закупок), и свои юрлица.</summary>
    public async Task<SettlementOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var sales = ctx.Permissions.Has(Permissions.SalesView);
        var purchases = ctx.Permissions.Has(Permissions.PurchaseView);
        var counterparties = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && (c.IsCustomer || c.IsSupplier)
                        && (!c.IsCustomer || sales) && (!c.IsSupplier || purchases))
            .OrderBy(c => c.Name).Select(c => new KnitErp.Application.Structure.LookupDto(c.Id, c.Name)).ToListAsync(ct);
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && !e.IsArchived)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName)
            .Select(e => new KnitErp.Application.Structure.LookupDto(e.Id, e.ShortName)).ToListAsync(ct);
        return new SettlementOptionsDto(counterparties, entities);
    }

    public async Task<SettlementStatementDto> BuildAsync(long counterpartyId, long? legalEntityId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        if (to < from || to.DayNumber - from.DayNumber > MaxDays)
        {
            throw new BusinessRuleException("settlement.period", "Период акта — от одного дня до трёх лет, дата «по» не раньше даты «с».");
        }

        var org = ctx.OrganizationId;
        var cp = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == counterpartyId && c.OrganizationId == org, ct)
                 ?? throw new NotFoundException("Контрагент");
        if (cp.IsCustomer)
        {
            await guard.DemandAsync(Permissions.SalesView, ct);
        }

        if (cp.IsSupplier)
        {
            await guard.DemandAsync(Permissions.PurchaseView, ct);
        }

        var entity = legalEntityId is { } eid
            ? await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eid && e.OrganizationId == org, ct) ?? throw new NotFoundException("Юрлицо")
            : await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.OrganizationId == org && e.IsDefault, ct)
              ?? throw new BusinessRuleException("legal_entity.no_default", "Нет основного юрлица — добавьте его в разделе «Юрлица и счета».");
        var defaultEntity = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && e.IsDefault).Select(e => (long?)e.Id)
            .SingleOrDefaultAsync(ct);
        var accounts = await db.LegalEntityAccounts.AsNoTracking().Where(a => a.OrganizationId == org).ToDictionaryAsync(a => a.Id, a => a.LegalEntityId, ct);

        var rows = new List<SettlementRowDto>();
        rows.AddRange(await SalesDocumentsAsync(org, cp.Id, entity.Id, to, ct));
        rows.AddRange(await PurchaseDocumentsAsync(org, cp.Id, entity.Id, to, ct));

        var orderEntities = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.CustomerId == cp.Id)
            .Select(o => new { o.Id, o.LegalEntityId }).ToDictionaryAsync(o => o.Id, o => o.LegalEntityId, ct);
        var customerPayments = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.CustomerId == cp.Id && p.Status == CustomerPaymentStatus.Posted && p.PaymentDate <= to)
            .Select(p => new { p.PaymentDate, p.Number, p.DocumentNumber, p.CashOrderNumber, p.Amount, p.MoneyAccountId, p.SalesOrderId }).ToListAsync(ct);
        rows.AddRange(customerPayments
            .Where(p => EntityOf(p.MoneyAccountId, p.SalesOrderId is { } o ? orderEntities.GetValueOrDefault(o) : null) == entity.Id)
            .Select(p => new SettlementRowDto(p.PaymentDate,
                p.CashOrderNumber is { } ko ? $"Оплата наличными, {ko}" : $"Оплата{(p.DocumentNumber is { } n ? $" по п/п № {n}" : "")}, {p.Number}",
                0, p.Amount, 2)));
        rows.AddRange(await KnitErp.Application.Sales.SalesRefunds.SettlementRowsAsync(db, org, cp.Id, entity.Id, to, ct)); // D1154 (сессия «Продажи»)
        rows.AddRange(await KnitErp.Application.Sales.SalesOpeningBalances.SettlementRowsAsync(db, org, cp.Id, entity.Id, to, ct)); // D1146 (сессия «Продажи»)

        var purchaseEntities = await db.PurchaseOrders.AsNoTracking().Where(o => o.OrganizationId == org && o.SupplierId == cp.Id)
            .Select(o => new { o.Id, o.LegalEntityId }).ToDictionaryAsync(o => o.Id, o => o.LegalEntityId, ct);
        var supplierPayments = await db.SupplierPayments.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.SupplierId == cp.Id && p.Status == SupplierPaymentStatus.Posted && p.PaymentDate <= to)
            .Select(p => new { p.PaymentDate, p.Number, p.CashOrderNumber, p.Amount, p.MoneyAccountId, p.PurchaseOrderId }).ToListAsync(ct);
        rows.AddRange(supplierPayments
            .Where(p => EntityOf(p.MoneyAccountId, p.PurchaseOrderId is { } o ? purchaseEntities.GetValueOrDefault(o) : null) == entity.Id)
            .Select(p => new SettlementRowDto(p.PaymentDate,
                p.CashOrderNumber is { } ko ? $"Оплата поставщику наличными, {ko}" : $"Оплата поставщику, {p.Number}", p.Amount, 0, 2)));
        rows.AddRange(await KnitErp.Application.Purchasing.PurchaseOpeningBalances.SettlementRowsAsync(db, org, cp.Id, entity.Id, to, ct)); // D133

        var opening = rows.Where(r => r.Date < from).Sum(r => r.Debit - r.Credit);
        var period = rows.Where(r => r.Date >= from).OrderBy(r => r.Date).ThenBy(r => r.Rank).ThenBy(r => r.Document, StringComparer.Ordinal).ToList();
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.CurrencyCode).SingleAsync(ct);
        return new SettlementStatementDto(entity.Id, entity.Name, entity.ShortName, entity.Inn, entity.Kpp, entity.DirectorPosition, entity.DirectorName,
            entity.AccountantName, cp.Id, cp.Name, cp.Inn, cp.Kpp, from, to, currency, opening, period);

        long? EntityOf(long? account, long? orderEntity) =>
            account is { } a && accounts.TryGetValue(a, out var e) ? e : orderEntity ?? defaultEntity;
    }

    private async Task<IEnumerable<SettlementRowDto>> SalesDocumentsAsync(long org, long customerId, long entityId, DateOnly to, CancellationToken ct)
    {
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OrganizationId == org && o.CustomerId == customerId && o.LegalEntityId == entityId).ToDictionaryAsync(o => o.Id, ct);
        var ids = orders.Keys.ToList();
        var docs = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId != null && ids.Contains(d.SalesOrderId.Value) && d.Status == StockDocumentStatus.Posted
                        && d.DocumentDate <= to
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn || d.Kind == StockOperationKind.ServiceAct))
            .Select(d => new { d.Number, d.DocumentDate, d.Kind, OrderId = d.SalesOrderId!.Value,
                Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }).ToList() })
            .ToListAsync(ct);
        return docs.Select(d =>
        {
            var order = orders[d.OrderId];
            // D352 (сессия 4, Р2): комплект — по цене строки заказа на количество комплектов.
            var value = Money.Round(BundleLines.InOrderTerms(d.Lines.Select(l => (0, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity)))
                .Sum(l => l.Quantity * order.UnitCostWithVat(l.ItemId)));
            // D354 (сессия 4, Р1): акт оказания услуг — в дебет, как отгрузка.
            return d.Kind switch
            {
                StockOperationKind.Shipment => new SettlementRowDto(d.DocumentDate, $"Отгрузка (УПД) № {d.Number} по заказу {order.Number}", value, 0),
                StockOperationKind.ServiceAct => new SettlementRowDto(d.DocumentDate, $"Акт оказания услуг № {d.Number} по заказу {order.Number}", value, 0),
                _ => new SettlementRowDto(d.DocumentDate, $"Возврат от покупателя № {d.Number} по заказу {order.Number}", 0, value, 1),
            };
        });
    }

    private async Task<IEnumerable<SettlementRowDto>> PurchaseDocumentsAsync(long org, long supplierId, long entityId, DateOnly to, CancellationToken ct)
    {
        var orders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OrganizationId == org && o.SupplierId == supplierId && o.LegalEntityId == entityId).ToDictionaryAsync(o => o.Id, ct);
        var ids = orders.Keys.ToList();
        var docs = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.PurchaseOrderId != null && ids.Contains(d.PurchaseOrderId.Value) && d.Status == StockDocumentStatus.Posted
                        && d.DocumentDate <= to
                        && (d.Kind == StockOperationKind.Receipt || d.Kind == StockOperationKind.ReturnToSupplier || d.Kind == StockOperationKind.SupplierServiceAct))
            .Select(d => new { d.Number, d.DocumentDate, d.Kind, OrderId = d.PurchaseOrderId!.Value, Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity }).ToList() })
            .ToListAsync(ct);
        return docs.Select(d =>
        {
            var order = orders[d.OrderId];
            var value = Money.Round(d.Lines.Sum(l => l.Quantity * order.UnitCostWithVat(l.ItemId)));
            var invoice = order.SupplierInvoice is { } si ? $", счёт {si}" : "";
            // D354 (сессия 4, Р1): акт услуг поставщика — в кредит, как поступление.
            return d.Kind switch
            {
                StockOperationKind.Receipt => new SettlementRowDto(d.DocumentDate, $"Поступление № {d.Number} по заказу {order.Number}{invoice}", 0, value),
                StockOperationKind.SupplierServiceAct =>
                    new SettlementRowDto(d.DocumentDate, $"Акт услуг поставщика № {d.Number} по заказу {order.Number}{invoice}", 0, value),
                _ => new SettlementRowDto(d.DocumentDate, $"Возврат поставщику № {d.Number} по заказу {order.Number}", value, 0, 1),
            };
        });
    }
}
