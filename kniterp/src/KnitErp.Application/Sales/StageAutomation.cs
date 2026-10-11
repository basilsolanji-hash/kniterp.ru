using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>
/// Автоматическая смена этапа заказа покупателя (D110). Вызывается сервисами после события (подтверждение, счёт, оплата, отгрузка,
/// производство) внутри их транзакции, после сохранения самого события: факты берутся из документов в базе. Меняет этап только
/// вперёд по списку и пишет запись журнала «этап: А → Б, автоматически: событие». Права проверил вызывающий сервис.
/// </summary>
internal static class StageAutomation
{
    public static async Task ApplyAsync(IKnitErpDbContext db, AccessContext ctx, IReadOnlyCollection<long> orderIds, DateTime nowUtc, string? correlationId,
        CancellationToken ct, params StageTrigger[] extra)
    {
        if (orderIds.Count == 0)
        {
            return;
        }

        var org = ctx.OrganizationId;
        var stages = await db.SalesOrderStages.AsNoTracking().Where(s => s.OrganizationId == org).ToDictionaryAsync(s => s.Id, ct);
        var rules = (await db.SalesStageRules.AsNoTracking().Where(r => r.OrganizationId == org && r.StageId != null).ToListAsync(ct))
            .Where(r => stages.TryGetValue(r.StageId!.Value, out var s) && !s.IsArchived)
            .Select(r => new StageRuleTarget(r.Trigger, r.StageId!.Value, stages[r.StageId!.Value].SortOrder))
            .ToList();
        if (rules.Count == 0)
        {
            return;
        }

        var ids = orderIds.Distinct().ToList();
        var orders = await db.SalesOrders.Include(o => o.Lines)
            .Where(o => o.OrganizationId == org && ids.Contains(o.Id) && o.Status != SalesOrderStatus.Draft && o.Status != SalesOrderStatus.Cancelled)
            .ToListAsync(ct);
        if (orders.Count == 0)
        {
            return;
        }

        ids = orders.Select(o => o.Id).ToList();
        var invoiced = await db.CustomerInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == org && ids.Contains(i.SalesOrderId) && i.Status == CustomerInvoiceStatus.Issued)
            .Select(i => i.SalesOrderId).Distinct().ToListAsync(ct);
        var paid = await KnitErp.Application.Finance.PaidByOrder.SalesAsync(db, org, ids, ct);
        var moved = await db.StockDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == org && d.SalesOrderId != null && ids.Contains(d.SalesOrderId.Value) && d.Status == StockDocumentStatus.Posted
                        && (d.Kind == StockOperationKind.Shipment || d.Kind == StockOperationKind.CustomerReturn))
            .SelectMany(d => d.Lines.Select(l => new { OrderId = d.SalesOrderId!.Value, d.Kind, l.ItemId, l.Quantity }))
            .ToListAsync(ct);

        foreach (var order in orders)
        {
            var net = order.Lines.Select(l => (l.Quantity, Net: moved.Where(m => m.OrderId == order.Id && m.ItemId == l.ItemId)
                .Sum(m => m.Kind == StockOperationKind.Shipment ? m.Quantity : -m.Quantity))).ToList();
            var facts = new SalesOrderFacts(
                Confirmed: true,
                Invoiced: invoiced.Contains(order.Id),
                Total: order.Lines.Sum(l => l.Amount),
                Paid: paid.GetValueOrDefault(order.Id),
                ShippedAny: net.Any(x => x.Net > 0),
                ShippedAll: net.Count > 0 && net.All(x => x.Net >= x.Quantity));
            var reached = SalesStageAutomation.Reached(facts).Union(extra).ToHashSet();
            var current = order.StageId is { } sid && stages.TryGetValue(sid, out var cs) && !cs.IsArchived ? cs : null;
            var target = SalesStageAutomation.Pick(current?.SortOrder, order.StageId, rules, reached);
            if (target is null)
            {
                continue;
            }

            order.SetStage(target.StageId);
            db.AuditEntries.Add(AuditEntry.Create(nowUtc, org, ctx.UserId, AuditActions.SalesOrderChanged, nameof(SalesOrder), order.Id.ToString(),
                current?.Name ?? "—", stages[target.StageId].Name, $"{order.Number}: этап, автоматически — {SalesStageAutomation.Name(target.Trigger)}",
                correlationId));
        }
    }

    /// <summary>Правила по умолчанию для новой организации — к этапам по умолчанию, найденным по названию.</summary>
    internal static void SeedDefaults(IKnitErpDbContext db, long organizationId, IEnumerable<SalesOrderStage> stages)
    {
        var byName = stages.ToDictionary(s => s.Name);
        foreach (var (trigger, name) in SalesStageAutomation.Defaults)
        {
            if (byName.TryGetValue(name, out var stage))
            {
                db.SalesStageRules.Add(SalesStageRule.Create(organizationId, trigger, stage.Id));
            }
        }
    }
}
