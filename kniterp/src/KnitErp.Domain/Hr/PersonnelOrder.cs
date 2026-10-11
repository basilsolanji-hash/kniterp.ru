using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>Вид кадрового приказа. Числа хранятся в базе и закреплены CHECK.</summary>
public enum PersonnelOrderKind : byte
{
    /// <summary>Приём на работу — форма Т-1.</summary>
    Hire = 1,

    /// <summary>Перевод на другую работу — форма Т-5.</summary>
    Transfer = 2,

    /// <summary>Предоставление отпуска — форма Т-6.</summary>
    Leave = 3,

    /// <summary>Прекращение трудового договора — форма Т-8.</summary>
    Dismissal = 4,
}

public enum PersonnelOrderStatus : byte
{
    Draft = 1,
    Posted = 2,

    /// <summary>Черновик аннулирован. Проведённый приказ не аннулируется — исправление новым приказом.</summary>
    Cancelled = 9,
}

/// <summary>Вид отпуска (ТК РФ) и его код в табеле Т-12/Т-13.</summary>
public enum LeaveKind : byte
{
    Annual = 1,
    Unpaid = 2,
    Study = 3,
    Maternity = 4,
    ChildCare = 5,
    Additional = 6,
}

public static class LeaveKinds
{
    public static readonly IReadOnlyList<LeaveKind> All = Enum.GetValues<LeaveKind>();

    public static string Name(LeaveKind kind) => kind switch
    {
        LeaveKind.Annual => "Ежегодный основной оплачиваемый (ст. 115 ТК РФ)",
        LeaveKind.Unpaid => "Без сохранения заработной платы (ст. 128 ТК РФ)",
        LeaveKind.Study => "Учебный (ст. 173 ТК РФ)",
        LeaveKind.Maternity => "По беременности и родам (ст. 255 ТК РФ)",
        LeaveKind.ChildCare => "По уходу за ребёнком (ст. 256 ТК РФ)",
        LeaveKind.Additional => "Ежегодный дополнительный оплачиваемый (ст. 116 ТК РФ)",
        _ => kind.ToString(),
    };

    /// <summary>Буквенный код неявки в табеле (приложение к постановлению Госкомстата № 1).</summary>
    public static string TimesheetCode(LeaveKind kind) => kind switch
    {
        LeaveKind.Annual => "ОТ",
        LeaveKind.Unpaid => "ДО",
        LeaveKind.Study => "У",
        LeaveKind.Maternity => "Р",
        LeaveKind.ChildCare => "ОЖ",
        LeaveKind.Additional => "ОД",
        _ => "ОТ",
    };
}

/// <summary>Основание прекращения трудового договора: код, статья ТК РФ и формулировка для записи в Т-8 (D204).</summary>
public sealed record DismissalGround(string Code, string Article, string Title, string Wording);

