using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Sales;

public enum SalesAgreementStatus : byte
{
    Active = 1,
    Archived = 2,
}

/// <summary>Кому адресовано соглашение (D1101): одному покупателю, покупателям с тегом (группа) или всем.</summary>
public enum SalesAgreementScope : byte
{
    Customer = 1,
    Group = 2,
    Everyone = 3,
}

/// <summary>
/// Условия соглашения. CounterpartyId — для одного покупателя, TagId — для группы (тег контрагента), оба null — типовое для всех.
/// PriceTypeId null — вид цены не меняется (берётся из карточки клиента или основной). ContractId — договор, с которым связано
/// соглашение: из него берутся отсрочка и лимит, если в соглашении они не заданы.
/// </summary>
public sealed record SalesAgreementTerms(
    string Name, long? CounterpartyId, long? TagId, long? PriceTypeId, decimal DiscountPercent, DateOnly ValidFrom, DateOnly? ValidTo,
    decimal? PrepaymentPercent, int? PaymentDays, decimal? CreditLimit, long? WarehouseId, long? ContractId, string? Comment);

/// <summary>
/// Соглашение с покупателем (D1101): вид цены, скидка, срок действия, условия оплаты (предоплата, отсрочка, лимит), склад
/// по умолчанию. Действует по датам; не удаляется — уходит в архив. История изменений — журнал аудита по соглашению.
/// Порядок выбора для заказа: соглашение покупателя → соглашение группы → типовое; при нескольких — с поздней датой начала.
/// </summary>
public sealed class SalesAgreement
{
    public const string NumberPrefix = "СГ";
    public const int NameMaxLength = 200;
    public const int CommentMaxLength = 1000;
    public const int MaxPaymentDays = 365;

    private SalesAgreement()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public long? CounterpartyId { get; private set; }
    public long? TagId { get; private set; }
    public long? PriceTypeId { get; private set; }

