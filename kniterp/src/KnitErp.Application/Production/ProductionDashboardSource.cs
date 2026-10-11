using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Production;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>
/// Производство на главном экране (контракт D111 базовой ветки, реализация — D405). Заказы в работе (запущен, выполнен частично) и с опозданием
/// (срок прошёл); готовность — упаковано / количество заказа; текущая операция — первый этап, где сделано меньше количества заказа;
/// выпуск сегодня — проведённые документы выпуска с сегодняшней датой; «требует внимания» — опоздания, брак за 7 дней и вчерашние
/// сменные задания, не выполненные без причины. Без права просмотра производственных заказов — null. Запросы идут по очереди
/// в контексте главного экрана (без параллельных вызовов).
/// </summary>
public sealed class ProductionDashboardSource : IProductionDashboardSource
{
    /// <summary>Сколько заказов показать в списке готовности.</summary>
    public const int MaxOrders = 6;

    /// <summary>Сколько открытых заказов разбирать для готовности (дальше — только счёт).</summary>
    public const int MaxScanned = 200;

    public const int DefectDays = 7;

    public const string LateText = "Производственные заказы с опозданием";
    public const string DefectText = "Брак за 7 дней";
    public const string MissedTasksText = "Сменные задания вчера не выполнены без причины";

    public async Task<ProductionDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.ProductionOrderView))
        {
            return null;
        }

        var org = ctx.OrganizationId;
        var open = db.ProductionOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && (o.Status == ProductionOrderStatus.Released || o.Status == ProductionOrderStatus.PartiallyDone));
        var inWork = await open.CountAsync(ct);
        var late = await open.CountAsync(o => o.DueDate != null && o.DueDate < today, ct);

        var orders = await (from o in open
                            join i in db.Items.AsNoTracking() on o.ItemId equals i.Id
                            orderby o.DueDate == null, o.DueDate, o.Id
                            select new { o.Id, o.Number, o.Quantity, o.DueDate, i.Name }).Take(MaxScanned).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var done = (await db.ProductionOperations.AsNoTracking()
                .Where(op => op.OrganizationId == org && ids.Contains(op.ProductionOrderId))
                .ToListAsync(ct))
            .ToLookup(op => op.ProductionOrderId);
        var progress = orders.Select(o =>
            {
                // Выпуск не нужен: готовность считаем по упакованному, а упакованное и выпущенное не убывают при выпуске.
                var flow = new ProductionFlow(o.Quantity, done[o.Id].Select(x => x.Step), 0m);
                var packed = flow.Done(ProductionStage.Packing);
                var current = ProductionFlow.Stages.FirstOrDefault(s => flow.Done(s) < o.Quantity, ProductionStage.Packing);
                var percent = o.Quantity <= 0 ? 0m : Math.Min(100m, decimal.Round(packed / o.Quantity * 100m, 1));
                var isLate = o.DueDate is { } due && due < today;
                return new ProductionOrderProgressDto(o.Id, o.Number, o.Name, percent, o.DueDate, isLate, $"production-orders/{o.Id}",
                    ProductionFlow.StageName(current));
            })
            .OrderByDescending(p => p.Late).ThenBy(p => p.DueDate == null).ThenBy(p => p.DueDate).ThenBy(p => p.Percent)
            .Take(MaxOrders).ToList();

        var output = await (from link in db.ProductionOrderDocuments.AsNoTracking()
                            where link.OrganizationId == org && link.Purpose == ProductionDocumentPurpose.Output
                            join d in db.StockDocuments.AsNoTracking() on link.StockDocumentId equals d.Id
                            where d.Status == StockDocumentStatus.Posted && d.DocumentDate == today
                            join o in db.ProductionOrders.AsNoTracking() on link.ProductionOrderId equals o.Id
                            from l in d.Lines
                            where l.ItemId == o.ItemId
                            join i in db.Items.AsNoTracking() on l.ItemId equals i.Id
                            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                            group l.Quantity by u.Symbol into g
                            select new { Unit = g.Key, Sum = g.Sum() }).ToListAsync(ct);
        // Разные единицы не складываются: показываем ту, в которой выпущено больше (для трикотажа обычно «шт»).
        var main = output.OrderByDescending(x => x.Sum).FirstOrDefault();

        var alerts = new List<ProductionAlertDto>();
        if (late > 0)
        {
            alerts.Add(new ProductionAlertDto(LateText, "production-orders", true, late));
        }

        var since = today.AddDays(-(DefectDays - 1));
        var defect = await db.ProductionOperations.AsNoTracking()
            .Where(op => op.OrganizationId == org && op.Kind == ProductionOperationKind.Defect && op.OccurredOn >= since && op.OccurredOn <= today)
            .SumAsync(op => (decimal?)op.Quantity, ct) ?? 0m;
        if (defect > 0)
        {
            alerts.Add(new ProductionAlertDto(DefectText, "reports/production", false, (int)Math.Ceiling(defect)));
        }

        var missed = (await ShiftTaskService.ListAsync(db, org, today.AddDays(-1), null, ct))
            .Count(t => !t.IsCancelled && !t.Done && t.ShortfallReason is null);
        if (missed > 0)
        {
            alerts.Add(new ProductionAlertDto(MissedTasksText, "production/shift-tasks", false, missed));
        }

        return new ProductionDashboardData(inWork, late, progress, main?.Sum, main?.Unit, alerts);
    }
}
