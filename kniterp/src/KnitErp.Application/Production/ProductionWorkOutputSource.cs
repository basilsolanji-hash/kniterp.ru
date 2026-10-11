using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>
/// Выработка для модуля «Сотрудники» (ТЗ «Сотрудники», §6; допущение D92, дополнение): отметки «Сделано» и исправления
/// с исполнителем, сгруппированные по сотруднику, дню, этапу и изделию. Операция — этап производства
/// (идентификатор — номер этапа 1–5, он не меняется); брак и переделка выработкой не считаются. Права проверяет вызывающий модуль.
/// Исправление брака не оплачивается (D224): после возврата изделий на переделку первые повторные отметки этапов возврата в том же
/// количестве в выработку не входят — к оплате идёт только годное (D407).
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
                          group o.Quantity by new { EmployeeId = o.EmployeeId!.Value, o.OccurredOn, o.Stage, p.ItemId, u.Symbol, OrderId = o.ProductionOrderId } into g
                          select new { g.Key, Quantity = g.Sum() }).ToListAsync(ct);
        var redo = await RedoAsync(organizationId, from, to, rows.Select(r => r.Key.OrderId).Distinct().ToList(), ct);
        return rows
            .Select(r => new { r.Key, Quantity = r.Quantity - redo.GetValueOrDefault((r.Key.OrderId, r.Key.EmployeeId, r.Key.OccurredOn, r.Key.Stage)) })
            .GroupBy(r => new { r.Key.EmployeeId, r.Key.OccurredOn, r.Key.Stage, r.Key.ItemId, r.Key.Symbol }, r => r.Quantity)
            .Select(g => new { g.Key, Quantity = g.Sum() })
            .Where(r => r.Quantity != 0)
            .OrderBy(r => r.Key.EmployeeId).ThenBy(r => r.Key.OccurredOn).ThenBy(r => r.Key.Stage)
            .Select(r => new WorkOutputDto(r.Key.EmployeeId, r.Key.OccurredOn, (long)r.Key.Stage, ProductionFlow.StageName(r.Key.Stage),
                r.Key.ItemId, r.Quantity, r.Key.Symbol))
            .ToList();
    }

    /// <summary>
    /// D224 (сессия «Сотрудники», решение владельца): неоплачиваемая повторная работа по заказам с переделкой. Переделка q с возвратом на этап r
    /// открывает «долг» q на этапах r…ВТО; отметки «Сделано» этих этапов после неё (по дате и порядку записи) погашают долг первыми —
    /// это исправление брака, оно не оплачивается (ст. 156 ТК РФ). Результат — сколько вычесть из выработки сотрудника за день и этап периода.
    /// </summary>
    private async Task<Dictionary<(long OrderId, long EmployeeId, DateOnly Day, ProductionStage Stage), decimal>> RedoAsync(
        long organizationId, DateOnly from, DateOnly to, List<long> orderIds, CancellationToken ct)
    {
        var result = new Dictionary<(long, long, DateOnly, ProductionStage), decimal>();
        if (orderIds.Count == 0)
        {
            return result;
        }

        var reworked = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && orderIds.Contains(o.ProductionOrderId) && o.Kind == ProductionOperationKind.Rework
                        && o.OccurredOn <= to)
            .Select(o => o.ProductionOrderId).Distinct().ToListAsync(ct);
        if (reworked.Count == 0)
        {
            return result;
        }

        var history = await db.ProductionOperations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && reworked.Contains(o.ProductionOrderId) && o.OccurredOn <= to
                        && (o.Kind == ProductionOperationKind.Done || o.Kind == ProductionOperationKind.Rework))
            .OrderBy(o => o.OccurredOn).ThenBy(o => o.Id)
            .Select(o => new { o.ProductionOrderId, o.Kind, o.Stage, o.ReturnToStage, o.Quantity, o.OccurredOn, o.EmployeeId })
            .ToListAsync(ct);
        foreach (var order in history.GroupBy(o => o.ProductionOrderId))
        {
            var debt = new Dictionary<ProductionStage, decimal>();
            foreach (var op in order)
            {
                if (op.Kind == ProductionOperationKind.Rework && op.ReturnToStage is { } back)
                {
                    for (var s = back; s < ProductionStage.QualityControl; s++)
                    {
                        debt[s] = debt.GetValueOrDefault(s) + op.Quantity;
                    }

                    continue;
                }

                var owed = debt.GetValueOrDefault(op.Stage);
                if (op.Kind != ProductionOperationKind.Done || owed <= 0)
                {
                    continue;
                }

                var unpaid = Math.Min(owed, op.Quantity);
                debt[op.Stage] = owed - unpaid;
                if (op.EmployeeId is { } employee && op.OccurredOn >= from)
                {
                    var key = (order.Key, employee, op.OccurredOn, op.Stage);
                    result[key] = result.GetValueOrDefault(key) + unpaid;
                }
            }
        }

        return result;
    }
}
