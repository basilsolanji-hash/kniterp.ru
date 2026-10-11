using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

public enum SalesOrderStatus : byte
{
    Draft = 1,
    Confirmed = 2,
    Closed = 3,
    Cancelled = 9,
}

/// <summary>
/// Заказ покупателя (D65): что, сколько и почём продаём. Черновик правится; «Подтвердить» фиксирует условия —
/// по подтверждённому заказу оформляются отгрузки и возвраты, их суммы считаются по ценам заказа, и из них
/// складывается долг покупателя. Цены — в валюте организации; НДС — по проценту строки.
/// </summary>
public sealed class SalesOrder
{
    public const string NumberPrefix = "ЗК";
    public const int CommentMaxLength = 1000;
    public const int InvoiceMaxLength = 100;
    public const int MaxLines = 500;

    private readonly List<SalesOrderLine> _lines = [];

    private SalesOrder()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly OrderDate { get; private set; }
    public long CustomerId { get; private set; }

    /// <summary>Склад, с которого отгружается товар.</summary>
    public long WarehouseId { get; private set; }

    public DateOnly? ShipDate { get; private set; }

    /// <summary>Договор или заказ покупателя: номер и дата, как в его документе («Договор № 7 от 01.10.2026»).</summary>
    public string? CustomerReference { get; private set; }

    /// <summary>Цены строк с НДС (НДС выделяется из суммы) или без (НДС начисляется сверху).</summary>
    public bool PricesIncludeVat { get; private set; }

    public SalesOrderStatus Status { get; private set; }

    /// <summary>
    /// Резерв (D76): товар подтверждённого заказа, ещё не отгруженный, держится на складе заказа и не считается доступным
    /// другим заказам. Галочка ставится и снимается в черновике и в подтверждённом заказе.
    /// </summary>
    public bool Reserve { get; private set; }

    /// <summary>Время заказа (местное) — к дате, как «09.10.2026 16:43» в МойСклад (D77).</summary>
    public TimeOnly? OrderTime { get; private set; }

    public long? ProjectId { get; private set; }
    public long? ChannelId { get; private set; }
    public string? DeliveryAddress { get; private set; }

    /// <summary>Своё юрлицо-продавец (D78): от его имени счёт, УПД и счёт-фактура. Меняется только в черновике.</summary>
    public long LegalEntityId { get; private set; }

    /// <summary>Расчётный счёт юрлица для оплаты; null — основной счёт юрлица.</summary>
    public long? BankAccountId { get; private set; }

    /// <summary>Ответственный сотрудник (пользователь организации); по умолчанию — автор.</summary>
    public long? ResponsibleUserId { get; private set; }

    /// <summary>Этап работы с заказом из справочника организации (D75); null — этап не выбран.</summary>
    public long? StageId { get; private set; }

    /// <summary>
    /// D117: сделка CRM, из которой создан заказ (модуль CRM, D600). Связь ставит и снимает только сервис CRM;
    /// внешний ключ на таблицу сделок добавляет миграция модуля CRM.
    /// </summary>
    public long? CrmDealId { get; private set; }

    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? ConfirmedByUserId { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<SalesOrderLine> Lines => _lines;

    public decimal Total => _lines.Sum(l => l.Amount);
    public decimal VatTotal => _lines.Sum(l => l.VatAmount);

    public static SalesOrder Create(long organizationId, string number, SalesOrderHeader header, long userId, DateTime nowUtc)
    {
        var order = new SalesOrder
        {
            OrganizationId = organizationId,
            Number = number,
            Status = SalesOrderStatus.Draft,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ResponsibleUserId = userId,
        };
        order.ApplyHeader(header);
        return order;
    }

    public void UpdateHeader(SalesOrderHeader header)
    {
        EnsureDraft();
        var vatModeChanged = header.PricesIncludeVat != PricesIncludeVat;
        ApplyHeader(header);
        if (vatModeChanged)
        {
            foreach (var line in _lines)
            {
                line.Recalculate(PricesIncludeVat);
            }
        }
    }

    /// <summary>Строка по позиции: новая или замена. vatPercent null — «без НДС»; discountPercent — скидка строки, 0–100 %.</summary>
    public void SetLine(long itemId, decimal quantity, decimal price, decimal? vatPercent, decimal discountPercent = 0)
    {
        EnsureDraft();
        EnsureDiscount(discountPercent);
        Quantities.EnsurePositive(quantity);
        if (price < 0 || decimal.Round(price, 4) != price)
        {
            throw new BusinessRuleException("sales.price", "Цена — не меньше нуля, не больше четырёх знаков после запятой.");
        }

        if (vatPercent is { } p && (p < 0 || p > 100 || decimal.Round(p, 2) != p))
        {
            throw new BusinessRuleException("sales.vat", "Ставка НДС — от 0 до 100%.");
        }

        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is null)
        {
            if (_lines.Count >= MaxLines)
            {
                throw new BusinessRuleException("sales.too_many_lines", $"В заказе не больше {MaxLines} строк.");
            }

            line = new SalesOrderLine(itemId);
            _lines.Add(line);
        }

        line.Set(quantity, price, vatPercent, discountPercent, PricesIncludeVat);
    }

