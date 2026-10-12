using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>Состояние ссылки анкеты сотрудника (D125). Числа хранятся в базе и закреплены CHECK.</summary>
public enum EmployeeDataRequestStatus : byte
{
    /// <summary>Выдана и ещё не использована (действует до ExpiresAtUtc).</summary>
    Issued = 1,

    /// <summary>Сотрудник отправил анкету — ссылка одноразовая и больше не открывается.</summary>
    Submitted = 2,

    /// <summary>Отменена новой ссылкой.</summary>
    Cancelled = 3,
}

/// <summary>Состояние анкеты сотрудника: ждёт проверки, принята в карточку или отклонена.</summary>
public enum EmployeeDataSubmissionStatus : byte
{
    Pending = 1,
    Accepted = 2,
    Rejected = 3,
}

/// <summary>
/// Ссылка «заполнить свои данные» (D125): кадровик выдаёт её сотруднику, сотрудник без входа в систему заполняет анкету.
/// В базе — только SHA-256 токена; срок — 4 часа; одноразовая; новая ссылка отменяет прежнюю. Закрытая ссылка не меняется (триггер).
/// </summary>
public sealed class EmployeeDataRequest
{
    /// <summary>Срок действия ссылки — 4 часа (запрос владельца).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private EmployeeDataRequest()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTime IssuedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public long IssuedByUserId { get; private set; }
    public EmployeeDataRequestStatus Status { get; private set; }

    /// <summary>Когда ссылку использовали или отменили.</summary>
    public DateTime? ClosedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static EmployeeDataRequest Issue(long organizationId, long employeeId, byte[] tokenHash, long userId, DateTime nowUtc)
    {
        if (tokenHash.Length != 32)
        {
            throw new ArgumentException("SHA-256 токена — 32 байта.", nameof(tokenHash));
        }

        return new EmployeeDataRequest
        {
            OrganizationId = organizationId, EmployeeId = employeeId, TokenHash = tokenHash, IssuedByUserId = userId,
            IssuedAtUtc = nowUtc, ExpiresAtUtc = nowUtc + Lifetime, Status = EmployeeDataRequestStatus.Issued,
        };
    }

    /// <summary>Ссылку ещё можно открыть: выдана, не использована, не отменена и срок не истёк.</summary>
    public bool IsOpen(DateTime nowUtc) => Status == EmployeeDataRequestStatus.Issued && ExpiresAtUtc > nowUtc;

    /// <summary>Отмена новой ссылкой. Использованную или уже отменённую не трогает.</summary>
    public bool Cancel(DateTime nowUtc)
    {
        if (Status != EmployeeDataRequestStatus.Issued)
        {
            return false;
        }

        Status = EmployeeDataRequestStatus.Cancelled;
        ClosedAtUtc = nowUtc;
        return true;
    }

    /// <summary>Анкета отправлена: ссылка закрывается. Закрытая или просроченная — отказ с нейтральным текстом.</summary>
    public void MarkSubmitted(DateTime nowUtc)
    {
        if (!IsOpen(nowUtc))
        {
            throw new BusinessRuleException(EmployeeDataForm.InvalidLinkCode, EmployeeDataForm.InvalidLinkMessage);
        }

        Status = EmployeeDataRequestStatus.Submitted;
        ClosedAtUtc = nowUtc;
    }
}

/// <summary>Значения анкеты: только то, что сотрудник заполнил (пустое поле — «не менять»).</summary>
public sealed record EmployeeDataValues(
    DateOnly? BirthDate = null,
    string? PassportNumber = null,
    string? PassportIssuer = null,
    DateOnly? PassportIssuedOn = null,
    string? PassportDivisionCode = null,
    string? RegistrationAddress = null,
    string? ActualAddress = null,
    string? Phone = null,
    string? PersonalEmail = null,
    string? Snils = null,
    string? Inn = null)
{
    /// <summary>Названия заполненных полей — для журнала и сравнения (без значений).</summary>
    public IReadOnlyList<string> FilledFields()
    {
        var list = new List<string>();
        Add(BirthDate is not null, "Дата рождения");
        Add(PassportNumber is not null, "Паспорт");
        Add(PassportIssuer is not null, "Кем выдан паспорт");
        Add(PassportIssuedOn is not null, "Дата выдачи паспорта");
        Add(PassportDivisionCode is not null, "Код подразделения");
        Add(RegistrationAddress is not null, "Адрес регистрации");
        Add(ActualAddress is not null, "Фактический адрес");
        Add(Phone is not null, "Телефон");
        Add(PersonalEmail is not null, "Личная почта");
        Add(Snils is not null, "СНИЛС");
        Add(Inn is not null, "ИНН");
        return list;

        void Add(bool filled, string name)
        {
            if (filled)
            {
                list.Add(name);
            }
        }
    }

    /// <summary>Карточка с подставленными значениями анкеты: заполненное заменяет, пустое оставляет как было.</summary>
    public PersonalDataValues MergeInto(PersonalDataValues current) => current with
    {
        PassportNumber = PassportNumber ?? current.PassportNumber,
        PassportIssuer = PassportIssuer ?? current.PassportIssuer,
        PassportIssuedOn = PassportIssuedOn ?? current.PassportIssuedOn,
        PassportDivisionCode = PassportDivisionCode ?? current.PassportDivisionCode,
        RegistrationAddress = RegistrationAddress ?? current.RegistrationAddress,
        ActualAddress = ActualAddress ?? current.ActualAddress,
        Phone = Phone ?? current.Phone,
        PersonalEmail = PersonalEmail ?? current.PersonalEmail,
        Snils = Snils ?? current.Snils,
        Inn = Inn ?? current.Inn,
    };
}

