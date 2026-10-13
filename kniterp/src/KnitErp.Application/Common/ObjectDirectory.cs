using KnitErp.Application.Access;
using KnitErp.Application.Tagging;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Common;

/// <summary>
/// Справочник объектов для тегов (D103) и обсуждений (D107): название, подпись и ссылка на карточку — только для объектов своей
/// организации, которые пользователь вправе видеть (права на вид объекта и область складов). Пустой ответ — «не найдено».
/// </summary>
public static class ObjectDirectory
{
    private static readonly string[] StockViews = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];
    private static readonly string[] OpeningViews = [Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView];

    /// <summary>Объект виден пользователю.</summary>
    public static async Task<TaggedObjectDto?> FindAsync(IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, long id, CancellationToken ct) =>
        (await DescribeAsync(db, ctx, entity, [id], ct)).FirstOrDefault();

    public static async Task<IReadOnlyList<TaggedObjectDto>> DescribeAsync(
        IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var has = ctx.Permissions.Has;
        switch (entity)
        {
            case TaggedEntity.Item when has(Permissions.CatalogView):
                return (await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id))
                        .Select(i => new { i.Id, i.Code, i.Name }).ToListAsync(ct))
                    .Select(i => new TaggedObjectDto(entity, i.Id, $"{i.Code} — {i.Name}", null, $"catalog/items/{i.Id}")).ToList();
            case TaggedEntity.Counterparty when has(Permissions.CatalogView):
                return (await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && ids.Contains(c.Id))
                        .Select(c => new { c.Id, c.Name, c.Inn }).ToListAsync(ct))
                    .Select(c => new TaggedObjectDto(entity, c.Id, c.Name, c.Inn is null ? null : "ИНН " + c.Inn,
                        $"counterparties?search={Uri.EscapeDataString(c.Name)}")).ToList();
            case TaggedEntity.SalesOrder when has(Permissions.SalesView):
                return (await (from o in db.SalesOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.CustomerId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"sales-orders/{o.Id}")).ToList();
            case TaggedEntity.PurchaseOrder when has(Permissions.PurchaseView):
                return (await (from o in db.PurchaseOrders.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on o.SupplierId equals c.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, c.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", o.Name, $"purchase-orders/{o.Id}")).ToList();
            case TaggedEntity.StockDocument when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.StockDocuments.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id)
                                    && (visible == null || visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value))))
                        .Select(d => new { d.Id, d.Number, d.Kind, d.DocumentDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"{StockDocument.KindName(d.Kind)} {d.Number}", d.DocumentDate.ToString("dd.MM.yyyy"),
                        $"stock-documents/{d.Id}")).ToList();
            }

            case TaggedEntity.InventoryCount when StockViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, StockViews);
                return (await db.InventoryCounts.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id) && (visible == null || visible.Contains(d.WarehouseId)))
                        .Select(d => new { d.Id, d.Number, d.CountDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"Инвентаризация {d.Number}", d.CountDate.ToString("dd.MM.yyyy"), $"inventory/{d.Id}"))
                    .ToList();
            }

            case TaggedEntity.StockBatch when StockViews.Any(has):
                return (await (from b in db.StockBatches.AsNoTracking()
                               join i in db.Items.AsNoTracking() on b.ItemId equals i.Id
                               where b.OrganizationId == org && ids.Contains(b.Id)
                               select new { b.Id, b.Number, b.Color, b.DyeLot, i.Code }).ToListAsync(ct))
                    .Select(b => new TaggedObjectDto(entity, b.Id, StockBatch.Describe(b.Number, b.Color, b.DyeLot), b.Code,
                        $"stock-batches?search={Uri.EscapeDataString(b.Number)}")).ToList();
            case TaggedEntity.OpeningBalance when OpeningViews.Any(has):
            {
                var visible = WarehouseScope.Visible(ctx, OpeningViews);
                return (await db.OpeningBalances.AsNoTracking()
                        .Where(d => d.OrganizationId == org && ids.Contains(d.Id) && (visible == null || visible.Contains(d.WarehouseId)))
                        .Select(d => new { d.Id, d.Number, d.AsOfDate }).ToListAsync(ct))
                    .Select(d => new TaggedObjectDto(entity, d.Id, $"Начальные остатки {d.Number}", d.AsOfDate.ToString("dd.MM.yyyy"), $"opening-balances/{d.Id}"))
                    .ToList();
            }

            case TaggedEntity.CustomerInvoice when has(Permissions.SalesView) && has(Permissions.PriceView):
                return (await (from i in db.CustomerInvoices.AsNoTracking()
                               join c in db.Counterparties.AsNoTracking() on i.CustomerId equals c.Id
                               where i.OrganizationId == org && ids.Contains(i.Id)
                               select new { i.Id, i.Number, i.InvoiceDate, c.Name }).ToListAsync(ct))
                    .Select(i => new TaggedObjectDto(entity, i.Id, $"Счёт {i.Number} от {i.InvoiceDate:dd.MM.yyyy}", i.Name, $"customer-invoices/{i.Id}")).ToList();
            case TaggedEntity.ExpenseReport when has(Permissions.PriceView):
                return (await db.ExpenseReports.AsNoTracking().Where(r => r.OrganizationId == org && ids.Contains(r.Id))
                        .Select(r => new { r.Id, r.Number, r.ReportDate }).ToListAsync(ct))
                    .Select(r => new TaggedObjectDto(entity, r.Id, $"Авансовый отчёт {r.Number}", r.ReportDate.ToString("dd.MM.yyyy"), $"expense-reports/{r.Id}"))
                    .ToList();
            case TaggedEntity.ProductionOrder when has(Permissions.ProductionOrderView):
                // D99: заказ, изделие и количество — без цен.
                return (await (from o in db.ProductionOrders.AsNoTracking()
                               join i in db.Items.AsNoTracking() on o.ItemId equals i.Id
                               where o.OrganizationId == org && ids.Contains(o.Id)
                               select new { o.Id, o.Number, o.OrderDate, i.Code, i.Name }).ToListAsync(ct))
                    .Select(o => new TaggedObjectDto(entity, o.Id, $"{o.Number} от {o.OrderDate:dd.MM.yyyy}", $"{o.Code} — {o.Name}", $"production-orders/{o.Id}"))
                    .ToList();
            case TaggedEntity.Employee when has(Permissions.EmployeeView) || ctx.Permissions.HasOwnOnly(Permissions.EmployeeView):
            {
                // D202: в обсуждениях и тегах — только ФИО и должность; паспорт, СНИЛС, ИНН, адрес и оклад сюда не попадают.
                var ownOnly = !has(Permissions.EmployeeView);
                var visible = ownOnly ? null : await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
                return (await (from e in db.Employees.AsNoTracking()
                               join p in db.Positions.AsNoTracking() on e.PositionId equals p.Id
                               where e.OrganizationId == org && ids.Contains(e.Id)
                                     && (!ownOnly || e.UserId == ctx.UserId)
                                     && (visible == null || visible.Contains(e.DepartmentId))
                               select new { e.Id, e.LastName, e.FirstName, e.MiddleName, Position = p.Name }).ToListAsync(ct))
                    .Select(e => new TaggedObjectDto(entity, e.Id, e.MiddleName is null ? $"{e.LastName} {e.FirstName}" : $"{e.LastName} {e.FirstName} {e.MiddleName}",
                        e.Position, $"employees/{e.Id}")).ToList();
            }

            case TaggedEntity.DevModel when has(Permissions.DevView):
                // D700: номер, название и стадия модели — без себестоимости и цены.
                return (await db.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && ids.Contains(m.Id))
                        .Select(m => new { m.Id, m.Number, m.Name, m.Stage }).ToListAsync(ct))
                    .Select(m => new TaggedObjectDto(entity, m.Id, $"{m.Number} — {m.Name}", KnitErp.Domain.Development.DevStages.Name(m.Stage),
                        $"dev/models/{m.Id}")).ToList();
            case TaggedEntity.DevPattern when has(Permissions.DevView):
                // D726: номер, название и статус рисунка.
                return (await db.DevPatterns.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.Id))
                        .Select(p => new { p.Id, p.Number, p.Name, p.Status }).ToListAsync(ct))
                    .Select(p => new TaggedObjectDto(entity, p.Id, $"{p.Number} — {p.Name}", KnitErp.Domain.Development.DevPattern.StatusName(p.Status),
                        $"dev/patterns/{p.Id}")).ToList();
            case TaggedEntity.CrmLead or TaggedEntity.CrmDeal or TaggedEntity.CrmContact:
                // D601 (CRM): видимость лида, сделки и контакта решает CRM — ответственный, наблюдатель или право crm.all.
                return await Crm.CrmVisibility.DescribeAsync(db, ctx, entity, ids, ct);

            default:
                return Modules.TryGetValue(entity, out var describe) ? await describe(db, ctx, ids, ct) : [];
        }
    }

    /// <summary>
    /// D130: права, которые позволяют менять объект (ставить и снимать на нём теги). Объект должен быть ещё и виден
    /// (<see cref="DescribeAsync"/>). Пустой список — менять нельзя никому, кроме того, у кого есть право управления тегами
    /// (объекты модулей, подключённые без прав изменения).
    /// </summary>
    public static IReadOnlyList<string> EditPermissions(TaggedEntity entity) => entity switch
    {
        TaggedEntity.Item or TaggedEntity.Counterparty => [Permissions.CatalogEdit],
        TaggedEntity.SalesOrder or TaggedEntity.CustomerInvoice => [Permissions.SalesEdit],
        TaggedEntity.PurchaseOrder => [Permissions.PurchaseEdit],
        TaggedEntity.StockDocument or TaggedEntity.InventoryCount or TaggedEntity.StockBatch => StockEdits,
        TaggedEntity.OpeningBalance => OpeningEdits,
        TaggedEntity.ExpenseReport => [Permissions.SalesEdit, Permissions.PurchaseEdit],
        TaggedEntity.ProductionOrder => [Permissions.ProductionOrderEdit],
        TaggedEntity.Employee => [Permissions.EmployeeEdit],
        TaggedEntity.DevModel => [Permissions.DevEdit],
        TaggedEntity.CrmLead or TaggedEntity.CrmDeal or TaggedEntity.CrmContact => [Permissions.CrmEdit],
        _ => ModuleEdits.TryGetValue(entity, out var codes) ? codes : [],
    };

    /// <summary>
    /// D130: пользователь вправе менять объект — есть право изменения его вида и, для складских объектов и сотрудников, объект в области
    /// этого права (склады, подразделения). Видимость объекта проверяется отдельно.
    /// </summary>
    public static async Task<bool> CanChangeAsync(IKnitErpDbContext db, AccessContext ctx, TaggedEntity entity, long id, CancellationToken ct)
    {
        var codes = EditPermissions(entity);
        if (!codes.Any(ctx.Permissions.Has))
        {
            return false;
        }

        var org = ctx.OrganizationId;
        switch (entity)
        {
            case TaggedEntity.StockDocument:
            {
                var visible = WarehouseScope.Visible(ctx, StockEdits);
                return visible is null || await db.StockDocuments.AsNoTracking().AnyAsync(d => d.OrganizationId == org && d.Id == id
                    && (visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value))), ct);
            }

            case TaggedEntity.InventoryCount:
            {
                var visible = WarehouseScope.Visible(ctx, StockEdits);
                return visible is null || await db.InventoryCounts.AsNoTracking().AnyAsync(d => d.OrganizationId == org && d.Id == id && visible.Contains(d.WarehouseId), ct);
            }

            case TaggedEntity.OpeningBalance:
            {
                var visible = WarehouseScope.Visible(ctx, OpeningEdits);
                return visible is null || await db.OpeningBalances.AsNoTracking().AnyAsync(d => d.OrganizationId == org && d.Id == id && visible.Contains(d.WarehouseId), ct);
            }

            case TaggedEntity.CustomerInvoice:
            case TaggedEntity.ExpenseReport:
                return ctx.Permissions.Has(Permissions.PriceView);
            case TaggedEntity.Employee:
            {
                var visible = await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
                return visible is null || await db.Employees.AsNoTracking().AnyAsync(e => e.OrganizationId == org && e.Id == id && visible.Contains(e.DepartmentId), ct);
            }

            default:
                return true;
        }
    }

    private static readonly string[] StockEdits = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost];
    private static readonly string[] OpeningEdits = [Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TaggedEntity, string[]> ModuleEdits = new();

    /// <summary>Описание объектов модуля: только объекты своей организации, которые пользователь вправе видеть; права проверяет модуль.</summary>
    public delegate Task<IReadOnlyList<TaggedObjectDto>> Describer(IKnitErpDbContext db, AccessContext ctx, IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>Первое значение <see cref="TaggedEntity"/> для модулей, подключаемых через <see cref="Register"/> (D117): CRM — 30–39, «Разработка» — 40–49.</summary>
    public const int ModuleRangeStart = 30;

    /// <summary>Наибольшее значение, которое пропускают CHECK тегов и обсуждений в базе (D117).</summary>
    public const int ModuleRangeEnd = 99;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TaggedEntity, Describer> Modules = new();

    /// <summary>
    /// D117: модуль (CRM, «Разработка» и следующие) подключает свои объекты к тегам и обсуждениям без правки этого класса —
    /// вызовом в регистрации своих сервисов. Значение — из диапазона модуля; встроенные объекты переопределить нельзя.
    /// </summary>
    /// <param name="editPermissions">D130: права изменения объекта — с любым из них можно ставить и снимать на нём теги.</param>
    public static void Register(TaggedEntity entity, Describer describer, params string[] editPermissions)
    {
        ArgumentNullException.ThrowIfNull(describer);
        if ((int)entity is < ModuleRangeStart or > ModuleRangeEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(entity), $"Объекты модулей — значения {ModuleRangeStart}–{ModuleRangeEnd}.");
        }

        if (!Modules.TryAdd(entity, describer) && Modules[entity] != describer)
        {
            throw new InvalidOperationException($"Объект {entity} уже подключён другим модулем.");
        }

        ModuleEdits[entity] = editPermissions;
    }
}
