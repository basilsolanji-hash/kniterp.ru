using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Вид скидки (D1106). Числа хранятся в базе — не перенумеровывать.</summary>
public enum DiscountKind : byte
{
    /// <summary>Скидка соглашения с покупателем (D1101).</summary>
    Agreement = 1,

    /// <summary>По сумме заказа: заказ не меньше порога — скидка всем строкам.</summary>
    OrderAmount = 2,

    /// <summary>По количеству: строка не меньше порога (штук) — скидка строке.</summary>
    Quantity = 3,

    /// <summary>Акция на группу позиций с датами.</summary>
    Promotion = 4,

    /// <summary>Ручная скидка строки или заказа.</summary>
    Manual = 9,
}

/// <summary>Как сочетаются скидки (D1106): последовательно по порядку применения или только наибольшая.</summary>
public enum DiscountCombineMode : byte
{
    /// <summary>Скидки применяются одна за другой в заданном порядке: цена × (1 − a) × (1 − b)…</summary>
    Sequential = 1,

    /// <summary>Скидки не суммируются: действует наибольшая (ручная — сверх неё).</summary>
    Best = 2,
}

/// <summary>
/// Правило скидки (D1106): по сумме заказа, по количеству или акция на группу позиций; срок действия, необязательная группа покупателей (тег).
/// Не удаляется — уходит в архив.
/// </summary>
public sealed class SalesDiscountRule
{
    public const int NameMaxLength = 200;

    private SalesDiscountRule()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public DiscountKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Percent { get; private set; }

    /// <summary>Порог суммы заказа (по ценам до скидок, с НДС как в заказе) — для скидки по сумме.</summary>
    public decimal? MinOrderAmount { get; private set; }

    /// <summary>Порог количества в строке — для скидки по количеству.</summary>
    public decimal? MinQuantity { get; private set; }

    /// <summary>Группа позиций (с подгруппами); для акции обязательна, для скидки по количеству — необязательна.</summary>
    public long? ItemGroupId { get; private set; }

    /// <summary>Группа покупателей (тег контрагента); null — все покупатели.</summary>
    public long? TagId { get; private set; }

    public DateOnly ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesDiscountRule Create(long organizationId, SalesDiscountRuleTerms terms)
    {
        var r = new SalesDiscountRule { OrganizationId = organizationId };
        r.Apply(terms);
        return r;
    }

    public void Update(SalesDiscountRuleTerms terms)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("sales.discount.archived", "Правило скидки в архиве. Сначала верните его.");
        }

        Apply(terms);
    }

    public void SetArchived(bool archived) => IsArchived = archived;

    public bool IsValidOn(DateOnly date) => !IsArchived && ValidFrom <= date && (ValidTo is null || date <= ValidTo);

    public static string KindName(DiscountKind kind) => kind switch
    {
        DiscountKind.Agreement => "По соглашению",
        DiscountKind.OrderAmount => "По сумме заказа",
        DiscountKind.Quantity => "По количеству",
        DiscountKind.Promotion => "Акция",
        _ => "Ручная",
    };

    private void Apply(SalesDiscountRuleTerms t)
    {
        if (t.Kind is not (DiscountKind.OrderAmount or DiscountKind.Quantity or DiscountKind.Promotion))
        {
            throw new BusinessRuleException("sales.discount.kind", "Выберите вид скидки: по сумме заказа, по количеству или акция.");
        }

        if (t.Percent <= 0 || t.Percent > 100 || decimal.Round(t.Percent, 2) != t.Percent)
        {
            throw new BusinessRuleException("sales.discount.percent", "Скидка — больше 0 и не больше 100%, до сотых.");
        }

        if (t.ValidTo is { } to && to < t.ValidFrom)
        {
            throw new BusinessRuleException("sales.discount.period", "Дата окончания скидки раньше даты начала.");
        }

        switch (t.Kind)
        {
            case DiscountKind.OrderAmount when t.MinOrderAmount is not > 0:
                throw new BusinessRuleException("sales.discount.threshold", "Укажите порог суммы заказа больше нуля.");
            case DiscountKind.Quantity when t.MinQuantity is not > 0:
                throw new BusinessRuleException("sales.discount.quantity", "Укажите порог количества больше нуля.");
            case DiscountKind.Promotion when t.ItemGroupId is null:
                throw new BusinessRuleException("sales.discount.group", "Для акции выберите группу позиций.");
        }

        Kind = t.Kind;
        Name = DomainText.Require(t.Name, NameMaxLength, "Название скидки");
        Percent = t.Percent;
        MinOrderAmount = t.Kind == DiscountKind.OrderAmount ? Money.Round(t.MinOrderAmount!.Value) : null;
        MinQuantity = t.Kind == DiscountKind.Quantity ? t.MinQuantity : null;
        ItemGroupId = t.Kind == DiscountKind.OrderAmount ? null : t.ItemGroupId;
        TagId = t.TagId;
        ValidFrom = t.ValidFrom;
        ValidTo = t.ValidTo;
    }
}