/// <summary>Правила анкеты сотрудника (D125): проверка и очистка значений, допустимые виды сканов.</summary>
public static class EmployeeDataForm
{
    public const string InvalidLinkCode = "hr.employee_data.link";

    /// <summary>Один ответ на неизвестную, просроченную, использованную и отменённую ссылку — по нему нельзя понять, какая из причин.</summary>
    public const string InvalidLinkMessage = "Ссылка недействительна: срок истёк, анкета уже отправлена или выдана новая ссылка. Попросите кадровика прислать новую.";

    /// <summary>Сканы, которые сотрудник может приложить сам.</summary>
    public static readonly IReadOnlyList<EmployeeDocumentKind> DocumentKinds =
    [
        EmployeeDocumentKind.Passport, EmployeeDocumentKind.Registration, EmployeeDocumentKind.Snils, EmployeeDocumentKind.Inn,
        EmployeeDocumentKind.Photo,
    ];

    /// <summary>
    /// Проверка тем же правилом, что и карточка (СНИЛС и ИНН с контрольными числами, код подразделения, телефон, почта, длины),
    /// плюс дата рождения: возраст от 14 до 100 лет и не меньше 14 лет на дату приёма (ст. 63 ТК РФ). Возвращает очищенные значения.
    /// </summary>
    public static EmployeeDataValues Normalize(EmployeeDataValues v, DateOnly hiredOn, DateOnly today, DateTime nowUtc)
    {
        if (v.BirthDate is { } birth)
        {
            if (birth > today.AddYears(-14) || birth < today.AddYears(-100))
            {
                throw new BusinessRuleException("hr.employee.birth_date", "Дата рождения: возраст от 14 до 100 лет.");
            }

            if (birth > hiredOn.AddYears(-14))
            {
                throw new BusinessRuleException("hr.employee.too_young", "На дату приёма сотруднику меньше 14 лет (ст. 63 ТК РФ).");
            }
        }

        var probe = EmployeePersonalData.Create(0, 0, nowUtc);
        probe.Update(new PersonalDataValues(
            PassportNumber: v.PassportNumber, PassportIssuer: v.PassportIssuer, PassportIssuedOn: v.PassportIssuedOn,
            PassportDivisionCode: v.PassportDivisionCode, Snils: v.Snils, Inn: v.Inn, RegistrationAddress: v.RegistrationAddress,
            ActualAddress: v.ActualAddress, Phone: v.Phone, PersonalEmail: v.PersonalEmail), today, nowUtc);
        return new EmployeeDataValues(v.BirthDate, probe.PassportNumber, probe.PassportIssuer, probe.PassportIssuedOn, probe.PassportDivisionCode,
            probe.RegistrationAddress, probe.ActualAddress, probe.Phone, probe.PersonalEmail, probe.Snils, probe.Inn);
    }
}

/// <summary>
/// Анкета сотрудника (D125): данные, которые он прислал по ссылке. В карточку не попадают, пока кадровик не сравнит и не примет;
/// отклонённая хранится с причиной. Значения зашифрованы, как и ПДн карточки; изменить их нельзя (триггер), решение — один раз.
/// Согласие на обработку ПДн (ст. 9 152-ФЗ) фиксируется редакцией и SHA-256 текста, временем и адресом.
/// </summary>
public sealed class EmployeeDataSubmission
{
    public const int ReasonMaxLength = 500;

