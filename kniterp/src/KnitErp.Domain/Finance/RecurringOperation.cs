using KnitErp.Domain.Common;

namespace KnitErp.Domain.Finance;

public enum RecurrencePeriod : byte
{
    Monthly = 1,
    Weekly = 2,
    Quarterly = 3,
}

public enum RecurringRunStatus : byte
{
    /// <summary>Создан по расписанию, ждёт решения человека.</summary>
    Pending = 1,

    /// <summary>Проведён: создана денежная операция.</summary>
    Posted = 2,

    /// <summary>Пропущен с причиной.</summary>
    Skipped = 3,
}

/// <summary>
/// D147: регулярная денежная операция (аренда, связь, зарплатный аванс) — шаблон с расписанием. По расписанию создаётся черновик
/// (<see cref="RecurringOperationRun"/>), проводит его человек: денежная операция создаётся только по его нажатию, с проверкой прав
/// и закрытого периода. Шаблон не удаляется — отключается (архив).
/// </summary>
public sealed class RecurringOperation
{
    public const int NameMaxLength = 200;
    public const int MaxCatchUp = 12;

    private RecurringOperation()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public MoneyOperationKind Kind { get; private set; }
    public long AccountId { get; private set; }
    public long? CashFlowItemId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Party { get; private set; }
    public string Basis { get; private set; } = string.Empty;
    public RecurrencePeriod Period { get; private set; }

    /// <summary>День месяца (1–31, в коротком месяце — последний день) или день недели (1 — понедельник … 7 — воскресенье).</summary>
    public int Day { get; private set; }

    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    /// <summary>Дата следующего черновика.</summary>
    public DateOnly NextDate { get; private set; }

