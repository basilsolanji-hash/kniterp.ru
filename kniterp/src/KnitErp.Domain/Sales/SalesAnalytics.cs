using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>
/// План продаж менеджера на месяц (D1162): сумма выручки без НДС, с которой сравнивается факт — отгрузки минус возвраты по ценам
/// заказов, где менеджер ответственный. Одна запись на (организация, месяц, менеджер); «снять план» — сумма 0.
/// </summary>
public sealed class SalesPlan
{
    public const decimal MaxAmount = 999_999_999_999m;

    private SalesPlan()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Первое число месяца.</summary>
    public DateOnly Month { get; private set; }

    public long ManagerUserId { get; private set; }
    public decimal Amount { get; private set; }
    public long UpdatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    public static SalesPlan Create(long organizationId, DateOnly month, long managerUserId) =>
        new() { OrganizationId = organizationId, Month = MonthOf(month), ManagerUserId = managerUserId };

    public void Set(decimal amount, long userId, DateTime nowUtc)
    {
        if (amount < 0 || amount > MaxAmount || Money.Round(amount) != amount)
        {
            throw new BusinessRuleException("sales.plan.amount", "План — сумма от нуля, до копеек.");
        }

        Amount = amount;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }
}

/// <summary>Площадка канала продаж (D1107). Числа хранятся в базе.</summary>
public enum MarketplaceKind : byte
{
    Wildberries = 1,
    Ozon = 2,
    Other = 9,
}

/// <summary>Схема работы с маркетплейсом (D1107): FBS — со своего склада, FBO — со склада маркетплейса.</summary>
public enum FulfillmentScheme : byte
{
    Fbs = 1,
    Fbo = 2,
}

/// <summary>
/// Свойства канала продаж (D1107): канал — маркетплейс (Wildberries, Ozon, иной), схема FBS/FBO и контрагент-маркетплейс
/// (комиссионер). Канал — запись справочника каналов (<c>Lookup</c> вида «Канал продаж»); нет записи — обычный канал.
/// </summary>
public sealed class SalesChannelSettings
{
    private SalesChannelSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ChannelId { get; private set; }
    public MarketplaceKind? Marketplace { get; private set; }
    public FulfillmentScheme? Scheme { get; private set; }

    /// <summary>Маркетплейс как контрагент (комиссионер), по которому идут взаиморасчёты; null — не указан.</summary>
    public long? CounterpartyId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static SalesChannelSettings Create(long organizationId, long channelId) => new() { OrganizationId = organizationId, ChannelId = channelId };

    public void Set(MarketplaceKind? marketplace, FulfillmentScheme? scheme, long? counterpartyId)
    {
        if (marketplace is { } m && !Enum.IsDefined(m))
        {
            throw new BusinessRuleException("sales.channel.marketplace", "Выберите площадку из списка.");
        }

        if (scheme is { } s && !Enum.IsDefined(s))
        {
            throw new BusinessRuleException("sales.channel.scheme", "Выберите схему FBS или FBO.");
        }

        if (marketplace is null && (scheme is not null || counterpartyId is not null))
        {
            throw new BusinessRuleException("sales.channel.not_marketplace", "Схема и контрагент указываются только для канала-маркетплейса.");
        }

        Marketplace = marketplace;
        Scheme = scheme;
        CounterpartyId = counterpartyId;
    }

    public static string MarketplaceName(MarketplaceKind kind) => kind switch
    {
        MarketplaceKind.Wildberries => "Wildberries",
        MarketplaceKind.Ozon => "Ozon",
        _ => "Иной маркетплейс",
    };

    public static string SchemeName(FulfillmentScheme scheme) => scheme == FulfillmentScheme.Fbo ? "FBO" : "FBS";
}
