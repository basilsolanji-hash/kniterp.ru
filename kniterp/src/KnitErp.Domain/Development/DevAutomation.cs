using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Настройки автоматизации разработки организации (D791, D792, D795): исполнители по умолчанию, расчёт плановой даты запуска,
/// автопереход стадии по загруженному файлу. Одна запись на организацию, создаётся при первом сохранении.
/// </summary>
public sealed class DevSettings
{
    private DevSettings()
    {
    }

    public long OrganizationId { get; private set; }
    public long? DefaultDesignerUserId { get; private set; }
    public long? DefaultDessinatorUserId { get; private set; }
    public long? DefaultConstructorUserId { get; private set; }
    public long? DefaultTechnologistUserId { get; private set; }
    public long? DefaultProgrammerUserId { get; private set; }

    /// <summary>Плановый запуск не указан — считается по нормативам стадий.</summary>
    public bool AutoLaunchDate { get; private set; } = true;

    /// <summary>Загрузка программы машины или рисунка переводит модель на следующую стадию.</summary>
    public bool AutoAdvance { get; private set; } = true;

    /// <summary>Допуск факта образца к плану, %: время или вес больше плана на столько — красным (D719).</summary>
    public int FactTolerancePercent { get; private set; } = DefaultTolerance;

    /// <summary>Какие разделы входят в показатель заполнения: бит i — раздел <see cref="DevCompleteness.SectionNames"/>[i] (D719).</summary>
    public int CompletenessSections { get; private set; } = DevCompleteness.AllSections;

    /// <summary>Этикетка модуля на рулоне, мм; или лист A4 3 × 8 (D719).</summary>
    public int LabelWidthMm { get; private set; } = DefaultLabelWidth;

    public int LabelHeightMm { get; private set; } = DefaultLabelHeight;

    public bool LabelsOnA4 { get; private set; }

    /// <summary>Отходы пряжи, % (D740): расчёт себестоимости и норма отхода в черновике техкарты производства.</summary>
    public decimal YarnWastePercent { get; private set; }

    /// <summary>Накладные расходы, % от прямых затрат (D740).</summary>
    public decimal OverheadPercent { get; private set; }

    /// <summary>Ставка работы за минуту для операций без расценки (D740), пока нет расценок модуля «Сотрудники».</summary>
    public decimal? LaborRatePerMinute { get; private set; }

    /// <summary>Наценка по умолчанию, % (D742).</summary>
    public decimal MarkupPercent { get; private set; }

    /// <summary>Утверждать модель только с сохранённым расчётом себестоимости (D746).</summary>
    public bool RequireCostForApproval { get; private set; } = true;

