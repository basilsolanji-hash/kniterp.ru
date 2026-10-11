using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>Код явки или неявки по табелю Т-12/Т-13 (постановление Госкомстата России от 05.01.2004 № 1).</summary>
public sealed record TimesheetCode(string Letter, string Digit, string Name, bool Worked);

/// <summary>Коды табеля, которые использует система (D209). Буквенный код хранится в базе.</summary>
public static class TimesheetCodes
{
    public const string Day = "Я";
    public const string Night = "Н";
    public const string Holiday = "РВ";
    public const string Overtime = "С";
    public const string Weekend = "В";
    public const string Unknown = "НН";

    public static readonly IReadOnlyList<TimesheetCode> All =
    [
        new(Day, "01", "Работа в дневное время", true),
        new(Night, "02", "Работа в ночное время", true),
        new(Holiday, "03", "Работа в выходные и праздничные дни", true),
        new(Overtime, "04", "Сверхурочная работа", true),
        new("К", "06", "Служебная командировка", true),
        new("ПК", "07", "Повышение квалификации с отрывом от работы", true),
        new("ОТ", "09", "Ежегодный основной оплачиваемый отпуск", false),
        new("ОД", "10", "Ежегодный дополнительный оплачиваемый отпуск", false),
        new("У", "11", "Учебный отпуск", false),
        new("Р", "14", "Отпуск по беременности и родам", false),
        new("ОЖ", "15", "Отпуск по уходу за ребёнком", false),
        new("ДО", "16", "Отпуск без сохранения заработной платы", false),
        new("Б", "19", "Временная нетрудоспособность", false),
        new("Г", "23", "Исполнение государственных обязанностей", false),
        new("ПР", "24", "Прогул", false),
        new(Weekend, "26", "Выходные и нерабочие праздничные дни", false),
        new(Unknown, "30", "Неявка по невыясненным причинам", false),
    ];

    public static TimesheetCode? Find(string? letter) => All.FirstOrDefault(c => c.Letter == letter);

    public static bool IsWorked(string letter) => Find(letter)?.Worked == true;
}

public enum WorkScheduleKind : byte
{
    /// <summary>Пятидневная неделя: понедельник — пятница по производственному календарю.</summary>
    FiveDay = 1,

    /// <summary>Сменный график N через M (2/2, 3/3…) от даты начала цикла.</summary>
    Shifts = 2,
}

/// <summary>План на день по графику: рабочий ли день, часы и из них ночные.</summary>
public readonly record struct DayPlan(bool Work, decimal Hours, decimal NightHours);

/// <summary>
/// График работы (D207): пятидневка или сменный N/M, длительность смены, ночные часы смены, суммированный учёт.
/// Справочник — не удаляется, архивируется.
/// </summary>
public sealed class WorkSchedule
{
    public const int NameMaxLength = 100;

    private WorkSchedule()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public WorkScheduleKind Kind { get; private set; }

    /// <summary>Часов в смене (в рабочем дне пятидневки).</summary>
    public decimal ShiftHours { get; private set; }

    /// <summary>Из них ночных (с 22 до 6 часов, ст. 96 ТК РФ).</summary>
    public decimal NightHours { get; private set; }

    public byte WorkDays { get; private set; }
    public byte RestDays { get; private set; }

    /// <summary>Первый день цикла сменного графика.</summary>
    public DateOnly? CycleStart { get; private set; }

    /// <summary>Суммированный учёт рабочего времени (ст. 104 ТК РФ): переработка считается за учётный период, а не за день.</summary>
    public bool Summarized { get; private set; }

    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static WorkSchedule Create(long organizationId, string name, WorkScheduleKind kind, decimal shiftHours, decimal nightHours,
        byte workDays, byte restDays, DateOnly? cycleStart, bool summarized)
    {
        var s = new WorkSchedule { OrganizationId = organizationId };
        s.Change(name, kind, shiftHours, nightHours, workDays, restDays, cycleStart, summarized);
        return s;
    }

