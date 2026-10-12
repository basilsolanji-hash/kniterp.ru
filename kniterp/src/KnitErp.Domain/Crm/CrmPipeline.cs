using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Вид этапа воронки: рабочий, финальный «выигрыш» или финальный «проигрыш».</summary>
public enum CrmStageKind : byte
{
    Open = 0,
    Won = 1,
    Lost = 2,
}

/// <summary>
/// Воронка продаж (D602): «Опт», «Маркетплейсы», «Пошив по ТЗ клиента». У организации их может быть несколько; у каждой —
/// свои этапы. Не удаляется — архивируется (сделки в архивной воронке остаются, новые в неё не заводятся).
/// </summary>
public sealed class CrmPipeline
{
    public const int NameMaxLength = 100;
    public const int MaxPipelines = 20;
    public const string DefaultName = "Продажи";

    private CrmPipeline()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmPipeline Create(long organizationId, string? name, int sortOrder) =>
        new() { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Название воронки"), SortOrder = sortOrder };

    public void Rename(string? name)
    {
        EnsureActive();
        Name = DomainText.Require(name, NameMaxLength, "Название воронки");
    }

    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    public void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Воронка «{Name}» в архиве.");
        }
    }
}

/// <summary>
/// Этап воронки (D602): порядок, цвет, вероятность сделки на этапе в процентах, вид (рабочий, выигрыш, проигрыш).
/// У воронки ровно один этап «выигрыш» и один «проигрыш»: их нельзя отправить в архив или сменить вид. Остальные этапы
/// не удаляются — архивируются, сделки на них остаются.
/// </summary>
public sealed class CrmStage
{
    public const int NameMaxLength = 60;
    public const int MaxStages = 30;

    /// <summary>Цвета — как у этапов заказов покупателей (D75), чтобы чипы выглядели одинаково.</summary>
    public static readonly IReadOnlyList<string> Colors = ["gray", "blue", "teal", "green", "yellow", "orange", "red", "magenta", "violet"];

    /// <summary>Готовая воронка новой организации (ТЗ CRM §3).</summary>
    public static readonly IReadOnlyList<(string Name, string Color, int Probability, CrmStageKind Kind)> Defaults =
    [
        ("Новая", "gray", 10, CrmStageKind.Open),
        ("Выявление потребности", "blue", 20, CrmStageKind.Open),
        ("Образцы / расчёт", "teal", 40, CrmStageKind.Open),
        ("КП отправлено", "yellow", 60, CrmStageKind.Open),
        ("Согласование", "orange", 80, CrmStageKind.Open),
        ("Выиграна", "green", 100, CrmStageKind.Won),
        ("Проиграна", "red", 0, CrmStageKind.Lost),
    ];

    private CrmStage()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PipelineId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Color { get; private set; } = "gray";
    public int SortOrder { get; private set; }

    /// <summary>Вероятность выигрыша сделки на этапе, 0–100 %. Подставляется в сделку при переходе на этап.</summary>
    public int Probability { get; private set; }

    public CrmStageKind Kind { get; private set; }

    /// <summary>
    /// Правило следующего шага (D622): при переходе на этап система предлагает задачу «NextStepSubject» через NextStepDays дней.
    /// null — не предлагать. Только предложение — пользователь подтверждает.
    /// </summary>
    public int? NextStepDays { get; private set; }

    public string? NextStepSubject { get; private set; }

    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsFinal => Kind != CrmStageKind.Open;

    public static CrmStage Create(long organizationId, long pipelineId, string? name, string? color, int probability, CrmStageKind kind, int sortOrder)
    {
        var stage = new CrmStage { OrganizationId = organizationId, PipelineId = pipelineId, Kind = kind, SortOrder = sortOrder };
        stage.Set(name, color, probability);
        return stage;
    }

