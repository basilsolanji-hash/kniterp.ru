using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>
/// Строка штатного расписания (Т-3, D203): подразделение, должность, число ставок, оклад и надбавка.
/// Не удаляется — архивируется. Пока в организации нет ни одной действующей строки, штат не проверяется.
/// </summary>
public sealed class StaffingPosition
{
    public const decimal MaxRates = 9999m;

    private StaffingPosition()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DepartmentId { get; private set; }
    public long PositionId { get; private set; }
    public decimal Rates { get; private set; }
    public decimal Salary { get; private set; }
    public decimal Allowance { get; private set; }
    public DateOnly ValidFrom { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static StaffingPosition Create(long organizationId, long departmentId, long positionId, decimal rates, decimal salary,
        decimal allowance, DateOnly validFrom)
    {
        var s = new StaffingPosition { OrganizationId = organizationId, DepartmentId = departmentId, PositionId = positionId };
        s.Change(rates, salary, allowance, validFrom);
        return s;
    }

    /// <summary>Изменение. Возвращает «было → стало» для журнала.</summary>
    public (string Before, string After) Change(decimal rates, decimal salary, decimal allowance, DateOnly validFrom)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("hr.staffing.archived", "Строка штатного расписания в архиве.");
        }

        if (rates <= 0 || rates > MaxRates || decimal.Round(rates, 2) != rates)
        {
            throw new BusinessRuleException("hr.staffing.rates", "Количество ставок — больше нуля, до двух знаков после запятой.");
        }

        if (salary < 0 || allowance < 0)
        {
            throw new BusinessRuleException("hr.staffing.salary", "Оклад и надбавка не могут быть отрицательными.");
        }

        var before = Describe();
        Rates = rates;
        Salary = Money.Round(salary);
        Allowance = Money.Round(allowance);
        ValidFrom = validFrom;
        return (before, Describe());
    }

    public void Archive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("hr.staffing.archived", "Строка штатного расписания в архиве.");
        }

        IsArchived = true;
    }

    /// <summary>Хватает ли ставок: занято + новая ≤ штат.</summary>
    public bool Fits(decimal occupied, decimal rate) => occupied + rate <= Rates;

    private string Describe() => Rates == 0 ? string.Empty : $"{Rates:0.##} ст., оклад {Salary:0.##}, надбавка {Allowance:0.##}, с {ValidFrom:dd.MM.yyyy}";
}
