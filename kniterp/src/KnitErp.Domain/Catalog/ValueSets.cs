using System.Globalization;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Набор значений характеристики (D321): «Размеры женские 42–56», «XS–XXL», «Цвета базовые». Модификации создаются выбором набора вместо
/// ввода значений каждый раз. Набор привязан к характеристике организации. Значения — через запятую; числовой диапазон пишется
/// с шагом «42-56/2» и раскрывается в 42, 44 … 56; «44-46» без шага — одно значение (парный размер).
/// </summary>
public sealed class CharacteristicValueSet
{
    public const int NameMaxLength = 100;
    public const int MaxValues = 100;
    public const int MaxPerOrganization = 100;

    private CharacteristicValueSet()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long CharacteristicId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Значения уже раскрытые и очищенные, через «, ».</summary>
    public string ValuesText { get; private set; } = string.Empty;

    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> Values => Variants.SplitList(ValuesText);

    public static CharacteristicValueSet Create(long organizationId, long characteristicId, string? name, string? values)
    {
        var set = new CharacteristicValueSet { OrganizationId = organizationId, CharacteristicId = characteristicId };
        set.Set(name, values);
        return set;
    }

    public void Set(string? name, string? values)
    {
        var n = DomainText.Require(name, NameMaxLength, "Название набора");
        var list = Expand(values);
        Name = n;
        ValuesText = string.Join(", ", list);
    }

    public void SetArchived(bool archived) => IsArchived = archived;

    /// <summary>Значения через запятую; «от-до/шаг» — числовой диапазон. Повторы (без учёта регистра) убираются, порядок сохраняется.</summary>
    public static IReadOnlyList<string> Expand(string? text)
    {
        var result = new List<string>();
        foreach (var token in Variants.SplitList(text))
        {
            if (TryRange(token, out var range))
            {
                result.AddRange(range);
            }
            else
            {
                result.Add(Variants.CleanValue(token));
            }

            if (result.Count > MaxValues)
            {
                throw new BusinessRuleException("catalog.value_set.too_many", "В наборе не больше 100 значений.");
            }
        }

        var distinct = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            throw new BusinessRuleException("catalog.value_set.empty", "Укажите значения через запятую или диапазон, например «42-56/2».");
        }

        if (string.Join(", ", distinct).Length > 2000)
        {
            throw new BusinessRuleException("catalog.value_set.long", "Значения набора — не длиннее 2000 символов.");
        }

        return distinct;
    }

    /// <summary>«42-56/2», «98-164/6», «1-2/0.5» — с обязательным шагом; дробная часть — через точку; не больше 100 значений.</summary>
    private static bool TryRange(string token, out IReadOnlyList<string> values)
    {
        values = [];
        if (!token.Contains('/'))
        {
            return false; // «44-46» — парный размер трикотажа, одно значение; диапазон — только с шагом: «42-56/2»
        }

        var parts = token.Replace('–', '-').Split('/', 2);
        var bounds = parts[0].Split('-', 2, StringSplitOptions.TrimEntries);
        if (bounds.Length != 2
            || !decimal.TryParse(bounds[0].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var from)
            || !decimal.TryParse(bounds[1].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var to))
        {
            return false;
        }

        var step = 1m;
        if (parts.Length == 2 && (!decimal.TryParse(parts[1].Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out step) || step <= 0))
        {
            throw new BusinessRuleException("catalog.value_set.step", "Шаг диапазона — положительное число, например «42-56/2».");
        }

        if (to < from || (to - from) / step >= MaxValues)
        {
            throw new BusinessRuleException("catalog.value_set.range", "Диапазон — «от-до/шаг», от меньшего к большему, не больше 100 значений.");
        }

        var list = new List<string>();
        for (var v = from; v <= to; v += step)
        {
            // Точка, не запятая: запятая — разделитель значений, «1,5» распалась бы на два.
            list.Add(v.ToString("0.##", CultureInfo.InvariantCulture));
        }

        values = list;
        return true;
    }

    /// <summary>Готовые наборы для быстрого старта: название характеристики, название набора, значения.</summary>
    public static readonly IReadOnlyList<(string Characteristic, string Name, string Values)> Presets =
    [
        ("Размер", "Женские 42–56", "42-56/2"),
        ("Размер", "Мужские 44–60", "44-60/2"),
        ("Размер", "Детские по росту 98–164", "98-164/6"),
        ("Размер", "Международные XS–XXL", "XS, S, M, L, XL, XXL"),
        ("Размер", "Шапки по обхвату 52–60", "52-60/2"),
        ("Цвет", "Базовые цвета", "белый, чёрный, серый, бежевый, синий, красный, зелёный"),
    ];
}
