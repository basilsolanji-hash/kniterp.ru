using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Отгружено по заказу: ничего, частично, всё.</summary>
public enum ShipmentState : byte
{
    None = 0,
    Partial = 1,
    Full = 2,
}

/// <summary>StageId: null — все, 0 — без этапа, иначе — этап.</summary>
public sealed record SalesOrderFilter(
    SalesOrderStatus? Status = null, string? Search = null, DateOnly? From = null, DateOnly? To = null, long? StageId = null,
    DateOnly? OverdueOn = null, long? TagId = null);

/// <summary>Этап заказа (D75) — название и цвет из справочника.</summary>
public sealed record SalesStageRefDto(long Id, string Name, string Color);

/// <summary>
/// Строка списка заказов. Суммы — null без права «Цены и суммы»: сумма заказа, выставлено счетов (действующие счета),
/// оплачено (оплаты по заказу), отгружено (отгружено − возвращено по ценам заказа с НДС). Как колонки МойСклад (D75).
/// </summary>
public sealed record SalesOrderRowDto(
    long Id, string Number, DateOnly OrderDate, string Customer, string Warehouse, DateOnly? ShipDate, SalesOrderStatus Status,
    ShipmentState Shipped, decimal? Total, SalesStageRefDto? Stage = null, decimal? Invoiced = null, decimal? Paid = null, decimal? ShippedValue = null)
{
    public string StatusName => SalesOrder.StatusName(Status);

    /// <summary>Не оплачено: сумма заказа минус оплаты (минус — переплата).</summary>
    public decimal? Unpaid => Total is { } t && Paid is { } p ? t - p : null;
}

/// <summary>
/// Строка заказа. Stock — остаток на складе заказа, ReservedByOthers — резерв других заказов на нём,
/// Available — что можно отгрузить по этому заказу (D76): остаток − резерв других заказов.
/// </summary>
public sealed record SalesOrderLineDto(
    long ItemId, string Code, string Name, string UnitSymbol, byte Precision, decimal Quantity, decimal? Price, decimal? VatPercent,
    decimal? Amount, decimal? VatAmount, decimal Shipped, decimal Returned, decimal DiscountPercent = 0, decimal Stock = 0, decimal ReservedByOthers = 0,
    bool Stocked = true, bool IsBundle = false, decimal? MinPrice = null)
{
    public decimal Left => Math.Max(0, Quantity - Shipped + Returned);

    /// <summary>D350 (сессия 4, Р3): цена после скидки строки ниже минимальной цены позиции — предупреждение, не запрет.</summary>
    public bool BelowMinimum => Price is { } p && TradeRules.BelowMinimum(decimal.Round(p * (1 - DiscountPercent / 100m), 2), MinPrice);
    public decimal Available => Stock - ReservedByOthers;

    /// <summary>Не хватает для отгрузки оставшегося — подсветка в строке. Услуга (D300) со склада не отгружается — ей не «не хватает».</summary>
    public bool Short => Stocked && Left > 0 && Available < Left;
}

public sealed record SalesLinkedDocumentDto(long Id, string Number, StockOperationKind Kind, DateOnly Date, StockDocumentStatus Status)
{
    public string KindName => StockDocument.KindName(Kind);
    public string StatusName => StockDocument.StatusName(Status);
}

/// <summary>
/// Оплата покупателя. Allocated — разнесено по заказам (D85), Orders — номера этих заказов, ToOrder — сколько из оплаты
/// разнесено на заказ, из карточки которого смотрят; CashOrderNumber — номер ПКО, если деньги пришли в кассу.
/// </summary>
public sealed record CustomerPaymentDto(
    long Id, string Number, DateOnly Date, long CustomerId, string Customer, long? OrderId, string? OrderNumber, decimal Amount, string? Comment,
    CustomerPaymentStatus Status, string? CancelReason, string CreatedBy, byte[] RowVersion,
    decimal Allocated = 0, string? Orders = null, decimal? ToOrder = null, string? CashOrderNumber = null)
{
    /// <summary>Не разнесено — аванс покупателя.</summary>
    public decimal Unallocated => Status == CustomerPaymentStatus.Posted ? Amount - Allocated : 0;
}

/// <summary>Суммы по заказу: отгружено и возвращено — по ценам заказа с НДС; Debt — долг покупателя по заказу.</summary>
public sealed record SalesOrderDto(
    long Id, string Number, DateOnly OrderDate, long CustomerId, string Customer, long WarehouseId, string Warehouse, DateOnly? ShipDate,
    string? CustomerReference, bool PricesIncludeVat, SalesOrderStatus Status, string? Comment, string CreatedBy, DateTime CreatedAtUtc,
    string? ConfirmedBy, DateTime? ConfirmedAtUtc, IReadOnlyList<SalesOrderLineDto> Lines, IReadOnlyList<SalesLinkedDocumentDto> Documents,
    IReadOnlyList<CustomerPaymentDto> Payments, decimal? Total, decimal? VatTotal, decimal? ShippedValue, decimal? ReturnedValue, decimal? Paid,
    bool CanEdit, bool CanSeePrices, bool CanCreateDocuments, byte[] RowVersion, SalesStageRefDto? Stage = null, bool Reserve = false,
    decimal? DiscountTotal = null, SalesOrderDetailsDto? Details = null)
{
    public string StatusName => SalesOrder.StatusName(Status);
    public decimal? Debt => ShippedValue is { } r && ReturnedValue is { } ret && Paid is { } p ? r - ret - p : null;
    public ShipmentState Shipped => SalesService.StateOf(Lines);
}

/// <summary>
/// Позиция для строки заказа. Price — цена основного вида (D79), PriceIncludesVat — она с НДС; Article и Barcodes (через пробел) —
/// чтобы найти позицию по артикулу или сканером.
/// </summary>
public sealed record SalesItemOptionDto(
    long Id, string Code, string Name, string UnitSymbol, byte Precision, decimal? VatPercent, decimal? Price = null, bool PriceIncludesVat = true,
    string? Article = null, string? Barcodes = null);

public sealed record SalesVatOptionDto(string Name, decimal? Percent);

public sealed record SalesOptionsDto(
    IReadOnlyList<LookupDto> Customers, IReadOnlyList<LookupWarehouseDto> Warehouses, IReadOnlyList<SalesItemOptionDto> Items,
    IReadOnlyList<SalesVatOptionDto> VatRates, string CurrencyCode,
    IReadOnlyList<KnitErp.Application.Organizations.LegalEntityOptionDto>? LegalEntities = null);

/// <summary>Расчёты с покупателем: отгружено, возвращено, оплачено и долг (плюс — покупатель должен, минус — его аванс).</summary>
/// <summary>
/// Детали заказа (D77). CustomerBalance — расчёты с покупателем по всем заказам: больше нуля — он должен нам, меньше — аванс;
/// null без права видеть цены.
/// </summary>
public sealed record SalesOrderDetailsDto(
    TimeOnly? OrderTime, long? ProjectId, string? Project, long? ChannelId, string? Channel, string? DeliveryAddress,
    long? ResponsibleUserId, string? Responsible, IReadOnlyList<SalesOrderCustomValueDto> CustomFields, decimal? CustomerBalance,
    long LegalEntityId = 0, string? LegalEntity = null, bool VatExempt = false, long? BankAccountId = null, string? BankAccount = null);