    public bool IsArchived { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static RecurringOperation Create(long organizationId, RecurringOperationData d, long userId, DateTime nowUtc)
    {
        var r = new RecurringOperation { OrganizationId = organizationId, CreatedByUserId = userId, CreatedAtUtc = nowUtc };
        r.Update(d);
        return r;
    }

    public void Update(RecurringOperationData d)
    {
        if (d.Kind is not (MoneyOperationKind.Income or MoneyOperationKind.Expense))
        {
            throw new BusinessRuleException("recurring.kind", "Регулярная операция — поступление или выдача денег.");
        }

        if (d.Amount <= 0 || Money.Round(d.Amount) != d.Amount || d.Amount > 999_999_999_999m)
        {
            throw new BusinessRuleException("recurring.amount", "Сумма — больше нуля, до копеек.");
        }

        var dayOk = d.Period == RecurrencePeriod.Weekly ? d.Day is >= 1 and <= 7 : d.Day is >= 1 and <= 31;
        if (!Enum.IsDefined(d.Period) || !dayOk)
        {
            throw new BusinessRuleException("recurring.day", "День: для месяца и квартала — 1–31, для недели — 1 (понедельник) … 7 (воскресенье).");
        }

        if (d.EndDate is { } end && end < d.StartDate)
        {
            throw new BusinessRuleException("recurring.end", "Дата окончания — не раньше даты начала.");
        }

        Name = DomainText.Require(d.Name, NameMaxLength, "Название");
        Basis = DomainText.Require(d.Basis, MoneyOperation.BasisMaxLength, "Основание");
        Party = DomainText.Optional(d.Party, MoneyOperation.PartyMaxLength, "Контрагент или получатель");
        Kind = d.Kind;
        AccountId = d.AccountId;
        CashFlowItemId = d.CashFlowItemId;
        Amount = d.Amount;
        var scheduleChanged = Period != d.Period || Day != d.Day || StartDate != d.StartDate;
        Period = d.Period;
        Day = d.Day;
        EndDate = d.EndDate;
        if (scheduleChanged || NextDate == default)
        {
            StartDate = d.StartDate;
            NextDate = FirstOnOrAfter(d.StartDate, Period, Day);
        }
    }

    public void Archive() => IsArchived = true;

    public void Restore(DateOnly today)
    {
        IsArchived = false;
        if (NextDate < today)
        {
            NextDate = FirstOnOrAfter(today, Period, Day); // пропущенное в архиве не догоняется
        }
    }

    public bool Active(DateOnly date) => !IsArchived && (EndDate is null || date <= EndDate);

    /// <summary>Даты черновиков к созданию на сегодня (не больше <see cref="MaxCatchUp"/>, если служба долго не работала); сдвигает NextDate.</summary>
    public IReadOnlyList<DateOnly> TakeDue(DateOnly today)
    {
        var due = new List<DateOnly>();
        while (Active(NextDate) && NextDate <= today && due.Count < MaxCatchUp)
        {
            due.Add(NextDate);
            NextDate = Following(NextDate, Period, Day);
        }

        return due;
    }

    public static DateOnly FirstOnOrAfter(DateOnly from, RecurrencePeriod period, int day)
    {
        if (period == RecurrencePeriod.Weekly)
        {
            var dow = ((int)from.DayOfWeek + 6) % 7 + 1; // 1 — понедельник
            return from.AddDays((day - dow + 7) % 7);
        }

        var step = period == RecurrencePeriod.Quarterly ? 3 : 1;
        var candidate = InMonth(from.Year, from.Month, day);
        return candidate >= from ? candidate : InMonth(from.AddMonths(step).Year, from.AddMonths(step).Month, day);
    }

    public static DateOnly Following(DateOnly date, RecurrencePeriod period, int day)
    {
        if (period == RecurrencePeriod.Weekly)
        {
            return date.AddDays(7);
        }

        var next = date.AddMonths(period == RecurrencePeriod.Quarterly ? 3 : 1);
        return InMonth(next.Year, next.Month, day);
    }

    private static DateOnly InMonth(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    public static string PeriodName(RecurrencePeriod p) => p switch
    {
        RecurrencePeriod.Weekly => "Каждую неделю",
        RecurrencePeriod.Quarterly => "Каждый квартал",
        _ => "Каждый месяц",
    };

    public string Describe() => $"{Name}: {(Kind == MoneyOperationKind.Income ? "поступление" : "выдача")} {Amount:0.00}, {PeriodName(Period).ToLowerInvariant()}, день {Day}";
}

public sealed record RecurringOperationData(
    string? Name, MoneyOperationKind Kind, long AccountId, long? CashFlowItemId, decimal Amount, string? Party, string? Basis,
    RecurrencePeriod Period, int Day, DateOnly StartDate, DateOnly? EndDate);

/// <summary>D147: черновик регулярной операции на дату. Один на шаблон и дату (UNIQUE); проводит или пропускает человек.</summary>
public sealed class RecurringOperationRun
{
    public const int ReasonMaxLength = 500;

    private RecurringOperationRun()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long RecurringOperationId { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal Amount { get; private set; }
    public RecurringRunStatus Status { get; private set; }
    public long? MoneyOperationId { get; private set; }
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? SkipReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static RecurringOperationRun Create(RecurringOperation template, DateOnly dueDate, DateTime nowUtc) => new()
    {
        OrganizationId = template.OrganizationId, RecurringOperationId = template.Id, DueDate = dueDate, Amount = template.Amount,
        Status = RecurringRunStatus.Pending, CreatedAtUtc = nowUtc,
    };

    public void MarkPosted(long moneyOperationId, long userId, DateTime nowUtc)
    {
        EnsurePending();
        Status = RecurringRunStatus.Posted;
        MoneyOperationId = moneyOperationId;
        DecidedByUserId = userId;
        DecidedAtUtc = nowUtc;
    }

    public void Skip(string? reason, long userId, DateTime nowUtc)
    {
        EnsurePending();
        SkipReason = DomainText.Require(reason, ReasonMaxLength, "Причина пропуска");
        Status = RecurringRunStatus.Skipped;
        DecidedByUserId = userId;
        DecidedAtUtc = nowUtc;
    }

    private void EnsurePending()
    {
        if (Status != RecurringRunStatus.Pending)
        {
            throw new BusinessRuleException("recurring.run.decided", "Черновик уже проведён или пропущен.");
        }
    }

    public static string StatusName(RecurringRunStatus s) => s switch
    {
        RecurringRunStatus.Posted => "Проведена",
        RecurringRunStatus.Skipped => "Пропущена",
        _ => "Ждёт проведения",
    };
}
