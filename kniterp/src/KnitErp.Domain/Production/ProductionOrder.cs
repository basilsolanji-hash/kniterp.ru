using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

public enum ProductionOrderStatus : byte
{
    Draft = 1,
    Released = 2,
    PartiallyDone = 3,
    Done = 4,
    Closed = 5,
    Cancelled = 9,
}

/// <summary>Откуда взялся производственный заказ: план производства или заказ покупателя.</summary>
public enum ProductionSource : byte
{
    Plan = 1,
    SalesOrder = 2,
}

/// <summary>
/// Производственный заказ (ПЗ-, D90): выпуск изделия — модели или модификации — по зафиксированной версии техкарты.
/// Черновик правится свободно. «Запустить» закрепляет действующую на этот момент версию техкарты и снимок норм
/// с плановыми ценами материалов: новая версия техкарты или новые цены закупки запущенный заказ не меняют.
/// Заказ не удаляется: черновик или запущенный заказ без движений можно только отменить с причиной.
/// </summary>
public sealed class ProductionOrder
{
    public const string NumberPrefix = "ПЗ";
    public const int CommentMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    private readonly List<ProductionOrderMaterial> _materials = [];

    private ProductionOrder()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly OrderDate { get; private set; }

    /// <summary>Что выпускается: готовая продукция или полуфабрикат, в том числе модификация.</summary>
    public long ItemId { get; private set; }

    /// <summary>Сколько выпустить, в единицах изделия.</summary>
    public decimal Quantity { get; private set; }

    public DateOnly? DueDate { get; private set; }
    public ProductionSource Source { get; private set; }

    /// <summary>Заказ покупателя — только у источника «Заказ покупателя».</summary>
    public long? SalesOrderId { get; private set; }

    /// <summary>Склад цеха (незавершённое производство): сюда выдаётся пряжа и отсюда списывается расход.</summary>
    public long WipWarehouseId { get; private set; }

    /// <summary>Склад готовой продукции: сюда приходит выпуск.</summary>
    public long OutputWarehouseId { get; private set; }

    /// <summary>
    /// Версия техкарты. В черновике — действующая на момент создания (подсказка); при запуске заменяется действующей
    /// на момент запуска и больше не меняется.
    /// </summary>
    public long TechCardId { get; private set; }