public sealed record SalesOrderCustomValueDto(
    long FieldId, string Name, CustomFieldType Type, bool IsArchived, string? Value, long? CatalogId = null, string? Display = null);

/// <summary>Списки для деталей заказа: действующие проекты, каналы и сотрудники организации.</summary>
public sealed record SalesOrderDetailOptionsDto(
    IReadOnlyList<LookupDto> Projects, IReadOnlyList<LookupDto> Channels, IReadOnlyList<LookupDto> Users,
    IReadOnlyList<KnitErp.Application.Organizations.LegalEntityOptionDto>? LegalEntities = null);

public sealed record CustomerBalanceDto(long CustomerId, string Customer, decimal Shipped, decimal Returned, decimal Paid)
{
    public decimal Debt => Shipped - Returned - Paid;
}

/// <summary>
/// Продажи (D65): заказы покупателей с ценами и НДС, отгрузки и возвраты по заказу, оплаты и расчёты.
/// Склад — факты (количества), заказ — условия (цены): кладовщик оформляет отгрузку по заказу, не видя цен,
/// а сумма отгрузки считается по ценам заказа. Права: «Продажи: просмотр», «Продажи: заказы и оплаты»;
/// цены и суммы видны только с правом «Цены и суммы».
/// </summary>
public sealed class SalesService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StockDocumentService documents)
{
    public const int MaxRows = 2000;

    public async Task<IReadOnlyList<SalesOrderRowDto>> ListOrdersAsync(SalesOrderFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var q = db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId);
        if (filter.TagId is { } tagId)
        {
            // D104: отбор по тегу — в запросе, чтобы находились и объекты за пределами первых строк списка.
            q = q.Where(o => db.TagLinks.Any(l => l.TagId == tagId && l.Entity == KnitErp.Domain.Tagging.TaggedEntity.SalesOrder && l.EntityId == o.Id));
        }

        if (filter.Status is { } status)
        {
            q = q.Where(o => o.Status == status);
        }

        if (filter.From is { } from)
        {
            q = q.Where(o => o.OrderDate >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(o => o.OrderDate <= to);
        }

        if (filter.StageId is { } stageId)
        {
            q = stageId == 0 ? q.Where(o => o.StageId == null) : q.Where(o => o.StageId == stageId);
        }

        // Просроченные на дату (D80): подтверждён, дата отгрузки прошла; отгруженные полностью отсеиваются ниже.
        if (filter.OverdueOn is { } overdueOn)
        {
            q = q.Where(o => o.Status == SalesOrderStatus.Confirmed && o.ShipDate != null && o.ShipDate < overdueOn);
        }

        var rows = from o in q
                   join s in db.Counterparties.AsNoTracking() on o.CustomerId equals s.Id
                   join w in db.Warehouses.AsNoTracking() on o.WarehouseId equals w.Id
                   select new { o, Customer = s.Name, s.Inn, Warehouse = w.Name };
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            rows = rows.Where(r => r.o.Number.Contains(text) || r.Customer.Contains(text) || r.Inn == text
                                   || (r.o.CustomerReference != null && r.o.CustomerReference.Contains(text)));
        }

