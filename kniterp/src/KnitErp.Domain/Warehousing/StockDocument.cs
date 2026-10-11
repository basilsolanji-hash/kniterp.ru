using KnitErp.Domain.Common;

namespace KnitErp.Domain.Warehousing;

public enum StockDocumentStatus : byte
{
    Draft = 1,
    Posted = 2,
    Reversed = 3,
    Cancelled = 9,
}

/// <summary>
/// Складской документ: поступление, списание или перемещение. Черновик правится свободно; проведение пишет
/// движения регистра, и после него документ не меняется. Ошибку в проведённом документе исправляет сторно —
/// те же движения с обратным знаком — и новый документ. Документ не удаляется: черновик можно только отменить.
/// </summary>
public sealed class StockDocument
{
    public const int CommentMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    /// <summary>Виды операций, которые оформляются этим документом. Инвентаризация — отдельный документ.</summary>
    public static readonly IReadOnlyList<StockOperationKind> Kinds =
        [StockOperationKind.Receipt, StockOperationKind.Transfer, StockOperationKind.WriteOff, StockOperationKind.ReturnToSupplier,
         StockOperationKind.Shipment, StockOperationKind.CustomerReturn];

    /// <summary>Документы с контрагентом: поставщиком (поступление, возврат поставщику) или покупателем (отгрузка, возврат).</summary>
    public static bool WithSupplier(StockOperationKind kind) => kind is StockOperationKind.Receipt or StockOperationKind.ReturnToSupplier;

    public static bool WithCustomer(StockOperationKind kind) => kind is StockOperationKind.Shipment or StockOperationKind.CustomerReturn;

    /// <summary>Расходные виды: товар уходит со склада документа.</summary>
    public static bool IsOutgoing(StockOperationKind kind) =>
        kind is StockOperationKind.WriteOff or StockOperationKind.Transfer or StockOperationKind.ReturnToSupplier or StockOperationKind.Shipment;

    private readonly List<StockDocumentLine> _lines = [];

    private StockDocument()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public StockOperationKind Kind { get; private set; }

    /// <summary>Склад документа: куда поступило, откуда списано или откуда перемещено.</summary>
    public long WarehouseId { get; private set; }

    /// <summary>Склад-получатель — только у перемещения.</summary>
    public long? TargetWarehouseId { get; private set; }

    /// <summary>Поставщик: у поступления необязателен (выпуск из производства идёт без поставщика), у возврата обязателен.</summary>
    public long? CounterpartyId { get; private set; }

    /// <summary>Заказ поставщику, по которому оформлено поступление или возврат (D64): по нему считаются суммы и долг.</summary>
    public long? PurchaseOrderId { get; private set; }

    /// <summary>Заказ покупателя, по которому оформлена отгрузка или возврат от покупателя (D65).</summary>
    public long? SalesOrderId { get; private set; }

    public long? ReasonId { get; private set; }
    public DateOnly DocumentDate { get; private set; }
    public StockDocumentStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? PostedByUserId { get; private set; }
    public DateTime? PostedAtUtc { get; private set; }
    public long? ReversedByUserId { get; private set; }
    public DateTime? ReversedAtUtc { get; private set; }
    public string? ReversalReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Растёт при каждой правке строк (аудит 10.10.2026, п. 1): строки — отдельная таблица, и без этого правка строки не меняла бы
    /// версию документа, а устаревшая форма не получала бы конфликт, если документ тем временем провели.
    /// </summary>
    public int LinesRevision { get; private set; }
    public IReadOnlyCollection<StockDocumentLine> Lines => _lines;

    public static string NumberPrefix(StockOperationKind kind) => kind switch
    {
        StockOperationKind.Receipt => "ПТ",
        StockOperationKind.WriteOff => "СП",
        StockOperationKind.Transfer => "ПМ",
        StockOperationKind.ReturnToSupplier => "ВП",
        StockOperationKind.Shipment => "ОТ",
        StockOperationKind.CustomerReturn => "ВК",
        _ => throw new BusinessRuleException("stock.document.kind", "Выберите вид документа."),
    };

