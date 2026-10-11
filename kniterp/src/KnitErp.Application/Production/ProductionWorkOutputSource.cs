using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>
/// Выработка для модуля «Сотрудники» (ТЗ «Сотрудники», §6; допущение D92, дополнение): отметки «Сделано» и исправления
/// с исполнителем, сгруппированные по сотруднику, дню, этапу и изделию. Операция — этап производства
/// (идентификатор — номер этапа 1–5, он не меняется); брак и переделка выработкой не считаются. Права проверяет вызывающий модуль.
/// </summary>
public sealed class ProductionWorkOutputSource(IKnitErpDbContext db) : IWorkOutputSource
{
    public async Task<IReadOnlyList<WorkOutputDto>> OutputAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await (from o in db.ProductionOperations.AsNoTracking()
                          where o.OrganizationId == organizationId && o.EmployeeId != null && o.OccurredOn >= @from && o.OccurredOn <= to
                                && (o.Kind == ProductionOperationKind.Done || o.Kind == ProductionOperationKind.Correction)
                          join p in db.ProductionOrders.AsNoTracking() on o.ProductionOrderId equals p.Id
                          join i in db.Items.AsNoTracking() on p.ItemId equals i.Id
                          join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                          group o.Quantity by new { EmployeeId = o.EmployeeId!.Value, o.OccurredOn, o.Stage, p.ItemId, u.Symbol } into g
                          select new { g.Key, Quantity = g.Sum() }).ToListAsync(ct);
        return rows.Where(r => r.Quantity != 0)
            .OrderBy(r => r.Key.EmployeeId).ThenBy(r => r.Key.OccurredOn).ThenBy(r => r.Key.Stage)
            .Select(r => new WorkOutputDto(r.Key.EmployeeId, r.Key.OccurredOn, (long)r.Key.Stage, ProductionFlow.StageName(r.Key.Stage),
                r.Key.ItemId, r.Quantity, r.Key.Symbol))
            .ToList();
    }
}