public static class DismissalGrounds
{
    public static readonly IReadOnlyList<DismissalGround> All =
    [
        new("77-1", "п. 1 ч. 1 ст. 77", "Соглашение сторон",
            "Трудовой договор расторгнут по соглашению сторон, пункт 1 части первой статьи 77 Трудового кодекса Российской Федерации"),
        new("77-2", "п. 2 ч. 1 ст. 77", "Истечение срока трудового договора",
            "Трудовой договор прекращён в связи с истечением срока трудового договора, пункт 2 части первой статьи 77 Трудового кодекса Российской Федерации"),
        new("80", "п. 3 ч. 1 ст. 77, ст. 80", "По инициативе работника (собственное желание)",
            "Трудовой договор расторгнут по инициативе работника, пункт 3 части первой статьи 77 Трудового кодекса Российской Федерации"),
        new("77-5", "п. 5 ч. 1 ст. 77", "Перевод к другому работодателю",
            "Уволен в порядке перевода к другому работодателю, пункт 5 части первой статьи 77 Трудового кодекса Российской Федерации"),
        new("71", "ч. 4 ст. 71", "Неудовлетворительный результат испытания",
            "Трудовой договор расторгнут в связи с неудовлетворительным результатом испытания, часть четвёртая статьи 71 Трудового кодекса Российской Федерации"),
        new("81-1", "п. 1 ч. 1 ст. 81", "Ликвидация организации",
            "Уволен в связи с ликвидацией организации, пункт 1 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-2", "п. 2 ч. 1 ст. 81", "Сокращение численности или штата",
            "Уволен в связи с сокращением штата работников организации, пункт 2 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-3", "п. 3 ч. 1 ст. 81", "Несоответствие занимаемой должности",
            "Уволен в связи с несоответствием работника занимаемой должности вследствие недостаточной квалификации, подтверждённой результатами аттестации, пункт 3 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-5", "п. 5 ч. 1 ст. 81", "Неоднократное неисполнение обязанностей",
            "Уволен за неоднократное неисполнение работником без уважительных причин трудовых обязанностей, если он имеет дисциплинарное взыскание, пункт 5 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-6а", "подп. «а» п. 6 ч. 1 ст. 81", "Прогул",
            "Уволен за однократное грубое нарушение работником трудовых обязанностей — прогул, подпункт «а» пункта 6 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-6б", "подп. «б» п. 6 ч. 1 ст. 81", "Появление на работе в состоянии опьянения",
            "Уволен за появление на работе в состоянии алкогольного, наркотического или иного токсического опьянения, подпункт «б» пункта 6 части первой статьи 81 Трудового кодекса Российской Федерации"),
        new("81-6г", "подп. «г» п. 6 ч. 1 ст. 81", "Хищение по месту работы",
            "Уволен за совершение по месту работы хищения чужого имущества, установленного вступившим в законную силу приговором суда, подпункт «г» пункта 6 части первой статьи 81 Трудового кодекса Российской Федерации"),
    ];

    public static DismissalGround? Find(string? code) => All.FirstOrDefault(g => g.Code == code);
}

/// <summary>Поля приказа из формы. Какие обязательны — зависит от вида приказа.</summary>
public sealed record PersonnelOrderValues(
    PersonnelOrderKind Kind,
    DateOnly OrderDate,
    DateOnly EffectiveFrom,
    long? EmployeeId = null,
    string? PersonnelNumber = null,
    string? LastName = null,
    string? FirstName = null,
    string? MiddleName = null,
    long? DepartmentId = null,
    long? PositionId = null,
    decimal? Rate = null,
    decimal? Salary = null,
    byte? TrialMonths = null,
    DateOnly? EffectiveTo = null,
    LeaveKind? LeaveKind = null,
    string? DismissalGround = null,
    string? Basis = null);

/// <summary>
/// Кадровый приказ (D204): черновик → проведён. Проведённый не меняется и не удаляется (триггер в базе): ошибка исправляется
/// новым приказом, как у складских документов. Проведение меняет карточку сотрудника и пишет историю назначений.
/// </summary>
public sealed class PersonnelOrder
{
    public const int BasisMaxLength = 500;
    public const decimal MaxRate = 1.5m;

