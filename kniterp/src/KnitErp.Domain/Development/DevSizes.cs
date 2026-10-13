using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Размер размерной сетки модели (D713): «1», «2», «42»… Базовый — от него считается градация. Шт в коробке — для упаковки.</summary>
public sealed class DevModelSize
{
    public const int NameMaxLength = 20;
    public const int MaxPerModel = 15;

    private DevModelSize()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Версия строки (D720): одновременное сохранение той же ячейки — конфликт, а не тихая перезапись.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public long ModelId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public bool IsBase { get; private set; }
    public int? PerBox { get; private set; }

    public static DevModelSize Create(long organizationId, long modelId, string? name, int sortOrder, bool isBase, int? perBox)
    {
        var s = new DevModelSize { OrganizationId = organizationId, ModelId = modelId, SortOrder = sortOrder, IsBase = isBase };
        s.Set(name, perBox);
        return s;
    }

    public void Set(string? name, int? perBox)
    {
        if (perBox is < 1 or > 10_000)
        {
            throw new BusinessRuleException("dev.size.box", "Штук в коробке — от 1 до 10 000.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Размер");
        PerBox = perBox;
    }

    public void SetBase(bool isBase) => IsBase = isBase;

    /// <summary>Вес изделия этого размера, г (ТЗ §8 «вес изделия по размерам», D728).</summary>
    public decimal? WeightGrams { get; private set; }

    public void SetWeight(decimal? grams)
    {
        if (grams is <= 0 or > 100_000)
        {
            throw new BusinessRuleException("dev.size.weight", "Вес изделия — больше 0 и не больше 100 кг.");
        }

        WeightGrams = grams;
    }

    public void Reorder(int sortOrder) => SortOrder = sortOrder;
}

/// <summary>Размеры детали по размеру сетки (D713): длина с машины, длина после ВТО, ширина — сантиметры.</summary>
public sealed class DevDetailSize
{
    private DevDetailSize()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Версия строки (D720): одновременное сохранение той же ячейки — конфликт, а не тихая перезапись.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public long DetailId { get; private set; }
    public long SizeId { get; private set; }
    public decimal? LengthOffMachineCm { get; private set; }
    public decimal? LengthAfterSteamCm { get; private set; }
    public decimal? WidthCm { get; private set; }

    public static DevDetailSize Create(long organizationId, long detailId, long sizeId) =>
        new() { OrganizationId = organizationId, DetailId = detailId, SizeId = sizeId };

    public void Set(decimal? offMachine, decimal? afterSteam, decimal? width)
    {
        DevMeasureRules.EnsureCm(offMachine);
        DevMeasureRules.EnsureCm(afterSteam);
        DevMeasureRules.EnsureCm(width);
        LengthOffMachineCm = DevMeasureRules.Round(offMachine);
        LengthAfterSteamCm = DevMeasureRules.Round(afterSteam);
        WidthCm = DevMeasureRules.Round(width);
    }

    public bool IsFilled => LengthOffMachineCm is not null || LengthAfterSteamCm is not null || WidthCm is not null;
}

/// <summary>Точка табеля мер (D713): «Длина изделия», «Ширина груди»… допуск ± см и шаг градации (+ см на размер).</summary>
public sealed class DevMeasure
{
    public const int NameMaxLength = 100;
    public const int MaxPerModel = 40;

    private DevMeasure()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Tolerance { get; private set; }
    public decimal? Step { get; private set; }
    public int SortOrder { get; private set; }

    public static DevMeasure Create(long organizationId, long modelId, string? name, decimal tolerance, decimal? step, int sortOrder)
    {
        var m = new DevMeasure { OrganizationId = organizationId, ModelId = modelId, SortOrder = sortOrder };
        m.Set(name, tolerance, step);
        return m;
    }

    public void Set(string? name, decimal tolerance, decimal? step)
    {
        if (tolerance is < 0 or > 50)
        {
            throw new BusinessRuleException("dev.measure.tolerance", "Допуск — от 0 до 50 см.");
        }

        if (step is < -50 or > 50)
        {
            throw new BusinessRuleException("dev.measure.step", "Шаг градации — от −50 до 50 см на размер.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Точка измерения");
        Tolerance = decimal.Round(tolerance, 2);
        Step = step is { } s ? decimal.Round(s, 2) : null;
    }
}

/// <summary>Значение табеля мер точки по размеру, см.</summary>
public sealed class DevMeasureValue
{
    private DevMeasureValue()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Версия строки (D720): одновременное сохранение той же ячейки — конфликт, а не тихая перезапись.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public long MeasureId { get; private set; }
    public long SizeId { get; private set; }
    public decimal Value { get; private set; }

    public static DevMeasureValue Create(long organizationId, long measureId, long sizeId, decimal value)
    {
        var v = new DevMeasureValue { OrganizationId = organizationId, MeasureId = measureId, SizeId = sizeId };
        v.Set(value);
        return v;
    }

    public void Set(decimal value)
    {
        DevMeasureRules.EnsureCm(value);
        Value = DevMeasureRules.Round(value)!.Value;
    }
}

/// <summary>Правила размеров (D713): пределы, округление, градация от базового размера, допуск замера.</summary>
public static class DevMeasureRules
{
    public const decimal MaxCm = 1000m;

    public static void EnsureCm(decimal? value)
    {
        if (value is < 0 or > MaxCm)
        {
            throw new BusinessRuleException("dev.measure.value", "Размер — от 0 до 1000 см.");
        }
    }

    public static decimal? Round(decimal? value) => value is { } v ? decimal.Round(v, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Градация: значение размера = базовое + (позиция размера − позиция базового) × шаг.</summary>
    public static decimal Grade(decimal baseValue, decimal step, int sizeIndex, int baseIndex) =>
        decimal.Round(baseValue + (sizeIndex - baseIndex) * step, 1, MidpointRounding.AwayFromZero);

    /// <summary>Отклонение замера от табеля и выход за допуск.</summary>
    public static (decimal Deviation, bool OutOfTolerance) Check(decimal plan, decimal fact, decimal tolerance)
    {
        var deviation = decimal.Round(fact - plan, 1, MidpointRounding.AwayFromZero);
        return (deviation, Math.Abs(deviation) > tolerance);
    }
}

/// <summary>
/// Шаблон табеля мер по категории изделия (D729, ТЗ §9): точки измерения с допуском и шагом градации. Применяется к модели кнопкой —
/// добавляет недостающие точки в её табель.
/// </summary>
public sealed class DevCategoryMeasure
{
    public const int MaxPerCategory = 40;

    private DevCategoryMeasure()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    /// <summary>Категория — запись справочника «Категории изделий» (D733).</summary>
    public long CategoryId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Tolerance { get; private set; }
    public decimal? Step { get; private set; }
    public int SortOrder { get; private set; }

    public static DevCategoryMeasure Create(long organizationId, long categoryId, string? name, decimal tolerance, decimal? step, int sortOrder)
    {
        if (categoryId <= 0)
        {
            throw new BusinessRuleException("dev.model.category", "Выберите категорию изделия.");
        }

        if (tolerance is < 0 or > 50 || step is < -50 or > 50)
        {
            throw new BusinessRuleException("dev.measure.tolerance", "Допуск — от 0 до 50 см, шаг градации — от −50 до 50 см.");
        }

        return new DevCategoryMeasure
        {
            OrganizationId = organizationId,
            CategoryId = categoryId,
            Name = DomainText.Require(name, DevMeasure.NameMaxLength, "Точка измерения"),
            Tolerance = tolerance,
            Step = step,
            SortOrder = sortOrder,
        };
    }
}