    public void Change(string name, WorkScheduleKind kind, decimal shiftHours, decimal nightHours, byte workDays, byte restDays,
        DateOnly? cycleStart, bool summarized)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("hr.schedule.kind", "Неизвестный вид графика.");
        }

        if (shiftHours <= 0 || shiftHours > 24 || nightHours < 0 || nightHours > shiftHours)
        {
            throw new BusinessRuleException("hr.schedule.hours", "Смена — от 0 до 24 часов, ночных — не больше длительности смены.");
        }

        if (kind == WorkScheduleKind.Shifts && (workDays is 0 or > 14 || restDays is 0 or > 14 || cycleStart is null))
        {
            throw new BusinessRuleException("hr.schedule.cycle", "Сменный график: рабочих и выходных дней от 1 до 14 и дата начала цикла.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название графика");
        Kind = kind;
        ShiftHours = decimal.Round(shiftHours, 2);
        NightHours = decimal.Round(nightHours, 2);
        WorkDays = kind == WorkScheduleKind.Shifts ? workDays : (byte)5;
        RestDays = kind == WorkScheduleKind.Shifts ? restDays : (byte)2;
        CycleStart = kind == WorkScheduleKind.Shifts ? cycleStart : null;
        Summarized = summarized;
    }

    public void Archive() => IsArchived = true;

    /// <summary>
    /// План дня. Пятидневка: суббота, воскресенье и праздники — выходные; накануне праздника день короче на час (ст. 95 ТК РФ).
    /// Сменный график праздники не пропускает: смена в праздник — работа в праздничный день.
    /// </summary>
    public DayPlan PlanFor(DateOnly date, IReadOnlySet<DateOnly> holidays) => Plan(Kind, ShiftHours, NightHours, WorkDays, RestDays, CycleStart, date, holidays);

    /// <summary>План по графику по умолчанию — пятидневка по 8 часов (D207), если сотруднику график не назначен.</summary>
    public static DayPlan DefaultPlan(DateOnly date, IReadOnlySet<DateOnly> holidays) =>
        Plan(WorkScheduleKind.FiveDay, 8m, 0m, 5, 2, null, date, holidays);

    private static DayPlan Plan(WorkScheduleKind kind, decimal shiftHours, decimal nightHours, byte workDays, byte restDays, DateOnly? cycleStart,
        DateOnly date, IReadOnlySet<DateOnly> holidays)
    {
        if (kind == WorkScheduleKind.FiveDay)
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(date))
            {
                return new DayPlan(false, 0, 0);
            }

            var hours = holidays.Contains(date.AddDays(1)) ? Math.Max(0, shiftHours - 1) : shiftHours;
            return new DayPlan(true, hours, Math.Min(nightHours, hours));
        }

        var cycle = workDays + restDays;
        var offset = ((date.DayNumber - cycleStart!.Value.DayNumber) % cycle + cycle) % cycle;
        return offset < workDays ? new DayPlan(true, shiftHours, nightHours) : new DayPlan(false, 0, 0);
    }
}

/// <summary>Назначение графика сотруднику с даты. Новое назначение не стирает старое — история остаётся.</summary>
public sealed class EmployeeSchedule
{
    private EmployeeSchedule()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public long ScheduleId { get; private set; }
    public DateOnly ValidFrom { get; private set; }

    public static EmployeeSchedule Create(long organizationId, long employeeId, long scheduleId, DateOnly validFrom) =>
        new() { OrganizationId = organizationId, EmployeeId = employeeId, ScheduleId = scheduleId, ValidFrom = validFrom };
}

public enum TimesheetStatus : byte
{
    Open = 1,
    Closed = 2,
}

/// <summary>
/// Табель учёта рабочего времени подразделения за месяц (Т-12/Т-13, D208). Открыт → закрыт (как закрытый период склада).
/// Закрытый не меняется (триггер в базе); исправление — исправительный табель: новая версия того же месяца с копией строк.
/// </summary>
public sealed class Timesheet
{
    private Timesheet()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DepartmentId { get; private set; }
    public short Year { get; private set; }
    public byte Month { get; private set; }

    /// <summary>0 — первичный табель, 1, 2… — исправительные.</summary>
    public byte Version { get; private set; }