    /// <summary>Порог маржи, % от цены (D746): ниже — утверждение только с обоснованием в комментарии; пусто — без порога.</summary>
    public decimal? MinMarginPercent { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public const int DefaultTolerance = 10;
    public const int DefaultLabelWidth = 58;
    public const int DefaultLabelHeight = 40;
    public const int MinLabelMm = 20;
    public const int MaxLabelMm = 150;

    /// <summary>Значения по умолчанию, пока организация ничего не сохраняла.</summary>
    public static DevSettings Defaults(long organizationId) => new() { OrganizationId = organizationId };

    public long? DefaultOf(DevRole role) => role switch
    {
        DevRole.Designer => DefaultDesignerUserId,
        DevRole.Dessinator => DefaultDessinatorUserId,
        DevRole.Constructor => DefaultConstructorUserId,
        DevRole.Technologist => DefaultTechnologistUserId,
        _ => DefaultProgrammerUserId,
    };

    public void Set(IReadOnlyDictionary<DevRole, long?> defaults, bool autoLaunchDate, bool autoAdvance)
    {
        DefaultDesignerUserId = defaults.GetValueOrDefault(DevRole.Designer);
        DefaultDessinatorUserId = defaults.GetValueOrDefault(DevRole.Dessinator);
        DefaultConstructorUserId = defaults.GetValueOrDefault(DevRole.Constructor);
        DefaultTechnologistUserId = defaults.GetValueOrDefault(DevRole.Technologist);
        DefaultProgrammerUserId = defaults.GetValueOrDefault(DevRole.Programmer);
        AutoLaunchDate = autoLaunchDate;
        AutoAdvance = autoAdvance;
    }

    /// <summary>Настройки цеха и печати (D719): допуск факта, разделы показателя заполнения, формат этикеток.</summary>
    public void SetShop(int factTolerancePercent, int completenessSections, int labelWidthMm, int labelHeightMm, bool labelsOnA4)
    {
        if (factTolerancePercent is < 1 or > 100)
        {
            throw new BusinessRuleException("dev.settings.tolerance", "Допуск факта к плану — от 1 до 100 %.");
        }

        if ((completenessSections & DevCompleteness.AllSections) == 0 || (completenessSections & ~DevCompleteness.AllSections) != 0)
        {
            throw new BusinessRuleException("dev.settings.sections", "В показатель заполнения должен входить хотя бы один раздел.");
        }

        if (labelWidthMm is < MinLabelMm or > MaxLabelMm || labelHeightMm is < MinLabelMm or > MaxLabelMm)
        {
            throw new BusinessRuleException("dev.settings.label", $"Размер этикетки — от {MinLabelMm} до {MaxLabelMm} мм.");
        }

        FactTolerancePercent = factTolerancePercent;
        CompletenessSections = completenessSections;
        LabelWidthMm = labelWidthMm;
        LabelHeightMm = labelHeightMm;
        LabelsOnA4 = labelsOnA4;
    }

    /// <summary>Условия расчёта себестоимости (D740, D742): отходы пряжи, накладные, ставка за минуту, наценка по умолчанию.</summary>
    public void SetCosting(decimal yarnWastePercent, decimal overheadPercent, decimal? laborRatePerMinute, decimal markupPercent,
        bool requireCostForApproval = true, decimal? minMarginPercent = null)
    {
        if (minMarginPercent is < -100 or > 100)
        {
            throw new BusinessRuleException("dev.cost.margin", "Порог маржи — от −100 до 100 %.");
        }

        if (yarnWastePercent is < 0 or > 100)
        {
            throw new BusinessRuleException("dev.cost.percent", "Отходы пряжи — от 0 до 100 %.");
        }

        DevCosting.EnsurePercent(overheadPercent, "Накладные расходы");
        DevCosting.EnsurePercent(markupPercent, "Наценка");
        if (laborRatePerMinute is < 0 or > 1_000_000)
        {
            throw new BusinessRuleException("dev.cost.rate", "Ставка за минуту — от 0 до 1 000 000.");
        }

        YarnWastePercent = yarnWastePercent;
        OverheadPercent = overheadPercent;
        LaborRatePerMinute = laborRatePerMinute;
        MarkupPercent = markupPercent;
        RequireCostForApproval = requireCostForApproval;
        MinMarginPercent = minMarginPercent;
    }

    /// <summary>Факт больше плана сверх допуска.</summary>
    public static bool OverPlan(decimal? fact, decimal? plan, int tolerancePercent) =>
        fact is { } f && plan is { } p && p > 0 && f > p * (100 + tolerancePercent) / 100m;
}

/// <summary>Шаблон задания по категории изделия (D790): что подставить в новую модель этой категории.</summary>
public sealed class DevCategoryTemplate
{
    private DevCategoryTemplate()
    {
    }

    public long OrganizationId { get; private set; }
    public DevCategory Category { get; private set; }
    public string? SizeRange { get; private set; }
    public string? ColorRange { get; private set; }
    public string? Composition { get; private set; }
    public string? Labeling { get; private set; }
    public string? Care { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevCategoryTemplate Create(long organizationId, DevCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new BusinessRuleException("dev.model.kind", "Выберите категорию, аудиторию и приоритет.");
        }

        return new DevCategoryTemplate { OrganizationId = organizationId, Category = category };
    }

    public void Set(string? sizeRange, string? colorRange, string? composition, string? labeling, string? care)
    {
        SizeRange = DomainText.Optional(sizeRange, DevModel.ShortTextMaxLength, "Размерный ряд");
        ColorRange = DomainText.Optional(colorRange, DevModel.ShortTextMaxLength, "Цветовая гамма");
        Composition = DomainText.Optional(composition, DevModel.ShortTextMaxLength, "Состав");
        Labeling = DomainText.Optional(labeling, DevModel.ShortTextMaxLength, "Маркировка");
        Care = DomainText.Optional(care, DevModel.ShortTextMaxLength, "Уход");
    }

    public bool IsEmpty => SizeRange is null && ColorRange is null && Composition is null && Labeling is null && Care is null;

