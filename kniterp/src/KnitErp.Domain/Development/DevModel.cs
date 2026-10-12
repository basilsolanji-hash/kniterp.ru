using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Development;

/// <summary>Категория изделия (Допущение D704). Числа хранятся в базе — не перенумеровывать.</summary>
public enum DevCategory : byte
{
    Sweater = 1,
    Cardigan = 2,
    Jumper = 3,
    Hat = 4,
    Scarf = 5,
    Dress = 6,
    Fabric = 7,
    Vest = 8,
    Set = 9,
    Accessory = 10,
    Other = 99,
}

/// <summary>Для кого изделие. Детское — с требованиями ТР ТС 007/2011.</summary>
public enum DevAudience : byte
{
    Women = 1,
    Men = 2,
    Kids = 3,
    Unisex = 4,
}

public enum DevPriority : byte
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>Исполнители модели (D706).</summary>
public enum DevRole : byte
{
    Designer = 1,
    Dessinator = 2,
    Constructor = 3,
    Technologist = 4,
    Programmer = 5,
}

/// <summary>Карточка модели: название, классификация, заказчик, исполнители, срок, приоритет.</summary>
public sealed record DevModelHeader(
    string Name, long? CollectionId, DevCategory Category, DevAudience Audience, bool ChildSafety, bool HasPattern, long? CustomerId, long? DealId,
    DateOnly? PlannedLaunch, DevPriority Priority, IReadOnlyDictionary<DevRole, long?> Assignees);

/// <summary>Задание на разработку (D701): что делаем, в каких размерах и цветах, за сколько, каким тиражом и к какому сроку.</summary>
public sealed record DevBrief(
    string? Description, string? SizeRange, string? ColorRange, decimal? TargetCost, decimal? TargetPrice, int? RunQuantity, DateOnly? DueDate,
    string? Composition, string? Labeling, string? Care);

/// <summary>
/// Модель трикотажного изделия в разработке (D701): от идеи до передачи в производство.
/// Стадия меняется только методами модели — каждая смена пишется в историю (<see cref="DevModelStageEntry"/>, только вставка).
/// </summary>
public sealed class DevModel
{
    public const int NameMaxLength = 200;
    public const int NumberMaxLength = 20;
    public const int TextMaxLength = 4000;
    public const int ShortTextMaxLength = 300;
    public const int ReasonMaxLength = 500;
    public const decimal MaxMoney = 999_999_999m;
    public const int MaxRun = 10_000_000;