    /// <summary>Скидка на весь заказ: один процент всем строкам черновика (как кнопка «Скидка» в МойСклад).</summary>
    public void SetDiscount(decimal discountPercent)
    {
        EnsureDraft();
        EnsureDiscount(discountPercent);
        foreach (var line in _lines)
        {
            line.Set(line.Quantity, line.Price, line.VatPercent, discountPercent, PricesIncludeVat);
        }
    }

    /// <summary>Сумма скидки по заказу — разница между суммой по ценам без скидки и итогом.</summary>
    public decimal DiscountTotal => _lines.Sum(l => l.DiscountAmount);

    public void SetReserve(bool reserve)
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
        {
            throw new BusinessRuleException("sales.reserve.status", "Резерв ставится и снимается в черновике или подтверждённом заказе.");
        }

        Reserve = reserve;
    }

    private static void EnsureDiscount(decimal discountPercent)
    {
        if (discountPercent < 0 || discountPercent > 100 || decimal.Round(discountPercent, 2) != discountPercent)
        {
            throw new BusinessRuleException("sales.discount", "Скидка — от 0 до 100%, до сотых.");
        }
    }

    public void RemoveLine(long itemId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId)
                   ?? throw new BusinessRuleException("sales.line_missing", "Такой строки в заказе нет.");
        _lines.Remove(line);
    }

    public const int AddressMaxLength = 500;

    /// <summary>
    /// Детали заказа (D77): время, проект, канал, адрес доставки, ответственный, расчётный счёт. На учёт не влияют, поэтому меняются
    /// в любом состоянии, кроме отменённого (ссылки проверяет сервис).
    /// </summary>
    public void SetDetails(SalesOrderDetails details)
    {
        if (Status == SalesOrderStatus.Cancelled)
        {
            throw new BusinessRuleException("sales.order.cancelled", "Заказ отменён — изменить нельзя.");
        }

        OrderTime = details.OrderTime is { } t ? new TimeOnly(t.Hour, t.Minute) : null;
        ProjectId = details.ProjectId;
        ChannelId = details.ChannelId;
        DeliveryAddress = DomainText.Optional(details.DeliveryAddress, AddressMaxLength, "Адрес доставки");
        ResponsibleUserId = details.ResponsibleUserId;
        BankAccountId = details.BankAccountId;
    }

    /// <summary>Этап меняется в любом состоянии, кроме отменённого: это метка работы, а не учёт.</summary>
    /// <summary>D117: связь заказа со сделкой CRM; null — снять. Не меняет статус и строки заказа.</summary>
    public void LinkCrmDeal(long? dealId)
    {
        if (dealId is <= 0)
        {
            throw new BusinessRuleException("sales.order.crm_deal", "Неверная сделка.");
        }

        CrmDealId = dealId;
    }

    public void SetStage(long? stageId)
    {
        if (Status == SalesOrderStatus.Cancelled)
        {
            throw new BusinessRuleException("sales.order.cancelled", "Заказ отменён — этап не меняется.");
        }

        StageId = stageId;
    }

    public void Confirm(long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("sales.empty", "В заказе нет строк.");
        }

        Status = SalesOrderStatus.Confirmed;
        ConfirmedByUserId = userId;
        ConfirmedAtUtc = nowUtc;
    }

    /// <summary>Закрыть: больше отгрузок по заказу не будет (отгружен полностью или остаток не нужен).</summary>
    public void Close()
    {
        if (Status != SalesOrderStatus.Confirmed)
        {
            throw new BusinessRuleException("sales.status", "Закрыть можно только подтверждённый заказ.");
        }

        Status = SalesOrderStatus.Closed;
    }

    /// <summary>Вернуть закрытый заказ в работу — например, покупатель всё-таки забирает остаток.</summary>
    public void Reopen()
    {
        if (Status != SalesOrderStatus.Closed)
        {
            throw new BusinessRuleException("sales.status", "Вернуть в работу можно только закрытый заказ.");
        }

        Status = SalesOrderStatus.Confirmed;
    }

    /// <summary>Отмена. По подтверждённому заказу — только если по нему нет проведённых документов и оплат (проверяет сервис).</summary>
    public void Cancel()
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
        {
            throw new BusinessRuleException("sales.status", "Отменить можно черновик или подтверждённый заказ.");
        }

        Status = SalesOrderStatus.Cancelled;
    }

    /// <summary>Цена единицы с НДС — по ней оценивается отгрузка и возврат по заказу.</summary>
    public decimal UnitCostWithVat(long itemId) =>
        _lines.FirstOrDefault(l => l.ItemId == itemId) is { } l && l.Quantity != 0 ? l.Amount / l.Quantity : 0m;

    public static string StatusName(SalesOrderStatus status) => status switch
    {
        SalesOrderStatus.Draft => "Черновик",
        SalesOrderStatus.Confirmed => "Подтверждён",
        SalesOrderStatus.Closed => "Закрыт",
        SalesOrderStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };

    private void ApplyHeader(SalesOrderHeader h)
    {
        if (h.ShipDate is { } expected && expected < h.OrderDate)
        {
            throw new BusinessRuleException("sales.expected_date", "Дата отгрузки раньше даты заказа.");
        }

        OrderDate = h.OrderDate;
        CustomerId = h.CustomerId;
        WarehouseId = h.WarehouseId;
        ShipDate = h.ShipDate;
        CustomerReference = DomainText.Optional(h.CustomerReference, InvoiceMaxLength, "Договор покупателя");
        PricesIncludeVat = h.PricesIncludeVat;
        Comment = DomainText.Optional(h.Comment, CommentMaxLength, "Комментарий");
        if (h.LegalEntityId is { } entity && entity != LegalEntityId)
        {
            // Другое юрлицо — свой расчётный счёт: прежний сбрасывается на основной счёт нового юрлица.
            LegalEntityId = entity;
            BankAccountId = null;
        }
    }

    private void EnsureDraft()
    {
        if (Status != SalesOrderStatus.Draft)
        {
            throw new BusinessRuleException("sales.not_draft", "Заказ уже подтверждён — менять его нельзя. Нужны другие условия — отмените и оформите новый.");
        }
    }
}

