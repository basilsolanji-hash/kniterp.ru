using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Warehousing;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace KnitErp.Application.Organizations;

/// <summary>Показатель главного экрана: число, подпись и куда ведёт щелчок. Warn — требует внимания.</summary>
public sealed record DashboardTileDto(string Title, int Value, string Hint, string Href, bool Warn = false);

public sealed record DashboardDocumentDto(DateOnly Date, string Kind, string Number, string Warehouse, string Status, string Href);

/// <summary>Документы склада за месяц по видам — количество проведённых.</summary>
public sealed record DashboardMonthDto(int Receipts, int Transfers, int WriteOffs, int Inventories, DateOnly From, DateOnly To);

/// <summary>
/// Динамика за последние дни: сумма по дням (Points[0] — день From), итог периода и итог такого же периода перед ним.
/// </summary>
public sealed record DashboardChartDto(string Title, string Href, DateOnly From, IReadOnlyList<decimal> Points, decimal Total, decimal PreviousTotal);

public sealed record DashboardDebtorDto(long Id, string Name, decimal Debt);

/// <summary>
/// Деньги и то, что ждёт действий по продажам и закупкам (D74). Поле null — нет права на эти данные.
/// Долг покупателей и наш долг поставщикам — по расчётам (отгружено/принято − возвраты − оплаты), только положительные остатки.
/// </summary>
public sealed record DashboardFinanceDto(
    decimal? CustomerDebt, decimal? SupplierDebt, int? OverdueInvoices, decimal? OverdueAmount, int? SupplierInvoicesToPay,
    decimal? SupplierToPay, int? OrdersToShip, int? ReceiptsWithoutVatInvoice, IReadOnlyList<DashboardDebtorDto> TopDebtors,
    int? OverdueShipments = null, int? OverdueReceipts = null, IReadOnlyList<MoneyAccountBalanceDto>? Accounts = null,
    KnitErp.Application.Finance.MoneyAlertsDto? MoneyAlerts = null)
{
    /// <summary>Деньги на всех счетах и в кассах всех юрлиц (D80).</summary>
    public decimal? AccountsTotal => Accounts?.Sum(a => a.Balance);
}

/// <summary>Данные главного экрана. Layout — личная настройка блоков; данные скрытых блоков не считаются. CachedAtUtc — когда посчитаны.</summary>
public sealed record DashboardDto(
    string OrganizationName, IReadOnlyList<DashboardTileDto> Tiles, DashboardMonthDto? Month, IReadOnlyList<DashboardDocumentDto> Recent,
    int? ReadinessPercent, IReadOnlyList<DashboardChartDto> Charts, DashboardFinanceDto? Finance = null, DashboardLayout? Layout = null,
    DateTime CachedAtUtc = default);

/// <summary>
/// Главный экран: показатели, что ждёт действия, последние документы. Каждый блок — только при праве на его данные
/// и в пределах складов пользователя; без прав на склад экран показывает то, что доступно (справочники, задачи).
/// <para>
/// Скорость: права проверяются при каждом вызове (свежие, из базы), затем данные берутся из кэша на
/// <see cref="CacheSeconds"/> секунд. Ключ кэша — организация, пользователь, его права и области складов и набор видимых блоков,
/// поэтому данные одной организации или одного набора прав не попадают другому. Суммы считаются агрегатами в SQL:
/// без загрузки документов целиком и без повторной проверки прав в каждом разделе.
/// </para>
/// </summary>
public sealed class DashboardService(IKnitErpDbContext db, IAccessGuard guard, IClock clock, LaunchReadinessService readiness, IMemoryCache cache)
{
    /// <summary>Дней в графике динамики.</summary>
    public const int ChartDays = 30;

    public const int TopDebtors = 5;

    /// <summary>Сколько секунд главный экран показывает уже посчитанные данные (D74: экран — обзор, не отчёт).</summary>
    public const int CacheSeconds = 60;

    private static readonly string[] DocumentPermissions =
        [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];

    /// <summary>Права, от которых зависит содержимое экрана, — входят в ключ кэша вместе с областями складов.</summary>
    private static readonly string[] RelevantPermissions =
    [
        Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView, Permissions.OpeningBalanceApprove,
        Permissions.CatalogView, Permissions.OrganizationEdit, Permissions.PriceView, Permissions.SalesView, Permissions.PurchaseView,
    ];