    public ProductionOrderStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? ReleasedByUserId { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public long? ClosedByUserId { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }

    /// <summary>Причина закрытия не полностью выполненного заказа.</summary>
    public string? CloseReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Нормы материалов на весь заказ, снятые при запуске. У черновика пусто.</summary>
    public IReadOnlyList<ProductionOrderMaterial> Materials => _materials;

    public static ProductionOrder Create(
        long organizationId, string number, long itemId, decimal quantity, long techCardId, ProductionOrderHeader header, long userId, DateTime nowUtc)
    {
        var order = new ProductionOrder
        {
            OrganizationId = organizationId,
            Number = number,
            ItemId = itemId,
            Status = ProductionOrderStatus.Draft,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        order.ApplyDraft(quantity, techCardId, header);
        return order;
    }

    /// <summary>Изменение черновика. Изделие не меняется — для другого изделия создаётся новый заказ.</summary>
    public void UpdateDraft(decimal quantity, long techCardId, ProductionOrderHeader header)
    {
        EnsureDraft();
        ApplyDraft(quantity, techCardId, header);
    }

    /// <summary>
    /// Запуск: фиксирует версию техкарты и снимок потребности материалов на количество заказа с плановой ценой единицы.
    /// Что карта подходит изделию и действует — проверяет сервис в той же транзакции.
    /// </summary>
    public void Release(TechCard card, Func<long, int> precisionOf, Func<long, decimal?> priceOf, long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (card.Status != TechCardStatus.Active)
        {
            throw new BusinessRuleException("production.order.techcard_inactive", "Техкарта не действует — введите версию в действие.");
        }

        if (card.Lines.Count == 0)
        {
            throw new BusinessRuleException("production.order.techcard_empty", "В техкарте нет материалов.");
        }

        TechCardId = card.Id;
        _materials.Clear();
        var waste = card.Lines.ToDictionary(l => l.ItemId, l => l.WastePercent);
        foreach (var (itemId, net, gross) in card.Requirement(Quantity, precisionOf))
        {
            var price = priceOf(itemId);
            _materials.Add(new ProductionOrderMaterial(itemId, net, gross, waste[itemId], price is { } p ? decimal.Round(p, 4, MidpointRounding.AwayFromZero) : null));
        }

        Status = ProductionOrderStatus.Released;
        ReleasedByUserId = userId;
        ReleasedAtUtc = nowUtc;
    }

    /// <summary>
    /// Отмена с причиной: черновик — всегда, запущенный — только пока по заказу нет движений (выдачи, этапов, выпуска).
    /// Если движения есть, заказ доводится до выпуска или закрывается — следы производства не исчезают.
    /// </summary>
    public void Cancel(bool hasMovements, string? reason, long userId, DateTime nowUtc)
    {
        if (Status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Released))
        {
            throw new BusinessRuleException("production.order.status", "Отменить можно только черновик или запущенный заказ.");
        }

        if (hasMovements)
        {
            throw new BusinessRuleException("production.order.has_movements",
                "По заказу уже есть движения (выдача материалов, этапы или выпуск) — отмена невозможна.");
        }

        CancelReason = DomainText.Require(reason, ReasonMaxLength, "Причина отмены");
        Status = ProductionOrderStatus.Cancelled;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }

    /// <summary>Этапы, выдача материалов и выпуск — только у запущенного и ещё не выполненного полностью заказа.</summary>
    public void EnsureInWork()
    {
        if (Status is not (ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone))
        {
            throw new BusinessRuleException("production.order.not_in_work",
                Status == ProductionOrderStatus.Draft ? "Заказ ещё не запущен — сначала нажмите «Запустить»." : $"Заказ в статусе «{StatusName(Status)}» — работы по нему не отмечаются.");
        }
    }

    /// <summary>Возврат остатков и отходы — и после полного выпуска, пока заказ не закрыт: остаток в цехе нужно разнести.</summary>
    public void EnsureMaterialsOpen()
    {
        if (Status is not (ProductionOrderStatus.Released or ProductionOrderStatus.PartiallyDone or ProductionOrderStatus.Done))
        {
            EnsureInWork();
        }
    }

    /// <summary>Выпуск (D95): статус по выпущенному всего — частично или полностью.</summary>
    public void ApplyOutput(decimal releasedTotal)
    {
        EnsureInWork();
        Status = releasedTotal >= Quantity ? ProductionOrderStatus.Done : ProductionOrderStatus.PartiallyDone;
    }

    /// <summary>
    /// Закрытие (D95): после выпуска, когда в цехе не осталось материалов заказа и всё упакованное выпущено.
    /// Не полностью выполненный заказ закрывается с причиной. После закрытия по заказу ничего не движется.
    /// </summary>
    public void Close(decimal releasedTotal, bool materialsLeft, decimal packedNotReleased, string? reason, long userId, DateTime nowUtc)
    {
        if (Status is not (ProductionOrderStatus.PartiallyDone or ProductionOrderStatus.Done))
        {
            throw new BusinessRuleException("production.order.close_status", "Закрыть можно заказ, по которому был выпуск.");
        }

        if (materialsLeft)
        {
            throw new BusinessRuleException("production.order.materials_left",
                "В цехе остались материалы заказа — верните их на склад или спишите отходами, затем закройте заказ.");
        }

        if (packedNotReleased > 0)
        {
            throw new BusinessRuleException("production.order.packed_not_released",
                $"Упаковано и не выпущено {Quantities.Format(packedNotReleased)} — выпустите их на склад.");
        }

        var partial = releasedTotal < Quantity;
        CloseReason = partial ? DomainText.Require(reason, ReasonMaxLength, "Причина закрытия не полностью выполненного заказа")
                              : DomainText.Optional(reason, ReasonMaxLength, "Комментарий");
        Status = ProductionOrderStatus.Closed;
        ClosedByUserId = userId;
        ClosedAtUtc = nowUtc;
    }

    /// <summary>Плановая себестоимость всего заказа по снимку норм: Σ потребность с отходом × плановая цена. null — цена хотя бы одного материала неизвестна.</summary>
    public decimal? PlannedCost()
    {
        if (_materials.Count == 0 || _materials.Any(m => m.PlannedPrice is null))
        {
            return null;
        }

        return Money.Round(_materials.Sum(m => m.PlannedGross * m.PlannedPrice!.Value));
    }

    public static string StatusName(ProductionOrderStatus status) => status switch
    {
        ProductionOrderStatus.Draft => "Черновик",
        ProductionOrderStatus.Released => "Запущен",
        ProductionOrderStatus.PartiallyDone => "Выполнен частично",
        ProductionOrderStatus.Done => "Выполнен",
        ProductionOrderStatus.Closed => "Закрыт",
        ProductionOrderStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };

    public static string SourceName(ProductionSource source) => source switch
    {
        ProductionSource.Plan => "План производства",
        ProductionSource.SalesOrder => "Заказ покупателя",
        _ => source.ToString(),
    };

    private void ApplyDraft(decimal quantity, long techCardId, ProductionOrderHeader h)
    {
        Quantities.EnsurePositive(quantity);
        if (h.WipWarehouseId == h.OutputWarehouseId)
        {
            throw new BusinessRuleException("production.order.same_warehouse",
                "Склад цеха (НЗП) и склад готовой продукции должны различаться.");
        }

        if (h.DueDate is { } due && due < h.OrderDate)
        {
            throw new BusinessRuleException("production.order.due_date", "Срок выпуска не может быть раньше даты заказа.");
        }

        switch (h.Source)
        {
            case ProductionSource.Plan when h.SalesOrderId is not null:
                throw new BusinessRuleException("production.order.sales_order_not_allowed", "Заказ покупателя указывается только у источника «Заказ покупателя».");
            case ProductionSource.SalesOrder when h.SalesOrderId is null:
                throw new BusinessRuleException("production.order.sales_order_required", "Выберите заказ покупателя.");
            case ProductionSource.Plan or ProductionSource.SalesOrder:
                break;
            default:
                throw new BusinessRuleException("production.order.source", "Выберите источник заказа.");
        }

        Quantity = quantity;
        TechCardId = techCardId;
        OrderDate = h.OrderDate;
        DueDate = h.DueDate;
        Source = h.Source;
        SalesOrderId = h.SalesOrderId;
        WipWarehouseId = h.WipWarehouseId;
        OutputWarehouseId = h.OutputWarehouseId;
        Comment = DomainText.Optional(h.Comment, CommentMaxLength, "Комментарий");
    }

    private void EnsureDraft()
    {
        if (Status != ProductionOrderStatus.Draft)
        {
            throw new BusinessRuleException("production.order.not_draft", "Заказ уже запущен — менять можно только черновик.");
        }
    }
}

/// <summary>Шапка заказа. Склады, заказ покупателя и изделие проверяет сервис — домен проверяет сочетания.</summary>
public sealed record ProductionOrderHeader(
    DateOnly OrderDate, DateOnly? DueDate, ProductionSource Source, long? SalesOrderId, long WipWarehouseId, long OutputWarehouseId, string? Comment);

/// <summary>Снимок нормы материала на весь заказ при запуске: без отхода, с отходом и плановая цена единицы (без НДС).</summary>
public sealed class ProductionOrderMaterial
{
    private ProductionOrderMaterial()
    {
    }

    internal ProductionOrderMaterial(long itemId, decimal plannedNet, decimal plannedGross, decimal wastePercent, decimal? plannedPrice)
    {
        ItemId = itemId;
        PlannedNet = plannedNet;
        PlannedGross = plannedGross;
        WastePercent = wastePercent;
        PlannedPrice = plannedPrice;
    }

    public long Id { get; private set; }
    public long ProductionOrderId { get; private set; }
    public long ItemId { get; private set; }

    /// <summary>Потребность без отхода, округлена вверх до точности единицы материала.</summary>
    public decimal PlannedNet { get; private set; }

    /// <summary>Потребность с отходом — столько планируется выдать в цех.</summary>
    public decimal PlannedGross { get; private set; }

    public decimal WastePercent { get; private set; }

    /// <summary>Плановая цена единицы без НДС на момент запуска; null — неизвестна (нет закупок и техкарты).</summary>
    public decimal? PlannedPrice { get; private set; }
}