public sealed record SalesDiscountRuleTerms(
    DiscountKind Kind, string Name, decimal Percent, decimal? MinOrderAmount, decimal? MinQuantity, long? ItemGroupId, long? TagId,
    DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>
/// Настройки скидок организации (D1106): порядок применения видов и суммирование. Нет записи — по умолчанию: соглашение → по количеству →
/// акция → по сумме заказа, последовательно; ручная скидка всегда последней.
/// </summary>
public sealed class SalesDiscountSettings
{
    public static readonly IReadOnlyList<DiscountKind> DefaultOrder =
        [DiscountKind.Agreement, DiscountKind.Quantity, DiscountKind.Promotion, DiscountKind.OrderAmount];

    private SalesDiscountSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public DiscountCombineMode Mode { get; private set; } = DiscountCombineMode.Sequential;

    /// <summary>Порядок видов через запятую: «1,3,4,2».</summary>
    public string KindOrder { get; private set; } = "1,3,4,2";

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<DiscountKind> Order => KindOrder.Split(',').Select(s => (DiscountKind)byte.Parse(s)).ToList();

    public static SalesDiscountSettings Create(long organizationId) => new() { OrganizationId = organizationId };

    public void Set(DiscountCombineMode mode, IReadOnlyList<DiscountKind> order)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new BusinessRuleException("sales.discount.mode", "Выберите, как сочетаются скидки.");
        }

        if (order.Count != DefaultOrder.Count || order.Distinct().Count() != DefaultOrder.Count || order.Any(k => !DefaultOrder.Contains(k)))
        {
            throw new BusinessRuleException("sales.discount.order", "Порядок применения — каждый вид скидки ровно один раз.");
        }

        Mode = mode;
        KindOrder = string.Join(',', order.Select(k => (byte)k));
    }
}

/// <summary>Одна применённая скидка строки — для расшифровки «откуда».</summary>
public sealed record DiscountPart(DiscountKind Kind, string Name, decimal Percent);

/// <summary>Расчёт итоговой скидки строки из частей (D1106).</summary>
public static class DiscountMath
{
    /// <summary>
    /// Итоговый процент: последовательно — 1 − Π(1 − pᵢ) в порядке видов; «наибольшая» — максимум автоматических, ручная — сверх него.
    /// Возвращает итог (до сотых, не больше 100) и применённые части в порядке применения.
    /// </summary>
    public static (decimal Total, IReadOnlyList<DiscountPart> Applied) Combine(
        IReadOnlyList<DiscountPart> automatic, decimal manual, DiscountCombineMode mode, IReadOnlyList<DiscountKind> order)
    {
        var ordered = automatic.Where(p => p.Percent > 0).OrderBy(p => Rank(p.Kind, order)).ThenByDescending(p => p.Percent).ToList();
        if (mode == DiscountCombineMode.Best && ordered.Count > 1)
        {
            ordered = [ordered.MaxBy(p => p.Percent)!];
        }

        if (manual > 0)
        {
            ordered.Add(new DiscountPart(DiscountKind.Manual, "Ручная", manual));
        }

        var factor = 1m;
        foreach (var p in ordered)
        {
            factor *= 1 - p.Percent / 100m;
        }

        return (Math.Min(100m, decimal.Round((1 - factor) * 100m, 2, MidpointRounding.AwayFromZero)), ordered);
    }

    public static string Describe(IReadOnlyList<DiscountPart> parts) =>
        string.Join("; ", parts.Select(p => $"{p.Name} {p.Percent:0.##}%"));

    private static int Rank(DiscountKind kind, IReadOnlyList<DiscountKind> order)
    {
        var i = order.ToList().IndexOf(kind);
        return i < 0 ? int.MaxValue : i;
    }
}