    public void Set(string? name, string? color, int probability)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Этап «{Name}» в архиве.");
        }

        var c = (color ?? "").Trim().ToLowerInvariant();
        if (!Colors.Contains(c))
        {
            throw new BusinessRuleException("crm.stage.color", "Неизвестный цвет этапа.");
        }

        if (probability is < 0 or > 100)
        {
            throw new BusinessRuleException("crm.stage.probability", "Вероятность — от 0 до 100 %.");
        }

        if (Kind == CrmStageKind.Won && probability != 100 || Kind == CrmStageKind.Lost && probability != 0)
        {
            throw new BusinessRuleException("crm.stage.final_probability", "У этапа «выигрыш» вероятность 100 %, у этапа «проигрыш» — 0 %.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название этапа");
        Color = c;
        Probability = probability;
    }

    public void MoveTo(int sortOrder) => SortOrder = sortOrder;

    /// <summary>Правило следующего шага; days null — выключено.</summary>
    public void SetNextStep(int? days, string? subject)
    {
        if (days is null)
        {
            NextStepDays = null;
            NextStepSubject = null;
            return;
        }

        if (days is < 0 or > 365)
        {
            throw new BusinessRuleException("crm.stage.next_step_days", "Срок следующего шага — от 0 до 365 дней.");
        }

        NextStepDays = days;
        NextStepSubject = DomainText.Require(subject, CrmActivity.SubjectMaxLength, "Задача");
    }

    /// <summary>Правила по умолчанию (ТЗ §5: включены для двух этапов).</summary>
    public static (int Days, string Subject)? DefaultNextStep(string stageName) => stageName switch
    {
        "КП отправлено" => (3, "Позвонить: обсудить КП"),
        "Образцы / расчёт" => (5, "Уточнить получение образцов"),
        _ => null,
    };

    public void Archive()
    {
        if (IsFinal)
        {
            throw new BusinessRuleException("crm.stage.final", "Этапы «выигрыш» и «проигрыш» есть у каждой воронки — их нельзя отправить в архив.");
        }

        IsArchived = true;
    }

    public void Restore() => IsArchived = false;

    public static string KindName(CrmStageKind kind) => kind switch
    {
        CrmStageKind.Won => "Выигрыш",
        CrmStageKind.Lost => "Проигрыш",
        _ => "Рабочий",
    };
}

/// <summary>Вид записи простого справочника CRM.</summary>
public enum CrmReferenceKind : byte
{
    /// <summary>Источник обращения: сайт, звонок, выставка…</summary>
    Source = 1,

    /// <summary>Причина отказа лида и проигрыша сделки (D603: один список на оба случая).</summary>
    LossReason = 2,
}

/// <summary>
/// Простой справочник CRM (D603): источники обращений и причины отказа. Только название и порядок; не удаляется —
/// архивируется; у действующих одного вида название уникально.
/// </summary>
public sealed class CrmReference
{
    public const int NameMaxLength = 100;

    public static readonly IReadOnlyList<string> DefaultSources =
        ["Сайт", "Звонок", "Выставка", "Маркетплейс", "Рекомендация", "Соцсети", "Мессенджер"];

    public static readonly IReadOnlyList<string> DefaultLossReasons =
        ["Дорого", "Не подошли сроки", "Выбрали другого поставщика", "Нет ответа", "Нет потребности", "Дубликат"];

    private CrmReference()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public CrmReferenceKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmReference Create(long organizationId, CrmReferenceKind kind, string? name, int sortOrder) =>
        Enum.IsDefined(kind)
            ? new() { OrganizationId = organizationId, Kind = kind, Name = DomainText.Require(name, NameMaxLength, "Название"), SortOrder = sortOrder }
            : throw new BusinessRuleException("crm.reference.kind", "Неизвестный справочник CRM.");

    public void Rename(string? name)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", "Запись в архиве. Сначала верните её из архива.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название");
    }

    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    public static string KindName(CrmReferenceKind kind) => kind switch
    {
        CrmReferenceKind.Source => "Источник",
        CrmReferenceKind.LossReason => "Причина отказа",
        _ => kind.ToString(),
    };
}