        var list = await rows.OrderByDescending(r => r.o.OrderDate).ThenByDescending(r => r.o.Id).Take(MaxRows)
            .Select(r => new { r.o.Id, r.o.Number, r.o.OrderDate, r.Customer, r.Warehouse, r.o.ShipDate, r.o.Status, r.o.StageId,
                Lines = r.o.Lines.Select(l => new { l.ItemId, l.Quantity, l.Amount }).ToList() })
            .ToListAsync(ct);
        var ids = list.Select(o => o.Id).ToList();
        var moved = await MovedAsync(ctx, ids, ct);
        var stages = await StagesAsync(ctx, ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        Dictionary<long, decimal> invoiced = [], paid = [];
        if (prices)
        {
            invoiced = await db.CustomerInvoices.AsNoTracking()
                .Where(i => i.OrganizationId == ctx.OrganizationId && ids.Contains(i.SalesOrderId) && i.Status == CustomerInvoiceStatus.Issued)
                .GroupBy(i => i.SalesOrderId).Select(g => new { g.Key, Sum = g.SelectMany(i => i.Lines).Sum(l => l.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            paid = await KnitErp.Application.Finance.PaidByOrder.SalesAsync(db, ctx.OrganizationId, ids, ct);
        }

        return list.Select(o => new SalesOrderRowDto(o.Id, o.Number, o.OrderDate, o.Customer, o.Warehouse, o.ShipDate, o.Status,
                StateOf(o.Lines.Select(l => (l.Quantity, Net(moved, o.Id, l.ItemId)))),
                prices ? o.Lines.Sum(l => l.Amount) : null,
                o.StageId is { } sid && stages.TryGetValue(sid, out var stage) ? stage : null,
                prices ? invoiced.GetValueOrDefault(o.Id) : null,
                prices ? paid.GetValueOrDefault(o.Id) : null,
                prices ? Money.Round(o.Lines.Sum(l => Net(moved, o.Id, l.ItemId) * (l.Quantity == 0 ? 0 : l.Amount / l.Quantity))) : null))
            .Where(r => filter.OverdueOn is null || r.Shipped != ShipmentState.Full)
            .ToList();
    }

    public async Task<SalesOrderDto> GetOrderAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                        .SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var itemIds = order.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision, i.MinPrice })
            .ToDictionaryAsync(x => x.Id, ct);
        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == order.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var warehouse = await db.Warehouses.AsNoTracking().Where(w => w.Id == order.WarehouseId).Select(w => w.Name).SingleAsync(ct);
        long?[] userIds = [order.CreatedByUserId, order.ConfirmedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var docs = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId == id)
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Number, d.Kind, d.DocumentDate, d.Status,
                Lines = d.Lines.Select(l => new { l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }).ToList() })
            .ToListAsync(ct);
        // D352 (сессия 4, Р2): строки документов в терминах заказа — комплект количеством комплектов.
        // D354 (сессия 4, Р1): акт услуг закрывает строку-услугу как отгрузка — «отгружено», суммы и долг считают его вместе с отгрузками.
        var posted = BundleLines.InOrderTerms(docs.Where(d => d.Status == StockDocumentStatus.Posted)
            .SelectMany(d => d.Lines.Select(l => ((d.Id, Kind: StockOperationKinds.OrderKind(d.Kind)), l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity))));
        decimal Moved(long itemId, StockOperationKind kind) => posted.Where(l => l.Key.Kind == kind && l.ItemId == itemId).Sum(l => l.Quantity);

        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, itemIds, ct); // D300 (сессия 4)
        var bundles = await BundleShipments.BundleIdsAsync(db, ctx.OrganizationId, itemIds, ct); // D302 (сессия 4)
        // D352 (сессия 4, Р2): у комплекта остаток и «Доступно» — сколько наборов можно собрать из компонентов на складе заказа.
        var specs = bundles.Count == 0 ? [] : await KnitErp.Application.Catalog.BundleService.SpecsAsync(db, ctx.OrganizationId, bundles, ct);
        var stockIds = itemIds.Concat(specs.Select(s => s.ComponentItemId)).Distinct().ToList();
        var stock = await db.StockMovements.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.WarehouseId == order.WarehouseId && stockIds.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var reservedByOthers = await Reservations.ByWarehouseItemAsync(db, ctx.OrganizationId, [order.WarehouseId], stockIds, order.Id, ct);
        var free = stock.ToDictionary(s => s.Key, s => s.Value - reservedByOthers.GetValueOrDefault((order.WarehouseId, s.Key)));
        (decimal Stock, decimal Reserved) StockOf(long itemId, byte precision)
        {
            if (!bundles.Contains(itemId))
            {
                return (stock.GetValueOrDefault(itemId), reservedByOthers.GetValueOrDefault((order.WarehouseId, itemId)));
            }

            var whole = BundleExpansion.Available(itemId, specs, stock, precision);
            return (whole, whole - Math.Min(whole, BundleExpansion.Available(itemId, specs, free, precision)));
        }

        var lines = order.Lines.Select(l =>
        {
            var item = items[l.ItemId];
            var (onHand, reserved) = StockOf(l.ItemId, item.Precision);
            return new SalesOrderLineDto(l.ItemId, item.Code, item.Name, item.Symbol, item.Precision, l.Quantity,
                prices ? l.Price : null, l.VatPercent, prices ? l.Amount : null, prices ? l.VatAmount : null,
                Moved(l.ItemId, StockOperationKind.Shipment), Moved(l.ItemId, StockOperationKind.CustomerReturn),
                l.DiscountPercent, onHand, reserved, !services.Contains(l.ItemId) || bundles.Contains(l.ItemId), bundles.Contains(l.ItemId),
                prices ? item.MinPrice : null); // D350: минимальная цена — только с правом на цены
        }).OrderBy(l => l.Code).ToList();

        var payments = await EnrichAsync(ctx, await PaymentsQuery(ctx, orderId: id).ToListAsync(ct), id, ct);
        decimal Value(StockOperationKind kind) => Money.Round(posted.Where(l => l.Key.Kind == kind).Sum(l => l.Quantity * order.UnitCostWithVat(l.ItemId)));

        var canEdit = ctx.Permissions.Has(Permissions.SalesEdit) && prices;
        var canDocs = order.Status is SalesOrderStatus.Confirmed or SalesOrderStatus.Closed
                      && WarehouseScope.Covers(ctx, Permissions.WarehouseDocumentCreate, order.WarehouseId);
        return new SalesOrderDto(order.Id, order.Number, order.OrderDate, order.CustomerId, customer, order.WarehouseId, warehouse,
            order.ShipDate, order.CustomerReference, order.PricesIncludeVat, order.Status, order.Comment,
            users[order.CreatedByUserId], order.CreatedAtUtc, order.ConfirmedByUserId is { } c ? users[c] : null, order.ConfirmedAtUtc,
            lines, docs.Select(d => new SalesLinkedDocumentDto(d.Id, d.Number, d.Kind, d.DocumentDate, d.Status)).ToList(),
            prices ? payments : [],
            prices ? order.Total : null, prices ? order.VatTotal : null,
            prices ? Value(StockOperationKind.Shipment) : null, prices ? Value(StockOperationKind.CustomerReturn) : null,
            prices ? payments.Where(p => p.Status == CustomerPaymentStatus.Posted).Sum(p => p.ToOrder ?? 0) : null,
            canEdit, prices, canDocs, order.RowVersion,
            order.StageId is { } sid && (await StagesAsync(ctx, ct)).TryGetValue(sid, out var stage) ? stage : null,
            order.Reserve, prices ? order.DiscountTotal : null, await DetailsAsync(ctx, order, prices, ct));
    }

    private async Task<SalesOrderDetailsDto> DetailsAsync(AccessContext ctx, SalesOrder order, bool prices, CancellationToken ct)
    {
        long?[] lookupIds = [order.ProjectId, order.ChannelId];
        var lookups = await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && lookupIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var responsible = order.ResponsibleUserId is { } rid
            ? await db.Users.AsNoTracking().Where(u => u.Id == rid).Select(u => u.DisplayName).SingleOrDefaultAsync(ct)
            : null;
        var custom = (await new CustomFieldStore(db, currentUser, clock).ValuesAsync(ctx, CustomFieldTarget.SalesOrder, order.Id, ct))
            .Select(v => new SalesOrderCustomValueDto(v.FieldId, v.Name, v.Type, v.IsArchived, v.Value, v.CatalogId, v.Display)).ToList();
        decimal? balance = prices
            ? (await ComputeBalancesAsync(ctx.OrganizationId, null, order.CustomerId, ct)).SingleOrDefault()?.Debt ?? 0m
            : null;
        var entity = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.Id == order.LegalEntityId)
            .Select(e => new { e.ShortName, e.VatExempt }).SingleAsync(ct);
        var account = await db.LegalEntityAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == order.LegalEntityId
                        && (order.BankAccountId == null ? a.IsDefault : a.Id == order.BankAccountId))
            .Select(a => new { a.BankName, a.Account }).FirstOrDefaultAsync(ct);
        return new SalesOrderDetailsDto(order.OrderTime,
            order.ProjectId, order.ProjectId is { } p ? lookups.GetValueOrDefault(p) : null,
            order.ChannelId, order.ChannelId is { } c ? lookups.GetValueOrDefault(c) : null,
            order.DeliveryAddress, order.ResponsibleUserId, responsible, custom, balance,
            order.LegalEntityId, entity.ShortName, entity.VatExempt, order.BankAccountId,
            account is null ? null : $"{account.BankName}, р/с …{account.Account[^Math.Min(4, account.Account.Length)..]}"
                                     + (order.BankAccountId is null ? " (основной)" : ""));
    }

    /// <summary>Действующие проекты, каналы продаж и сотрудники организации — для деталей заказа (D77).</summary>
    public async Task<SalesOrderDetailOptionsDto> DetailOptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var lookups = await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && !l.IsArchived)
            .OrderBy(l => l.Name).Select(l => new { l.Kind, Dto = new LookupDto(l.Id, l.Name) }).ToListAsync(ct);
        var users = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.Status == MembershipStatus.Active)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.DisplayName).Select(u => new LookupDto(u.Id, u.DisplayName)).ToListAsync(ct);
        return new SalesOrderDetailOptionsDto(
            lookups.Where(l => l.Kind == LookupKind.Project).Select(l => l.Dto).ToList(),
            lookups.Where(l => l.Kind == LookupKind.SalesChannel).Select(l => l.Dto).ToList(), users,
            await KnitErp.Application.Organizations.LegalEntityService.OptionsAsync(db, ctx.OrganizationId, ct));
    }

    /// <summary>
    /// Детали и дополнительные поля заказа (D77). На учёт не влияют: меняются в любом состоянии, кроме отменённого; цены не нужны.
    /// Проект и канал — действующие записи своей организации, ответственный — её активный сотрудник (прежний, уже
    /// заблокированный, можно оставить). Поля из архива не меняются.
    /// </summary>
    public async Task SetOrderDetailsAsync(long id, SalesOrderDetails details, IReadOnlyDictionary<long, string?> customFields, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var order = await LoadOrderAsync(ctx, id, ct);
        order.EnsureVersion(order.RowVersion, rowVersion);
        await EnsureLookupAsync(ctx, details.ProjectId, order.ProjectId, LookupKind.Project, ct);
        await EnsureLookupAsync(ctx, details.ChannelId, order.ChannelId, LookupKind.SalesChannel, ct);
        if (details.ResponsibleUserId is { } uid && uid != order.ResponsibleUserId
            && !await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == uid
                                                          && m.Status == MembershipStatus.Active, ct))
        {
            throw new NotFoundException("Сотрудник");
        }

        if (details.BankAccountId is { } accountId && accountId != order.BankAccountId)
        {
            var account = await db.LegalEntityAccounts.AsNoTracking()
                              .SingleOrDefaultAsync(a => a.Id == accountId && a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == order.LegalEntityId
                                           && a.Kind == MoneyAccountKind.Bank, ct)
                          ?? throw new NotFoundException("Расчётный счёт");
            if (account.IsArchived)
            {
                throw new BusinessRuleException("legal_entity.account_archived", "Счёт в архиве.");
            }
        }

        var before = DescribeDetails(order);
        order.SetDetails(details);

        var changes = await new CustomFieldStore(db, currentUser, clock).SetValuesAsync(ctx, CustomFieldTarget.SalesOrder, id, customFields, ct);

        var after = DescribeDetails(order);
        if (before != after || changes.Count > 0)
        {
            Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, before,
                changes.Count == 0 ? after : $"{after}; {string.Join("; ", changes)}", $"{order.Number}: детали");
        }

        await db.SaveOrConflictAsync(ct);
    }

    private async Task EnsureLookupAsync(AccessContext ctx, long? id, long? current, LookupKind kind, CancellationToken ct)
    {
        if (id is not { } lid || lid == current)
        {
            return;
        }

        var lookup = await db.Lookups.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lid && l.OrganizationId == ctx.OrganizationId, ct);
        if (lookup is null || lookup.Kind != kind)
        {
            throw new NotFoundException(Lookup.KindName(kind));
        }

        if (lookup.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"«{lookup.Name}» в архиве.");
        }
    }

    private static string DescribeDetails(SalesOrder o) =>
        $"время {o.OrderTime?.ToString("HH:mm") ?? "—"}, проект {o.ProjectId?.ToString() ?? "—"}, канал {o.ChannelId?.ToString() ?? "—"}, "
        + $"адрес {o.DeliveryAddress ?? "—"}, ответственный {o.ResponsibleUserId?.ToString() ?? "—"}, р/с {o.BankAccountId?.ToString() ?? "основной"}";

    /// <summary>Скидка на весь заказ (D76): один процент всем строкам черновика.</summary>
    public async Task SetOrderDiscountAsync(long id, decimal discountPercent, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        order.SetDiscount(discountPercent);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, null, $"скидка {discountPercent:0.##}% на все строки, итого {order.Total:0.00}",
            order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Резерв товара под заказ (D76). Право — «Продажи: заказы и оплаты»; цены не нужны.</summary>
    public async Task SetOrderReserveAsync(long id, bool reserve, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var order = await LoadOrderAsync(ctx, id, ct);
        order.EnsureVersion(order.RowVersion, rowVersion);
        order.SetReserve(reserve);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, reserve ? "без резерва" : "резерв", reserve ? "резерв" : "без резерва",
            order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Этап заказа (D75): метка работы, меняется в любом состоянии, кроме отменённого. Право — «Продажи: заказы и оплаты»;
    /// цены для этого не нужны. null — снять этап.
    /// </summary>
    public async Task SetOrderStageAsync(long id, long? stageId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var order = await LoadOrderAsync(ctx, id, ct);
        order.EnsureVersion(order.RowVersion, rowVersion);
        var stages = await StagesAsync(ctx, ct);
        string? name = null;
        if (stageId is { } sid)
        {
            var stage = await db.SalesOrderStages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sid && s.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Этап заказа");
            if (stage.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Этап «{stage.Name}» в архиве.");
            }

            name = stage.Name;
        }

        var before = order.StageId is { } old && stages.TryGetValue(old, out var o) ? o.Name : null;
        order.SetStage(stageId);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, before ?? "—", name ?? "—", $"{order.Number}: этап");
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<Dictionary<long, SalesStageRefDto>> StagesAsync(AccessContext ctx, CancellationToken ct) =>
        await db.SalesOrderStages.AsNoTracking().Where(s => s.OrganizationId == ctx.OrganizationId)
            .ToDictionaryAsync(s => s.Id, s => new SalesStageRefDto(s.Id, s.Name, s.Color), ct);

    /// <summary>Справочники формы заказа: покупатели, склады, позиции с НДС по умолчанию, ставки НДС организации.</summary>
    public async Task<SalesOptionsDto> GetOptionsAsync(DateOnly? onDate = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var date = onDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, org.TimeZoneId));
        var customers = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.IsCustomer)
            .OrderBy(c => c.Name).Select(c => new LookupDto(c.Id, c.Name)).ToListAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived)
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
        var rates = await db.VatRates.AsNoTracking().Include(r => r.Periods)
            .Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived).ToListAsync(ct);
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && !i.IsArchived).OrderBy(i => i.Code)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol, u.Precision, i.VatRateId, i.Article })
            .ToListAsync(ct);
        var standard = rates.FirstOrDefault(r => r.Kind == VatRateKind.Standard)?.PercentOn(date);
        // Цена основного вида — только тем, кто видит цены (D79); штрихкоды — для поиска сканером.
        var priceType = await db.PriceTypes.AsNoTracking().FirstOrDefaultAsync(t => t.OrganizationId == ctx.OrganizationId && t.IsDefault, ct);
        var prices = priceType is not null && ctx.Permissions.Has(Permissions.PriceView)
            ? await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.PriceTypeId == priceType.Id)
                .ToDictionaryAsync(p => p.ItemId, p => p.Price, ct)
            : [];
        var barcodes = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == ctx.OrganizationId).Select(b => new { b.ItemId, b.Code })
                .ToListAsync(ct))
            .GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => string.Join(' ', g.Select(b => b.Code)));
        return new SalesOptionsDto(customers, warehouses,
            items.Select(i => new SalesItemOptionDto(i.Id, i.Code, i.Name, i.Symbol, i.Precision,
                i.VatRateId is { } rid && rates.FirstOrDefault(r => r.Id == rid) is { } rate ? rate.PercentOn(date) : standard,
                prices.TryGetValue(i.Id, out var price) ? price : null, priceType?.IncludesVat ?? true, i.Article,
                barcodes.TryGetValue(i.Id, out var codes) ? codes : null)).ToList(),
            rates.OrderBy(r => r.Kind).ThenBy(r => r.Name).Select(r => new SalesVatOptionDto(r.Name, r.PercentOn(date)))
                .Where(r => r.Percent is not null || rates.Any(x => x.Kind == VatRateKind.Exempt && x.Name == r.Name))
                .DistinctBy(r => r.Percent).ToList(),
            org.CurrencyCode, await KnitErp.Application.Organizations.LegalEntityService.OptionsAsync(db, ctx.OrganizationId, ct));
    }

    public async Task<long> CreateOrderAsync(SalesOrderHeader header, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        header = await ValidateHeaderAsync(ctx, header, null, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SalesOrder.NumberPrefix, ct);
        var order = SalesOrder.Create(ctx.OrganizationId, number, header, ctx.UserId, clock.UtcNow);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.SalesOrderCreated, nameof(SalesOrder), order.Id, null, $"{order.Number} от {order.OrderDate:dd.MM.yyyy}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order.Id;
    }

    public async Task UpdateOrderHeaderAsync(long id, SalesOrderHeader header, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        header = await ValidateHeaderAsync(ctx, header, order.LegalEntityId, ct);
        order.UpdateHeader(header);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, null, $"{order.OrderDate:dd.MM.yyyy}, договор {order.CustomerReference ?? "—"}",
            $"{order.Number}: шапка");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task SetOrderLineAsync(long id, long itemId, decimal quantity, decimal price, decimal? vatPercent, byte[] rowVersion,
        CancellationToken ct = default) =>
        await SetOrderLineAsync(id, itemId, quantity, price, vatPercent, 0, rowVersion, ct);

    /// <summary>Строка заказа со скидкой строки в процентах (D76).</summary>
    public async Task SetOrderLineAsync(long id, long itemId, decimal quantity, decimal price, decimal? vatPercent, decimal discountPercent,
        byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        var item = await StockEntry.ActiveItems(db, ctx.OrganizationId, itemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Номенклатура");
        StockEntry.EnsurePrecision(item, quantity);
        order.SetLine(itemId, quantity, price, vatPercent, discountPercent);
        var line = order.Lines.Single(l => l.ItemId == itemId);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, null,
            $"{item.Code}: {Quantities.Format(quantity)} {item.UnitSymbol} × {price:0.####}{(discountPercent > 0 ? $" − {discountPercent:0.##}%" : "")} = {line.Amount:0.00}",
            $"{order.Number}: строка");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveOrderLineAsync(long id, long itemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        order.RemoveLine(itemId);
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, $"позиция №{itemId}", "строка удалена", order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Подтверждение заказа. D350 (сессия 4, Р3): возвращает коды позиций, проданных ниже минимальной цены (после скидки строки) —
    /// подтверждение не блокируется, пометка пишется в журнал.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConfirmOrderAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        await ValidateHeaderAsync(ctx, Header(order) with { LegalEntityId = order.LegalEntityId }, null, ct);
        var itemIds = order.Lines.Select(l => l.ItemId).ToList();
        var minPrices = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.MinPrice != null)
            .Select(i => new { i.Id, i.Code, i.MinPrice }).ToListAsync(ct);
        var below = minPrices.Where(m => order.Lines.Any(l => l.ItemId == m.Id
                && TradeRules.BelowMinimum(decimal.Round(l.NetPrice, 2), m.MinPrice)))
            .Select(m => m.Code).Order(StringComparer.Ordinal).ToList();
        order.Confirm(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.SalesOrderConfirmed, nameof(SalesOrder), id, "Черновик", "Подтверждён",
            $"{order.Number}: {order.Total:0.00}" + (below.Count > 0 ? $"; ниже минимальной цены: {string.Join(", ", below)}" : ""));
        await db.SaveOrConflictAsync(ct);
        return ctx.Permissions.Has(Permissions.PriceView) ? below : [];
    }

    public async Task CloseOrderAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        order.Close();
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, "Подтверждён", "Закрыт", order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task ReopenOrderAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        order.Reopen();
        Audit(ctx, AuditActions.SalesOrderChanged, nameof(SalesOrder), id, "Закрыт", "Подтверждён", order.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Отмена заказа. Если по нему уже есть документы склада или оплаты — нельзя: сначала их сторно и отмена.</summary>
    public async Task CancelOrderAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        var hasDocs = await db.StockDocuments.AnyAsync(d => d.SalesOrderId == id
            && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft), ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Allocator.LockCounterpartyAsync(ctx, order.CustomerId, ct);
        var hasPayments = await Allocator.OrderHasAllocationsAsync(ctx, id, ct);
        if (hasDocs || hasPayments)
        {
            throw new BusinessRuleException("sales.order.in_use",
                "По заказу есть отгрузки, возвраты или оплаты. Отмените черновики, сторнируйте проведённые документы и снимите разноски оплат — затем заказ.");
        }

        var before = SalesOrder.StatusName(order.Status);
        order.Cancel();
        Audit(ctx, AuditActions.SalesOrderCancelled, nameof(SalesOrder), id, before, "Отменён", order.Number);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Отгрузка по заказу: черновик со всем, что ещё не отгружено. Кладовщик правит количества по факту и проводит.</summary>
    public async Task<long> CreateShipmentAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var moved = await MovedAsync(ctx, [orderId], ct);
        // D300 (сессия 4): строки-услуги со склада не отгружаются — они закрываются актом или УПД.
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, order.Lines.Select(l => l.ItemId), ct);
        // D352 (сессия 4, Р2): комплект отгружается компонентами — остаток строки комплекта разворачивается по текущему составу,
        // строки разворота помнят комплект и количество наборов. Состав фиксируется документом.
        var bundles = await BundleShipments.BundleIdsAsync(db, ctx.OrganizationId, order.Lines.Select(l => l.ItemId), ct);
        var left = order.Lines.Where(l => !services.Contains(l.ItemId) || bundles.Contains(l.ItemId))
            .Select(l => (l.ItemId, Quantity: l.Quantity - Net(moved, orderId, l.ItemId))).Where(l => l.Quantity > 0).ToList();
        if (left.Count == 0)
        {
            throw services.Count == order.Lines.Count && bundles.Count == 0
                ? new BusinessRuleException("sales.order.services_only", "В заказе только услуги — со склада отгружать нечего, они закрываются актом или УПД.")
                : new BusinessRuleException("sales.order.shipped", "По заказу всё уже отгружено.");
        }

        var lines = new List<StockLineSpec>();
        foreach (var (itemId, quantity) in left)
        {
            if (bundles.Contains(itemId))
            {
                lines.AddRange((await BundleShipments.ExpandAsync(db, ctx.OrganizationId, itemId, quantity, ct))
                    .Select(c => new StockLineSpec(c.ItemId, c.Quantity, itemId, quantity)));
            }
            else
            {
                lines.Add(new StockLineSpec(itemId, quantity));
            }
        }

        var reason = await DefaultReasonAsync(ctx, StockOperationKind.Shipment, "Продажа покупателю", ct);
        return await documents.CreateWithLinesAsync(StockOperationKind.Shipment,
            new StockDocumentHeader(order.WarehouseId, null, order.CustomerId, reason, await TodayAsync(ctx, ct), $"Отгрузка по заказу {order.Number}", SalesOrderId: orderId),
            lines, ct);
    }

    /// <summary>
    /// D354 (сессия 4, Р1): акт оказания услуг по заказу — черновик со строками-услугами на ещё не закрытое количество.
    /// Цена и НДС — из заказа; проведение движений склада не пишет. Права и область склада — как у отгрузки.
    /// </summary>
    public async Task<long> CreateServiceActAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var moved = await MovedAsync(ctx, [orderId], ct);
        var services = await ServiceIdsAsync(ctx, order.Lines.Select(l => l.ItemId), ct);
        var lines = order.Lines.Where(l => services.Contains(l.ItemId))
            .Select(l => (l.ItemId, Quantity: l.Quantity - Net(moved, orderId, l.ItemId))).Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0)
        {
            throw services.Count == 0
                ? new BusinessRuleException("sales.order.no_services", "В заказе нет услуг — акт не нужен, товары оформляются отгрузкой.")
                : new BusinessRuleException("sales.order.services_closed", "Все услуги заказа уже закрыты актами.");
        }

        var reason = await DefaultReasonAsync(ctx, StockOperationKind.ServiceAct, "Оказание услуг", ct);
        return await documents.CreateWithLinesAsync(StockOperationKind.ServiceAct,
            new StockDocumentHeader(order.WarehouseId, null, order.CustomerId, reason, await TodayAsync(ctx, ct), $"Акт по заказу {order.Number}", SalesOrderId: orderId),
            lines, ct);
    }

    /// <summary>D354: услуги среди позиций (без комплектов — они отгружаются компонентами).</summary>
    private async Task<HashSet<long>> ServiceIdsAsync(AccessContext ctx, IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        return (await db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && ids.Contains(i.Id) && i.Type == ItemType.Service)
            .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Возврат по заказу: черновик со всем полученным; лишние строки удаляют, количества уменьшают.</summary>
    public async Task<long> CreateReturnAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var order = await LoadOrderAsync(ctx, orderId, ct);
        var moved = await MovedAsync(ctx, [orderId], ct);
        var services = await StockEntry.NotStockedAsync(db, ctx.OrganizationId, order.Lines.Select(l => l.ItemId), ct); // D300 (сессия 4)
        // D352 (сессия 4, Р2): комплект возвращается тем же разворотом — компонентами, отгруженными в его составе.
        var bundles = await BundleShipments.BundleIdsAsync(db, ctx.OrganizationId, order.Lines.Select(l => l.ItemId), ct);
        var shipped = bundles.Count == 0 ? [] : await BundleShipments.ShippedAsync(db, ctx.OrganizationId, orderId, ct);
        var lines = new List<StockLineSpec>();
        foreach (var l in order.Lines.Where(l => !services.Contains(l.ItemId) || bundles.Contains(l.ItemId)))
        {
            var net = Net(moved, orderId, l.ItemId);
            if (net <= 0)
            {
                continue;
            }

            lines.AddRange(bundles.Contains(l.ItemId)
                ? (await BundleShipments.ReturnAsync(db, ctx.OrganizationId, l.ItemId, net, shipped.GetValueOrDefault(l.ItemId), ct))
                    .Select(c => new StockLineSpec(c.ItemId, c.Quantity, l.ItemId, net))
                : [new StockLineSpec(l.ItemId, net)]);
        }

        if (lines.Count == 0)
        {
            throw new BusinessRuleException("sales.order.nothing_shipped", "По заказу ещё ничего не отгружено — возвращать нечего.");
        }

        var reason = await DefaultReasonAsync(ctx, StockOperationKind.CustomerReturn, "Возврат от покупателя", ct);
        return await documents.CreateWithLinesAsync(StockOperationKind.CustomerReturn,
            new StockDocumentHeader(order.WarehouseId, null, order.CustomerId, reason, await TodayAsync(ctx, ct), $"Возврат по заказу {order.Number}", SalesOrderId: orderId),
            lines, ct);
    }

    public async Task<IReadOnlyList<CustomerPaymentDto>> ListPaymentsAsync(DateOnly? from = null, DateOnly? to = null, string? search = null,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return await EnrichAsync(ctx, await PaymentsQuery(ctx, from: from, to: to, search: search).ToListAsync(ct), null, ct);
    }

    /// <summary>Оплата от покупателя: сразу уменьшает его долг. Заказ — по желанию, того же покупателя.</summary>
    public async Task<long> CreatePaymentAsync(DateOnly date, long customerId, long? orderId, decimal amount, string? comment, CancellationToken ct = default) =>
        await CreatePaymentAsync(date, customerId, orderId, amount, comment, null, null, ct);

    /// <summary>
    /// documentNumber — номер платёжного поручения покупателя: нужен для строки 5 счёта-фактуры при предоплате.
    /// moneyAccountId — счёт или касса, куда пришли деньги (D80); не указан — счёт заказа или основной счёт юрлица.
    /// </summary>
    public async Task<long> CreatePaymentAsync(
        DateOnly date, long customerId, long? orderId, decimal amount, string? comment, string? documentNumber, long? moneyAccountId = null,
        CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var customer = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId && c.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Контрагент");
        if (!customer.IsCustomer)
        {
            throw new BusinessRuleException("stock.document.not_customer", $"«{customer.Name}» не отмечен как покупатель.");
        }

        long? entityId = null, orderAccount = null;
        if (orderId is { } oid)
        {
            var order = await db.SalesOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == oid && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            if (order.CustomerId != customerId || order.Status is SalesOrderStatus.Draft or SalesOrderStatus.Cancelled)
            {
                throw new BusinessRuleException("sales.payment.order", $"Заказ {order.Number} другого покупателя или не подтверждён.");
            }

            (entityId, orderAccount) = (order.LegalEntityId, order.BankAccountId);
        }

        var account = await KnitErp.Application.Finance.MoneyService.ResolveAsync(db, ctx.OrganizationId, moneyAccountId, entityId, orderAccount, ct);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, date, ct);

        // Проверка данных — до выдачи номеров: отказ после счётчика оставил бы его в контексте вкладки.
        CustomerPayment.Create(ctx.OrganizationId, "—", date, customerId, orderId, amount, comment, ctx.UserId, clock.UtcNow, documentNumber, account);
        var cash = account is { } acc && await db.LegalEntityAccounts.AsNoTracking().AnyAsync(a => a.Id == acc && a.Kind == MoneyAccountKind.Cash, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CustomerPayment.NumberPrefix, ct);
        var payment = CustomerPayment.Create(ctx.OrganizationId, number, date, customerId, orderId, amount, comment, ctx.UserId, clock.UtcNow, documentNumber,
            account);

        // D85: деньги пришли в кассу — приходный кассовый ордер в общей нумерации ПКО.
        if (cash)
        {
            payment.AssignCashOrderNumber(await DocumentNumbers.NextAsync(db, ctx.OrganizationId,
                KnitErp.Domain.Finance.MoneyOperation.PrefixFor(KnitErp.Domain.Finance.MoneyOperationKind.Income, true), ct));
        }

        db.CustomerPayments.Add(payment);
        await db.SaveChangesAsync(ct);
        if (orderId is { } allocateTo)
        {
            await Allocator.AllocateNewAsync(ctx, payment.Id, customerId, allocateTo, amount, ct);
        }

        Audit(ctx, AuditActions.CustomerPaymentCreated, nameof(CustomerPayment), payment.Id, null, $"{payment.Amount:0.00}",
            $"{number}: {customer.Name}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return payment.Id;
    }

    public async Task CancelPaymentAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var payment = await db.CustomerPayments.SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Оплата");
        payment.EnsureVersion(payment.RowVersion, rowVersion);
        await ClosedPeriod.EnsureOpenAsync(db, ctx.OrganizationId, payment.PaymentDate, ct);
        payment.Cancel(ctx.UserId, reason, clock.UtcNow);
        Audit(ctx, AuditActions.CustomerPaymentCancelled, nameof(CustomerPayment), id, $"{payment.Amount:0.00}", "отменена",
            $"{payment.Number}: {payment.CancelReason}");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Разноски оплаты и заказы покупателя, на которые можно разнести остаток (D85).</summary>
    public async Task<KnitErp.Application.Finance.PaymentAllocationsDto> GetPaymentAllocationsAsync(long paymentId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return await Allocator.GetAsync(ctx, paymentId, ct);
    }

    public async Task AllocatePaymentAsync(long paymentId, long orderId, decimal amount, CancellationToken ct = default) =>
        await Allocator.AllocateAsync(await DemandEditAsync(ct), paymentId, orderId, amount, ct);

    /// <summary>Разнести остаток оплаты по открытым заказам покупателя — сначала с ранним сроком. Возвращает разнесённую сумму.</summary>
    public async Task<decimal> AutoAllocatePaymentAsync(long paymentId, CancellationToken ct = default) =>
        await Allocator.AutoAllocateAsync(await DemandEditAsync(ct), paymentId, ct);

    public async Task RemovePaymentAllocationAsync(long allocationId, byte[] rowVersion, CancellationToken ct = default) =>
        await Allocator.RemoveAsync(await DemandEditAsync(ct), allocationId, rowVersion, ct);

    /// <summary>Зачесть авансы покупателя (не разнесённые остатки его оплат) в заказ. Возвращает зачтённую сумму.</summary>
    public async Task<decimal> ApplyAdvancesAsync(long orderId, CancellationToken ct = default) =>
        await Allocator.ApplyAdvancesAsync(await DemandEditAsync(ct), orderId, ct);

    /// <summary>Сколько авансов покупателя можно зачесть в заказ — подсказка в карточке заказа.</summary>
    public async Task<decimal> AvailableAdvancesAsync(long orderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return await Allocator.AvailableAdvancesAsync(ctx, orderId, ct);
    }

    /// <summary>Приходный кассовый ордер (КО-1) к оплате, пришедшей в кассу (D85).</summary>
    public async Task<KnitErp.Application.Finance.CashOrderPrintDto> PaymentCashOrderAsync(long paymentId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var p = await db.CustomerPayments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == paymentId && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Оплата");
        if (p.CashOrderNumber is null || p.MoneyAccountId is null)
        {
            throw new BusinessRuleException("money.print.not_cash", "Кассовый ордер печатается для оплаты, принятой в кассу.");
        }

        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == p.CustomerId).Select(c => c.Name).SingleAsync(ct);
        var allocations = await db.CustomerPaymentAllocations.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.PaymentId == p.Id && a.RemovedAtUtc == null)
            .Select(a => new { a.OrderId, a.Amount }).ToListAsync(ct);
        var orderIds = allocations.Select(a => a.OrderId).ToList();
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).OrderBy(o => o.Number).ToListAsync(ct);
        var basis = orders.Count == 0
            ? "Предварительная оплата (аванс)"
            : $"Оплата по заказ{(orders.Count == 1 ? "у" : "ам")} {string.Join(", ", orders.Select(o => $"№ {o.Number} от {o.OrderDate:dd.MM.yyyy}"))}";
        if (orders.Count > 0 && allocations.Sum(a => a.Amount) < p.Amount)
        {
            basis += " и предварительная оплата (аванс)";
        }

        var vat = KnitErp.Application.Finance.MoneyOperationService.VatText(p.Amount, orders.SelectMany(o => o.Lines).Select(l => l.VatPercent).ToList());
        return await KnitErp.Application.Finance.MoneyOperationService.BuildCashOrderAsync(db, ctx.OrganizationId, false, p.CashOrderNumber, p.PaymentDate,
            p.MoneyAccountId.Value, p.Amount, customer, basis, p.Comment, p.Status == CustomerPaymentStatus.Cancelled, vat, ct);
    }

    /// <summary>Разноски для списка оплат: сколько разнесено, на какие заказы и сколько — на заказ из карточки.</summary>
    private async Task<IReadOnlyList<CustomerPaymentDto>> EnrichAsync(AccessContext ctx, List<CustomerPaymentDto> list, long? orderId, CancellationToken ct)
    {
        var ids = list.Select(p => p.Id).ToList();
        var rows = await db.CustomerPaymentAllocations.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.RemovedAtUtc == null && ids.Contains(a.PaymentId))
            .Join(db.SalesOrders.AsNoTracking(), a => a.OrderId, o => o.Id, (a, o) => new { a.PaymentId, a.OrderId, o.Number, a.Amount })
            .ToListAsync(ct);
        var cash = await db.CustomerPayments.AsNoTracking().Where(p => ids.Contains(p.Id) && p.CashOrderNumber != null)
            .ToDictionaryAsync(p => p.Id, p => p.CashOrderNumber!, ct);
        var byPayment = rows.ToLookup(r => r.PaymentId);
        return list.Select(p => p with
        {
            Allocated = byPayment[p.Id].Sum(r => r.Amount),
            Orders = byPayment[p.Id].Any() ? string.Join(", ", byPayment[p.Id].OrderBy(r => r.Number).Select(r => r.Number)) : null,
            ToOrder = orderId is { } oid ? byPayment[p.Id].Where(r => r.OrderId == oid).Sum(r => r.Amount) : null,
            CashOrderNumber = cash.GetValueOrDefault(p.Id),
        }).ToList();
    }

    private KnitErp.Application.Finance.PaymentAllocator Allocator => new(db, clock, currentUser, sales: true);

    /// <summary>
    /// Расчёты с покупателями на дату: отгружено и возвращено по проведённым документам по заказам (по ценам заказа с НДС),
    /// оплачено — действующие оплаты. Отгрузки без заказа в расчёты не входят: у них нет цены.
    /// </summary>
    public async Task<IReadOnlyList<CustomerBalanceDto>> BalancesAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return await ComputeBalancesAsync(ctx.OrganizationId, asOf, null, ct);
    }

    private async Task<IReadOnlyList<CustomerBalanceDto>> ComputeBalancesAsync(long org, DateOnly? asOf, long? customerId, CancellationToken ct)
    {
        var raw = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId != null && d.Status == StockDocumentStatus.Posted
                        && (asOf == null || d.DocumentDate <= asOf)
                        && (customerId == null || db.SalesOrders.Any(o => o.Id == d.SalesOrderId && o.CustomerId == customerId)))
            .SelectMany(d => d.Lines.Select(l => new { d.Id, d.Kind, OrderId = d.SalesOrderId!.Value, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }))
            .ToListAsync(ct);
        // D352 (сессия 4, Р2): комплект — по цене строки заказа на количество комплектов. D354 (Р1): акт услуг — как отгрузка.
        var docs = BundleLines.InOrderTerms(raw.Select(r => ((r.Id, Kind: StockOperationKinds.OrderKind(r.Kind), r.OrderId), r.ItemId, r.Quantity,
                r.BundleItemId, r.BundleQuantity)))
            .Select(x => new { x.Key.Kind, x.Key.OrderId, x.ItemId, x.Quantity }).ToList();
        var orderIds = docs.Select(d => d.OrderId).Distinct().ToList();
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var paid = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.Status == CustomerPaymentStatus.Posted && (asOf == null || p.PaymentDate <= asOf)
                        && (customerId == null || p.CustomerId == customerId))
            .GroupBy(p => p.CustomerId).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

        var moved = docs.GroupBy(d => orders[d.OrderId].CustomerId).ToDictionary(g => g.Key, g => (
            Shipped: Money.Round(g.Where(d => d.Kind == StockOperationKind.Shipment).Sum(d => d.Quantity * orders[d.OrderId].UnitCostWithVat(d.ItemId))),
            Returned: Money.Round(g.Where(d => d.Kind == StockOperationKind.CustomerReturn).Sum(d => d.Quantity * orders[d.OrderId].UnitCostWithVat(d.ItemId)))));
        var customerIds = moved.Keys.Union(paid.Keys).ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return customerIds.Select(id => new CustomerBalanceDto(id, names[id],
                moved.TryGetValue(id, out var m) ? m.Shipped : 0, moved.TryGetValue(id, out var r) ? r.Returned : 0, paid.GetValueOrDefault(id)))
            .OrderByDescending(b => b.Debt).ThenBy(b => b.Customer).ToList();
    }

    public static ShipmentState StateOf(IEnumerable<SalesOrderLineDto> lines) =>
        StateOf(lines.Select(l => (l.Quantity, l.Shipped - l.Returned)));

    private static ShipmentState StateOf(IEnumerable<(decimal Ordered, decimal Net)> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0 || list.All(l => l.Net <= 0))
        {
            return ShipmentState.None;
        }

        return list.All(l => l.Net >= l.Ordered) ? ShipmentState.Full : ShipmentState.Partial;
    }

    /// <summary>Отгружено минус возвращено по заказу и позиции — по проведённым документам.</summary>
    private static decimal Net(IReadOnlyList<(long OrderId, StockOperationKind Kind, long ItemId, decimal Quantity)> moved, long orderId, long itemId) =>
        moved.Where(m => m.OrderId == orderId && m.ItemId == itemId)
            .Sum(m => m.Kind == StockOperationKind.Shipment ? m.Quantity : -m.Quantity);

    /// <summary>
    /// Движения по строкам заказов; комплект (D352) — количеством комплектов, а не компонентами; акт услуг (D354) — как отгрузка.
    /// </summary>
    private async Task<IReadOnlyList<(long OrderId, StockOperationKind Kind, long ItemId, decimal Quantity)>> MovedAsync(
        AccessContext ctx, IReadOnlyCollection<long> orderIds, CancellationToken ct) =>
        BundleLines.InOrderTerms((await db.StockDocuments.AsNoTracking()
                .Where(d => d.OrganizationId == ctx.OrganizationId && d.SalesOrderId != null && orderIds.Contains(d.SalesOrderId.Value)
                            && d.Status == StockDocumentStatus.Posted)
                .SelectMany(d => d.Lines.Select(l => new { d.Id, OrderId = d.SalesOrderId!.Value, d.Kind, l.ItemId, l.Quantity, l.BundleItemId, l.BundleQuantity }))
                .ToListAsync(ct))
            .Select(x => ((x.Id, x.OrderId, Kind: StockOperationKinds.OrderKind(x.Kind)), x.ItemId, x.Quantity, x.BundleItemId, x.BundleQuantity)))
        .Select(x => (x.Key.OrderId, x.Key.Kind, x.ItemId, x.Quantity)).ToList();

    /// <summary>Оплаты с фильтрами; фильтры — до проекции в DTO, чтобы запрос переводился в SQL.</summary>
    private IQueryable<CustomerPaymentDto> PaymentsQuery(
        AccessContext ctx, long? orderId = null, DateOnly? from = null, DateOnly? to = null, string? search = null)
    {
        var q = from p in db.CustomerPayments.AsNoTracking()
                where p.OrganizationId == ctx.OrganizationId
                join s in db.Counterparties.AsNoTracking() on p.CustomerId equals s.Id
                join u in db.Users.AsNoTracking() on p.CreatedByUserId equals u.Id
                join o in db.SalesOrders.AsNoTracking() on p.SalesOrderId equals o.Id into oj
                from o in oj.DefaultIfEmpty()
                select new { p, Customer = s.Name, Author = u.DisplayName, OrderNumber = o == null ? null : o.Number };
        if (orderId is { } oid)
        {
            q = q.Where(x => db.CustomerPaymentAllocations.Any(a => a.PaymentId == x.p.Id && a.OrderId == oid && a.RemovedAtUtc == null));
        }

        if (from is { } f)
        {
            q = q.Where(x => x.p.PaymentDate >= f);
        }

        if (to is { } t)
        {
            q = q.Where(x => x.p.PaymentDate <= t);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            q = q.Where(x => x.p.Number.Contains(text) || x.Customer.Contains(text) || (x.OrderNumber != null && x.OrderNumber.Contains(text))
                             || (x.p.CashOrderNumber != null && x.p.CashOrderNumber.Contains(text))
                             || db.CustomerPaymentAllocations.Any(a => a.PaymentId == x.p.Id && a.RemovedAtUtc == null
                                                     && db.SalesOrders.Any(o => o.Id == a.OrderId && o.Number.Contains(text))));
        }

        return q.OrderByDescending(x => x.p.PaymentDate).ThenByDescending(x => x.p.Id).Take(MaxRows)
            .Select(x => new CustomerPaymentDto(x.p.Id, x.p.Number, x.p.PaymentDate, x.p.CustomerId, x.Customer, x.p.SalesOrderId, x.OrderNumber,
                x.p.Amount, x.p.Comment, x.p.Status, x.p.CancelReason, x.Author, x.p.RowVersion));
    }

    private async Task<long?> DefaultReasonAsync(AccessContext ctx, StockOperationKind kind, string preferred, CancellationToken ct)
    {
        var reasons = await db.OperationReasons.AsNoTracking()
            .Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == kind && !r.IsArchived)
            .Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        return (reasons.FirstOrDefault(r => r.Name == preferred) ?? reasons.FirstOrDefault())?.Id;
    }

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    /// <summary>
    /// Проверка шапки; возвращает шапку с юрлицом (D78): у нового заказа без юрлица — основное, при смене — только действующее своё.
    /// </summary>
    private async Task<SalesOrderHeader> ValidateHeaderAsync(AccessContext ctx, SalesOrderHeader h, long? currentEntityId, CancellationToken ct)
    {
        if (h.LegalEntityId is null && currentEntityId is null)
        {
            h = h with
            {
                LegalEntityId = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault)
                                    .Select(e => (long?)e.Id).SingleOrDefaultAsync(ct)
                                ?? throw new BusinessRuleException("legal_entity.no_default", "Нет основного юрлица — добавьте его в разделе «Юрлица и счета»."),
            };
        }
        else if (h.LegalEntityId is { } entityId && entityId != currentEntityId)
        {
            var entity = await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.OrganizationId == ctx.OrganizationId, ct)
                         ?? throw new NotFoundException("Юрлицо");
            entity.EnsureActive();
        }

        var customer = await db.Counterparties.AsNoTracking().SingleOrDefaultAsync(c => c.Id == h.CustomerId && c.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Контрагент");
        if (customer.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Контрагент «{customer.Name}» в архиве.");
        }

        if (!customer.IsCustomer)
        {
            throw new BusinessRuleException("stock.document.not_customer", $"«{customer.Name}» не отмечен как покупатель.");
        }

        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == h.WarehouseId && w.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Склад");
        if (warehouse.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Склад «{warehouse.Name}» в архиве.");
        }

        return h;
    }

    private static SalesOrderHeader Header(SalesOrder o) =>
        new(o.OrderDate, o.CustomerId, o.WarehouseId, o.ShipDate, o.CustomerReference, o.PricesIncludeVat, o.Comment);

    /// <summary>Правка заказа и оплат — право «Продажи: заказы и оплаты» вместе с «Цены и суммы»: без цен заказ не составить.</summary>
    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<SalesOrder> LoadOrderAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.SalesOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Заказ покупателя");

    private async Task<(AccessContext, SalesOrder)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await DemandEditAsync(ct);
        var order = await LoadOrderAsync(ctx, id, ct);
        return (ctx, order.EnsureVersion(order.RowVersion, rowVersion));
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
