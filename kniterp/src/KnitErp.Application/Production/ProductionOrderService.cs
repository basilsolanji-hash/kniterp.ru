using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

public sealed record ProductionOrderRowDto(
    long Id, string Number, DateOnly OrderDate, string ItemCode, string ItemName, decimal Quantity, string UnitSymbol, DateOnly? DueDate,
    ProductionSource Source, string? SalesOrderNumber, ProductionOrderStatus Status, int TechCardVersion)
{
    public string StatusName => ProductionOrder.StatusName(Status);
    public string SourceName => ProductionOrder.SourceName(Source);
}

/// <summary>Материал заказа: потребность без отхода и с отходом; цена и сумма — только с правом «Цены и суммы».</summary>
public sealed record ProductionMaterialDto(
    long ItemId, string Code, string Name, string UnitSymbol, decimal Net, decimal Gross, decimal WastePercent, decimal? Price,
    bool CanProduce = false, decimal ChildPlanned = 0)
{
    public decimal? Amount => Price is { } p ? Money.Round(p * Gross) : null;
}

public sealed record ProductionOrderDto(
    long Id, string Number, ProductionOrderStatus Status, DateOnly OrderDate, DateOnly? DueDate,
    long ItemId, string ItemCode, string ItemName, string UnitSymbol, byte Precision, bool IsModification, decimal Quantity,
    ProductionSource Source, long? SalesOrderId, string? SalesOrderNumber,
    long WipWarehouseId, string WipWarehouseName, long OutputWarehouseId, string OutputWarehouseName,
    long? TechCardId, int? TechCardVersion, string? TechCardItemName, string? Comment,
    string CreatedBy, DateTime CreatedAtUtc, string? ReleasedBy, DateTime? ReleasedAtUtc,
    string? CancelledBy, DateTime? CancelledAtUtc, string? CancelReason,
    IReadOnlyList<ProductionMaterialDto> Materials, bool MaterialsFixed, bool PricesVisible, decimal? PlannedCost,
    byte[] RowVersion, bool CanEdit, bool CanRelease, bool CanCancel,
    decimal Released = 0, decimal? ActualCost = null, string? ClosedBy = null, DateTime? ClosedAtUtc = null, string? CloseReason = null,
    bool CanClose = false, long? ParentOrderId = null, string? ParentOrderNumber = null, IReadOnlyList<ProductionChildDto>? Children = null,
    bool CanCreateChild = false, IReadOnlyList<ProductionStage>? Route = null)
{
    public string StatusName => ProductionOrder.StatusName(Status);
    public string SourceName => ProductionOrder.SourceName(Source);

    /// <summary>Фактическая себестоимость единицы: по выданной пряже (выдано − возвращено) на выпущенное.</summary>
    public decimal? ActualUnitCost => ActualCost is { } c && Released > 0 ? decimal.Round(c / Released, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Плановая себестоимость единицы изделия; null — нет права на цены или цена материала неизвестна.</summary>
    public decimal? PlannedUnitCost => PlannedCost is { } c && Quantity > 0 ? decimal.Round(c / Quantity, 2, MidpointRounding.AwayFromZero) : null;
}

/// <summary>Заказ на полуфабрикат для этого заказа (D90, дополнение «сборка из полуфабрикатов»).</summary>
public sealed record ProductionChildDto(long Id, string Number, string ItemCode, string ItemName, decimal Quantity, ProductionOrderStatus Status)
{
    public string StatusName => ProductionOrder.StatusName(Status);
}

/// <summary>Шапка и количество заказа из формы.</summary>
public sealed record ProductionOrderCommand(
    long ItemId, decimal Quantity, DateOnly OrderDate, DateOnly? DueDate, ProductionSource Source, long? SalesOrderId,
    long WipWarehouseId, long OutputWarehouseId, string? Comment, long? ParentOrderId = null);

/// <summary>Изделие для заказа и версия техкарты, по которой его будут выпускать (null — действующей техкарты нет).</summary>
public sealed record ProductionProductOptionDto(
    long Id, string Code, string Name, string UnitSymbol, byte Precision, bool IsModification, int? TechCardVersion, bool CardFromModel);

public sealed record ProductionSalesLineDto(long ItemId, decimal Quantity);

/// <summary>Подтверждённый заказ покупателя; покупатель виден только с правом «Продажи: просмотр».</summary>
public sealed record ProductionSalesOrderOptionDto(long Id, string Number, DateOnly OrderDate, string? CustomerName, IReadOnlyList<ProductionSalesLineDto> Lines);

public sealed record ProductionOrderOptions(
    IReadOnlyList<ProductionProductOptionDto> Products, IReadOnlyList<LookupWarehouseDto> Warehouses,
    IReadOnlyList<ProductionSalesOrderOptionDto> SalesOrders, long? DefaultWipWarehouseId);

/// <summary>Производство по строке заказа покупателя: заказано, запланировано в производственных заказах (кроме отменённых), выпущено.</summary>
public sealed record SalesProductionLineDto(
    long ItemId, string Code, string Name, string UnitSymbol, decimal Ordered, decimal Planned, decimal Released, bool CanPlan, string? Hint)
{
    /// <summary>Сколько ещё не запланировано.</summary>
    public decimal Remaining => Math.Max(0, Ordered - Planned);
}

public sealed record SalesProductionOrderDto(long Id, string Number, string ItemCode, decimal Quantity, decimal Released, ProductionOrderStatus Status)
{
    public string StatusName => ProductionOrder.StatusName(Status);
}

/// <summary>Производство по заказу покупателя (D90, дополнение): строки, производственные заказы и можно ли создать недостающие.</summary>
public sealed record SalesProductionDto(
    long SalesOrderId, IReadOnlyList<SalesProductionLineDto> Lines, IReadOnlyList<SalesProductionOrderDto> Orders, bool CanCreate, string? CreateHint);

/// <summary>Коды подсказок производства по заказу покупателя.</summary>
public static class SalesProductionHints
{
    public const string Purchased = "purchased";
    public const string Archived = "archived";
    public const string NoTechCard = "no_card";
    public const string NotConfirmed = "not_confirmed";
    public const string AllPlanned = "all_planned";
}

/// <summary>Итог создания: номера новых черновиков и коды пропущенных позиций (без техкарты, покупные, в архиве).</summary>
public sealed record SalesProductionResultDto(IReadOnlyList<long> Created, IReadOnlyList<string> Numbers, IReadOnlyList<string> Skipped);

public sealed record ProductionOrderListFilter(ProductionOrderStatus? Status = null, string? Search = null, long? SalesOrderId = null);

/// <summary>
/// Производственные заказы (D90): черновик → «Запустить» (фиксация версии техкарты и снимок норм с плановыми ценами) → этапы, выдача,
/// выпуск (следующие шаги) → закрытие. Права (допущение D91): смотреть — «Производство: просмотр заказов», создавать, запускать
/// и отменять — «Производство: заказы — создание, запуск и отмена»; цены и плановая себестоимость — только с «Цены и суммы».
/// </summary>
public sealed class ProductionOrderService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxListRows = 1000;

    /// <summary>Склад цеха по умолчанию в форме — действующий склад с таким названием (допущение D90).</summary>
    public const string DefaultWipWarehouseName = "Цех (НЗП)";

    public async Task<IReadOnlyList<ProductionOrderRowDto>> ListAsync(ProductionOrderListFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var q = db.ProductionOrders.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId);
        if (filter.Status is { } status)
        {
            q = q.Where(o => o.Status == status);
        }

        if (filter.SalesOrderId is { } salesId)
        {
            q = q.Where(o => o.SalesOrderId == salesId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            q = q.Where(o => o.Number.Contains(text) || db.Items.Any(i => i.Id == o.ItemId && (i.Code.Contains(text) || i.Name.Contains(text))));
        }

        var rows = await (
            from o in q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Take(MaxListRows)
            join i in db.Items.AsNoTracking() on o.ItemId equals i.Id
            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
            join c in db.TechCards.AsNoTracking() on o.TechCardId equals c.Id
            join s in db.SalesOrders.AsNoTracking() on o.SalesOrderId equals s.Id into sj
            from s in sj.DefaultIfEmpty()
            select new ProductionOrderRowDto(o.Id, o.Number, o.OrderDate, i.Code, i.Name, o.Quantity, u.Symbol, o.DueDate, o.Source,
                s == null ? null : s.Number, o.Status, c.Version)).ToListAsync(ct);
        return rows.OrderByDescending(r => r.OrderDate).ThenByDescending(r => r.Id).ToList();
    }

    public async Task<ProductionOrderDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials)
                        .SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        var product = await ItemsQuery(ctx, includeArchived: true, id: order.ItemId).SingleAsync(ct);
        var pricesVisible = ctx.Permissions.Has(Permissions.PriceView);

        // Черновик показывает, что будет зафиксировано при запуске: действующую сейчас версию и её нормы с текущими ценами.
        // Запущенный — снимок, сделанный при запуске; новые версии техкарты и цены его не меняют.
        TechCard? card;
        IReadOnlyList<(long ItemId, decimal Net, decimal Gross, decimal Waste, decimal? Price)> materials;
        if (order.Status == ProductionOrderStatus.Draft)
        {
            card = await ActiveCardAsync(ctx, product.Id, product.ParentItemId, ct);
            materials = card is null ? [] : await PreviewAsync(ctx, card, order.Quantity, ct);
        }
        else
        {
            card = await db.TechCards.AsNoTracking().SingleAsync(c => c.Id == order.TechCardId, ct);
            materials = order.Materials.Select(m => (m.ItemId, m.PlannedNet, m.PlannedGross, m.WastePercent, m.PlannedPrice)).ToList();
        }

        var itemIds = materials.Select(m => m.ItemId).ToList();
        if (card is not null)
        {
            itemIds.Add(card.ItemId);
        }

        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol })
            .ToDictionaryAsync(x => x.Id, ct);
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == order.WipWarehouseId || w.Id == order.OutputWarehouseId).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        long?[] userIds = [order.CreatedByUserId, order.ReleasedByUserId, order.CancelledByUserId, order.ClosedByUserId];
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var salesNumber = order.SalesOrderId is { } sid
            ? await db.SalesOrders.AsNoTracking().Where(s => s.Id == sid).Select(s => s.Number).SingleAsync(ct)
            : null;

        // Сборка из полуфабрикатов: заказы на полуфабрикаты этого заказа и какие материалы можно произвести (есть действующая техкарта).
        var childOrders = await (from ch in db.ProductionOrders.AsNoTracking()
                                 where ch.OrganizationId == ctx.OrganizationId && ch.ParentOrderId == order.Id
                                 join i in db.Items.AsNoTracking() on ch.ItemId equals i.Id
                                 orderby ch.Id
                                 select new ProductionChildDto(ch.Id, ch.Number, i.Code, i.Name, ch.Quantity, ch.Status)).ToListAsync(ct);
        var materialIds = materials.Select(m => m.ItemId).ToList();
        var producible = await db.TechCards.AsNoTracking()
            .Where(t => t.OrganizationId == ctx.OrganizationId && t.Status == TechCardStatus.Active && materialIds.Contains(t.ItemId))
            .Select(t => t.ItemId).ToListAsync(ct);
        var childPlanned = (await db.ProductionOrders.AsNoTracking()
                .Where(ch => ch.OrganizationId == ctx.OrganizationId && ch.ParentOrderId == order.Id && ch.Status != ProductionOrderStatus.Cancelled)
                .Select(ch => new { ch.ItemId, ch.Quantity }).ToListAsync(ct))
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var lines = materials.Select(m => new ProductionMaterialDto(m.ItemId, items[m.ItemId].Code, items[m.ItemId].Name, items[m.ItemId].Symbol,
                m.Net, m.Gross, m.Waste, pricesVisible ? m.Price : null, producible.Contains(m.ItemId), childPlanned.GetValueOrDefault(m.ItemId)))
            .OrderBy(m => m.Code).ToList();
        var parentNumber = order.ParentOrderId is { } pid
            ? await db.ProductionOrders.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Number).SingleAsync(ct)
            : null;
        decimal? plannedCost = pricesVisible && materials.Count > 0 && materials.All(m => m.Price is not null)
            ? Money.Round(materials.Sum(m => m.Gross * m.Price!.Value))
            : null;

        var canEdit = ctx.Permissions.Has(Permissions.ProductionOrderEdit);
        var draft = order.Status == ProductionOrderStatus.Draft;
        var released = draft ? 0m : await ProductionLocks.ReleasedAsync(db, order, ct);
        decimal? actualCost = null;
        if (pricesVisible && !draft && order.Status != ProductionOrderStatus.Cancelled)
        {
            actualCost = await ActualCostAsync(ctx, order, ct);
        }

        var cancellable = order.Status is ProductionOrderStatus.Draft or ProductionOrderStatus.Released
                          && !await HasMovementsAsync(order, ct);
        return new ProductionOrderDto(order.Id, order.Number, order.Status, order.OrderDate, order.DueDate,
            order.ItemId, product.Code, product.Name, product.UnitSymbol, product.Precision, product.ParentItemId is not null, order.Quantity,
            order.Source, order.SalesOrderId, salesNumber,
            order.WipWarehouseId, warehouses[order.WipWarehouseId], order.OutputWarehouseId, warehouses[order.OutputWarehouseId],
            card?.Id, card?.Version, card is null ? null : items[card.ItemId].Name, order.Comment,
            users[order.CreatedByUserId], order.CreatedAtUtc,
            order.ReleasedByUserId is { } r ? users[r] : null, order.ReleasedAtUtc,
            order.CancelledByUserId is { } c ? users[c] : null, order.CancelledAtUtc, order.CancelReason,
            lines, !draft, pricesVisible, plannedCost, order.RowVersion,
            canEdit && draft, canEdit && draft && card is not null, canEdit && cancellable,
            released, actualCost, order.ClosedByUserId is { } cl ? users[cl] : null, order.ClosedAtUtc, order.CloseReason,
            canEdit && order.Status is ProductionOrderStatus.PartiallyDone or ProductionOrderStatus.Done,
            order.ParentOrderId, parentNumber, childOrders,
            canEdit && order.Status is ProductionOrderStatus.Draft or ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone,
            // Маршрут (D401): у запущенного — снимок, у черновика — маршрут техкарты, которая зафиксируется при запуске.
            (draft && card is not null ? card.RouteValue : order.RouteValue).Stages);
    }

    /// <summary>Справочники формы: изделия с версией техкарты, действующие склады и подтверждённые заказы покупателей.</summary>
    public async Task<ProductionOrderOptions> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var products = await ItemsQuery(ctx, productsOnly: true).ToListAsync(ct);
        var cards = await db.TechCards.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status == TechCardStatus.Active)
            .ToDictionaryAsync(c => c.ItemId, c => c.Version, ct);
        var options = products.Select(p =>
        {
            var own = cards.TryGetValue(p.Id, out var v) ? v : (int?)null;
            var model = own is null && p.ParentItemId is { } parent && cards.TryGetValue(parent, out var pv) ? pv : (int?)null;
            return new ProductionProductOptionDto(p.Id, p.Code, p.Name, p.UnitSymbol, p.Precision, p.ParentItemId is not null, own ?? model, model is not null);
        }).ToList();

        var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived)
            .OrderBy(w => w.Name).Select(w => new LookupWarehouseDto(w.Id, w.Name)).ToListAsync(ct);
        var showCustomer = ctx.Permissions.Has(Permissions.SalesView);
        var sales = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.Status == SalesOrderStatus.Confirmed)
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id).Take(500)
            .ToListAsync(ct);
        var customerIds = sales.Select(o => o.CustomerId).Distinct().ToList();
        var customers = showCustomer
            ? await db.Counterparties.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : [];
        var salesOptions = sales.Select(o => new ProductionSalesOrderOptionDto(o.Id, o.Number, o.OrderDate, customers.GetValueOrDefault(o.CustomerId),
            o.Lines.GroupBy(l => l.ItemId).Select(g => new ProductionSalesLineDto(g.Key, g.Sum(l => l.Quantity))).ToList())).ToList();
        var wip = warehouses.FirstOrDefault(w => string.Equals(w.Name, DefaultWipWarehouseName, StringComparison.OrdinalIgnoreCase))?.Id;
        return new ProductionOrderOptions(options, warehouses, salesOptions, wip);
    }

    public async Task<long> CreateAsync(ProductionOrderCommand command, CancellationToken ct = default)
    {
        try
        {
            return await CreateCoreAsync(command, ct);
        }
        catch
        {
            db.DiscardChanges(); // отказ после выдачи номера не должен оставить счётчик и заказ в контексте вкладки
            throw;
        }
    }

    private async Task<long> CreateCoreAsync(ProductionOrderCommand command, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var (product, card) = await ValidateAsync(ctx, command, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, ProductionOrder.NumberPrefix, ct);
        var order = ProductionOrder.Create(ctx.OrganizationId, number, product.Id, command.Quantity, card.Id, Header(command), ctx.UserId, clock.UtcNow,
            command.ParentOrderId);
        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ProductionOrderCreated, order, null,
            $"{product.Code} {product.Name}: {Quantities.Format(order.Quantity)} {product.UnitSymbol}, {ProductionOrder.SourceName(order.Source)}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order.Id;
    }

    /// <summary>Производство по заказу покупателя: по каждой позиции — заказано, запланировано, выпущено; производственные заказы.</summary>
    public async Task<SalesProductionDto> ForSalesOrderAsync(long salesOrderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var sales = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                        .SingleOrDefaultAsync(o => o.Id == salesOrderId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Заказ покупателя");
        var orders = await db.ProductionOrders.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.SalesOrderId == salesOrderId).OrderBy(o => o.Id).ToListAsync(ct);
        var released = new Dictionary<long, decimal>();
        foreach (var o in orders.Where(o => o.Status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Cancelled)))
        {
            released[o.Id] = await ProductionLocks.ReleasedAsync(db, o, ct);
        }

        var itemIds = sales.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await ItemsQuery(ctx, includeArchived: true, ids: itemIds).ToDictionaryAsync(i => i.Id, ct);
        var cards = await db.TechCards.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status == TechCardStatus.Active).Select(c => c.ItemId).ToListAsync(ct);
        var lines = sales.Lines.GroupBy(l => l.ItemId).Select(g =>
        {
            var item = items[g.Key];
            var mine = orders.Where(o => o.ItemId == g.Key && o.Status != ProductionOrderStatus.Cancelled).ToList();
            var hasCard = cards.Contains(item.Id) || (item.ParentItemId is { } p && cards.Contains(p));
            // Коды подсказок — текст выводит интерфейс на языке пользователя.
            var hint = !TechCardService.ProductTypes.Contains(item.Type) ? SalesProductionHints.Purchased
                : item.IsArchived ? SalesProductionHints.Archived
                : !hasCard ? SalesProductionHints.NoTechCard
                : null;
            return new SalesProductionLineDto(item.Id, item.Code, item.Name, item.UnitSymbol, g.Sum(l => l.Quantity), mine.Sum(o => o.Quantity),
                mine.Sum(o => released.GetValueOrDefault(o.Id)), hint is null, hint);
        }).OrderBy(l => l.Code).ToList();
        var view = orders.Select(o => new SalesProductionOrderDto(o.Id, o.Number, items.TryGetValue(o.ItemId, out var i) ? i.Code : "", o.Quantity,
            released.GetValueOrDefault(o.Id), o.Status)).ToList();
        string? createHint = sales.Status != KnitErp.Domain.Sales.SalesOrderStatus.Confirmed ? SalesProductionHints.NotConfirmed
            : lines.All(l => !l.CanPlan || l.Remaining == 0) ? SalesProductionHints.AllPlanned
            : null;
        return new SalesProductionDto(salesOrderId, lines, view, ctx.Permissions.Has(Permissions.ProductionOrderEdit) && createHint is null, createHint);
    }

    /// <summary>
    /// Черновики производственных заказов на незапланированный остаток каждой производимой позиции подтверждённого заказа покупателя
    /// (D90, дополнение): источник — этот заказ, срок — дата отгрузки, склад готовой продукции — склад заказа покупателя, склад цеха —
    /// «Цех (НЗП)». Под блокировкой заказа покупателя: двойное нажатие не создаст заказы дважды. Позиции без техкарты пропускаются с причиной.
    /// </summary>
    public async Task<SalesProductionResultDto> CreateForSalesOrderAsync(long salesOrderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var sales = await db.SalesOrders.AsNoTracking()
                            .SingleOrDefaultAsync(o => o.Id == salesOrderId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            await db.LockAsync($"kniterp.production.sales.{salesOrderId}", ct);
            var plan = await ForSalesOrderAsync(salesOrderId, ct);
            if (plan.CreateHint == SalesProductionHints.NotConfirmed)
            {
                throw new BusinessRuleException("production.sales.not_confirmed", "Производство создаётся по подтверждённому заказу покупателя.");
            }

            if (plan.CreateHint == SalesProductionHints.AllPlanned)
            {
                throw new BusinessRuleException("production.sales.all_planned", "Всё, что можно произвести, уже запланировано.");
            }

            var wip = await db.Warehouses.AsNoTracking()
                          .Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived && w.Name == DefaultWipWarehouseName)
                          .Select(w => (long?)w.Id).FirstOrDefaultAsync(ct)
                      ?? throw new BusinessRuleException("production.sales.no_wip",
                          $"Нет склада «{DefaultWipWarehouseName}» — заведите его или создайте производственный заказ вручную.");
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(org.TimeZoneId)));
            var due = sales.ShipDate is { } ship && ship >= today ? ship : (DateOnly?)null;
            var created = new List<long>();
            var skipped = plan.Lines.Where(l => !l.CanPlan).Select(l => l.Code).ToList();
            foreach (var line in plan.Lines.Where(l => l.CanPlan && l.Remaining > 0))
            {
                created.Add(await CreateCoreAsync(new ProductionOrderCommand(line.ItemId, line.Remaining, today, due, ProductionSource.SalesOrder, salesOrderId,
                    wip, sales.WarehouseId, $"По заказу покупателя {sales.Number}"), ct));
            }

            await tx.CommitAsync(ct);
            var numbers = await db.ProductionOrders.AsNoTracking().Where(o => created.Contains(o.Id)).OrderBy(o => o.Id).Select(o => o.Number).ToListAsync(ct);
            return new SalesProductionResultDto(created, numbers, skipped);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Заказ на полуфабрикат для этого заказа (D90, дополнение «сборка из полуфабрикатов»): черновик на ещё не обеспеченную потребность —
    /// потребность с отходом по нормам заказа минус выданное в цех и минус уже запланированное в заказах на этот полуфабрикат.
    /// Склады и срок — как у заказа на изделие; полуфабрикат приходит на склад готовой продукции и выдаётся в цех обычной выдачей.
    /// </summary>
    public async Task<long> CreateChildAsync(long parentId, long materialItemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var exists = await db.ProductionOrders.AsNoTracking().AnyAsync(o => o.Id == parentId && o.OrganizationId == ctx.OrganizationId, ct);
        if (!exists)
        {
            throw new NotFoundException("Производственный заказ");
        }

        await db.LockAsync($"kniterp.production.order.{parentId}", ct);
        var dto = await GetAsync(parentId, ct);
        if (!dto.CanCreateChild)
        {
            throw new BusinessRuleException("production.order.parent_status", $"Заказ {dto.Number} уже не в работе — полуфабрикат для него не нужен.");
        }

        var material = dto.Materials.SingleOrDefault(m => m.ItemId == materialItemId)
                       ?? throw new BusinessRuleException("production.order.parent_item", "Этого материала нет в нормах заказа.");
        if (!material.CanProduce)
        {
            throw new BusinessRuleException("production.order.no_techcard", $"У «{material.Name}» нет действующей техкарты — его не производят, а закупают.");
        }

        var parent = await db.ProductionOrders.AsNoTracking().SingleAsync(o => o.Id == parentId, ct);
        var issued = (await ProductionPosting.MovementsAsync(db, parent, ct))
            .Where(m => m.ItemId == materialItemId)
            .Sum(m => m.Purpose switch { ProductionDocumentPurpose.Issue => m.Quantity, ProductionDocumentPurpose.Return => -m.Quantity, _ => 0m });
        var precision = await db.Items.AsNoTracking().Where(i => i.Id == materialItemId)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => (int)u.Precision).SingleAsync(ct);
        var factor = (decimal)Math.Pow(10, precision);
        var need = Math.Ceiling((material.Gross - issued - material.ChildPlanned) * factor) / factor;
        if (need <= 0)
        {
            throw new BusinessRuleException("production.order.child_covered", $"Потребность в «{material.Name}» уже выдана или запланирована.");
        }

        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(org.TimeZoneId)));
        var id = await CreateCoreAsync(new ProductionOrderCommand(materialItemId, need, today, parent.DueDate is { } d && d >= today ? d : null,
            ProductionSource.Plan, null, parent.WipWarehouseId, parent.OutputWarehouseId, $"Полуфабрикат для заказа {parent.Number}", parentId), ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Правка черновика: количество, сроки, источник, склады, комментарий. Изделие не меняется.</summary>
    public async Task UpdateAsync(long id, ProductionOrderCommand command, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        if (command.ItemId != order.ItemId)
        {
            throw new BusinessRuleException("production.order.item_fixed", "Изделие заказа не меняется — создайте новый заказ.");
        }

        var (product, card) = await ValidateAsync(ctx, command, ct);
        var before = Describe(order, product.UnitSymbol);
        order.UpdateDraft(command.Quantity, card.Id, Header(command));
        var after = Describe(order, product.UnitSymbol);
        if (before != after)
        {
            Audit(ctx, AuditActions.ProductionOrderChanged, order, before, after, null);
        }

        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Запуск: повторная проверка изделия, складов и источника, фиксация действующей версии техкарты и снимок норм
    /// с плановыми ценами материалов — в одной транзакции.
    /// </summary>
    public async Task ReleaseAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        try
        {
            await ReleaseCoreAsync(id, rowVersion, ct);
        }
        catch
        {
            db.DiscardChanges(); // отказ после order.Release не должен оставить «запущенный» заказ в контексте вкладки
            throw;
        }
    }

    private async Task ReleaseCoreAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var (product, card) = await ValidateAsync(ctx, Command(order), ct);
        var materialIds = card.Lines.Select(l => l.ItemId).ToList();
        var materials = await ItemsQuery(ctx, includeArchived: true, ids: materialIds).ToDictionaryAsync(m => m.Id, ct);
        var archived = materials.Values.Where(m => m.IsArchived).Select(m => m.Code).ToList();
        if (archived.Count > 0)
        {
            throw new BusinessRuleException("production.order.material_archived",
                $"В техкарте есть материалы из архива: {string.Join(", ", archived.Take(10))}. Создайте новую версию техкарты.");
        }

        var prices = await MaterialPricesAsync(ctx, materialIds, ct);
        order.Release(card, itemId => materials[itemId].Precision, itemId => prices.TryGetValue(itemId, out var p) ? p : null, ctx.UserId, clock.UtcNow);
        var cost = order.PlannedCost();
        Audit(ctx, AuditActions.ProductionOrderReleased, order, "Черновик", "Запущен",
            $"техкарта {product.Code}, версия {card.Version}; материалов: {order.Materials.Count}"
            + (cost is { } c ? $"; плановая себестоимость {Quantities.Format(c)}" : "; плановая себестоимость неизвестна"));
        if (order.SalesOrderId is { } salesOrder)
        {
            // D110 базы: производство по заказу покупателя начато — этап заказа сдвигается по правилам организации (только вперёд).
            // Следствие производственного действия: право на изменение продаж у начальника цеха не требуется.
            await KnitErp.Application.Sales.StageAutomation.ApplyAsync(db, ctx, [salesOrder], clock.UtcNow, currentUser.CorrelationId, ct,
                KnitErp.Domain.Sales.StageTrigger.ProductionStarted);
        }

        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Отмена с причиной: черновик или запущенный заказ без движений.</summary>
    public async Task CancelAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        // Под блокировкой заказа: отметка этапа, начатая одновременно, не проскочит между проверкой и отменой.
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.production.order.{id}", ct);
        var before = ProductionOrder.StatusName(order.Status);
        order.Cancel(await HasMovementsAsync(order, ct), reason, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.ProductionOrderCancelled, order, before, "Отменён", order.CancelReason);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Закрытие (D95): после выпуска, когда в цехе не осталось материалов заказа и всё упакованное выпущено;
    /// не полностью выполненный заказ — с причиной. Проверки — под блокировкой заказа.
    /// </summary>
    public async Task CloseAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, order) = await LoadForEditAsync(id, rowVersion, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.production.order.{id}", ct);
        var released = await ProductionLocks.ReleasedAsync(db, order, ct);
        var steps = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && o.ProductionOrderId == id).ToListAsync(ct);
        var flow = new ProductionFlow(order.Quantity, steps.Select(o => o.Step), released, order.RouteValue);
        var balance = MaterialBalance.Calculate(order.Materials, order.Quantity, await ProductionPosting.MovementsAsync(db, order, ct), released, flow.Defect);
        var before = ProductionOrder.StatusName(order.Status);
        order.Close(released, balance.Any(b => b.Remaining != 0), flow.ReadyForOutput, reason, ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.ProductionOrderClosed, order, before, "Закрыт",
            $"выпущено {Quantities.Format(released)} из {Quantities.Format(order.Quantity)}" + (order.CloseReason is null ? "" : $"; {order.CloseReason}"));
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Фактическая себестоимость (D95) по выданной пряже: (выдано − возвращено) × цена материала. Цена — текущая средняя закупочная
    /// без НДС (как плановая, D69), если её нет — плановая из снимка; неизвестна хотя бы одна — null.
    /// </summary>
    private async Task<decimal?> ActualCostAsync(AccessContext ctx, ProductionOrder order, CancellationToken ct)
    {
        var moves = await ProductionPosting.MovementsAsync(db, order, ct);
        var net = order.Materials.ToDictionary(m => m.ItemId, m =>
            moves.Where(x => x.ItemId == m.ItemId && x.Purpose == ProductionDocumentPurpose.Issue).Sum(x => x.Quantity)
            - moves.Where(x => x.ItemId == m.ItemId && x.Purpose == ProductionDocumentPurpose.Return).Sum(x => x.Quantity));
        if (net.Values.All(v => v == 0))
        {
            return null;
        }

        var prices = await MaterialPricesAsync(ctx, net.Keys.ToList(), ct);
        decimal total = 0;
        foreach (var m in order.Materials)
        {
            var price = prices.TryGetValue(m.ItemId, out var p) ? p : m.PlannedPrice;
            if (price is null)
            {
                return null;
            }

            total += net[m.ItemId] * price.Value;
        }

        return Money.Round(total);
    }

    /// <summary>Есть ли по заказу движения, после которых отмена запрещена: отметки этапов (D92) и складские документы (D93, D95).</summary>
    private async Task<bool> HasMovementsAsync(ProductionOrder order, CancellationToken ct) =>
        await db.ProductionOperations.AnyAsync(o => o.OrganizationId == order.OrganizationId && o.ProductionOrderId == order.Id, ct)
        || await db.ProductionOrderDocuments.AnyAsync(d => d.OrganizationId == order.OrganizationId && d.ProductionOrderId == order.Id, ct);

    /// <summary>Изделие, склады, заказ покупателя и техкарта — своей организации и действующие. Возвращает изделие и техкарту для запуска.</summary>
    private async Task<(ItemRow Product, TechCard Card)> ValidateAsync(AccessContext ctx, ProductionOrderCommand c, CancellationToken ct)
    {
        var product = await ItemsQuery(ctx, includeArchived: true, id: c.ItemId).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Изделие");
        if (product.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Изделие «{product.Name}» в архиве.");
        }

        if (!TechCardService.ProductTypes.Contains(product.Type))
        {
            throw new BusinessRuleException("production.order.product_type", "Производственный заказ оформляется на готовую продукцию или полуфабрикат.");
        }

        StockEntry.EnsurePrecision(new EntryItemDto(product.Id, product.Code, product.Name, product.UnitSymbol, product.Precision), c.Quantity);
        foreach (var warehouseId in new[] { c.WipWarehouseId, c.OutputWarehouseId })
        {
            var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == warehouseId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Склад");
            if (w.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Склад «{w.Name}» в архиве.");
            }
        }

        if (c.Source == ProductionSource.SalesOrder && c.SalesOrderId is { } salesId)
        {
            var order = await db.SalesOrders.AsNoTracking().Include(o => o.Lines)
                            .SingleOrDefaultAsync(o => o.Id == salesId && o.OrganizationId == ctx.OrganizationId, ct)
                        ?? throw new NotFoundException("Заказ покупателя");
            if (order.Status != SalesOrderStatus.Confirmed)
            {
                throw new BusinessRuleException("production.order.sales_order_not_confirmed", $"Заказ покупателя {order.Number} не подтверждён или закрыт.");
            }

            if (order.Lines.All(l => l.ItemId != product.Id))
            {
                throw new BusinessRuleException("production.order.sales_order_item", $"В заказе покупателя {order.Number} нет позиции «{product.Name}».");
            }
        }

        if (c.ParentOrderId is { } parentId)
        {
            var parent = await db.ProductionOrders.AsNoTracking()
                             .SingleOrDefaultAsync(o => o.Id == parentId && o.OrganizationId == ctx.OrganizationId, ct)
                         ?? throw new NotFoundException("Производственный заказ");
            if (parent.Status is ProductionOrderStatus.Cancelled or ProductionOrderStatus.Closed or ProductionOrderStatus.Done)
            {
                throw new BusinessRuleException("production.order.parent_status", $"Заказ {parent.Number} уже не в работе — полуфабрикат для него не нужен.");
            }

            var inCard = await db.TechCards.AsNoTracking().Where(t => t.Id == parent.TechCardId).SelectMany(t => t.Lines).AnyAsync(l => l.ItemId == product.Id, ct);
            if (!inCard)
            {
                throw new BusinessRuleException("production.order.parent_item", $"«{product.Name}» не входит в техкарту заказа {parent.Number}.");
            }
        }

        var card = await ActiveCardAsync(ctx, product.Id, product.ParentItemId, ct)
                   ?? throw new BusinessRuleException("production.order.no_techcard",
                       $"У изделия «{product.Name}» нет действующей техкарты" + (product.ParentItemId is null ? "." : " — ни у модификации, ни у основной модели."));
        return (product, card);
    }

    /// <summary>
    /// Действующая техкарта изделия. У модификации без своей карты — карта основной модели (допущение D90):
    /// модификации обычно отличаются цветом или размером, а не составом.
    /// </summary>
    private async Task<TechCard?> ActiveCardAsync(AccessContext ctx, long itemId, long? parentItemId, CancellationToken ct)
    {
        var ids = parentItemId is { } parent ? new[] { itemId, parent } : [itemId];
        var cards = await db.TechCards.AsNoTracking().Include(c => c.Lines)
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Status == TechCardStatus.Active && ids.Contains(c.ItemId))
            .ToListAsync(ct);
        return cards.FirstOrDefault(c => c.ItemId == itemId) ?? cards.FirstOrDefault();
    }

    private async Task<IReadOnlyList<(long ItemId, decimal Net, decimal Gross, decimal Waste, decimal? Price)>> PreviewAsync(
        AccessContext ctx, TechCard card, decimal quantity, CancellationToken ct)
    {
        var ids = card.Lines.Select(l => l.ItemId).ToList();
        var materials = await ItemsQuery(ctx, includeArchived: true, ids: ids).ToDictionaryAsync(m => m.Id, ct);
        var prices = await MaterialPricesAsync(ctx, ids, ct);
        var waste = card.Lines.ToDictionary(l => l.ItemId, l => l.WastePercent);
        return card.Requirement(quantity, itemId => materials[itemId].Precision)
            .Select(r => (r.ItemId, r.Net, r.Gross, waste[r.ItemId],
                prices.TryGetValue(r.ItemId, out var p) ? decimal.Round(p, 4, MidpointRounding.AwayFromZero) : (decimal?)null))
            .ToList();
    }

    private Task<IReadOnlyDictionary<long, decimal>> MaterialPricesAsync(AccessContext ctx, IReadOnlyList<long> itemIds, CancellationToken ct) =>
        ProductionPrices.CurrentAsync(db, ctx.OrganizationId, itemIds, ct);

    private sealed record ItemRow(long Id, string Code, string Name, string UnitSymbol, byte Precision, ItemType Type, long? ParentItemId, bool IsArchived);

    private IQueryable<ItemRow> ItemsQuery(
        AccessContext ctx, bool includeArchived = false, long? id = null, IReadOnlyCollection<long>? ids = null, bool productsOnly = false)
    {
        var q = db.Items.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId);
        if (!includeArchived)
        {
            q = q.Where(i => !i.IsArchived);
        }

        if (id is { } one)
        {
            q = q.Where(i => i.Id == one);
        }

        if (ids is not null)
        {
            q = q.Where(i => ids.Contains(i.Id));
        }

        if (productsOnly)
        {
            q = q.Where(i => i.Type == ItemType.Finished || i.Type == ItemType.SemiFinished);
        }

        return q.OrderBy(i => i.Code).Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id,
            (i, u) => new ItemRow(i.Id, i.Code, i.Name, u.Symbol, u.Precision, i.Type, i.ParentItemId, i.IsArchived));
    }

    private async Task<(AccessContext, ProductionOrder)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderEdit, ct);
        var order = await db.ProductionOrders.Include(o => o.Materials)
                        .SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Производственный заказ");
        return (ctx, order.EnsureVersion(order.RowVersion, rowVersion));
    }

    private static ProductionOrderHeader Header(ProductionOrderCommand c) =>
        new(c.OrderDate, c.DueDate, c.Source, c.Source == ProductionSource.SalesOrder ? c.SalesOrderId : null, c.WipWarehouseId, c.OutputWarehouseId, c.Comment);

    private static ProductionOrderCommand Command(ProductionOrder o) =>
        new(o.ItemId, o.Quantity, o.OrderDate, o.DueDate, o.Source, o.SalesOrderId, o.WipWarehouseId, o.OutputWarehouseId, o.Comment, o.ParentOrderId);

    private static string Describe(ProductionOrder o, string unit) =>
        $"{Quantities.Format(o.Quantity)} {unit}, {o.OrderDate:dd.MM.yyyy}" + (o.DueDate is { } d ? $", срок {d:dd.MM.yyyy}" : "")
        + $", {ProductionOrder.SourceName(o.Source)}" + (o.SalesOrderId is { } s ? $" №{s}" : "")
        + $", цех №{o.WipWarehouseId}, ГП №{o.OutputWarehouseId}" + (o.Comment is null ? "" : $", «{o.Comment}»");

    private void Audit(AccessContext ctx, string action, ProductionOrder order, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(ProductionOrder), order.Id.ToString(),
            before, after, reason is null ? order.Number : $"{order.Number}: {reason}", currentUser.CorrelationId));
}

/// <summary>
/// Производство на карточке заказа покупателя (D90, дополнение): каждый вызов — со своим контекстом базы, как теги и обсуждения,
/// чтобы не мешать операциям самой карточки (у вкладки один контекст). Права и правила — в <see cref="ProductionOrderService"/>.
/// </summary>
public sealed class SalesProductionService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    public async Task<SalesProductionDto> ForSalesOrderAsync(long salesOrderId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await Service(db).ForSalesOrderAsync(salesOrderId, ct);
    }

    public async Task<SalesProductionResultDto> CreateAsync(long salesOrderId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await Service(db).CreateForSalesOrderAsync(salesOrderId, ct);
    }

    private ProductionOrderService Service(IKnitErpDbContext db) => new(db, new AccessGuard(db, currentUser, clock), currentUser, clock);
}
