using System.Globalization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Вид продукции для маркировки «Честный ЗНАК» (D305). Числа хранятся в базе; код МойСклад (trackingType) — <see cref="Marking.MoySkladCode"/>.
/// Трикотаж и одежда — «Предметы одежды, бельё» (LP_CLOTHES) и «Постельное, столовое, туалетное бельё» (LP_LINENS).
/// </summary>
public enum TrackingType : byte
{
    None = 0,
    Clothes = 1,
    Linens = 2,
    Shoes = 3,
    Other = 9,
}

/// <summary>Сведения для торговли: предмет расчёта (54-ФЗ), маркировка и минимальная цена продажи (D305).</summary>
public sealed record ItemTradeDetails(PaymentSubject PaymentSubject, bool IsMarked, TrackingType TrackingType, string? Gtin, decimal? MinPrice);

public static class Marking
{
    public static readonly IReadOnlyList<TrackingType> All = [TrackingType.None, TrackingType.Clothes, TrackingType.Linens, TrackingType.Shoes, TrackingType.Other];

    public static string Name(TrackingType type) => type switch
    {
        TrackingType.Clothes => "Предметы одежды (лёгкая промышленность)",
        TrackingType.Linens => "Постельное, столовое и туалетное бельё",
        TrackingType.Shoes => "Обувь",
        TrackingType.Other => "Другой вид маркировки",
        _ => "Не маркируется",
    };

    /// <summary>Значение trackingType в JSON API МойСклад 1.2.</summary>
    public static string MoySkladCode(TrackingType type) => type switch
    {
        TrackingType.Clothes => "LP_CLOTHES",
        TrackingType.Linens => "LP_LINENS",
        TrackingType.Shoes => "SHOES",
        TrackingType.Other => "OTP",
        _ => "NOT_TRACKED",
    };

    public static TrackingType FromMoySklad(string? code) => code switch
    {
        "LP_CLOTHES" => TrackingType.Clothes,
        "LP_LINENS" => TrackingType.Linens,
        "SHOES" => TrackingType.Shoes,
        null or "" or "NOT_TRACKED" => TrackingType.None,
        _ => TrackingType.Other,
    };

    /// <summary>
    /// GTIN для маркировки — 14 цифр с верной контрольной цифрой (веса 3 и 1 справа налево). EAN-13 дополняется ведущим нулём
    /// до GTIN-14, как в Национальном каталоге. Пробелы убираются; пусто — null.
    /// </summary>
    public static string? NormalizeGtin(string? value)
    {
        var v = value?.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (v.Length == 13 && v.All(char.IsAsciiDigit))
        {
            v = "0" + v;
        }

        if (v.Length != 14 || !v.All(char.IsAsciiDigit) || !CheckDigitValid(v))
        {
            throw new BusinessRuleException("catalog.gtin.invalid", $"GTIN «{value}» неверный: нужно 14 цифр (или EAN-13) с верной контрольной цифрой.");
        }

        return v;
    }

