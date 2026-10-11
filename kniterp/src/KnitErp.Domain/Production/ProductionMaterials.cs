using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

/// <summary>Зачем складской документ оформлен по производственному заказу (D93, D95).</summary>
public enum ProductionDocumentPurpose : byte
{
    /// <summary>Выдача в цех: перемещение со склада на склад цеха (НЗП).</summary>
    Issue = 1,

    /// <summary>Возврат остатка: перемещение со склада цеха обратно на склад.</summary>
    Return = 2,

    /// <summary>Отходы: списание со склада цеха с причиной.</summary>
    Waste = 3,

    /// <summary>Расход по факту при выпуске: списание со склада цеха.</summary>
    Consumption = 4,

    /// <summary>Выпуск готовой продукции: поступление на склад готовой продукции.</summary>
    Output = 5,
}

/// <summary>
/// Складской документ, оформленный по производственному заказу. Только вставка (триггер): связь не снимается,
/// а сторнированный документ просто перестаёт участвовать в балансе — считаются только проведённые.
/// </summary>
public sealed class ProductionOrderDocument
{
    private ProductionOrderDocument()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ProductionOrderId { get; private set; }
    public long StockDocumentId { get; private set; }
    public ProductionDocumentPurpose Purpose { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static ProductionOrderDocument Create(
        long organizationId, long orderId, long stockDocumentId, ProductionDocumentPurpose purpose, long userId, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        ProductionOrderId = orderId,
        StockDocumentId = stockDocumentId,
        Purpose = purpose,
        CreatedByUserId = userId,
        CreatedAtUtc = nowUtc,
    };

    public static string PurposeName(ProductionDocumentPurpose purpose) => purpose switch
    {
        ProductionDocumentPurpose.Issue => "Выдача в цех",
        ProductionDocumentPurpose.Return => "Возврат из цеха",
        ProductionDocumentPurpose.Waste => "Отходы",
        ProductionDocumentPurpose.Consumption => "Расход при выпуске",
        ProductionDocumentPurpose.Output => "Выпуск продукции",
        _ => purpose.ToString(),
    };
}

/// <summary>Количество материала в проведённом документе заказа.</summary>
public readonly record struct MaterialMovement(long ItemId, ProductionDocumentPurpose Purpose, decimal Quantity);

/// <summary>
/// Строка материального баланса (D94): выдано = израсходовано + отходы + возвращено + остаток в НЗП.
/// Норма на сделанное — потребность с отходом на выпущенные изделия и брак; отклонение — фактический расход с отходами к норме.
/// </summary>
public sealed record MaterialBalanceLine(
    long ItemId, decimal Planned, decimal Issued, decimal Consumed, decimal Waste, decimal Returned, decimal? Normative)
{
    /// <summary>Остаток материала заказа на складе цеха.</summary>
    public decimal Remaining => Issued - Consumed - Waste - Returned;

    /// <summary>Отклонение фактического расхода с отходами от нормы, %; null — нормы ещё нет (ничего не выпущено).</summary>
    public decimal? DeviationPercent => Normative is { } n && n > 0 ? decimal.Round((Consumed + Waste - n) * 100m / n, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Отклонение больше допуска — предупреждение.</summary>
    public bool OverTolerance(decimal tolerancePercent) => DeviationPercent is { } d && Math.Abs(d) > tolerancePercent;
}

public static class MaterialBalance
{
    /// <summary>Допуск отклонения расхода от нормы, % (допущение D94).</summary>
    public const decimal TolerancePercent = 5m;

    /// <summary>
    /// Баланс по каждому материалу снимка норм заказа. <paramref name="producedUnits"/> — выпущено плюс брак: на них уже ушла пряжа.
    /// Норма появляется после первого выпуска — до него расход списывать не на что.
    /// </summary>
    public static IReadOnlyList<MaterialBalanceLine> Calculate(
        IReadOnlyList<ProductionOrderMaterial> materials, decimal orderQuantity, IEnumerable<MaterialMovement> movements, decimal released, decimal defect)
    {
        var moves = movements.ToList();
        decimal Sum(long itemId, ProductionDocumentPurpose purpose) =>
            moves.Where(m => m.ItemId == itemId && m.Purpose == purpose).Sum(m => m.Quantity);

        return materials.Select(m => new MaterialBalanceLine(m.ItemId, m.PlannedGross,
            Sum(m.ItemId, ProductionDocumentPurpose.Issue), Sum(m.ItemId, ProductionDocumentPurpose.Consumption),
            Sum(m.ItemId, ProductionDocumentPurpose.Waste), Sum(m.ItemId, ProductionDocumentPurpose.Return),
            released > 0 && orderQuantity > 0 ? m.PlannedGross * (released + defect) / orderQuantity : null)).ToList();
    }

    /// <summary>Возврат, отходы и расход — не больше остатка материала этого заказа на складе цеха: двойного списания нет.</summary>
    public static void EnsureWithinRemaining(IReadOnlyList<MaterialBalanceLine> balance, IEnumerable<(long ItemId, decimal Quantity)> outgoing, Func<long, string> codeOf)
    {
        var over = outgoing.GroupBy(o => o.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(x => x.Quantity),
                Left: balance.FirstOrDefault(b => b.ItemId == g.Key)?.Remaining ?? 0m))
            .Where(x => x.Quantity > x.Left).ToList();
        if (over.Count > 0)
        {
            throw new BusinessRuleException("production.material.exceeds_remaining",
                "Больше, чем осталось в цехе по этому заказу — " + string.Join("; ", over.Take(10).Select(x =>
                    $"{codeOf(x.ItemId)}: нужно {Quantities.Format(x.Quantity)}, осталось {Quantities.Format(Math.Max(0, x.Left))}")) + ".");
        }
    }
}
