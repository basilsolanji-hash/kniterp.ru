using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Состояние задания на сборку (D1150): открыто (собирается, в т. ч. частично), собрано полностью, отменено.</summary>
public enum PickListStatus : byte
{
    Open = 1,
    Picked = 2,
    Cancelled = 9,
}

/// <summary>
/// Задание на сборку для склада (D1150): позиции подтверждённых заказов одного склада к сборке, с местом хранения. Мест и ячеек
/// в модели склада нет — место хранения задаётся в строке задания (подставляется из прошлых заданий). Кладовщик отмечает собранное
/// количество построчно; задание собрано, когда собраны все строки. Не удаляется — отменяется.
/// </summary>
public sealed class SalesPickList
{
    public const string NumberPrefix = "СБ";
    public const int CommentMaxLength = 500;
    public const int MaxLines = 500;

    private readonly List<SalesPickListLine> _lines = [];

    private SalesPickList()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly ListDate { get; private set; }
    public long WarehouseId { get; private set; }
    public PickListStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? PickedByUserId { get; private set; }
    public DateTime? PickedAtUtc { get; private set; }

    /// <summary>Растёт при правке строк: строки — отдельная таблица, а форма должна получить конфликт версии.</summary>
    public int LinesRevision { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<SalesPickListLine> Lines => _lines;

    public bool PartiallyPicked => Status == PickListStatus.Open && _lines.Any(l => l.Picked > 0);

    public static SalesPickList Create(
        long organizationId, string number, DateOnly date, long warehouseId, string? comment,
        IReadOnlyList<(long OrderId, long ItemId, decimal Quantity, string? Location)> lines, long userId, DateTime nowUtc)
    {
        if (lines.Count == 0)
        {
            throw new BusinessRuleException("sales.pick.empty", "Собирать нечего: по выбранным заказам всё отгружено или в них только услуги.");
        }

        if (lines.Count > MaxLines)
        {
            throw new BusinessRuleException("sales.pick.too_many", $"В задании на сборку — не больше {MaxLines} строк. Разбейте заказы на несколько заданий.");
        }

        var list = new SalesPickList
        {
            OrganizationId = organizationId,
            Number = number,
            ListDate = date,
            WarehouseId = warehouseId,
            Status = PickListStatus.Open,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        foreach (var (orderId, itemId, quantity, location) in lines)
        {
            Quantities.EnsurePositive(quantity);
            list._lines.Add(new SalesPickListLine(organizationId, orderId, itemId, quantity, SalesPickListLine.CleanLocation(location)));
        }

        return list;
    }

    /// <summary>Собрано по строке: от 0 до количества к сборке. Все строки собраны полностью — задание «Собрано».</summary>
    public void SetPicked(long lineId, decimal picked, long userId, DateTime nowUtc)
    {
        EnsureOpenOrPicked();
        var line = Line(lineId);
        if (picked < 0 || picked > line.Quantity)
        {
            throw new BusinessRuleException("sales.pick.quantity", $"Собрано — от 0 до {Quantities.Format(line.Quantity)}.");
        }

        line.Picked = picked;
        LinesRevision++;
        Refresh(userId, nowUtc);
    }

    /// <summary>Отметить всё задание собранным полностью.</summary>
    public void PickAll(long userId, DateTime nowUtc)
    {
        EnsureOpenOrPicked();
        foreach (var line in _lines)
        {
            line.Picked = line.Quantity;
        }

        LinesRevision++;
        Refresh(userId, nowUtc);
    }

    public void SetLocation(long lineId, string? location)
    {
        EnsureOpenOrPicked();
        Line(lineId).Location = SalesPickListLine.CleanLocation(location);
        LinesRevision++;
    }

    public void Cancel()
    {
        if (Status != PickListStatus.Open)
        {
            throw new BusinessRuleException("sales.pick.status", "Отменить можно только задание, которое ещё не собрано.");
        }

        Status = PickListStatus.Cancelled;
    }

    public static string StatusName(PickListStatus status) => status switch
    {
        PickListStatus.Open => "Собирается",
        PickListStatus.Picked => "Собрано",
        PickListStatus.Cancelled => "Отменено",
        _ => status.ToString(),
    };

    private void Refresh(long userId, DateTime nowUtc)
    {
        if (_lines.All(l => l.Picked >= l.Quantity))
        {
            Status = PickListStatus.Picked;
            PickedByUserId = userId;
            PickedAtUtc = nowUtc;
        }
        else
        {
            Status = PickListStatus.Open;
            PickedByUserId = null;
            PickedAtUtc = null;
        }
    }

    private SalesPickListLine Line(long lineId) =>
        _lines.FirstOrDefault(l => l.Id == lineId) ?? throw new BusinessRuleException("sales.pick.line", "Такой строки в задании нет.");

    private void EnsureOpenOrPicked()
    {
        if (Status == PickListStatus.Cancelled)
        {
            throw new BusinessRuleException("sales.pick.status", "Задание на сборку отменено — изменения запрещены.");
        }
    }
}

public sealed class SalesPickListLine
{
    public const int LocationMaxLength = 50;

    private SalesPickListLine()
    {
    }

    internal SalesPickListLine(long organizationId, long orderId, long itemId, decimal quantity, string? location)
    {
        OrganizationId = organizationId;
        SalesOrderId = orderId;
        ItemId = itemId;
        Quantity = quantity;
        Location = location;
    }

    public long Id { get; private set; }
    public long PickListId { get; private set; }
    public long OrganizationId { get; private set; }
    public long SalesOrderId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Picked { get; internal set; }

    /// <summary>Место хранения (стеллаж, ячейка) — свободный текст: ячеек в модели склада нет.</summary>
    public string? Location { get; internal set; }

    internal static string? CleanLocation(string? location) => DomainText.Optional(location, LocationMaxLength, "Место хранения");
}

/// <summary>Вид способа доставки (D1152).</summary>
public enum DeliveryKind : byte
{
    Pickup = 1,
    OwnDelivery = 2,
    Carrier = 3,
}

/// <summary>
/// Способ доставки (D1152): справочник организации — самовывоз, своя доставка, транспортные компании по названию. Без интеграций
/// с ТК: в отгрузке — способ, трек-номер и печать. Не удаляется — архив.
/// </summary>
public sealed class SalesDeliveryMethod
{
    public const int NameMaxLength = 150;
    public const int MaxMethods = 100;

    public static readonly IReadOnlyList<(string Name, DeliveryKind Kind)> Defaults =
        [("Самовывоз", DeliveryKind.Pickup), ("Своя доставка", DeliveryKind.OwnDelivery), ("Транспортная компания", DeliveryKind.Carrier)];

    private SalesDeliveryMethod()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DeliveryKind Kind { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesDeliveryMethod Create(long organizationId, string? name, DeliveryKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("sales.delivery.kind", "Выберите вид доставки.");
        }

        return new SalesDeliveryMethod { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Способ доставки"), Kind = kind };
    }

    public void SetArchived(bool archived) => IsArchived = archived;

    public static string KindName(DeliveryKind kind) => kind switch
    {
        DeliveryKind.Pickup => "Самовывоз",
        DeliveryKind.OwnDelivery => "Своя доставка",
        DeliveryKind.Carrier => "Транспортная компания",
        _ => kind.ToString(),
    };
}

public enum ShipmentGroupStatus : byte
{
    Draft = 1,
    Posted = 2,
    Cancelled = 9,
}

/// <summary>Логистика отгрузки: способ доставки, трек-номер, мест, вес брутто, комментарий.</summary>
public sealed record SalesShipmentLogistics(long? DeliveryMethodId, string? TrackingNumber, int? Packages, decimal? GrossWeightKg, string? Comment);

/// <summary>
/// Отгрузка по нескольким заказам (D1151): один покупатель, один склад, одна машина. Сам товар списывают документы отгрузки —
/// по одному на заказ (учёт «отгружено» по заказу не меняется); консолидация связывает их, хранит логистику (способ доставки,
/// трек-номер, мест, вес брутто) и печатает упаковочный лист. Проводится целиком: все документы — в одной транзакции.
/// </summary>
public sealed class SalesShipmentGroup
{
    public const string NumberPrefix = "ОГ";
    public const int TrackingMaxLength = 100;
    public const int CommentMaxLength = 500;
    public const int MaxOrders = 50;

    private readonly List<SalesShipmentGroupDocument> _documents = [];

    private SalesShipmentGroup()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly GroupDate { get; private set; }
    public long CustomerId { get; private set; }
    public long WarehouseId { get; private set; }
    public long? DeliveryMethodId { get; private set; }
    public string? TrackingNumber { get; private set; }
    public int? Packages { get; private set; }
    public decimal? GrossWeightKg { get; private set; }
    public string? Comment { get; private set; }
    public ShipmentGroupStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? PostedByUserId { get; private set; }
    public DateTime? PostedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<SalesShipmentGroupDocument> Documents => _documents;

    public static SalesShipmentGroup Create(
        long organizationId, string number, DateOnly date, long customerId, long warehouseId, SalesShipmentLogistics logistics, long userId, DateTime nowUtc)
    {
        var group = new SalesShipmentGroup
        {
            OrganizationId = organizationId,
            Number = number,
            GroupDate = date,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = ShipmentGroupStatus.Draft,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        group.SetLogistics(logistics);
        return group;
    }

    /// <summary>Логистику можно уточнить и после проведения: трек-номер часто приходит позже.</summary>
    public void SetLogistics(SalesShipmentLogistics l)
    {
        if (Status == ShipmentGroupStatus.Cancelled)
        {
            throw new BusinessRuleException("sales.shipment_group.status", "Отгрузка отменена — изменения запрещены.");
        }

        if (l.Packages is < 1 or > 100_000)
        {
            throw new BusinessRuleException("sales.shipment_group.packages", "Мест — целое число от 1.");
        }

        if (l.GrossWeightKg is { } w && (w <= 0 || w > 1_000_000 || decimal.Round(w, 3) != w))
        {
            throw new BusinessRuleException("sales.shipment_group.weight", "Вес брутто — больше нуля, до граммов (3 знака).");
        }

        DeliveryMethodId = l.DeliveryMethodId;
        TrackingNumber = DomainText.Optional(l.TrackingNumber, TrackingMaxLength, "Трек-номер");
        Packages = l.Packages;
        GrossWeightKg = l.GrossWeightKg;
        Comment = DomainText.Optional(l.Comment, CommentMaxLength, "Комментарий");
    }

    public void AddDocument(long stockDocumentId, long orderId)
    {
        EnsureDraft();
        if (_documents.Any(d => d.SalesOrderId == orderId))
        {
            throw new BusinessRuleException("sales.shipment_group.duplicate", "Заказ уже в этой отгрузке.");
        }

        if (_documents.Count >= MaxOrders)
        {
            throw new BusinessRuleException("sales.shipment_group.too_many", $"В одной отгрузке — не больше {MaxOrders} заказов.");
        }

        _documents.Add(new SalesShipmentGroupDocument(OrganizationId, stockDocumentId, orderId));
    }

    public void MarkPosted(long userId, DateTime nowUtc)
    {
        EnsureDraft();
        Status = ShipmentGroupStatus.Posted;
        PostedByUserId = userId;
        PostedAtUtc = nowUtc;
    }

    public void Cancel()
    {
        EnsureDraft();
        Status = ShipmentGroupStatus.Cancelled;
    }

    public static string StatusName(ShipmentGroupStatus status) => status switch
    {
        ShipmentGroupStatus.Draft => "Черновик",
        ShipmentGroupStatus.Posted => "Отгружено",
        ShipmentGroupStatus.Cancelled => "Отменено",
        _ => status.ToString(),
    };

    private void EnsureDraft()
    {
        if (Status != ShipmentGroupStatus.Draft)
        {
            throw new BusinessRuleException("sales.shipment_group.status", "Отгрузка уже проведена или отменена.");
        }
    }
}

/// <summary>Документ отгрузки в составе консолидированной отгрузки; документ — не больше чем в одной (UNIQUE).</summary>
public sealed class SalesShipmentGroupDocument
{
    private SalesShipmentGroupDocument()
    {
    }

    internal SalesShipmentGroupDocument(long organizationId, long stockDocumentId, long orderId)
    {
        OrganizationId = organizationId;
        StockDocumentId = stockDocumentId;
        SalesOrderId = orderId;
    }

    public long Id { get; private set; }
    public long GroupId { get; private set; }
    public long OrganizationId { get; private set; }
    public long StockDocumentId { get; private set; }
    public long SalesOrderId { get; private set; }
}
