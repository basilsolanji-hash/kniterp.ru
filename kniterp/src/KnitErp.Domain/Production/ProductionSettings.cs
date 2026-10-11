using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

/// <summary>
/// Настройки производства организации (D92, D94, дополнения; задаёт заказчик): допуск отклонения расхода материалов от нормы
/// и правила баллов за качество и дисциплину. Одна строка на организацию; пока её нет — значения по умолчанию.
/// </summary>
public sealed class ProductionSettings
{
    public const decimal DefaultTolerancePercent = 5m;
    public const decimal MaxTolerancePercent = 50m;
    public const int MaxPoints = 100;

    private ProductionSettings()
    {
    }

    public long OrganizationId { get; private set; }

    /// <summary>Допуск отклонения расхода с отходами от нормы, % (D94).</summary>
    public decimal TolerancePercent { get; private set; } = DefaultTolerancePercent;

    /// <summary>Считать ли баллы. Выключено — сотрудники и мастер баллов не видят.</summary>
    public bool PointsEnabled { get; private set; }

    /// <summary>За сменное задание, выполненное полностью (факт не меньше плана).</summary>
    public int PointsTaskDone { get; private set; } = 1;

    /// <summary>За невыполненное задание прошедшего дня, если сотрудник указал причину.</summary>
    public int PointsTaskMissedWithReason { get; private set; }

    /// <summary>За невыполненное задание прошедшего дня без причины.</summary>
    public int PointsTaskMissedNoReason { get; private set; } = -1;

    /// <summary>За каждую единицу брака, где сотрудник указан виновником на контроле качества.</summary>
    public int PointsPerDefectUnit { get; private set; } = -1;

    public long? UpdatedByUserId { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ProductionSettings Default(long organizationId) => new() { OrganizationId = organizationId };

    public void Update(decimal tolerance, bool pointsEnabled, int taskDone, int missedWithReason, int missedNoReason, int perDefectUnit,
        long userId, DateTime nowUtc)
    {
        if (tolerance < 0 || tolerance > MaxTolerancePercent || decimal.Round(tolerance, 2) != tolerance)
        {
            throw new BusinessRuleException("production.settings.tolerance", "Допуск — от 0 до 50%, не больше двух знаков после запятой.");
        }

        foreach (var points in new[] { taskDone, missedWithReason, missedNoReason, perDefectUnit })
        {
            if (Math.Abs(points) > MaxPoints)
            {
                throw new BusinessRuleException("production.settings.points", "Баллы — целые числа от −100 до 100.");
            }
        }

        TolerancePercent = tolerance;
        PointsEnabled = pointsEnabled;
        PointsTaskDone = taskDone;
        PointsTaskMissedWithReason = missedWithReason;
        PointsTaskMissedNoReason = missedNoReason;
        PointsPerDefectUnit = perDefectUnit;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Баллы сотрудника за период по счётчикам (единицы брака — может быть дробным, округление до целого вниз по модулю).</summary>
    public int Points(int tasksDone, int missedWithReason, int missedNoReason, decimal defectUnits) =>
        tasksDone * PointsTaskDone + missedWithReason * PointsTaskMissedWithReason + missedNoReason * PointsTaskMissedNoReason
        + (int)decimal.Truncate(defectUnits * PointsPerDefectUnit);

    public string Describe() =>
        $"допуск {TolerancePercent:0.##}%; баллы {(PointsEnabled ? "включены" : "выключены")}: выполнено {PointsTaskDone}, "
        + $"не выполнено с причиной {PointsTaskMissedWithReason}, без причины {PointsTaskMissedNoReason}, за единицу брака {PointsPerDefectUnit}";
}