/// <summary>Детали заказа (D77). BankAccountId — расчётный счёт своего юрлица (D78), null — основной.</summary>
public sealed record SalesOrderDetails(
    TimeOnly? OrderTime, long? ProjectId, long? ChannelId, string? DeliveryAddress, long? ResponsibleUserId, long? BankAccountId = null);

/// <summary>Шапка заказа. LegalEntityId — своё юрлицо-продавец (D78); null — не менять (у нового заказа сервис ставит основное).</summary>
public sealed record SalesOrderHeader(
    DateOnly OrderDate, long CustomerId, long WarehouseId, DateOnly? ShipDate, string? CustomerReference, bool PricesIncludeVat, string? Comment,
    long? LegalEntityId = null);

/// <summary>Строка заказа: количество, цена, процент НДС; сумма и НДС считаются и хранятся с округлением до копеек.</summary>
public sealed class SalesOrderLine
{
    private SalesOrderLine()
    {
    }

    internal SalesOrderLine(long itemId) => ItemId = itemId;

    public long Id { get; private set; }
    public long OrderId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Price { get; private set; }

    /// <summary>Процент НДС; null — без НДС.</summary>
    public decimal? VatPercent { get; private set; }

    /// <summary>Скидка строки, % (D76). Цена — до скидки; сумма и НДС — по цене со скидкой.</summary>
    public decimal DiscountPercent { get; private set; }

