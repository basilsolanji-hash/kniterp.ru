using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>
/// Сдельная расценка (D211): операция производства (и при необходимости изделие) → цена за штуку или за килограмм с даты.
/// Только вставка: новая цена — новая строка с новой датой, история расценок сохраняется.
/// </summary>
public sealed class PieceRate
{
    public const int NameMaxLength = 200;
    public const int UnitMaxLength = 10;

    /// <summary>Частые единицы для подсказки в форме; расценка принимает любую единицу изделия (D215).</summary>
    public static readonly IReadOnlyList<string> CommonUnits = ["шт", "пар", "кг", "м", "компл"];

    /// <summary>Единица для сравнения: без пробелов и точки на конце, строчными — «Шт.» и «шт» одно и то же.</summary>
    public static string NormalizeUnit(string? unit) => (unit ?? string.Empty).Trim().TrimEnd('.').Trim().ToLowerInvariant();

    private PieceRate()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Операция в производстве (идентификатор из <c>IWorkOutputSource</c>); FK нет — производство ведёт другой модуль.</summary>
    public long OperationId { get; private set; }

    public string OperationName { get; private set; } = string.Empty;

    /// <summary>Изделие; пусто — расценка операции для любого изделия.</summary>
    public long? ItemId { get; private set; }

    public string Unit { get; private set; } = "шт";
    public decimal Price { get; private set; }
    public DateOnly ValidFrom { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static PieceRate Create(long organizationId, long operationId, string operationName, long? itemId, string unit, decimal price,
        DateOnly validFrom, long userId, DateTime nowUtc)
    {
        if (operationId <= 0)
        {
            throw new BusinessRuleException("hr.rate.operation", "Укажите операцию.");
        }

        unit = NormalizeUnit(unit);
        if (unit.Length is 0 or > UnitMaxLength)
        {
            throw new BusinessRuleException("hr.rate.unit", "Единица расценки — обозначение единицы изделия до 10 символов (шт, пар, кг…).");
        }

        if (price <= 0 || decimal.Round(price, 4) != price)
        {
            throw new BusinessRuleException("hr.rate.price", "Расценка — больше нуля, до четырёх знаков после запятой.");
        }

        return new PieceRate
        {
            OrganizationId = organizationId,
            OperationId = operationId,
            OperationName = DomainText.Require(operationName, NameMaxLength, "Операция"),
            ItemId = itemId is > 0 ? itemId : null,
            Unit = unit,
            Price = price,
            ValidFrom = validFrom,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>
    /// Расценка на дату для операции и изделия: сначала по изделию, затем общая по операции; из подходящих — с последней датой не позже дня работы.
    /// </summary>
    public static PieceRate? Find(IEnumerable<PieceRate> rates, long operationId, long? itemId, DateOnly date)
    {
        var applicable = rates.Where(r => r.OperationId == operationId && r.ValidFrom <= date).ToList();
        return applicable.Where(r => itemId is not null && r.ItemId == itemId).MaxBy(r => r.ValidFrom)
               ?? applicable.Where(r => r.ItemId is null).MaxBy(r => r.ValidFrom);
    }
}

/// <summary>Вид начисления в расчётном листке. Числа хранятся в базе.</summary>
public enum PayrollLineKind : byte
{
    /// <summary>Сдельно: выработка × расценка.</summary>
    Piece = 1,

    /// <summary>Оклад (тариф) за отработанное время.</summary>
    Salary = 2,

    /// <summary>Доплата за работу в ночное время (ст. 154 ТК РФ).</summary>
    Night = 3,

    /// <summary>Оплата сверхурочной работы (ст. 152 ТК РФ).</summary>
    Overtime = 4,

    /// <summary>Доплата за работу в выходные и праздники (ст. 153 ТК РФ).</summary>
    Holiday = 5,

    /// <summary>Перерасчёт сдельной оплаты за утверждённый месяц, выработку которого исправили (D219).</summary>
    PieceCorrection = 6,
}

public enum PayrollStatus : byte
{
    Draft = 1,
    Approved = 2,
}

/// <summary>
/// Правила доплат (D212) — минимальные по ТК РФ: ночные — 20 % часовой ставки за час (ст. 154 ТК РФ, постановление Правительства № 554),
/// сверхурочные — первые 2 часа в день в полуторном, остальные в двойном размере (ст. 152), выходные и праздники — не менее двойной
/// (ст. 153: доплата сверх уже оплаченного часа — ещё одна часовая ставка).
/// </summary>
public static class PayrollRules
{
    public const decimal NightShare = 0.20m;
    public const decimal OvertimeFirst = 1.5m;
    public const decimal OvertimeNext = 2m;
    public const decimal OvertimeFirstHours = 2m;
    public const decimal HolidayExtra = 1m;

    /// <summary>Оплата сверхурочных одного дня по часовой ставке.</summary>
    public static decimal OvertimeDay(decimal hours, decimal hourRate, decimal first = OvertimeFirst, decimal next = OvertimeNext) =>
        Math.Min(hours, OvertimeFirstHours) * hourRate * first + Math.Max(0, hours - OvertimeFirstHours) * hourRate * next;

    /// <summary>Оклад за отработанное время: оклад × отработано / норма (норма — по графику с учётом ставки).</summary>
    /// <remarks>Работает и для дней: отработано дней / норма дней (база оклада задаётся в настройках, D216).</remarks>
    public static decimal SalaryPart(decimal salary, decimal workedHours, decimal normHours) =>
        normHours <= 0 ? 0 : Money.Round(salary * Math.Min(workedHours, normHours) / normHours);

    public static string KindName(PayrollLineKind kind) => kind switch
    {
        PayrollLineKind.Piece => "Сдельная оплата",
        PayrollLineKind.Salary => "Оплата по окладу (тарифу)",
        PayrollLineKind.Night => "Доплата за работу в ночное время (ст. 154 ТК РФ)",
        PayrollLineKind.Overtime => "Оплата сверхурочной работы (ст. 152 ТК РФ)",
        PayrollLineKind.Holiday => "Оплата работы в выходные и праздники (ст. 153 ТК РФ)",
        PayrollLineKind.PieceCorrection => "Перерасчёт сдельной оплаты за прошлый месяц",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Расчёт заработка за месяц (D212): черновик пересчитывается целиком, утверждённый не меняется (триггер); НДФЛ и взносы — в 1С ЗУП.
/// </summary>
public sealed class PayrollRun
{
    private PayrollRun()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public short Year { get; private set; }
    public byte Month { get; private set; }
    public PayrollStatus Status { get; private set; }

    /// <summary>При расчёте все табели месяца были закрыты.</summary>
    public bool TimesheetsClosed { get; private set; }

    public DateTime CalculatedAtUtc { get; private set; }
    public long CalculatedByUserId { get; private set; }

    /// <summary>SHA-256 выработки месяца на момент расчёта (D217): исправление выработки после расчёта видно при утверждении и выгрузке.</summary>
    public byte[]? OutputFingerprint { get; private set; }
    public long? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public DateOnly From => new(Year, Month, 1);
    public DateOnly To => From.AddMonths(1).AddDays(-1);

    public static PayrollRun Create(long organizationId, int year, int month, long userId, DateTime nowUtc)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100)
        {
            throw new BusinessRuleException("hr.payroll.period", "Неверный месяц расчёта.");
        }

        return new PayrollRun
        {
            OrganizationId = organizationId, Year = (short)year, Month = (byte)month, Status = PayrollStatus.Draft,
            CalculatedAtUtc = nowUtc, CalculatedByUserId = userId,
        };
    }

    public void Recalculated(bool timesheetsClosed, byte[] outputFingerprint, long userId, DateTime nowUtc)
    {
        EnsureDraft();
        TimesheetsClosed = timesheetsClosed;
        OutputFingerprint = outputFingerprint;
        CalculatedByUserId = userId;
        CalculatedAtUtc = nowUtc;
    }

    /// <summary>Выработка совпадает с той, по которой считали. Старые расчёты без отпечатка считаются совпадающими.</summary>
    public bool OutputMatches(byte[] currentFingerprint) => OutputFingerprint is null || OutputFingerprint.AsSpan().SequenceEqual(currentFingerprint);

    /// <summary>Утверждение: только по закрытым табелям — иначе часы ещё могут измениться.</summary>
    public void Approve(long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (!TimesheetsClosed)
        {
            throw new BusinessRuleException("hr.payroll.timesheets_open", "Утвердить расчёт можно только по закрытым табелям: закройте табели и пересчитайте.");
        }

        Status = PayrollStatus.Approved;
        ApprovedByUserId = userId;
        ApprovedAtUtc = nowUtc;
    }

    public void EnsureDraft()
    {
        if (Status != PayrollStatus.Draft)
        {
            throw new BusinessRuleException("hr.payroll.approved", "Расчёт утверждён и не меняется.");
        }
    }
}

/// <summary>Строка начисления сотруднику в расчёте.</summary>
public sealed class PayrollLine
{
    public const int DescriptionMaxLength = 300;

    private PayrollLine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long RunId { get; private set; }
    public long EmployeeId { get; private set; }
    public PayrollLineKind Kind { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal Rate { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>Месяц, за который сделан перерасчёт (только у <see cref="PayrollLineKind.PieceCorrection"/>): первое число месяца.</summary>
    public DateOnly? CorrectsPeriod { get; private set; }

    /// <summary>
    /// Доплата считалась от часовой ставки «сдельный заработок / часы» (у сдельщика без оклада и тарифа): при исправлении выработки
    /// она пересчитывается пропорционально сдельной оплате (D220). Только у ночных, сверхурочных и праздничных.
    /// </summary>
    public bool FromPiece { get; private set; }

    public PayrollLine MarkFromPiece()
    {
        if (Kind is not (PayrollLineKind.Night or PayrollLineKind.Overtime or PayrollLineKind.Holiday))
        {
            throw new BusinessRuleException("hr.payroll.from_piece", "От сдельной ставки считаются только доплаты.");
        }

        FromPiece = true;
        return this;
    }

    /// <summary>
    /// Доплата по перерасчёту за утверждённый месяц <paramref name="period"/>, выработку которого исправили (D219, D220). Сдельная —
    /// видом «Перерасчёт сдельной оплаты», доплата сдельщику — тем же видом доплаты (свой код в 1С: ЗУП). Только положительная.
    /// </summary>
    public static PayrollLine Correction(long organizationId, long runId, long employeeId, PayrollLineKind kind, DateOnly period, decimal amount)
    {
        if (amount <= 0 || period.Day != 1
            || kind is not (PayrollLineKind.PieceCorrection or PayrollLineKind.Night or PayrollLineKind.Overtime or PayrollLineKind.Holiday))
        {
            throw new BusinessRuleException("hr.payroll.correction", "Перерасчёт — положительная доплата за прошлый месяц.");
        }

        var description = kind == PayrollLineKind.PieceCorrection
            ? $"Перерасчёт сдельной оплаты за {period:MM.yyyy}: исправлена выработка"
            : $"Перерасчёт за {period:MM.yyyy}: исправлена выработка — изменилась часовая ставка сдельщика";
        var line = Create(organizationId, runId, employeeId, kind, description, 1, amount, amount);
        line.CorrectsPeriod = period;
        return line;
    }

    public static PayrollLine Create(long organizationId, long runId, long employeeId, PayrollLineKind kind, string description, decimal quantity,
        decimal rate, decimal amount)
    {
        var d = description.Length > DescriptionMaxLength ? description[..DescriptionMaxLength] : description;
        return new PayrollLine
        {
            OrganizationId = organizationId, RunId = runId, EmployeeId = employeeId, Kind = kind, Description = d,
            Quantity = decimal.Round(quantity, 6), Rate = decimal.Round(rate, 4), Amount = Money.Round(amount),
        };
    }
}