    private EmployeeDataSubmission()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public long RequestId { get; private set; }
    public DateOnly? BirthDate { get; private set; }
    public string? PassportNumber { get; private set; }
    public string? PassportIssuer { get; private set; }
    public DateOnly? PassportIssuedOn { get; private set; }
    public string? PassportDivisionCode { get; private set; }
    public string? RegistrationAddress { get; private set; }
    public string? ActualAddress { get; private set; }
    public string? Phone { get; private set; }
    public string? PersonalEmail { get; private set; }
    public string? Snils { get; private set; }
    public string? Inn { get; private set; }
    public string ConsentVersion { get; private set; } = string.Empty;
    public string ConsentTextHash { get; private set; } = string.Empty;

    /// <summary>Адрес, с которого отправлена анкета (с согласием). Зашифрован.</summary>
    public string? ClientAddress { get; private set; }

    public DateTime SubmittedAtUtc { get; private set; }
    public EmployeeDataSubmissionStatus Status { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public long? ReviewedByUserId { get; private set; }
    public string? RejectReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public EmployeeDataValues Values => new(BirthDate, PassportNumber, PassportIssuer, PassportIssuedOn, PassportDivisionCode,
        RegistrationAddress, ActualAddress, Phone, PersonalEmail, Snils, Inn);

    public static EmployeeDataSubmission Create(EmployeeDataRequest request, EmployeeDataValues v, AgreementText consent, string? clientAddress,
        DateTime nowUtc) => new()
    {
        OrganizationId = request.OrganizationId,
        EmployeeId = request.EmployeeId,
        RequestId = request.Id,
        BirthDate = v.BirthDate,
        PassportNumber = v.PassportNumber,
        PassportIssuer = v.PassportIssuer,
        PassportIssuedOn = v.PassportIssuedOn,
        PassportDivisionCode = v.PassportDivisionCode,
        RegistrationAddress = v.RegistrationAddress,
        ActualAddress = v.ActualAddress,
        Phone = v.Phone,
        PersonalEmail = v.PersonalEmail,
        Snils = v.Snils,
        Inn = v.Inn,
        ConsentVersion = consent.Version,
        ConsentTextHash = consent.Hash,
        ClientAddress = string.IsNullOrWhiteSpace(clientAddress) ? null : clientAddress.Length > 45 ? clientAddress[..45] : clientAddress,
        SubmittedAtUtc = nowUtc,
        Status = EmployeeDataSubmissionStatus.Pending,
    };

    public void Accept(long userId, DateTime nowUtc)
    {
        EnsurePending();
        Status = EmployeeDataSubmissionStatus.Accepted;
        ReviewedAtUtc = nowUtc;
        ReviewedByUserId = userId;
    }

    public void Reject(long userId, string? reason, DateTime nowUtc)
    {
        EnsurePending();
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r))
        {
            throw new BusinessRuleException("hr.employee_data.reason", "Укажите причину отказа — её увидит кадровик в карточке.");
        }

        if (r.Length > ReasonMaxLength)
        {
            throw new BusinessRuleException("field.too_long", $"Поле «Причина» длиннее {ReasonMaxLength} символов.");
        }

        Status = EmployeeDataSubmissionStatus.Rejected;
        ReviewedAtUtc = nowUtc;
        ReviewedByUserId = userId;
        RejectReason = r;
    }

    private void EnsurePending()
    {
        if (Status != EmployeeDataSubmissionStatus.Pending)
        {
            throw new BusinessRuleException("hr.employee_data.reviewed", "Анкета уже рассмотрена.");
        }
    }
}

/// <summary>Скан, приложенный к анкете (D125): попадает в документы сотрудника только при принятии анкеты. Только вставка, зашифрован.</summary>
public sealed class EmployeeDataSubmissionFile
{
    private EmployeeDataSubmissionFile()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long SubmissionId { get; private set; }
    public EmployeeDocumentKind Kind { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];

    /// <summary>Тип — по сигнатуре файла, а не по заголовку браузера; виды — только из анкеты; правила размера и типа — как у документов карточки.</summary>
    public static EmployeeDataSubmissionFile Create(long organizationId, EmployeeDocumentKind kind, string? fileName, byte[] content)
    {
        if (!EmployeeDataForm.DocumentKinds.Contains(kind))
        {
            throw new BusinessRuleException("hr.document.kind", "Неизвестный вид документа.");
        }

        var type = ConsentScan.DetectType(content) ?? "application/octet-stream";
        var name = ConsentScan.Check(kind, fileName, type, content);
        return new EmployeeDataSubmissionFile
        {
            OrganizationId = organizationId, Kind = kind, FileName = name, ContentType = type, SizeBytes = content.Length, Content = content,
        };
    }

    /// <summary>Привязка к анкете после её вставки (в той же транзакции).</summary>
    public EmployeeDataSubmissionFile For(EmployeeDataSubmission submission)
    {
        SubmissionId = submission.Id;
        return this;
    }
}