    /// <summary>Задание, где пустые поля заполнены из шаблона; заполненные пользователем не трогаются.</summary>
    public DevBrief FillBlanks(DevBrief b) => b with
    {
        SizeRange = b.SizeRange ?? SizeRange,
        ColorRange = b.ColorRange ?? ColorRange,
        Composition = b.Composition ?? Composition,
        Labeling = b.Labeling ?? Labeling,
        Care = b.Care ?? Care,
    };
}

/// <summary>Правила автоматизации (D792, D795, D798): расчёты без базы данных — их проверяют модульные тесты.</summary>
public static class DevAutomationRules
{
    /// <summary>Плановый запуск = сегодня + нормативы включённых стадий от «Идеи» до «Утверждено» (без «Рисунка» у модели без рисунка).</summary>
    public static DateOnly PlannedLaunch(DateOnly today, bool hasPattern, Func<DevStage, bool> isEnabled, Func<DevStage, int> planDays)
    {
        var days = DevStages.Path
            .Where(s => s != DevStage.Handed && isEnabled(s) && (hasPattern || s != DevStage.Pattern))
            .Sum(planDays);
        return today.AddDays(days);
    }

    /// <summary>Название, если пользователь его не ввёл: «Свитер — Осень-зима 2026 — ООО «Пример»».</summary>
    public static string DefaultName(DevCategory category, string? collection, string? customer)
    {
        var parts = new[] { DevModel.CategoryName(category), collection, customer }.Where(p => !string.IsNullOrWhiteSpace(p));
        var name = string.Join(" — ", parts);
        return name.Length > DevModel.NameMaxLength ? name[..DevModel.NameMaxLength] : name;
    }

    /// <summary>
    /// Куда перевести модель после загрузки файла (D795) или null. Программа машины на стадии «Программа» — следующая стадия
    /// (обычно «Образец»); рисунок на «Идее» или «Задании» у модели с рисунком — «Рисунок».
    /// </summary>
    public static DevStage? AutoAdvance(DevStage stage, DevFileKind kind, bool hasPattern, Func<DevStage, bool> isEnabled)
    {
        if (kind == DevFileKind.MachineProgram && stage == DevStage.Program)
        {
            return DevStages.Next(stage, hasPattern, isEnabled);
        }

        if (kind == DevFileKind.Pattern && hasPattern && stage is DevStage.Idea or DevStage.Brief && isEnabled(DevStage.Pattern))
        {
            return DevStage.Pattern;
        }

        return null;
    }

    /// <summary>Подсказка следующего шага по стадии и тому, чего не хватает модели (D798). Возвращает тексты для перевода.</summary>
    public static IReadOnlyList<string> Hints(DevStage stage, bool hasBrief, bool hasSketch, bool hasPatternFile, bool hasProgram, bool hasResponsible)
    {
        var hints = new List<string>();
        if (!DevStages.IsFinal(stage) && !hasResponsible && DevStages.ResponsibleRole(stage) is not null)
        {
            hints.Add("На этой стадии не назначен ответственный — укажите исполнителя в карточке.");
        }

        switch (stage)
        {
            case DevStage.Idea when !hasSketch:
                hints.Add("Приложите эскиз или образцы для вдохновения (вкладка «Файлы»).");
                break;
            case DevStage.Brief when !hasBrief:
                hints.Add("Заполните задание: размерный ряд, состав, тираж и срок (можно из шаблона категории).");
                break;
            case DevStage.Pattern when !hasPatternFile:
                hints.Add("Приложите файл рисунка (вид «Рисунок»): по нему работают технолог и программист.");
                break;
            case DevStage.Program when !hasProgram:
                hints.Add("Приложите программу машины (.sin или .dat) — модель сама перейдёт на стадию «Образец».");
                break;
        }

        return hints;
    }

    /// <summary>Все тексты подсказок — для словарей переводов.</summary>
    public static readonly IReadOnlyList<string> AllHints =
    [
        "На этой стадии не назначен ответственный — укажите исполнителя в карточке.",
        "Приложите эскиз или образцы для вдохновения (вкладка «Файлы»).",
        "Заполните задание: размерный ряд, состав, тираж и срок (можно из шаблона категории).",
        "Приложите файл рисунка (вид «Рисунок»): по нему работают технолог и программист.",
        "Приложите программу машины (.sin или .dat) — модель сама перейдёт на стадию «Образец».",
    ];
}