    public long? CorrectsTimesheetId { get; private set; }
    public TimesheetStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? ClosedByUserId { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public DateOnly From => new(Year, Month, 1);
    public DateOnly To => From.AddMonths(1).AddDays(-1);

    public static Timesheet Create(long organizationId, long departmentId, int year, int month, long userId, DateTime nowUtc)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
        {
            throw new BusinessRuleException("hr.timesheet.period", "Неверный месяц табеля.");
        }

        return new Timesheet
        {
            OrganizationId = organizationId, DepartmentId = departmentId, Year = (short)year, Month = (byte)month,
            Status = TimesheetStatus.Open, CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Исправительный табель к закрытому: следующая версия того же месяца.</summary>
    public Timesheet Correction(long userId, DateTime nowUtc)
    {
        if (Status != TimesheetStatus.Closed)
        {
            throw new BusinessRuleException("hr.timesheet.correct_open", "Исправительный табель составляется только к закрытому.");
        }

        return new Timesheet
        {
            OrganizationId = OrganizationId, DepartmentId = DepartmentId, Year = Year, Month = Month, Version = (byte)(Version + 1),
            CorrectsTimesheetId = Id, Status = TimesheetStatus.Open, CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }

    public void Close(long userId, DateTime nowUtc)
    {
        EnsureOpen();
        Status = TimesheetStatus.Closed;
        ClosedByUserId = userId;
        ClosedAtUtc = nowUtc;
    }

    public void EnsureOpen()
    {
        if (Status != TimesheetStatus.Open)
        {
            throw new BusinessRuleException("hr.timesheet.closed", "Табель закрыт: исправление — исправительным табелем.");
        }
    }

    public void EnsureDay(DateOnly date)
    {
        if (date < From || date > To)
        {
            throw new BusinessRuleException("hr.timesheet.day", "День вне месяца табеля.");
        }
    }
}

/// <summary>Отметка дня в табеле: код, часы, из них ночные и сверхурочные. Ручная правка помечается и не затирается автозаполнением.</summary>
public sealed class TimesheetDay
{
    public const decimal MaxHours = 24m;

    private TimesheetDay()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long TimesheetId { get; private set; }
    public long EmployeeId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public decimal Hours { get; private set; }
    public decimal NightHours { get; private set; }
    public decimal OvertimeHours { get; private set; }
    public bool IsManual { get; private set; }

    public static TimesheetDay Create(long organizationId, long timesheetId, long employeeId, DateOnly date, string code, decimal hours,
        decimal nightHours, decimal overtimeHours, bool manual)
    {
        var d = new TimesheetDay { OrganizationId = organizationId, TimesheetId = timesheetId, EmployeeId = employeeId, Date = date };
        d.Set(code, hours, nightHours, overtimeHours, manual);
        return d;
    }

    /// <summary>Новое значение дня. Возвращает «было» для журнала.</summary>
    public string Set(string code, decimal hours, decimal nightHours, decimal overtimeHours, bool manual)
    {
        var c = TimesheetCodes.Find(code?.Trim().ToUpperInvariant())
                ?? throw new BusinessRuleException("hr.timesheet.code", "Неизвестный код табеля.");
        if (hours < 0 || nightHours < 0 || overtimeHours < 0 || hours + overtimeHours > MaxHours || nightHours > hours + overtimeHours)
        {
            throw new BusinessRuleException("hr.timesheet.hours", "Часы: от 0 до 24 в сутки, ночных — не больше отработанных.");
        }

        if (!c.Worked && (hours > 0 || nightHours > 0 || overtimeHours > 0))
        {
            throw new BusinessRuleException("hr.timesheet.absence_hours", "У кода неявки не бывает отработанных часов.");
        }

        var before = Describe();
        Code = c.Letter;
        Hours = decimal.Round(hours, 2);
        NightHours = decimal.Round(nightHours, 2);
        OvertimeHours = decimal.Round(overtimeHours, 2);
        IsManual = manual;
        return before;
    }

    public string Describe() => Code.Length == 0 ? "—"
        : $"{Code} {Hours:0.##}{(NightHours > 0 ? $", ночных {NightHours:0.##}" : "")}{(OvertimeHours > 0 ? $", сверхурочных {OvertimeHours:0.##}" : "")}";
}