    /// <summary>Скидка соглашения, % — ко всем строкам заказа, до сотых.</summary>
    public decimal DiscountPercent { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    /// <summary>null — бессрочно.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Предоплата, % суммы заказа; null — не требуется.</summary>
    public decimal? PrepaymentPercent { get; private set; }

    /// <summary>Отсрочка оплаты, календарных дней от отгрузки; null — как в договоре.</summary>
    public int? PaymentDays { get; private set; }

    /// <summary>Кредитный лимит (долг + заказ), в валюте организации; null — как в договоре.</summary>
    public decimal? CreditLimit { get; private set; }

    public long? WarehouseId { get; private set; }
    public long? ContractId { get; private set; }
    public string? Comment { get; private set; }
    public SalesAgreementStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public SalesAgreementScope Scope =>
        CounterpartyId is not null ? SalesAgreementScope.Customer : TagId is not null ? SalesAgreementScope.Group : SalesAgreementScope.Everyone;

    public static SalesAgreement Create(long organizationId, string number, SalesAgreementTerms terms, long userId, DateTime nowUtc)
    {
        var a = new SalesAgreement
        {
            OrganizationId = organizationId,
            Number = number,
            Status = SalesAgreementStatus.Active,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        a.Apply(terms);
        return a;
    }

    /// <summary>Изменение условий; возвращает список изменённых полей для журнала (история соглашения).</summary>
    public IReadOnlyList<FieldChange> Update(SalesAgreementTerms terms)
    {
        if (Status == SalesAgreementStatus.Archived)
        {
            throw new BusinessRuleException("sales.agreement.archived", "Соглашение в архиве. Сначала верните его.");
        }

        var before = Snapshot();
        Apply(terms);
        var after = Snapshot();
        return before.Zip(after).Where(p => p.First.Value != p.Second.Value)
            .Select(p => new FieldChange(p.First.Field, p.First.Value, p.Second.Value)).ToList();
    }

    public void SetArchived(bool archived) => Status = archived ? SalesAgreementStatus.Archived : SalesAgreementStatus.Active;

    /// <summary>Действует на дату: не в архиве и дата внутри срока.</summary>
    public bool IsValidOn(DateOnly date) =>
        Status == SalesAgreementStatus.Active && ValidFrom <= date && (ValidTo is null || date <= ValidTo);

    public static void EnsurePercent(decimal? value, string field)
    {
        if (value is { } v && (v < 0 || v > 100 || decimal.Round(v, 2) != v))
        {
            throw new BusinessRuleException("sales.agreement.percent", $"{field} — от 0 до 100%, до сотых.");
        }
    }

    public static string ScopeName(SalesAgreementScope scope) => scope switch
    {
        SalesAgreementScope.Customer => "Покупатель",
        SalesAgreementScope.Group => "Группа покупателей",
        _ => "Все покупатели",
    };

    private void Apply(SalesAgreementTerms t)
    {
        if (t.CounterpartyId is not null && t.TagId is not null)
        {
            throw new BusinessRuleException("sales.agreement.scope", "Соглашение — либо для покупателя, либо для группы покупателей.");
        }

        if (t.ValidTo is { } to && to < t.ValidFrom)
        {
            throw new BusinessRuleException("sales.agreement.period", "Дата окончания соглашения раньше даты начала.");
        }

        EnsurePercent(t.DiscountPercent, "Скидка");
        EnsurePercent(t.PrepaymentPercent, "Предоплата");
        if (t.PaymentDays is < 0 or > MaxPaymentDays)
        {
            throw new BusinessRuleException("sales.agreement.payment_days", $"Отсрочка оплаты — от 0 до {MaxPaymentDays} дней.");
        }

        if (t.CreditLimit is { } limit && (limit < 0 || limit > 999_999_999_999m))
        {
            throw new BusinessRuleException("sales.agreement.limit", "Кредитный лимит не может быть отрицательным.");
        }

        Name = DomainText.Require(t.Name, NameMaxLength, "Название соглашения");
        CounterpartyId = t.CounterpartyId;
        TagId = t.TagId;
        PriceTypeId = t.PriceTypeId;
        DiscountPercent = t.DiscountPercent;
        ValidFrom = t.ValidFrom;
        ValidTo = t.ValidTo;
        PrepaymentPercent = t.PrepaymentPercent;
        PaymentDays = t.PaymentDays;
        CreditLimit = t.CreditLimit is { } l ? Money.Round(l) : null;
        WarehouseId = t.WarehouseId;
        ContractId = t.ContractId;
        Comment = DomainText.Optional(t.Comment, CommentMaxLength, "Комментарий");
    }

    private (string Field, string? Value)[] Snapshot() =>
    [
        ("Название", Name),
        ("Покупатель", CounterpartyId?.ToString()),
        ("Группа (тег)", TagId?.ToString()),
        ("Вид цены", PriceTypeId?.ToString()),
        ("Скидка, %", DiscountPercent.ToString("0.##")),
        ("Действует с", ValidFrom.ToString("dd.MM.yyyy")),
        ("Действует по", ValidTo?.ToString("dd.MM.yyyy")),
        ("Предоплата, %", PrepaymentPercent?.ToString("0.##")),
        ("Отсрочка, дней", PaymentDays?.ToString()),
        ("Кредитный лимит", CreditLimit?.ToString("0.00")),
        ("Склад", WarehouseId?.ToString()),
        ("Договор", ContractId?.ToString()),
        ("Комментарий", Comment),
    ];
}

/// <summary>
/// Условия продаж покупателя (D1102) — «карточка клиента» модуля продаж: вид цены клиента и закреплённый менеджер.
/// Хранится в продажах, а не в контрагенте: справочник контрагентов принадлежит каталогу.
/// </summary>
public sealed class CustomerSalesProfile
{
    private CustomerSalesProfile()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long CounterpartyId { get; private set; }
    public long? PriceTypeId { get; private set; }
    public long? ManagerUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CustomerSalesProfile Create(long organizationId, long counterpartyId) =>
        new() { OrganizationId = organizationId, CounterpartyId = counterpartyId };

    public void Set(long? priceTypeId, long? managerUserId)
    {
        PriceTypeId = priceTypeId;
        ManagerUserId = managerUserId;
    }
}

/// <summary>Какие заказы видит пользователь с ролью (D1110): свои, своего подразделения, все.</summary>
public enum SalesOrderVisibility : byte
{
    All = 1,
    Department = 2,
    Own = 3,
}

/// <summary>
/// Настройки продаж для роли (D1103): максимальная скидка от цены по соглашению, вид цены — нижняя граница, видимость заказов.
/// Нет записи — ограничений нет и видны все заказы (как до этого шага). У пользователя с несколькими ролями действует самое
/// мягкое ограничение среди ролей, у которых оно задано (как объединение прав).
/// </summary>
public sealed class SalesRoleSettings
{
    private SalesRoleSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long RoleId { get; private set; }

    /// <summary>Наибольшая ручная скидка от цены по соглашению (после скидки соглашения), %; null — без ограничения.</summary>
    public decimal? MaxDiscountPercent { get; private set; }

    /// <summary>Вид цены — нижняя граница цены продажи для роли (например, «Крупный опт»); null — только минимальная цена позиции.</summary>
    public long? MinPriceTypeId { get; private set; }

    public SalesOrderVisibility Visibility { get; private set; } = SalesOrderVisibility.All;
    public byte[] RowVersion { get; private set; } = [];

    public static SalesRoleSettings Create(long organizationId, long roleId) =>
        new() { OrganizationId = organizationId, RoleId = roleId };

    public void Set(decimal? maxDiscountPercent, long? minPriceTypeId, SalesOrderVisibility visibility)
    {
        SalesAgreement.EnsurePercent(maxDiscountPercent, "Максимальная скидка");
        if (!Enum.IsDefined(visibility))
        {
            throw new BusinessRuleException("sales.role.visibility", "Выберите, какие заказы видит роль.");
        }

        MaxDiscountPercent = maxDiscountPercent;
        MinPriceTypeId = minPriceTypeId;
        Visibility = visibility;
    }

    public static string VisibilityName(SalesOrderVisibility v) => v switch
    {
        SalesOrderVisibility.Own => "Свои",
        SalesOrderVisibility.Department => "Своего подразделения",
        _ => "Все",
    };
}