    public static StockDocument Create(
        long organizationId, string number, StockOperationKind kind, StockDocumentHeader header, long createdBy, DateTime nowUtc)
    {
        if (!Kinds.Contains(kind))
        {
            throw new BusinessRuleException("stock.document.kind", "Выберите вид документа.");
        }

        var doc = new StockDocument
        {
            OrganizationId = organizationId,
            Number = number,
            Kind = kind,
            Status = StockDocumentStatus.Draft,
            CreatedByUserId = createdBy,
            CreatedAtUtc = nowUtc,
        };
        doc.ApplyHeader(header);
        return doc;
    }

    public void UpdateHeader(StockDocumentHeader header)
    {
        EnsureDraft();
        ApplyHeader(header);
    }

    /// <summary>Строка по позиции: новая или замена количества. Одна позиция — одна строка.</summary>
    public void SetLine(long itemId, decimal quantity)
    {
        EnsureDraft();
        Quantities.EnsurePositive(quantity);
        LinesRevision++;
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is null)
        {
            _lines.Add(new StockDocumentLine(itemId, quantity));
        }
        else
        {
            line.SetQuantity(quantity);
        }
    }

    /// <summary>Разбивка строк по партиям изменилась (D89): версия документа растёт, устаревшая форма получит конфликт.</summary>
    public void MarkLinesChanged()
    {
        EnsureDraft();
        LinesRevision++;
    }

    public void RemoveLine(long itemId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId)
                   ?? throw new BusinessRuleException("stock.document.line_missing", "Такой строки в документе нет.");
        LinesRevision++;
        _lines.Remove(line);
    }

    /// <summary>Замена всех строк (загрузка из файла).</summary>
    public void ReplaceLines(IReadOnlyList<(long ItemId, decimal Quantity)> lines)
    {
        EnsureDraft();
        if (lines.Select(l => l.ItemId).Distinct().Count() != lines.Count)
        {
            throw new BusinessRuleException("stock.document.duplicate_item", "Позиция повторяется в документе.");
        }

        foreach (var (_, quantity) in lines)
        {
            Quantities.EnsurePositive(quantity);
        }

        _lines.Clear();
        _lines.AddRange(lines.Select(l => new StockDocumentLine(l.ItemId, l.Quantity)));
        LinesRevision++;
    }

    /// <summary>
    /// Проведение. Причина обязательна — по ней отчёт объясняет, откуда пришёл и куда ушёл материал;
    /// причина с флагом «нужно пояснение» требует комментария.
    /// </summary>
    public void Post(long userId, bool reasonRequiresComment, DateTime nowUtc)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("stock.document.empty", "В документе нет строк.");
        }

        if (ReasonId is null)
        {
            throw new BusinessRuleException("stock.document.reason_required", "Укажите причину операции.");
        }

        if (reasonRequiresComment && Comment is null)
        {
            throw new BusinessRuleException("stock.document.comment_required", "Для этой причины нужно пояснение в комментарии.");
        }

        Status = StockDocumentStatus.Posted;
        PostedByUserId = userId;
        PostedAtUtc = nowUtc;
    }

    /// <summary>Сторно: документ остаётся в истории, его движения гасятся обратными. Повторно не проводится.</summary>
    public void Reverse(long userId, string? reason, DateTime nowUtc)
    {
        EnsureStatus(StockDocumentStatus.Posted, "Сторнировать можно только проведённый документ.");
        ReversalReason = DomainText.Require(reason, ReasonMaxLength, "Причина сторно");
        Status = StockDocumentStatus.Reversed;
        ReversedByUserId = userId;
        ReversedAtUtc = nowUtc;
    }

    public void Cancel()
    {
        EnsureDraft();
        Status = StockDocumentStatus.Cancelled;
    }

    /// <summary>Движения проведения: приход (+) и расход (−) по складам. Для сторно знак меняется.</summary>
    public IReadOnlyList<(long WarehouseId, long ItemId, decimal Quantity)> MovementDeltas()
    {
        var result = new List<(long, long, decimal)>();
        foreach (var line in _lines)
        {
            switch (Kind)
            {
                case StockOperationKind.Receipt:
                case StockOperationKind.CustomerReturn:
                    result.Add((WarehouseId, line.ItemId, line.Quantity));
                    break;
                case StockOperationKind.WriteOff:
                case StockOperationKind.ReturnToSupplier:
                case StockOperationKind.Shipment:
                    result.Add((WarehouseId, line.ItemId, -line.Quantity));
                    break;
                case StockOperationKind.Transfer:
                    result.Add((WarehouseId, line.ItemId, -line.Quantity));
                    result.Add((TargetWarehouseId!.Value, line.ItemId, line.Quantity));
                    break;
            }
        }

        return result;
    }

    public static string KindName(StockOperationKind kind) => StockOperationKinds.Name(kind);

    public static string StatusName(StockDocumentStatus status) => status switch
    {
        StockDocumentStatus.Draft => "Черновик",
        StockDocumentStatus.Posted => "Проведён",
        StockDocumentStatus.Reversed => "Сторнирован",
        StockDocumentStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };

    private void ApplyHeader(StockDocumentHeader h)
    {
        if (Kind == StockOperationKind.Transfer)
        {
            if (h.TargetWarehouseId is null)
            {
                throw new BusinessRuleException("stock.document.target_required", "Укажите склад-получатель.");
            }

            if (h.TargetWarehouseId == h.WarehouseId)
            {
                throw new BusinessRuleException("stock.document.same_warehouse", "Склад-получатель должен отличаться от склада-отправителя.");
            }
        }
        else if (h.TargetWarehouseId is not null)
        {
            throw new BusinessRuleException("stock.document.target_not_allowed", "Склад-получатель указывается только в перемещении.");
        }

        var withSupplier = WithSupplier(Kind);
        if (!withSupplier && !WithCustomer(Kind) && h.CounterpartyId is not null)
        {
            throw new BusinessRuleException("stock.document.counterparty_not_allowed", "Контрагент указывается только в поступлении, отгрузке и возвратах.");
        }

        if (WithCustomer(Kind) && h.CounterpartyId is null)
        {
            throw new BusinessRuleException("stock.document.customer_required", "Укажите покупателя.");
        }

        if (!WithCustomer(Kind) && h.SalesOrderId is not null)
        {
            throw new BusinessRuleException("stock.document.sales_order_not_allowed", "Заказ покупателя указывается только в отгрузке и возврате от покупателя.");
        }

        if (Kind == StockOperationKind.ReturnToSupplier && h.CounterpartyId is null)
        {
            throw new BusinessRuleException("stock.document.supplier_required", "Укажите поставщика, которому возвращается товар.");
        }

        if (!withSupplier && h.PurchaseOrderId is not null)
        {
            throw new BusinessRuleException("stock.document.order_not_allowed", "Заказ поставщику указывается только в поступлении и возврате.");
        }

        WarehouseId = h.WarehouseId;
        TargetWarehouseId = h.TargetWarehouseId;
        CounterpartyId = h.CounterpartyId;
        PurchaseOrderId = h.PurchaseOrderId;
        SalesOrderId = h.SalesOrderId;
        ReasonId = h.ReasonId;
        DocumentDate = h.DocumentDate;
        Comment = DomainText.Optional(h.Comment, CommentMaxLength, "Комментарий");
    }

    private void EnsureDraft() => EnsureStatus(StockDocumentStatus.Draft, "Документ уже не черновик — изменения запрещены.");

    private void EnsureStatus(StockDocumentStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new BusinessRuleException("stock.document.status", message);
        }
    }
}

/// <summary>Шапка документа. Справочники (склады, поставщик, причина) проверяет сервис — домен проверяет сочетания.</summary>
public sealed record StockDocumentHeader(
    long WarehouseId, long? TargetWarehouseId, long? CounterpartyId, long? ReasonId, DateOnly DocumentDate, string? Comment,
    long? PurchaseOrderId = null, long? SalesOrderId = null);

public sealed class StockDocumentLine
{
    private StockDocumentLine()
    {
    }

    internal StockDocumentLine(long itemId, decimal quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public long Id { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }

    internal void SetQuantity(decimal quantity) => Quantity = quantity;
}
