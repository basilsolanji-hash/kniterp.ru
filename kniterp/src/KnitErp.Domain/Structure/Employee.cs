using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Structure;

/// <summary>Статус занятости. Числа хранятся в базе и закреплены CHECK.</summary>
public enum EmploymentStatus : byte
{
    Working = 1,
    OnLeave = 2,
    Dismissed = 9,
}

/// <summary>
/// Карточка сотрудника. Не удаляется: увольнение — статус с датой, история сохраняется.
/// Учётная запись (вход в систему) привязывается отдельно: не у каждого сотрудника цеха она есть.
/// Паспортные и иные персональные данные здесь не хранятся — для них отдельное право и отдельный срез.
/// </summary>
public sealed class Employee
{
    public const int PersonnelNumberMaxLength = 20;
    public const int NamePartMaxLength = 100;

    private Employee()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string PersonnelNumber { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string? MiddleName { get; private set; }
    public long DepartmentId { get; private set; }
    public long PositionId { get; private set; }
    public EmploymentStatus Status { get; private set; }
    public DateOnly HiredOn { get; private set; }
    public DateOnly? DismissedOn { get; private set; }
    public long? UserId { get; private set; }

    /// <summary>Дата рождения (D83) — персональные данные (152-ФЗ): видна кадровику; коллегам — только день и месяц и только с согласия.</summary>
    public DateOnly? BirthDate { get; private set; }

    /// <summary>Сотрудник согласен, чтобы коллеги видели его день рождения в календаре (без года).</summary>
    public bool ShareBirthday { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public string FullName => MiddleName is null ? $"{LastName} {FirstName}" : $"{LastName} {FirstName} {MiddleName}";

    public static Employee Hire(
        long organizationId, string personnelNumber, string lastName, string firstName, string? middleName,
        long departmentId, long positionId, DateOnly hiredOn)
    {
        var e = new Employee
        {
            OrganizationId = organizationId,
            Status = EmploymentStatus.Working,
            HiredOn = hiredOn,
        };
        e.SetNumber(personnelNumber);
        e.SetName(lastName, firstName, middleName);
        e.DepartmentId = departmentId;
        e.PositionId = positionId;
        return e;
    }

    /// <summary>Изменение карточки. Возвращает изменения для журнала аудита.</summary>
    public IReadOnlyList<FieldChange> Update(
        string personnelNumber, string lastName, string firstName, string? middleName, long departmentId, long positionId)
    {
        EnsureNotDismissed();
        var before = (PersonnelNumber, FullName, DepartmentId, PositionId);
        SetNumber(personnelNumber);
        SetName(lastName, firstName, middleName);
        DepartmentId = departmentId;
        PositionId = positionId;

        var changes = new List<FieldChange>();
        if (before.PersonnelNumber != PersonnelNumber)
        {
            changes.Add(new FieldChange(nameof(PersonnelNumber), before.PersonnelNumber, PersonnelNumber));
        }

        if (before.FullName != FullName)
        {
            changes.Add(new FieldChange(nameof(FullName), before.FullName, FullName));
        }

        if (before.DepartmentId != DepartmentId)
        {
            changes.Add(new FieldChange(nameof(DepartmentId), before.DepartmentId.ToString(), DepartmentId.ToString()));
        }

        if (before.PositionId != PositionId)
        {
            changes.Add(new FieldChange(nameof(PositionId), before.PositionId.ToString(), PositionId.ToString()));
        }

        return changes;
    }

    /// <summary>Дата рождения и согласие показывать день рождения коллегам. Возраст — от 14 до 100 лет на сегодня.</summary>
    public FieldChange? SetBirthday(DateOnly? birthDate, bool share, DateOnly today)
    {
        EnsureNotDismissed();
        if (birthDate is { } d && (d > today.AddYears(-14) || d < today.AddYears(-100)))
        {
            throw new BusinessRuleException("hr.employee.birth_date", "Дата рождения: возраст от 14 до 100 лет.");
        }

        // D201: к дате приёма сотруднику не меньше 14 лет (ст. 63 ТК РФ).
        if (birthDate is { } b && b > HiredOn.AddYears(-14))
        {
            throw new BusinessRuleException("hr.employee.too_young", "На дату приёма сотруднику меньше 14 лет (ст. 63 ТК РФ).");
        }

        if (share && birthDate is null)
        {
            throw new BusinessRuleException("hr.employee.birthday_share", "Чтобы показывать день рождения, укажите дату рождения.");
        }

        var before = $"{BirthDate:dd.MM.yyyy}{(ShareBirthday ? ", видна коллегам" : "")}";
        var after = $"{birthDate:dd.MM.yyyy}{(share ? ", видна коллегам" : "")}";
        BirthDate = birthDate;
        ShareBirthday = share;
        return before == after ? null : new FieldChange("Дата рождения", before, after);
    }

    /// <summary>Перевод по приказу Т-5 (D204): новое подразделение и (или) должность.</summary>
    public IReadOnlyList<FieldChange> Transfer(long departmentId, long positionId)
    {
        EnsureNotDismissed();
        if (departmentId == DepartmentId && positionId == PositionId)
        {
            throw new BusinessRuleException("hr.employee.transfer_same", "Перевод не меняет ни подразделение, ни должность.");
        }

        var changes = new List<FieldChange>();
        if (departmentId != DepartmentId)
        {
            changes.Add(new FieldChange(nameof(DepartmentId), DepartmentId.ToString(), departmentId.ToString()));
            DepartmentId = departmentId;
        }

        if (positionId != PositionId)
        {
            changes.Add(new FieldChange(nameof(PositionId), PositionId.ToString(), positionId.ToString()));
            PositionId = positionId;
        }

        return changes;
    }

    public FieldChange SetOnLeave(bool onLeave)
    {
        EnsureNotDismissed();
        var target = onLeave ? EmploymentStatus.OnLeave : EmploymentStatus.Working;
        if (Status == target)
        {
            throw new BusinessRuleException("hr.employee.status_unchanged", "Статус не изменился.");
        }

        var change = new FieldChange(nameof(Status), StatusName(Status), StatusName(target));
        Status = target;
        return change;
    }

    public FieldChange Dismiss(DateOnly dismissedOn)
    {
        EnsureNotDismissed();
        if (dismissedOn < HiredOn)
        {
            throw new BusinessRuleException("hr.employee.dismiss_before_hire", "Дата увольнения раньше даты приёма.");
        }

        var change = new FieldChange(nameof(Status), StatusName(Status), $"{StatusName(EmploymentStatus.Dismissed)} {dismissedOn:dd.MM.yyyy}");
        Status = EmploymentStatus.Dismissed;
        DismissedOn = dismissedOn;
        return change;
    }

    /// <summary>Связь с учётной записью: один сотрудник — одна учётная запись в организации.</summary>
    public void LinkUser(long userId)
    {
        EnsureNotDismissed();
        if (UserId is { } existing && existing != userId)
        {
            throw new BusinessRuleException("hr.employee.has_user", "У сотрудника уже есть учётная запись.");
        }

        UserId = userId;
    }

    public static string StatusName(EmploymentStatus status) => status switch
    {
        EmploymentStatus.Working => "Работает",
        EmploymentStatus.OnLeave => "В отпуске",
        EmploymentStatus.Dismissed => "Уволен",
        _ => status.ToString(),
    };

    private void SetNumber(string? number)
    {
        var n = number?.Trim().ToUpperInvariant() ?? string.Empty;
        if (n.Length is 0 or > PersonnelNumberMaxLength || !n.All(c => char.IsLetterOrDigit(c) || c is '-' or '/'))
        {
            throw new BusinessRuleException("hr.employee.number_invalid",
                $"Табельный номер: 1–{PersonnelNumberMaxLength} символов, буквы, цифры, «-» и «/».");
        }

        PersonnelNumber = n;
    }

    private void SetName(string lastName, string firstName, string? middleName)
    {
        LastName = DomainText.Require(lastName, NamePartMaxLength, "Фамилия");
        FirstName = DomainText.Require(firstName, NamePartMaxLength, "Имя");
        MiddleName = DomainText.Optional(middleName, NamePartMaxLength, "Отчество");
    }

    private void EnsureNotDismissed()
    {
        if (Status == EmploymentStatus.Dismissed)
        {
            throw new BusinessRuleException("hr.employee.dismissed", "Сотрудник уволен, карточка закрыта для изменений.");
        }
    }
}