    public static bool CheckDigitValid(string digits)
    {
        var sum = 0;
        for (int i = digits.Length - 2, w = 3; i >= 0; i--, w = 4 - w)
        {
            sum += (digits[i] - '0') * w;
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}

public static class TradeRules
{
    /// <summary>
    /// Проверка и приведение (D305): услуга — предмет расчёта «услуга» и без маркировки; маркируемый товар — с видом продукции;
    /// GTIN — по <see cref="Marking.NormalizeGtin"/>; минимальная цена — не меньше нуля, до 4 знаков.
    /// </summary>
    public static ItemTradeDetails Normalize(ItemType type, ItemTradeDetails d)
    {
        if (!Enum.IsDefined(d.PaymentSubject))
        {
            throw new BusinessRuleException("catalog.payment_subject", "Выберите признак предмета расчёта.");
        }

        if (!Marking.All.Contains(d.TrackingType))
        {
            throw new BusinessRuleException("catalog.tracking_type", "Выберите вид продукции для маркировки.");
        }

        if (type == ItemType.Service)
        {
            if (d.IsMarked || d.Gtin is not null)
            {
                throw new BusinessRuleException("catalog.service.marking", "Услуга не маркируется — снимите признак маркировки и GTIN.");
            }

            d = d with { PaymentSubject = PaymentSubject.Service, TrackingType = TrackingType.None };
        }
        else if (d.PaymentSubject == PaymentSubject.Service)
        {
            throw new BusinessRuleException("catalog.payment_subject.goods", "Признак «услуга» — только у услуг. Для товара выберите «товар» или «подакцизный товар».");
        }

        if (d.IsMarked && d.TrackingType == TrackingType.None)
        {
            throw new BusinessRuleException("catalog.marking.type_required", "Для маркируемого товара выберите вид продукции «Честного ЗНАКа».");
        }

        var minPrice = d.MinPrice;
        if (minPrice is { } m && (m < 0 || decimal.Round(m, 4) != m || m > 999_999_999_999m))
        {
            throw new BusinessRuleException("catalog.min_price", "Минимальная цена — не меньше нуля, до четырёх знаков после запятой.");
        }

        return d with { Gtin = Marking.NormalizeGtin(d.Gtin), TrackingType = d.IsMarked ? d.TrackingType : TrackingType.None };
    }

    /// <summary>Цена ниже минимальной — предупреждение (продавать можно, но с согласием).</summary>
    public static bool BelowMinimum(decimal price, decimal? minPrice) => minPrice is { } m && price < m;

    public static string PaymentSubjectName(PaymentSubject subject) => subject switch
    {
        PaymentSubject.Service => "Услуга",
        PaymentSubject.ExcisableGoods => "Подакцизный товар",
        _ => "Товар",
    };

    public static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>Откуда изменение цены (D305): вручную, переоценкой, загрузкой из Excel, из внешней системы, при создании модификаций.</summary>
public enum PriceChangeSource : byte
{
    Manual = 1,
    Revaluation = 2,
    Import = 3,
    External = 4,
    Modification = 5,
}

/// <summary>
/// История цен (D305): каждое изменение цены позиции по виду цены — отдельная запись; только вставка (триггер базы).
/// OldPrice null — цены не было, NewPrice null — цену убрали.
/// </summary>
public sealed class ItemPriceHistory
{
    private ItemPriceHistory()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public long PriceTypeId { get; private set; }
    public decimal? OldPrice { get; private set; }
    public decimal? NewPrice { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }
    public long? UserId { get; private set; }
    public PriceChangeSource Source { get; private set; }
    public long? RevaluationId { get; private set; }

    public static ItemPriceHistory Create(long organizationId, long itemId, long priceTypeId, decimal? oldPrice, decimal? newPrice, DateTime nowUtc,
        long? userId, PriceChangeSource source, long? revaluationId = null) =>
        new()
        {
            OrganizationId = organizationId, ItemId = itemId, PriceTypeId = priceTypeId, OldPrice = oldPrice, NewPrice = newPrice,
            ChangedAtUtc = nowUtc, UserId = userId, Source = source, RevaluationId = revaluationId,
        };
}

/// <summary>Правило массовой переоценки (D306).</summary>
public enum RevaluationMode : byte
{
    /// <summary>Текущая цена выбранного вида ± N %.</summary>
    Percent = 1,

    /// <summary>Закупочная цена × коэффициент (наценка от закупки).</summary>
    FromPurchase = 2,
}

/// <summary>Шаг округления новой цены: до копеек, рубля, 10 или 100 рублей (по правилам округления от середины — вверх).</summary>
public enum PriceRounding : byte
{
    Kopeck = 0,
    Ruble = 1,
    Ten = 2,
    Hundred = 3,
}

/// <summary>Журнал массовой переоценки (D306): кто, когда, вид цены, правило и сколько позиций изменено. Только вставка.</summary>
public sealed class PriceRevaluation
{
    public const int RuleMaxLength = 300;

    private PriceRevaluation()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PriceTypeId { get; private set; }
    public RevaluationMode Mode { get; private set; }
    public decimal Value { get; private set; }
    public PriceRounding Rounding { get; private set; }
    public string Rule { get; private set; } = string.Empty;
    public int Items { get; private set; }
    public long UserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static PriceRevaluation Create(long organizationId, long priceTypeId, RevaluationMode mode, decimal value, PriceRounding rounding, string rule,
        int items, long userId, DateTime nowUtc) =>
        new()
        {
            OrganizationId = organizationId, PriceTypeId = priceTypeId, Mode = mode, Value = value, Rounding = rounding,
            Rule = rule.Length > RuleMaxLength ? rule[..RuleMaxLength] : rule, Items = items, UserId = userId, CreatedAtUtc = nowUtc,
        };
}

/// <summary>Расчёт массовой переоценки (D306) — чистая функция.</summary>
public static class Revaluation
{
    public const decimal MaxPercent = 1000m;
    public const decimal MaxFactor = 100m;

    public static void Validate(RevaluationMode mode, decimal value)
    {
        switch (mode)
        {
            case RevaluationMode.Percent when value <= -100 || value > MaxPercent || value == 0:
                throw new BusinessRuleException("catalog.revaluation.percent", $"Процент переоценки — от −99,99 до {MaxPercent:0}, не ноль.");
            case RevaluationMode.FromPurchase when value <= 0 || value > MaxFactor:
                throw new BusinessRuleException("catalog.revaluation.factor", $"Коэффициент к закупочной цене — больше нуля и не больше {MaxFactor:0}.");
            case RevaluationMode.Percent or RevaluationMode.FromPurchase:
                return;
            default:
                throw new BusinessRuleException("catalog.revaluation.mode", "Выберите правило переоценки.");
        }
    }

    /// <summary>Новая цена или null, если считать не от чего (нет текущей цены или закупочной цены).</summary>
    public static decimal? NewPrice(RevaluationMode mode, decimal value, PriceRounding rounding, decimal? currentPrice, decimal? purchasePrice)
    {
        decimal? raw = mode switch
        {
            RevaluationMode.Percent => currentPrice * (1 + value / 100m),
            RevaluationMode.FromPurchase => purchasePrice * value,
            _ => null,
        };
        return raw is { } r ? Round(r, rounding) : null;
    }

    public static decimal Round(decimal price, PriceRounding rounding)
    {
        var step = rounding switch
        {
            PriceRounding.Ruble => 1m,
            PriceRounding.Ten => 10m,
            PriceRounding.Hundred => 100m,
            _ => 0.01m,
        };
        return Math.Max(0, decimal.Round(price / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    public static string Describe(RevaluationMode mode, decimal value, PriceRounding rounding)
    {
        var rule = mode == RevaluationMode.Percent
            ? $"{(value > 0 ? "+" : "")}{value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"))} % к текущей цене"
            : $"закупочная цена × {value.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))}";
        var round = rounding switch
        {
            PriceRounding.Ruble => "до рубля",
            PriceRounding.Ten => "до 10",
            PriceRounding.Hundred => "до 100",
            _ => "до копеек",
        };
        return $"{rule}, округление {round}";
    }
}
