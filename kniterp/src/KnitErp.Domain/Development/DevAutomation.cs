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

    public byte[] RowVersion { get; private set; } = [];

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