    [Flags]
    private enum Parts
    {
        None = 0,
        Attention = 1,
        Month = 2,
        Recent = 4,
        Reference = 8,
        Money = 16,
        Readiness = 32,
        Accounts = 64,
    }

    private sealed record CacheKey(long OrganizationId, long UserId, string Permissions, Parts Parts);

    /// <param name="refresh">true — посчитать заново, не глядя в кэш (кнопка «Обновить»).</param>
    public async Task<DashboardDto> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var org = ctx.OrganizationId;
        var head = await db.Organizations.AsNoTracking().Where(o => o.Id == org)
            .Select(o => new
            {
                o.ShortName,
                o.TimeZoneId,
                Layout = db.UserToolData.Where(d => d.OrganizationId == org && d.UserId == ctx.UserId && d.Kind == PersonalToolsService.DashboardKind)
                    .Select(d => d.Json).FirstOrDefault(),
            })
            .SingleAsync(ct);
        var layout = DashboardBlocks.Parse(head.Layout);
        var parts = PartsFor(layout);
        var key = new CacheKey(org, ctx.UserId, Fingerprint(ctx.Permissions), parts);
        if (!refresh && cache.TryGetValue(key, out DashboardDto? cached) && cached is not null)
        {
            return cached with { OrganizationName = head.ShortName, Layout = layout };
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, head.TimeZoneId));
        var data = await BuildAsync(ctx, parts, today, ct) with { OrganizationName = head.ShortName, CachedAtUtc = clock.UtcNow };
        cache.Set(key, data, TimeSpan.FromSeconds(CacheSeconds));
        return data with { Layout = layout };
    }

    private static Parts PartsFor(DashboardLayout layout)
    {
        var parts = Parts.None;
        if (layout.Shows(DashboardBlocks.Attention) || layout.Shows(DashboardBlocks.Kpi))
        {
            parts |= Parts.Attention;
        }

        if (layout.Shows(DashboardBlocks.Kpi) || layout.Shows(DashboardBlocks.Debtors) || layout.Shows(DashboardBlocks.Charts))
        {
            parts |= Parts.Money;
        }

        if (layout.Shows(DashboardBlocks.Money))
        {
            parts |= Parts.Accounts;
        }

        if (layout.Shows(DashboardBlocks.Month))
        {
            parts |= Parts.Month;
        }

        if (layout.Shows(DashboardBlocks.Recent))
        {
            parts |= Parts.Recent;
        }

        if (layout.Shows(DashboardBlocks.Reference))
        {
            parts |= Parts.Reference;
        }

        if (layout.Shows(DashboardBlocks.Readiness))
        {
            parts |= Parts.Readiness;
        }

        return parts;
    }

    /// <summary>Отпечаток прав для ключа кэша: есть ли право и в пределах каких складов.</summary>
    private static string Fingerprint(EffectivePermissionSet permissions) =>
        string.Join(';', RelevantPermissions.Select(code =>
        {
            if (!permissions.Has(code))
            {
                return "-";
            }

            var scope = permissions.ScopeOf(code);
            return scope.All ? "*" : string.Join(',', scope.WarehouseIds.Order());
        }));

    private async Task<DashboardDto> BuildAsync(AccessContext ctx, Parts parts, DateOnly today, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var p = ctx.Permissions;

        // Все счётчики — одним запросом; каждый считается, только если есть право и блок виден.
        var hasDocs = DocumentPermissions.Any(p.Has);
        var docsVisible = hasDocs ? WarehouseScope.Visible(ctx, DocumentPermissions) : null;
        var openingVisible = p.Has(Permissions.OpeningBalanceApprove) ? WarehouseScope.Visible(ctx, Permissions.OpeningBalanceApprove) : null;
        var stockVisible = p.Has(Permissions.WarehouseReportView) ? WarehouseScope.Visible(ctx, Permissions.WarehouseReportView) : null;
        var attentionDocs = hasDocs && parts.HasFlag(Parts.Attention);
        var monthDocs = hasDocs && parts.HasFlag(Parts.Month);
        var opening = p.Has(Permissions.OpeningBalanceApprove) && parts.HasFlag(Parts.Attention);
        var positions = p.Has(Permissions.WarehouseReportView) && parts.HasFlag(Parts.Reference);
        var catalog = p.Has(Permissions.CatalogView) && parts.HasFlag(Parts.Reference);
        var sales = p.Has(Permissions.PriceView) && p.Has(Permissions.SalesView);
        var purchases = p.Has(Permissions.PriceView) && p.Has(Permissions.PurchaseView);
        var withoutVat = purchases && parts.HasFlag(Parts.Attention);

        var docs = db.StockDocuments.Where(d => d.OrganizationId == org
            && (docsVisible == null || docsVisible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && docsVisible.Contains(d.TargetWarehouseId.Value))));
        var counts = db.InventoryCounts.Where(d => d.OrganizationId == org && (docsVisible == null || docsVisible.Contains(d.WarehouseId)));
        var monthPosted = docs.Where(d => d.Status != StockDocumentStatus.Draft && d.Status != StockDocumentStatus.Cancelled
                                          && d.DocumentDate >= monthStart && d.DocumentDate <= today);
        var c = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => new
        {
            Drafts = attentionDocs ? docs.Count(d => d.Status == StockDocumentStatus.Draft) : 0,
            OpenCounts = attentionDocs ? counts.Count(d => d.Status == InventoryStatus.Draft) : 0,
            Receipts = monthDocs ? monthPosted.Count(d => d.Kind == StockOperationKind.Receipt) : 0,
            Transfers = monthDocs ? monthPosted.Count(d => d.Kind == StockOperationKind.Transfer) : 0,
            WriteOffs = monthDocs ? monthPosted.Count(d => d.Kind == StockOperationKind.WriteOff) : 0,
            Inventories = monthDocs ? counts.Count(d => d.Status == InventoryStatus.Posted && d.CountDate >= monthStart && d.CountDate <= today) : 0,
            Opening = opening
                ? db.OpeningBalances.Count(d => d.OrganizationId == org && d.Status == OpeningBalanceStatus.Submitted
                                                && (openingVisible == null || openingVisible.Contains(d.WarehouseId)))
                : 0,
            Positions = positions
                ? db.StockMovements.Where(m => m.OrganizationId == org && (stockVisible == null || stockVisible.Contains(m.WarehouseId)))
                    .GroupBy(m => new { m.WarehouseId, m.ItemId }).Count(g => g.Sum(m => m.Quantity) != 0)
                : 0,
            Items = catalog ? db.Items.Count(i => i.OrganizationId == org && !i.IsArchived) : 0,
            Cards = catalog ? db.TechCards.Count(t => t.OrganizationId == org && t.Status == TechCardStatus.Active) : 0,
            WithoutVat = withoutVat
                ? db.StockDocuments.Count(d => d.OrganizationId == org && d.Kind == StockOperationKind.Receipt && d.PurchaseOrderId != null
                                               && d.CounterpartyId != null && d.Status == StockDocumentStatus.Posted
                                               && !db.ReceivedVatInvoices.Any(i => i.ReceiptDocumentId == d.Id && i.Status == ReceivedVatInvoiceStatus.Registered))
                : 0,
        }).SingleAsync(ct);

        var tiles = new List<DashboardTileDto>();
        if (attentionDocs)
        {
            var drafts = c.Drafts;
            tiles.Add(new DashboardTileDto("Черновики документов", drafts, "ждут проведения", "stock-documents?status=1", drafts > 0));
            var openCounts = c.OpenCounts;
            tiles.Add(new DashboardTileDto("Инвентаризации в работе", openCounts, "не проведены", "inventory", openCounts > 0));
        }

        if (opening)
        {
            var waiting = c.Opening;
            tiles.Add(new DashboardTileDto("Начальные остатки", waiting, "на утверждении", "opening-balances", waiting > 0));
        }

        if (positions)
        {
            var count = c.Positions;
            tiles.Add(new DashboardTileDto("Позиций на остатке", count, "склад × позиция", "stock"));
        }

        if (catalog)
        {
            var items = c.Items;
            tiles.Add(new DashboardTileDto("Номенклатура", items, "действующих позиций", "catalog"));
            var cards = c.Cards;
            tiles.Add(new DashboardTileDto("Техкарты", cards, "действующих", "tech-cards"));
        }

        var month = monthDocs ? new DashboardMonthDto(c.Receipts, c.Transfers, c.WriteOffs, c.Inventories, monthStart, today) : null;

        IReadOnlyList<DashboardDocumentDto> recent = [];
        if (hasDocs && parts.HasFlag(Parts.Recent))
        {
            recent = (await (from d in docs.AsNoTracking().OrderByDescending(d => d.Id).Take(8)
                             join w in db.Warehouses.AsNoTracking() on d.WarehouseId equals w.Id
                             orderby d.Id descending
                             select new { d.Id, d.DocumentDate, d.Kind, d.Number, w.Name, d.Status }).ToListAsync(ct))
                .Select(d => new DashboardDocumentDto(d.DocumentDate, StockDocument.KindName(d.Kind), d.Number, d.Name,
                    StockDocument.StatusName(d.Status), $"stock-documents/{d.Id}"))
                .ToList();
        }

        int? readinessPercent = null;
        if (p.Has(Permissions.OrganizationEdit) && parts.HasFlag(Parts.Readiness))
        {
            var report = await readiness.BuildAsync(ctx, ct);
            var checkable = report.Items.Where(i => i.Done is not null).ToList();
            readinessPercent = checkable.Count == 0 ? 100 : checkable.Count(i => i.Done == true) * 100 / checkable.Count;
        }

        var charts = new List<DashboardChartDto>();
        DashboardFinanceDto? finance = null;
        if (p.Has(Permissions.PriceView))
        {
            finance = await FinanceAsync(ctx, parts, today, charts, ct);
            if (withoutVat)
            {
                finance = finance with { ReceiptsWithoutVatInvoice = c.WithoutVat };
            }
        }

        return new DashboardDto(string.Empty, tiles, month, recent, readinessPercent, charts, finance);
    }

    /// <summary>
    /// Деньги (D67, D74) по правилам разделов продаж и закупок: суммы отгрузок и поступлений — по ценам заказа с НДС,
    /// только проведённые документы и оплаты. Динамика за 30 дней и расчёты с контрагентами считаются из одних и тех же
    /// агрегатов: количество по (контрагент, вид, день, строка заказа) складывается в SQL, умножение на цену — здесь.
    /// </summary>
    private async Task<DashboardFinanceDto> FinanceAsync(AccessContext ctx, Parts parts, DateOnly today, List<DashboardChartDto> charts, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var windowStart = today.AddDays(-(2 * ChartDays - 1));
        var money = parts.HasFlag(Parts.Money);
        var attention = parts.HasFlag(Parts.Attention);
        decimal? customerDebt = null, supplierDebt = null, overdueAmount = null, supplierToPay = null;
        int? overdue = null, toPay = null, toShip = null, lateShipments = null, lateReceipts = null;
        IReadOnlyList<DashboardDebtorDto> top = [];

        if (ctx.Permissions.Has(Permissions.SalesView))
        {
            if (money)
            {
                // Отгрузки и возвраты по заказам: день — в окне двух периодов графика, раньше и позже — только в расчёты.
                var moved = await (from d in db.StockDocuments.AsNoTracking()
                                   where d.OrganizationId == org && d.SalesOrderId != null && d.Status == StockDocumentStatus.Posted
                                   from l in d.Lines
                                   join o in db.SalesOrders.AsNoTracking() on d.SalesOrderId equals o.Id
                                   join cp in db.Counterparties.AsNoTracking() on o.CustomerId equals cp.Id
                                   from ol in o.Lines.Where(x => x.ItemId == l.ItemId).DefaultIfEmpty()
                                   group l.Quantity by new
                                   {
                                       o.CustomerId, cp.Name, d.Kind,
                                       Day = d.DocumentDate >= windowStart && d.DocumentDate <= today ? (DateOnly?)d.DocumentDate : null,
                                       Amount = (decimal?)ol.Amount, LineQuantity = (decimal?)ol.Quantity,
                                   } into g
                                   select new { g.Key.CustomerId, g.Key.Name, g.Key.Kind, g.Key.Day, g.Key.Amount, g.Key.LineQuantity, Quantity = g.Sum() })
                    .ToListAsync(ct);
                var paid = await (from pay in db.CustomerPayments.AsNoTracking()
                                  where pay.OrganizationId == org && pay.Status == CustomerPaymentStatus.Posted
                                  join cp in db.Counterparties.AsNoTracking() on pay.CustomerId equals cp.Id
                                  group pay.Amount by new
                                  {
                                      pay.CustomerId, cp.Name,
                                      Day = pay.PaymentDate >= windowStart && pay.PaymentDate <= today ? (DateOnly?)pay.PaymentDate : null,
                                  } into g
                                  select new { g.Key.CustomerId, g.Key.Name, g.Key.Day, Sum = g.Sum() })
                    .ToListAsync(ct);

                charts.Add(Chart("Продажи", "reports/customer-balances", today, moved.Where(m => m.Day != null).Select(m => (m.Day!.Value,
                    (m.Kind == StockOperationKind.CustomerReturn ? -1 : 1) * Value(m.Quantity, m.Amount, m.LineQuantity)))));
                charts.Add(Chart("Поступления от покупателей", "customer-payments", today,
                    paid.Where(x => x.Day != null).Select(x => (x.Day!.Value, x.Sum))));

                var balances = Balances(
                    moved.Select(m => (m.CustomerId, m.Name, m.Kind, Value: Value(m.Quantity, m.Amount, m.LineQuantity))),
                    paid.Select(x => (x.CustomerId, x.Name, x.Sum)), StockOperationKind.Shipment, StockOperationKind.CustomerReturn);
                customerDebt = balances.Where(b => b.Debt > 0).Sum(b => b.Debt);
                top = balances.Where(b => b.Debt > 0).OrderByDescending(b => b.Debt).ThenBy(b => b.Name).Take(TopDebtors)
                    .Select(b => new DashboardDebtorDto(b.Id, b.Name, b.Debt)).ToList();
            }

            if (attention)
            {
                // Просроченные счета: выставлены, срок прошёл, оплаты по заказу меньше суммы счёта.
                var invoices = await db.CustomerInvoices.AsNoTracking()
                    .Where(i => i.OrganizationId == org && i.Status == CustomerInvoiceStatus.Issued && i.DueDate != null && i.DueDate < today)
                    .Select(i => new
                    {
                        Total = i.Lines.Sum(l => l.Amount),
                        Paid = db.CustomerPaymentAllocations.Where(a => a.OrganizationId == org && a.OrderId == i.SalesOrderId && a.RemovedAtUtc == null
                                                                        && db.CustomerPayments.Any(pay => pay.Id == a.PaymentId
                                                                                                          && pay.Status == CustomerPaymentStatus.Posted))
                            .Sum(a => (decimal?)a.Amount) ?? 0m,
                    })
                    .ToListAsync(ct);
                var late = invoices.Select(i => (i.Total, Paid: Math.Min(i.Paid, i.Total))).Where(i => i.Paid < i.Total).ToList();
                overdue = late.Count;
                overdueAmount = late.Sum(i => i.Total - i.Paid);

                // Подтверждённые заказы, отгруженные не полностью: по каждой строке — отгружено минус возвращено.
                var lines = await (from o in db.SalesOrders.AsNoTracking()
                                   where o.OrganizationId == org && o.Status == SalesOrderStatus.Confirmed
                                   from l in o.Lines.DefaultIfEmpty()
                                   select new
                                   {
                                       o.Id,
                                       o.ShipDate,
                                       Ordered = (decimal?)l.Quantity,
                                       Net = l == null
                                           ? 0m
                                           : db.StockDocuments.Where(d => d.OrganizationId == org && d.SalesOrderId == o.Id && d.Status == StockDocumentStatus.Posted)
                                               .SelectMany(d => d.Lines.Where(x => x.ItemId == l.ItemId)
                                                   .Select(x => d.Kind == StockOperationKind.Shipment ? x.Quantity : -x.Quantity))
                                               .Sum(),
                                   })
                    .ToListAsync(ct);
                var notShipped = lines.GroupBy(l => (l.Id, l.ShipDate))
                    .Where(g => !FullyMoved(g.Where(l => l.Ordered != null).Select(l => (l.Ordered!.Value, l.Net)).ToList())).ToList();
                toShip = notShipped.Count;
                lateShipments = notShipped.Count(g => g.Key.ShipDate is { } d && d < today);
            }
        }

        if (ctx.Permissions.Has(Permissions.PurchaseView))
        {
            if (money)
            {
                var moved = await (from d in db.StockDocuments.AsNoTracking()
                                   where d.OrganizationId == org && d.PurchaseOrderId != null && d.Status == StockDocumentStatus.Posted
                                   from l in d.Lines
                                   join o in db.PurchaseOrders.AsNoTracking() on d.PurchaseOrderId equals o.Id
                                   from ol in o.Lines.Where(x => x.ItemId == l.ItemId).DefaultIfEmpty()
                                   group l.Quantity by new
                                   {
                                       o.SupplierId, d.Kind,
                                       Day = d.DocumentDate >= windowStart && d.DocumentDate <= today ? (DateOnly?)d.DocumentDate : null,
                                       Amount = (decimal?)ol.Amount, LineQuantity = (decimal?)ol.Quantity,
                                   } into g
                                   select new { g.Key.SupplierId, g.Key.Kind, g.Key.Day, g.Key.Amount, g.Key.LineQuantity, Quantity = g.Sum() })
                    .ToListAsync(ct);
                var paid = await db.SupplierPayments.AsNoTracking()
                    .Where(pay => pay.OrganizationId == org && pay.Status == SupplierPaymentStatus.Posted)
                    .GroupBy(pay => new { pay.SupplierId, Day = pay.PaymentDate >= windowStart && pay.PaymentDate <= today ? (DateOnly?)pay.PaymentDate : null })
                    .Select(g => new { g.Key.SupplierId, g.Key.Day, Sum = g.Sum(pay => pay.Amount) })
                    .ToListAsync(ct);

                charts.Add(Chart("Закупки", "reports/supplier-balances", today, moved.Where(m => m.Day != null).Select(m => (m.Day!.Value,
                    (m.Kind == StockOperationKind.ReturnToSupplier ? -1 : 1) * Value(m.Quantity, m.Amount, m.LineQuantity)))));
                charts.Add(Chart("Оплаты поставщикам", "supplier-payments", today, paid.Where(x => x.Day != null).Select(x => (x.Day!.Value, x.Sum))));

                supplierDebt = Balances(
                        moved.Select(m => (m.SupplierId, string.Empty, m.Kind, Value: Value(m.Quantity, m.Amount, m.LineQuantity))),
                        paid.Select(x => (x.SupplierId, string.Empty, x.Sum)), StockOperationKind.Receipt, StockOperationKind.ReturnToSupplier)
                    .Where(b => b.Debt > 0).Sum(b => b.Debt);
            }

            if (attention)
            {
                // Счета поставщиков (D64 — счёт в шапке заказа): заказ не черновик и не отменён, оплачено меньше суммы.
                var unpaid = await db.PurchaseOrders.AsNoTracking()
                    .Where(o => o.OrganizationId == org && o.SupplierInvoice != null
                                && o.Status != PurchaseOrderStatus.Draft && o.Status != PurchaseOrderStatus.Cancelled)
                    .Select(o => new
                    {
                        Total = o.Lines.Sum(l => l.Amount),
                        Paid = db.SupplierPaymentAllocations.Where(a => a.OrganizationId == org && a.OrderId == o.Id && a.RemovedAtUtc == null
                                                                        && db.SupplierPayments.Any(pay => pay.Id == a.PaymentId
                                                                                                          && pay.Status == SupplierPaymentStatus.Posted))
                            .Sum(a => (decimal?)a.Amount) ?? 0m,
                    })
                    .Where(x => x.Total - x.Paid > 0)
                    .Select(x => x.Total - x.Paid)
                    .ToListAsync(ct);
                toPay = unpaid.Count;
                supplierToPay = unpaid.Sum();

                // Просроченные поступления: подтверждённый заказ поставщику, ожидаемая дата прошла, принято не всё.
                var receipts = await (from o in db.PurchaseOrders.AsNoTracking()
                                      where o.OrganizationId == org && o.Status == PurchaseOrderStatus.Confirmed && o.ExpectedDate != null
                                            && o.ExpectedDate < today
                                      from l in o.Lines.DefaultIfEmpty()
                                      select new
                                      {
                                          o.Id,
                                          Ordered = (decimal?)l.Quantity,
                                          Net = l == null
                                              ? 0m
                                              : db.StockDocuments.Where(d => d.OrganizationId == org && d.PurchaseOrderId == o.Id
                                                                             && d.Status == StockDocumentStatus.Posted)
                                                  .SelectMany(d => d.Lines.Where(x => x.ItemId == l.ItemId)
                                                      .Select(x => d.Kind == StockOperationKind.Receipt ? x.Quantity : -x.Quantity))
                                                  .Sum(),
                                      })
                    .ToListAsync(ct);
                lateReceipts = receipts.GroupBy(l => l.Id)
                    .Count(g => !FullyMoved(g.Where(l => l.Ordered != null).Select(l => (l.Ordered!.Value, l.Net)).ToList()));
            }
        }

        var accounts = parts.HasFlag(Parts.Accounts) ? await MoneyService.BalancesAsync(db, org, today, includeArchived: false, ct) : null;

        // D87: просроченные авансовые отчёты, долг подотчётных лиц, кассы сверх лимита.
        var alerts = attention ? await KnitErp.Application.Finance.MoneyAlerts.ComputeAsync(db, org, today, ct) : null;
        return new DashboardFinanceDto(customerDebt, supplierDebt, overdue, overdueAmount, toPay, supplierToPay, toShip, null, top,
            lateShipments, lateReceipts, accounts, alerts);
    }

    /// <summary>Сумма по цене строки заказа с НДС: сумма строки / количество строки (как UnitCostWithVat заказа).</summary>
    private static decimal Value(decimal quantity, decimal? amount, decimal? lineQuantity) =>
        amount is { } a && lineQuantity is { } q && q != 0 ? quantity * (a / q) : 0m;

    /// <summary>Заказ выполнен полностью: есть строки, хоть что-то отгружено и по каждой строке отгружено не меньше заказанного.</summary>
    private static bool FullyMoved(IReadOnlyList<(decimal Ordered, decimal Net)> lines) =>
        lines.Count > 0 && !lines.All(l => l.Net <= 0) && lines.All(l => l.Net >= l.Ordered);

    /// <summary>Расчёты по контрагентам — как в отчётах расчётов: отгружено/принято и возвращено округляются по контрагенту.</summary>
    private static List<(long Id, string Name, decimal Debt)> Balances(
        IEnumerable<(long Id, string Name, StockOperationKind Kind, decimal Value)> moved, IEnumerable<(long Id, string Name, decimal Sum)> paid,
        StockOperationKind outgoing, StockOperationKind returned)
    {
        var result = new Dictionary<long, (string Name, decimal Out, decimal Back, decimal Paid)>();
        foreach (var m in moved)
        {
            var r = result.GetValueOrDefault(m.Id, (Name: m.Name, Out: 0m, Back: 0m, Paid: 0m));
            if (m.Kind == outgoing)
            {
                r.Out += m.Value;
            }
            else if (m.Kind == returned)
            {
                r.Back += m.Value;
            }

            result[m.Id] = r;
        }

        foreach (var x in paid)
        {
            var r = result.GetValueOrDefault(x.Id, (Name: x.Name, Out: 0m, Back: 0m, Paid: 0m));
            r.Paid += x.Sum;
            result[x.Id] = r;
        }

        return result.Select(kv => (kv.Key, kv.Value.Name, Money.Round(kv.Value.Out) - Money.Round(kv.Value.Back) - kv.Value.Paid)).ToList();
    }

    /// <summary>Раскладывает суммы по дням последних ChartDays дней; всё, что раньше, — в итог предыдущего периода.</summary>
    private static DashboardChartDto Chart(string title, string href, DateOnly today, IEnumerable<(DateOnly Date, decimal Amount)> rows)
    {
        var from = today.AddDays(-(ChartDays - 1));
        var points = new decimal[ChartDays];
        var previous = 0m;
        foreach (var (date, amount) in rows)
        {
            if (date >= from)
            {
                points[date.DayNumber - from.DayNumber] += amount;
            }
            else
            {
                previous += amount;
            }
        }

        var rounded = points.Select(Money.Round).ToList();
        return new DashboardChartDto(title, href, from, rounded, rounded.Sum(), Money.Round(previous));
    }
}