    private PersonnelOrder()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public PersonnelOrderKind Kind { get; private set; }
    public PersonnelOrderStatus Status { get; private set; }
    public DateOnly OrderDate { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public long? EmployeeId { get; private set; }
    public string? PersonnelNumber { get; private set; }
    public string? LastName { get; private set; }
    public string? FirstName { get; private set; }
    public string? MiddleName { get; private set; }
    public long? DepartmentId { get; private set; }
    public long? PositionId { get; private set; }
    public decimal? Rate { get; private set; }
    public decimal? Salary { get; private set; }
    public byte? TrialMonths { get; private set; }
    public LeaveKind? LeaveKind { get; private set; }
    public string? DismissalGround { get; private set; }
    public string? Basis { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? PostedByUserId { get; private set; }
    public DateTime? PostedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Календарных дней отпуска (с первого по последний включительно).</summary>
    public int? LeaveDays => Kind == PersonnelOrderKind.Leave && EffectiveTo is { } to ? to.DayNumber - EffectiveFrom.DayNumber + 1 : null;

    public string FullName => LastName is null ? string.Empty : MiddleName is null ? $"{LastName} {FirstName}" : $"{LastName} {FirstName} {MiddleName}";

    public static PersonnelOrder Create(long organizationId, string number, PersonnelOrderValues values, long userId, DateTime nowUtc)
    {
        var order = new PersonnelOrder
        {
            OrganizationId = organizationId,
            Number = number,
            Kind = values.Kind,
            Status = PersonnelOrderStatus.Draft,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        order.Apply(values);
        return order;
    }

    /// <summary>Изменение черновика. Вид приказа не меняется — для другого вида создаётся новый приказ.</summary>
    public void Edit(PersonnelOrderValues values)
    {
        EnsureDraft();
        if (values.Kind != Kind)
        {
            throw new BusinessRuleException("hr.order.kind_change", "Вид приказа не меняется: создайте новый приказ.");
        }

        Apply(values);
    }

    public void Post(long userId, DateTime nowUtc)
    {
        EnsureDraft();
        Status = PersonnelOrderStatus.Posted;
        PostedByUserId = userId;
        PostedAtUtc = nowUtc;
    }

    /// <summary>Сотрудник, созданный при проведении приказа о приёме.</summary>
    public void AttachHiredEmployee(long employeeId)
    {
        if (Kind != PersonnelOrderKind.Hire || EmployeeId is not null)
        {
            throw new BusinessRuleException("hr.order.attach", "Сотрудник уже указан в приказе.");
        }

        EmployeeId = employeeId;
    }

    public void Cancel()
    {
        EnsureDraft();
        Status = PersonnelOrderStatus.Cancelled;
    }

    public static string KindName(PersonnelOrderKind kind) => kind switch
    {
        PersonnelOrderKind.Hire => "Приём на работу",
        PersonnelOrderKind.Transfer => "Перевод",
        PersonnelOrderKind.Leave => "Отпуск",
        PersonnelOrderKind.Dismissal => "Увольнение",
        _ => kind.ToString(),
    };

    public static string FormName(PersonnelOrderKind kind) => kind switch
    {
        PersonnelOrderKind.Hire => "Т-1",
        PersonnelOrderKind.Transfer => "Т-5",
        PersonnelOrderKind.Leave => "Т-6",
        PersonnelOrderKind.Dismissal => "Т-8",
        _ => string.Empty,
    };

    public static string StatusName(PersonnelOrderStatus status) => status switch
    {
        PersonnelOrderStatus.Draft => "Черновик",
        PersonnelOrderStatus.Posted => "Проведён",
        PersonnelOrderStatus.Cancelled => "Аннулирован",
        _ => status.ToString(),
    };

    private void Apply(PersonnelOrderValues v)
    {
        if (!Enum.IsDefined(v.Kind))
        {
            throw new BusinessRuleException("hr.order.kind", "Неизвестный вид приказа.");
        }

        if (v.EffectiveFrom < v.OrderDate.AddYears(-1) || v.EffectiveFrom > v.OrderDate.AddYears(1))
        {
            throw new BusinessRuleException("hr.order.effective_date", "Дата действия приказа — в пределах года от даты приказа.");
        }

        var basis = string.IsNullOrWhiteSpace(v.Basis) ? null : v.Basis.Trim();
        if (basis is { Length: > BasisMaxLength })
        {
            throw new BusinessRuleException("field.too_long", $"Поле «Основание» длиннее {BasisMaxLength} символов.");
        }

        OrderDate = v.OrderDate;
        EffectiveFrom = v.EffectiveFrom;
        Basis = basis;
        EffectiveTo = null;
        EmployeeId = null;
        PersonnelNumber = LastName = FirstName = MiddleName = null;
        DepartmentId = PositionId = null;
        Rate = Salary = null;
        TrialMonths = null;
        LeaveKind = null;
        DismissalGround = null;

        switch (v.Kind)
        {
            case PersonnelOrderKind.Hire:
                PersonnelNumber = Text(v.PersonnelNumber, 20, "Табельный номер").ToUpperInvariant();
                LastName = Text(v.LastName, 100, "Фамилия");
                FirstName = Text(v.FirstName, 100, "Имя");
                MiddleName = string.IsNullOrWhiteSpace(v.MiddleName) ? null : Text(v.MiddleName, 100, "Отчество");
                Assignment(v);
                if (v.TrialMonths is > 6)
                {
                    throw new BusinessRuleException("hr.order.trial", "Испытание — не больше 6 месяцев (ст. 70 ТК РФ).");
                }

                TrialMonths = v.TrialMonths is 0 ? null : v.TrialMonths;
                break;
            case PersonnelOrderKind.Transfer:
                EmployeeId = RequireEmployee(v);
                Assignment(v);
                break;
            case PersonnelOrderKind.Leave:
                EmployeeId = RequireEmployee(v);
                if (v.LeaveKind is not { } kind || !Enum.IsDefined(kind))
                {
                    throw new BusinessRuleException("hr.order.leave_kind", "Укажите вид отпуска.");
                }

                if (v.EffectiveTo is not { } to || to < v.EffectiveFrom)
                {
                    throw new BusinessRuleException("hr.order.leave_period", "Отпуск: дата окончания не раньше даты начала.");
                }

                if (to.DayNumber - v.EffectiveFrom.DayNumber + 1 > 1100)
                {
                    throw new BusinessRuleException("hr.order.leave_long", "Отпуск длиннее трёх лет.");
                }

                LeaveKind = kind;
                EffectiveTo = to;
                break;
            case PersonnelOrderKind.Dismissal:
                EmployeeId = RequireEmployee(v);
                DismissalGround = DismissalGrounds.Find(v.DismissalGround)?.Code
                                  ?? throw new BusinessRuleException("hr.order.ground", "Укажите основание увольнения по ТК РФ.");
                break;
        }
    }

    private void Assignment(PersonnelOrderValues v)
    {
        if (v.DepartmentId is not { } dep || v.PositionId is not { } pos)
        {
            throw new BusinessRuleException("hr.order.assignment", "Укажите подразделение и должность.");
        }

        var rate = v.Rate ?? 1m;
        if (rate <= 0 || rate > MaxRate || decimal.Round(rate, 2) != rate)
        {
            throw new BusinessRuleException("hr.order.rate", "Ставка — от 0,01 до 1,5.");
        }

        if (v.Salary is < 0)
        {
            throw new BusinessRuleException("hr.order.salary", "Оклад не может быть отрицательным.");
        }

        DepartmentId = dep;
        PositionId = pos;
        Rate = rate;
        Salary = v.Salary is null ? null : Money.Round(v.Salary.Value);
    }

    private static long RequireEmployee(PersonnelOrderValues v) =>
        v.EmployeeId is { } id and > 0 ? id : throw new BusinessRuleException("hr.order.employee", "Укажите сотрудника.");

    private static string Text(string? value, int max, string field)
    {
        var t = value?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            throw new BusinessRuleException("field.required", $"Поле «{field}» обязательно.");
        }

        return t.Length <= max ? t : throw new BusinessRuleException("field.too_long", $"Поле «{field}» длиннее {max} символов.");
    }

    private void EnsureDraft()
    {
        if (Status != PersonnelOrderStatus.Draft)
        {
            throw new BusinessRuleException("hr.order.not_draft", "Приказ уже проведён или аннулирован: исправление — новым приказом.");
        }
    }
}

/// <summary>Запись истории назначений сотрудника (только вставка): что изменил проведённый приказ.</summary>
public sealed class EmploymentRecord
{
    private EmploymentRecord()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public long? OrderId { get; private set; }
    public PersonnelOrderKind Kind { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public long DepartmentId { get; private set; }
    public long PositionId { get; private set; }
    public decimal Rate { get; private set; }
    public decimal? Salary { get; private set; }
    public LeaveKind? LeaveKind { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static EmploymentRecord From(PersonnelOrder order, long employeeId, long departmentId, long positionId, decimal rate,
        decimal? salary, string? note, DateTime nowUtc) => new()
    {
        OrganizationId = order.OrganizationId,
        EmployeeId = employeeId,
        OrderId = order.Id,
        Kind = order.Kind,
        EffectiveFrom = order.EffectiveFrom,
        EffectiveTo = order.EffectiveTo,
        DepartmentId = departmentId,
        PositionId = positionId,
        Rate = rate,
        Salary = salary,
        LeaveKind = order.LeaveKind,
        Note = note,
        CreatedAtUtc = nowUtc,
    };
}
