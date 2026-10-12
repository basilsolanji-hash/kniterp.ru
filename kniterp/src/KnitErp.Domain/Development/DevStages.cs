using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Стадии разработки модели (D702). Числа хранятся в базе — не перенумеровывать; порядок значений — порядок пути модели.
/// </summary>
public enum DevStage : byte
{
    Idea = 1,
    Brief = 2,
    Pattern = 3,
    YarnAndKnit = 4,
    Program = 5,
    Sample = 6,
    Rework = 7,
    Approval = 8,
    Costing = 9,
    Approved = 10,
    Handed = 11,
    Cancelled = 99,
}

/// <summary>Норматив и включённость стадии в организации (D702): настраиваются на странице «Настройки → Разработка».</summary>
public sealed class DevStageSetting
{
    public const int MaxPlanDays = 365;

    private DevStageSetting()
    {
    }

    public long OrganizationId { get; private set; }
    public DevStage Stage { get; private set; }

    /// <summary>Норматив стадии, рабочих дней не различаем — календарных; 0 — без норматива.</summary>
    public int PlanDays { get; private set; }

    public bool IsEnabled { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevStageSetting Create(long organizationId, DevStage stage, int planDays, bool isEnabled)
    {
        var s = new DevStageSetting { OrganizationId = organizationId, Stage = stage };
        s.Set(planDays, isEnabled);
        return s;
    }

    public void Set(int planDays, bool isEnabled)
    {
        if (!DevStages.Path.Contains(Stage) || Stage == DevStage.Handed)
        {
            throw new BusinessRuleException("dev.stage.setting", "У этой стадии нет настроек.");
        }

        if (planDays is < 0 or > MaxPlanDays)
        {
            throw new BusinessRuleException("dev.stage.plan_days", $"Норматив стадии — от 0 до {MaxPlanDays} дней.");
        }

        if (!isEnabled && !DevStages.Optional.Contains(Stage))
        {
            throw new BusinessRuleException("dev.stage.required", "Эту стадию нельзя отключить.");
        }

        PlanDays = planDays;
        IsEnabled = isEnabled;
    }
}

/// <summary>Правила стадий: путь модели, переходы и права (D702, D703).</summary>
public static class DevStages
{
    /// <summary>Путь модели без «Отменено».</summary>
    public static readonly IReadOnlyList<DevStage> Path =
    [
        DevStage.Idea, DevStage.Brief, DevStage.Pattern, DevStage.YarnAndKnit, DevStage.Program, DevStage.Sample, DevStage.Rework,
        DevStage.Approval, DevStage.Costing, DevStage.Approved, DevStage.Handed,
    ];

    /// <summary>Все стадии для отборов и переводов.</summary>
    public static readonly IReadOnlyList<DevStage> All = [.. Path, DevStage.Cancelled];

    /// <summary>Стадии, которые организация может отключить (Допущение D702): без программиста, без доработок, без согласования с заказчиком.</summary>
    public static readonly IReadOnlySet<DevStage> Optional = new HashSet<DevStage>
        { DevStage.Program, DevStage.Rework, DevStage.Approval, DevStage.Costing };

    /// <summary>Работа над моделью закончена: дальше стадии не считаются просроченными и не попадают в «Мои задачи».</summary>
    public static bool IsFinal(DevStage stage) => stage is DevStage.Handed or DevStage.Cancelled;

    /// <summary>Переход на эту стадию (и уход назад с неё) требует права «утверждать» (D703).</summary>
    public static bool NeedsApproval(DevStage stage) => stage is DevStage.Approved or DevStage.Handed;

    public static string Name(DevStage stage) => stage switch
    {
        DevStage.Idea => "Идея",
        DevStage.Brief => "Задание",
        DevStage.Pattern => "Рисунок",
        DevStage.YarnAndKnit => "Пряжа и вязка",
        DevStage.Program => "Программа",
        DevStage.Sample => "Образец",
        DevStage.Rework => "Доработка",
        DevStage.Approval => "Согласование",
        DevStage.Costing => "Себестоимость",
        DevStage.Approved => "Утверждено",
        DevStage.Handed => "Передано в производство",
        DevStage.Cancelled => "Отменено",
        _ => stage.ToString(),
    };

    /// <summary>Норматив по умолчанию, дней (Допущение D702).</summary>
    public static int DefaultPlanDays(DevStage stage) => stage switch
    {
        DevStage.Idea => 3,
        DevStage.Brief => 3,
        DevStage.Pattern => 7,
        DevStage.YarnAndKnit => 5,
        DevStage.Program => 3,
        DevStage.Sample => 5,
        DevStage.Rework => 5,
        DevStage.Approval => 7,
        DevStage.Costing => 2,
        DevStage.Approved => 3,
        _ => 0,
    };

    /// <summary>Кто из исполнителей модели отвечает за стадию (D706, для «Моих задач»).</summary>
    public static DevRole? ResponsibleRole(DevStage stage) => stage switch
    {
        DevStage.Idea or DevStage.Brief => DevRole.Designer,
        DevStage.Pattern => DevRole.Dessinator,
        DevStage.YarnAndKnit or DevStage.Sample or DevStage.Rework or DevStage.Costing => DevRole.Technologist,
        DevStage.Program => DevRole.Programmer,
        _ => null,
    };

    /// <summary>
    /// Можно ли перейти со стадии <paramref name="from"/> на <paramref name="to"/> (D703). Возвращает null или текст отказа.
    /// Переход вперёд — на любую включённую стадию (лишние можно пропустить), назад — тоже (доработка после согласования).
    /// «Передано в производство» ставит только передача в производство, «Отменено» — отмена с причиной.
    /// </summary>
    public static string? CheckMove(DevStage from, DevStage to, bool hasPattern, bool canApprove, Func<DevStage, bool> isEnabled)
    {
        if (from == to)
        {
            return "Модель уже на этой стадии.";
        }

        if (from == DevStage.Handed)
        {
            return "Модель передана в производство — изменения вносятся новой версией модели.";
        }

        if (from == DevStage.Cancelled || to == DevStage.Cancelled)
        {
            return "Отмена и возврат в работу — отдельными кнопками.";
        }

        if (to == DevStage.Handed)
        {
            return "В производство модель передаёт кнопка «Передать в производство».";
        }

        if (to == DevStage.Pattern && !hasPattern)
        {
            return "У модели нет рисунка — стадия «Рисунок» пропускается.";
        }

        if (!isEnabled(to))
        {
            return "Эта стадия отключена в настройках разработки.";
        }

        if ((NeedsApproval(to) || NeedsApproval(from)) && !canApprove)
        {
            return "Утверждать модель и снимать утверждение может только главный технолог (право «Разработка: утверждение»).";
        }

        return null;
    }

    /// <summary>Следующая стадия пути: пропускает отключённые и «Рисунок» у модели без рисунка; после «Утверждено» — нет.</summary>
    public static DevStage? Next(DevStage from, bool hasPattern, Func<DevStage, bool> isEnabled)
    {
        if (from is DevStage.Cancelled or DevStage.Approved or DevStage.Handed)
        {
            return null;
        }

        for (var i = IndexOf(from) + 1; i < Path.Count; i++)
        {
            var s = Path[i];
            if ((s == DevStage.Pattern && !hasPattern) || !isEnabled(s))
            {
                continue;
            }

            return s == DevStage.Handed ? null : s;
        }

        return null;
    }

    public static int IndexOf(DevStage stage) => stage == DevStage.Cancelled ? int.MaxValue : Path.ToList().IndexOf(stage);
}