    /// <summary>Сумма строки с НДС.</summary>
    public decimal Amount { get; private set; }

    public decimal VatAmount { get; private set; }

    /// <summary>Цена со скидкой (не округляется: округляется сумма строки).</summary>
    public decimal NetPrice => Price * (1 - DiscountPercent / 100m);

    /// <summary>Скидка в деньгах: сумма по цене без скидки минус сумма строки (в той же базе — с НДС или без).</summary>
    public decimal DiscountAmount => DiscountPercent == 0 ? 0 : Money.Round(Quantity * Price) - Money.Round(Quantity * NetPrice);

    internal void Set(decimal quantity, decimal price, decimal? vatPercent, decimal discountPercent, bool pricesIncludeVat)
    {
        Quantity = quantity;
        Price = price;
        VatPercent = vatPercent;
        DiscountPercent = discountPercent;
        Recalculate(pricesIncludeVat);
    }

    internal void Recalculate(bool pricesIncludeVat) =>
        (Amount, VatAmount) = Money.LineAmounts(Quantity, NetPrice, VatPercent, pricesIncludeVat);
}

public enum CustomerPaymentStatus : byte
{
    Posted = 2,
    Cancelled = 9,
}

/// <summary>
/// Оплата от покупателя (D65): дата, покупатель, сумма, при желании — заказ. Уменьшает долг покупателя сразу.
/// Ошибочная оплата не удаляется, а отменяется с причиной — в истории остаются обе записи.
/// </summary>
public sealed class CustomerPayment
{
    public const string NumberPrefix = "ПО";
    public const int CommentMaxLength = 500;
    public const int DocumentNumberMaxLength = 30;

    private CustomerPayment()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly PaymentDate { get; private set; }
    public long CustomerId { get; private set; }
    public long? SalesOrderId { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>
    /// Номер платёжного документа покупателя (платёжного поручения); дата — дата оплаты. Печатается в строке 5 счёта-фактуры
    /// при отгрузке в счёт предоплаты (подп. «з» п. 1 Правил заполнения, ПП № 1137).
    /// </summary>
    public string? DocumentNumber { get; private set; }

    /// <summary>Расчётный счёт или касса своего юрлица, куда пришли (откуда ушли) деньги (D80).</summary>
    public long? MoneyAccountId { get; private set; }

    /// <summary>
    /// Номер кассового ордера (D85): оплата наличными через кассу получает номер из общей нумерации ПКО или РКО организации,
    /// чтобы приходные и расходные ордера кассы шли подряд вместе с прочими кассовыми операциями.
    /// </summary>
    public string? CashOrderNumber { get; private set; }

    public string? Comment { get; private set; }
    public CustomerPaymentStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CustomerPayment Create(
        long organizationId, string number, DateOnly date, long customerId, long? orderId, decimal amount, string? comment, long userId, DateTime nowUtc,
        string? documentNumber = null, long? moneyAccountId = null)
    {
        if (amount <= 0 || Money.Round(amount) != amount)
        {
            throw new BusinessRuleException("sales.payment.amount", "Сумма оплаты — больше нуля, до копеек.");
        }

        return new CustomerPayment
        {
            DocumentNumber = DomainText.Optional(documentNumber, DocumentNumberMaxLength, "№ платёжного документа"),
            OrganizationId = organizationId,
            Number = number,
            PaymentDate = date,
            CustomerId = customerId,
            SalesOrderId = orderId,
            MoneyAccountId = moneyAccountId,
            Amount = amount,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
            Status = CustomerPaymentStatus.Posted,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    public void AssignCashOrderNumber(string number)
    {
        if (CashOrderNumber is not null)
        {
            throw new InvalidOperationException("Номер кассового ордера уже присвоен.");
        }

        CashOrderNumber = number;
    }

    public void Cancel(long userId, string? reason, DateTime nowUtc)
    {
        if (Status != CustomerPaymentStatus.Posted)
        {
            throw new BusinessRuleException("sales.payment.cancelled", "Оплата уже отменена.");
        }

        CancelReason = DomainText.Require(reason, CommentMaxLength, "Причина отмены");
        Status = CustomerPaymentStatus.Cancelled;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }
}