    private DevModel()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>М-00001 — сквозной номер организации.</summary>
    public string Number { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public long? CollectionId { get; private set; }
    public DevCategory Category { get; private set; }
    public DevAudience Audience { get; private set; }

    /// <summary>Детское изделие с требованиями ТР ТС 007/2011 (состав, маркировка, гигиена).</summary>
    public bool ChildSafety { get; private set; }

    /// <summary>У модели есть рисунок; гладь и ластик — без рисунка, стадия «Рисунок» пропускается.</summary>
    public bool HasPattern { get; private set; }

    public long? CustomerId { get; private set; }

    /// <summary>Сделка CRM (D708): ссылка без внешнего ключа, пока модуль CRM не влит в базу.</summary>
    public long? DealId { get; private set; }

    public long? DesignerUserId { get; private set; }
    public long? DessinatorUserId { get; private set; }
    public long? ConstructorUserId { get; private set; }
    public long? TechnologistUserId { get; private set; }
    public long? ProgrammerUserId { get; private set; }
    public DateOnly? PlannedLaunch { get; private set; }
    public DevPriority Priority { get; private set; }

    public DevStage Stage { get; private set; }
    public DateTime StageSinceUtc { get; private set; }

    /// <summary>Стадия, с которой модель отменили, — на неё возвращает «Вернуть в работу».</summary>
    public DevStage? StageBeforeCancel { get; private set; }

    public string? CancelReason { get; private set; }

    // Задание на разработку.
    public string? Description { get; private set; }
    public string? SizeRange { get; private set; }
    public string? ColorRange { get; private set; }
    public decimal? TargetCost { get; private set; }
    public decimal? TargetPrice { get; private set; }
    public int? RunQuantity { get; private set; }
    public DateOnly? BriefDueDate { get; private set; }
    public string? Composition { get; private set; }
    public string? Labeling { get; private set; }
    public string? Care { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }

    /// <summary>Размер короба упаковки («40 × 30 × 30 см») — для техкарты (D713).</summary>
    public string? PackBox { get; private set; }

    /// <summary>Закреплённый дизайнером эскиз (файл модели вида «Эскиз»), D712.</summary>
    public long? MainSketchFileId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static string CategoryName(DevCategory category) => category switch
    {
        DevCategory.Sweater => "Свитер",
        DevCategory.Cardigan => "Кардиган",
        DevCategory.Jumper => "Джемпер",
        DevCategory.Hat => "Шапка",
        DevCategory.Scarf => "Шарф",
        DevCategory.Dress => "Платье",
        DevCategory.Fabric => "Полотно",
        DevCategory.Vest => "Жилет",
        DevCategory.Set => "Комплект",
        DevCategory.Accessory => "Аксессуар",
        _ => "Прочее",
    };

    public static string AudienceName(DevAudience audience) => audience switch
    {
        DevAudience.Women => "Женское",
        DevAudience.Men => "Мужское",
        DevAudience.Kids => "Детское",
        _ => "Унисекс",
    };

    public static string PriorityName(DevPriority priority) => priority switch
    {
        DevPriority.Low => "Низкий",
        DevPriority.High => "Высокий",
        DevPriority.Urgent => "Срочно",
        _ => "Обычный",
    };

    public static string RoleName(DevRole role) => role switch
    {
        DevRole.Designer => "Дизайнер",
        DevRole.Dessinator => "Дессинатор",
        DevRole.Constructor => "Конструктор",
        DevRole.Technologist => "Технолог",
        _ => "Программист",
    };

    public static readonly IReadOnlyList<DevRole> Roles = [DevRole.Designer, DevRole.Dessinator, DevRole.Constructor, DevRole.Technologist, DevRole.Programmer];

    public long? AssigneeOf(DevRole role) => role switch
    {
        DevRole.Designer => DesignerUserId,
        DevRole.Dessinator => DessinatorUserId,
        DevRole.Constructor => ConstructorUserId,
        DevRole.Technologist => TechnologistUserId,
        _ => ProgrammerUserId,
    };

    public static (DevModel Model, DevModelStageEntry First) Create(
        long organizationId, string number, DevModelHeader header, DateTime nowUtc, long userId)
    {
        var m = new DevModel
        {
            OrganizationId = organizationId,
            Number = DomainText.Require(number, NumberMaxLength, "Номер"),
            Stage = DevStage.Idea,
            StageSinceUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = userId,
        };
        m.Apply(header);
        return (m, DevModelStageEntry.Create(m, null, DevStage.Idea, nowUtc, userId, null, null));
    }

    /// <summary>Изменение карточки; возвращает изменённые поля для журнала аудита.</summary>
    public IReadOnlyList<FieldChange> Update(DevModelHeader header)
    {
        EnsureEditable();
        if (!header.HasPattern && Stage == DevStage.Pattern)
        {
            throw new BusinessRuleException("dev.model.pattern_stage", "Модель на стадии «Рисунок» — сначала переведите её дальше.");
        }

        return Diff(Snapshot, () => Apply(header));
    }

    public IReadOnlyList<FieldChange> UpdateBrief(DevBrief brief)
    {
        EnsureEditable();
        return Diff(BriefSnapshot, () => ApplyBrief(brief));
    }

    public void SetPackBox(string? packBox)
    {
        EnsureEditable();
        PackBox = DomainText.Optional(packBox, 60, "Размер короба");
    }

    /// <summary>Закрепить эскиз (файл проверяет сервис: свой, вида «Эскиз», картинка) или снять закрепление.</summary>
    public void PinSketch(long? fileId)
    {
        EnsureEditable();
        MainSketchFileId = fileId;
    }

    /// <summary>Смена стадии; правило перехода проверяет <see cref="DevStages.CheckMove"/>.</summary>
    public DevModelStageEntry MoveTo(DevStage to, bool canApprove, Func<DevStage, bool> isEnabled, string? comment, DateTime nowUtc, long userId)
    {
        if (DevStages.CheckMove(Stage, to, HasPattern, canApprove, isEnabled) is { } refusal)
        {
            throw new BusinessRuleException("dev.stage.move", refusal);
        }

        return SetStage(to, comment, nowUtc, userId);
    }

    public DevModelStageEntry Cancel(string? reason, DateTime nowUtc, long userId)
    {
        if (DevStages.IsFinal(Stage))
        {
            throw new BusinessRuleException("dev.model.final", "Модель уже передана в производство или отменена.");
        }

        var text = DomainText.Require(reason, ReasonMaxLength, "Причина отмены");
        StageBeforeCancel = Stage;
        CancelReason = text;
        return SetStage(DevStage.Cancelled, text, nowUtc, userId);
    }

    /// <summary>Вернуть отменённую модель на стадию, с которой её отменили (утверждённую — только с правом утверждать).</summary>
    public DevModelStageEntry Restore(bool canApprove, DateTime nowUtc, long userId)
    {
        if (Stage != DevStage.Cancelled)
        {
            throw new BusinessRuleException("dev.model.not_cancelled", "Модель не отменена.");
        }

        var to = StageBeforeCancel ?? DevStage.Idea;
        if (to == DevStage.Pattern && !HasPattern)
        {
            to = DevStage.YarnAndKnit;
        }

        if (DevStages.NeedsApproval(to) && !canApprove)
        {
            throw new BusinessRuleException("dev.stage.move",
                "Утверждать модель и снимать утверждение может только главный технолог (право «Разработка: утверждение»).");
        }

        StageBeforeCancel = null;
        CancelReason = null;
        return SetStage(to, "Возвращена в работу", nowUtc, userId);
    }

    /// <summary>Просрочка: плановая дата запуска прошла, а модель ещё не передана.</summary>
    public static bool IsLate(DevStage stage, DateOnly? plannedLaunch, DateOnly today) =>
        !DevStages.IsFinal(stage) && plannedLaunch is { } d && d < today;

    /// <summary>Стадия идёт дольше норматива.</summary>
    public static bool IsStageOverdue(DevStage stage, DateTime sinceUtc, int planDays, DateTime nowUtc) =>
        !DevStages.IsFinal(stage) && planDays > 0 && (nowUtc - sinceUtc).TotalDays > planDays;

    private DevModelStageEntry SetStage(DevStage to, string? comment, DateTime nowUtc, long userId)
    {
        var days = (int)Math.Floor((nowUtc - StageSinceUtc).TotalDays);
        var entry = DevModelStageEntry.Create(this, Stage, to, nowUtc, userId, DomainText.Optional(comment, ReasonMaxLength, "Комментарий"), Math.Max(0, days));
        Stage = to;
        StageSinceUtc = nowUtc;
        return entry;
    }

    private void EnsureEditable()
    {
        if (Stage == DevStage.Handed)
        {
            throw new BusinessRuleException("dev.model.handed", "Модель передана в производство — изменения вносятся новой версией модели.");
        }

        if (Stage == DevStage.Cancelled)
        {
            throw new BusinessRuleException("dev.model.cancelled", "Модель отменена — сначала верните её в работу.");
        }
    }

    private void Apply(DevModelHeader h)
    {
        if (!Enum.IsDefined(h.Category) || !Enum.IsDefined(h.Audience) || !Enum.IsDefined(h.Priority))
        {
            throw new BusinessRuleException("dev.model.kind", "Выберите категорию, аудиторию и приоритет.");
        }

        if (h.PlannedLaunch is { Year: < 2000 or > 2100 })
        {
            throw new BusinessRuleException("dev.model.date", "Проверьте плановую дату запуска.");
        }

        Name = DomainText.Require(h.Name, NameMaxLength, "Название");
        CollectionId = h.CollectionId;
        Category = h.Category;
        Audience = h.Audience;
        // ТР ТС 007/2011 касается только детской одежды.
        ChildSafety = h.Audience == DevAudience.Kids && h.ChildSafety;
        HasPattern = h.HasPattern;
        CustomerId = h.CustomerId;
        DealId = h.DealId is > 0 ? h.DealId : null;
        PlannedLaunch = h.PlannedLaunch;
        Priority = h.Priority;
        DesignerUserId = h.Assignees.GetValueOrDefault(DevRole.Designer);
        DessinatorUserId = h.Assignees.GetValueOrDefault(DevRole.Dessinator);
        ConstructorUserId = h.Assignees.GetValueOrDefault(DevRole.Constructor);
        TechnologistUserId = h.Assignees.GetValueOrDefault(DevRole.Technologist);
        ProgrammerUserId = h.Assignees.GetValueOrDefault(DevRole.Programmer);
    }

    private void ApplyBrief(DevBrief b)
    {
        if (b.TargetCost is < 0 or > MaxMoney || b.TargetPrice is < 0 or > MaxMoney)
        {
            throw new BusinessRuleException("dev.brief.money", "Целевая себестоимость и цена — от 0 до 999 999 999.");
        }

        if (b.RunQuantity is < 1 or > MaxRun)
        {
            throw new BusinessRuleException("dev.brief.run", "Тираж — целое число от 1 до 10 000 000.");
        }

        if (b.DueDate is { Year: < 2000 or > 2100 })
        {
            throw new BusinessRuleException("dev.brief.date", "Проверьте срок задания.");
        }

        Description = DomainText.Optional(b.Description, TextMaxLength, "Описание");
        SizeRange = DomainText.Optional(b.SizeRange, ShortTextMaxLength, "Размерный ряд");
        ColorRange = DomainText.Optional(b.ColorRange, ShortTextMaxLength, "Цветовая гамма");
        TargetCost = b.TargetCost is { } c ? decimal.Round(c, 2, MidpointRounding.AwayFromZero) : null;
        TargetPrice = b.TargetPrice is { } p ? decimal.Round(p, 2, MidpointRounding.AwayFromZero) : null;
        RunQuantity = b.RunQuantity;
        BriefDueDate = b.DueDate;
        Composition = DomainText.Optional(b.Composition, ShortTextMaxLength, "Состав");
        Labeling = DomainText.Optional(b.Labeling, ShortTextMaxLength, "Маркировка");
        Care = DomainText.Optional(b.Care, ShortTextMaxLength, "Уход");
    }

    private (string Field, string? Value)[] Snapshot() =>
    [
        ("Название", Name),
        ("Коллекция", CollectionId?.ToString()),
        ("Категория", CategoryName(Category)),
        ("Аудитория", AudienceName(Audience)),
        ("ТР ТС 007/2011", ChildSafety ? "да" : "нет"),
        ("Есть рисунок", HasPattern ? "да" : "нет"),
        ("Заказчик", CustomerId?.ToString()),
        ("Сделка", DealId?.ToString()),
        ("Плановый запуск", PlannedLaunch?.ToString("dd.MM.yyyy")),
        ("Приоритет", PriorityName(Priority)),
        ("Дизайнер", DesignerUserId?.ToString()),
        ("Дессинатор", DessinatorUserId?.ToString()),
        ("Конструктор", ConstructorUserId?.ToString()),
        ("Технолог", TechnologistUserId?.ToString()),
        ("Программист", ProgrammerUserId?.ToString()),
    ];

    // Суммы в журнал не пишутся (журнал видят без права на себестоимость, D707) — только факт изменения.
    private (string Field, string? Value)[] BriefSnapshot() =>
    [
        ("Описание", Description is null ? null : Description.Length > 60 ? Description[..60] + "…" : Description),
        ("Размерный ряд", SizeRange),
        ("Цветовая гамма", ColorRange),
        ("Целевая себестоимость", TargetCost?.ToString("0.00")),
        ("Целевая цена", TargetPrice?.ToString("0.00")),
        ("Тираж", RunQuantity?.ToString()),
        ("Срок", BriefDueDate?.ToString("dd.MM.yyyy")),
        ("Состав", Composition),
        ("Маркировка", Labeling),
        ("Уход", Care),
    ];

    private static IReadOnlyList<FieldChange> Diff(Func<(string Field, string? Value)[]> snapshot, Action apply)
    {
        var before = snapshot();
        apply();
        var after = snapshot();
        var changes = new List<FieldChange>();
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Value != after[i].Value)
            {
                var money = before[i].Field.StartsWith("Целевая", StringComparison.Ordinal);
                changes.Add(money
                    ? new FieldChange(before[i].Field, before[i].Value is null ? null : "задана", after[i].Value is null ? null : "изменена")
                    : new FieldChange(before[i].Field, before[i].Value, after[i].Value));
            }
        }

        return changes;
    }
}

/// <summary>Запись истории стадий модели (D702): кто, когда, откуда, куда и сколько дней модель провела на прежней стадии. Только вставка.</summary>
public sealed class DevModelStageEntry
{
    private DevModelStageEntry()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public DevStage? FromStage { get; private set; }
    public DevStage ToStage { get; private set; }
    public DateTime AtUtc { get; private set; }
    public long UserId { get; private set; }
    public string? Comment { get; private set; }

    /// <summary>Дней на прежней стадии; у первой записи — null.</summary>
    public int? DaysInPrevious { get; private set; }

    /// <summary>Модель, к которой относится запись, — EF подставит её Id при сохранении в одной транзакции.</summary>
    public DevModel? Model { get; private set; }

    internal static DevModelStageEntry Create(DevModel model, DevStage? from, DevStage to, DateTime nowUtc, long userId, string? comment, int? days) => new()
    {
        OrganizationId = model.OrganizationId,
        ModelId = model.Id,
        Model = model.Id == 0 ? model : null,
        FromStage = from,
        ToStage = to,
        AtUtc = nowUtc,
        UserId = userId,
        Comment = comment,
        DaysInPrevious = days,
    };
}
