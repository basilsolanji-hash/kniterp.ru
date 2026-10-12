using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>Пол для личной карточки Т-2. Числа хранятся в базе и закреплены CHECK.</summary>
public enum Gender : byte
{
    NotSet = 0,
    Male = 1,
    Female = 2,
}

/// <summary>Состояние согласия на обработку персональных данных (ст. 9 152-ФЗ) на дату.</summary>
public enum ConsentState : byte
{
    Missing = 0,
    Valid = 1,
    Expired = 2,
    Revoked = 3,
}

/// <summary>Значения персональных данных из формы — все необязательные: карточку заполняют постепенно.</summary>
public sealed record PersonalDataValues(
    Gender Gender = Gender.NotSet,
    string? BirthPlace = null,
    string? Citizenship = null,
    string? PassportNumber = null,
    string? PassportIssuer = null,
    DateOnly? PassportIssuedOn = null,
    string? PassportDivisionCode = null,
    string? Snils = null,
    string? Inn = null,
    string? RegistrationAddress = null,
    string? ActualAddress = null,
    string? Phone = null,
    string? Education = null,
    string? Profession = null,
    string? MaritalStatus = null,
    string? FamilyMembers = null);

/// <summary>
/// Персональные данные сотрудника (Т-2, 152-ФЗ) — отдельно от карточки (D200): своё право на чтение и изменение,
/// значения в базе зашифрованы мастер-ключом, каждое чтение пишется в журнал аудита. Одна запись на сотрудника.
/// Согласие на обработку хранится датами здесь, сканы — отдельной таблицей.
/// </summary>
public sealed class EmployeePersonalData
{
    public const int ShortMaxLength = 100;
    public const int LongMaxLength = 500;

    private EmployeePersonalData()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public Gender Gender { get; private set; }
    public string? BirthPlace { get; private set; }
    public string? Citizenship { get; private set; }
    public string? PassportNumber { get; private set; }
    public string? PassportIssuer { get; private set; }
    public DateOnly? PassportIssuedOn { get; private set; }
    public string? PassportDivisionCode { get; private set; }
    public string? Snils { get; private set; }
    public string? Inn { get; private set; }
    public string? RegistrationAddress { get; private set; }
    public string? ActualAddress { get; private set; }
    public string? Phone { get; private set; }
    public string? Education { get; private set; }
    public string? Profession { get; private set; }
    public string? MaritalStatus { get; private set; }
    public string? FamilyMembers { get; private set; }

    /// <summary>Дата подписания согласия на обработку ПДн.</summary>
    public DateOnly? ConsentGivenOn { get; private set; }

    /// <summary>Срок действия согласия; пусто — до отзыва (ч. 1 ст. 9 152-ФЗ допускает бессрочное).</summary>
    public DateOnly? ConsentValidUntil { get; private set; }

    /// <summary>Дата отзыва согласия (ч. 2 ст. 9 152-ФЗ).</summary>
    public DateOnly? ConsentRevokedOn { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static EmployeePersonalData Create(long organizationId, long employeeId, DateTime nowUtc) =>
        new() { OrganizationId = organizationId, EmployeeId = employeeId, UpdatedAtUtc = nowUtc };

    /// <summary>
    /// Замена значений. Возвращает названия изменённых полей — в журнал аудита пишутся только они, без самих значений:
    /// журнал читают шире, чем персональные данные.
    /// </summary>
    public IReadOnlyList<string> Update(PersonalDataValues v, DateOnly today, DateTime nowUtc)
    {
        var snils = PersonIds.Digits(Optional(v.Snils, 20, "СНИЛС"));
        if (snils is { Length: > 0 } && !PersonIds.IsValidSnils(snils))
        {
            throw new BusinessRuleException("hr.personal_data.snils", "СНИЛС: 11 цифр с верным контрольным числом.");
        }

        var inn = Optional(v.Inn, 12, "ИНН");
        if (inn is not null && !PersonIds.IsValidPersonInn(inn))
        {
            throw new BusinessRuleException("hr.personal_data.inn", "ИНН физического лица: 12 цифр с верными контрольными разрядами.");
        }

        var phone = Optional(v.Phone, 20, "Телефон");
        if (phone is not null && (!phone.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or '(' or ')' or ' ') || phone.Count(char.IsAsciiDigit) < 5))
        {
            throw new BusinessRuleException("hr.personal_data.phone", "Телефон: цифры, «+», «-», скобки и пробелы.");
        }

        var division = Optional(v.PassportDivisionCode, 7, "Код подразделения");
        if (division is not null && !System.Text.RegularExpressions.Regex.IsMatch(division, @"^\d{3}-\d{3}$"))
        {
            throw new BusinessRuleException("hr.personal_data.division", "Код подразделения, выдавшего паспорт: «000-000».");
        }

        if (v.PassportIssuedOn is { } issued && issued > today)
        {
            throw new BusinessRuleException("hr.personal_data.passport_date", "Дата выдачи паспорта не может быть в будущем.");
        }

        if (!Enum.IsDefined(v.Gender))
        {
            throw new BusinessRuleException("hr.personal_data.gender", "Неизвестное значение пола.");
        }

        var changed = new List<string>();
        Set(Gender, v.Gender, x => Gender = x, "Пол", changed);
        Set(BirthPlace, Optional(v.BirthPlace, LongMaxLength, "Место рождения"), x => BirthPlace = x, "Место рождения", changed);
        Set(Citizenship, Optional(v.Citizenship, ShortMaxLength, "Гражданство"), x => Citizenship = x, "Гражданство", changed);
        Set(PassportNumber, Optional(v.PassportNumber, 20, "Паспорт"), x => PassportNumber = x, "Паспорт", changed);
        Set(PassportIssuer, Optional(v.PassportIssuer, LongMaxLength, "Кем выдан паспорт"), x => PassportIssuer = x, "Кем выдан паспорт", changed);
        Set(PassportIssuedOn, v.PassportIssuedOn, x => PassportIssuedOn = x, "Дата выдачи паспорта", changed);
        Set(PassportDivisionCode, division, x => PassportDivisionCode = x, "Код подразделения", changed);
        Set(Snils, snils is { Length: > 0 } ? snils : null, x => Snils = x, "СНИЛС", changed);
        Set(Inn, inn, x => Inn = x, "ИНН", changed);
        Set(RegistrationAddress, Optional(v.RegistrationAddress, LongMaxLength, "Адрес регистрации"), x => RegistrationAddress = x, "Адрес регистрации", changed);
        Set(ActualAddress, Optional(v.ActualAddress, LongMaxLength, "Фактический адрес"), x => ActualAddress = x, "Фактический адрес", changed);
        Set(Phone, phone, x => Phone = x, "Телефон", changed);
        Set(Education, Optional(v.Education, LongMaxLength, "Образование"), x => Education = x, "Образование", changed);
        Set(Profession, Optional(v.Profession, ShortMaxLength, "Профессия"), x => Profession = x, "Профессия", changed);
        Set(MaritalStatus, Optional(v.MaritalStatus, ShortMaxLength, "Семейное положение"), x => MaritalStatus = x, "Семейное положение", changed);
        Set(FamilyMembers, Optional(v.FamilyMembers, LongMaxLength, "Состав семьи"), x => FamilyMembers = x, "Состав семьи", changed);
        if (changed.Count > 0)
        {
            UpdatedAtUtc = nowUtc;
        }

        return changed;
    }

    /// <summary>Согласие подписано: дата не в будущем, срок не раньше даты. Новое согласие снимает отметку об отзыве.</summary>
    public void GiveConsent(DateOnly givenOn, DateOnly? validUntil, DateOnly today, DateTime nowUtc)
    {
        if (givenOn > today)
        {
            throw new BusinessRuleException("hr.consent.future", "Дата согласия не может быть в будущем.");
        }

        if (validUntil is { } until && until < givenOn)
        {
            throw new BusinessRuleException("hr.consent.period", "Срок согласия раньше даты его подписания.");
        }

        ConsentGivenOn = givenOn;
        ConsentValidUntil = validUntil;
        ConsentRevokedOn = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Отзыв согласия. Данные не стираются сразу: работодатель обрабатывает их без согласия по ТК РФ (п. 2 ч. 1 ст. 6 152-ФЗ).</summary>
    public void RevokeConsent(DateOnly revokedOn, DateTime nowUtc)
    {
        if (ConsentGivenOn is not { } given || ConsentRevokedOn is not null)
        {
            throw new BusinessRuleException("hr.consent.none", "Действующего согласия нет.");
        }

        if (revokedOn < given)
        {
            throw new BusinessRuleException("hr.consent.revoke_date", "Дата отзыва раньше даты согласия.");
        }

        ConsentRevokedOn = revokedOn;
        UpdatedAtUtc = nowUtc;
    }

    public ConsentState ConsentOn(DateOnly date) => StateOf(ConsentGivenOn, ConsentValidUntil, ConsentRevokedOn, date);

    public static ConsentState StateOf(DateOnly? givenOn, DateOnly? validUntil, DateOnly? revokedOn, DateOnly date) =>
        givenOn is null ? ConsentState.Missing
        : revokedOn is { } r && r <= date ? ConsentState.Revoked
        : validUntil is { } u && u < date ? ConsentState.Expired
        : ConsentState.Valid;

    public static string ConsentName(ConsentState state) => state switch
    {
        ConsentState.Valid => "Согласие действует",
        ConsentState.Expired => "Срок согласия истёк",
        ConsentState.Revoked => "Согласие отозвано",
        _ => "Согласия нет",
    };

    public static string GenderName(Gender gender) => gender switch
    {
        Gender.Male => "мужской",
        Gender.Female => "женский",
        _ => "не указан",
    };

    private static string? Optional(string? value, int max, string field)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        return v.Length <= max ? v : throw new BusinessRuleException("field.too_long", $"Поле «{field}» длиннее {max} символов.");
    }

    private static void Set<T>(T current, T next, Action<T> assign, string label, List<string> changed)
    {
        if (!EqualityComparer<T>.Default.Equals(current, next))
        {
            assign(next);
            changed.Add(label);
        }
    }
}

/// <summary>Вид документа сотрудника (D226): сканы личного дела — паспорт, регистрация, договор, заявления и т. д. — и фотография.</summary>
public enum EmployeeDocumentKind : byte
{
    Consent = 1,
    Passport = 2,
    Registration = 3,
    Photo = 4,
    Contract = 5,
    Application = 6,
    Snils = 7,
    Inn = 8,
    Education = 9,
    Military = 10,
    Medical = 11,
    WorkBook = 12,
    Other = 13,
}

public static class EmployeeDocumentKinds
{
    public static readonly IReadOnlyList<EmployeeDocumentKind> All =
    [
        EmployeeDocumentKind.Passport, EmployeeDocumentKind.Registration, EmployeeDocumentKind.Photo, EmployeeDocumentKind.Contract,
        EmployeeDocumentKind.Application, EmployeeDocumentKind.Consent, EmployeeDocumentKind.Snils, EmployeeDocumentKind.Inn,
        EmployeeDocumentKind.Education, EmployeeDocumentKind.Military, EmployeeDocumentKind.Medical, EmployeeDocumentKind.WorkBook,
        EmployeeDocumentKind.Other,
    ];

    public static string Name(EmployeeDocumentKind kind) => kind switch
    {
        EmployeeDocumentKind.Consent => "Скан согласия на обработку ПДн",
        EmployeeDocumentKind.Passport => "Скан паспорта",
        EmployeeDocumentKind.Registration => "Скан регистрации",
        EmployeeDocumentKind.Photo => "Фотография",
        EmployeeDocumentKind.Contract => "Скан трудового договора и дополнительных соглашений",
        EmployeeDocumentKind.Application => "Скан заявления (о приёме, переводе, отпуске, увольнении)",
        EmployeeDocumentKind.Snils => "Скан СНИЛС",
        EmployeeDocumentKind.Inn => "Скан ИНН",
        EmployeeDocumentKind.Education => "Скан документа об образовании",
        EmployeeDocumentKind.Military => "Скан военного билета (приписного свидетельства)",
        EmployeeDocumentKind.Medical => "Скан медицинской книжки или заключения медосмотра",
        EmployeeDocumentKind.WorkBook => "Скан трудовой книжки (сведений о трудовой деятельности)",
        EmployeeDocumentKind.Other => "Другой документ",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Документ сотрудника: скан согласия на обработку ПДн, паспорта, регистрации или фотография (D226; таблица прежняя — сканы согласия).
/// Только вставка: новый файл не заменяет старый, история остаётся (действует последний). Содержимое в базе зашифровано, как и сами ПДн.
/// </summary>
public sealed class ConsentScan
{
    public const int MaxBytes = 3 * 1024 * 1024;
    public static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "image/jpeg", "image/png" };

    private ConsentScan()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];
    public DateTime UploadedAtUtc { get; private set; }
    public long UploadedByUserId { get; private set; }
    public EmployeeDocumentKind Kind { get; private set; } = EmployeeDocumentKind.Consent;

    public static ConsentScan Create(long organizationId, long employeeId, string fileName, string contentType, byte[] content,
        DateTime nowUtc, long userId, EmployeeDocumentKind kind = EmployeeDocumentKind.Consent)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("hr.document.kind", "Неизвестный вид документа.");
        }

        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new BusinessRuleException("hr.consent.scan_size", $"{EmployeeDocumentKinds.Name(kind)}: файл до 3 МБ.");
        }

        // Тип — по сигнатуре файла, а не по заголовку браузера: иначе под видом PDF можно загрузить HTML со скриптом. Фото — только картинка.
        if (!AllowedTypes.Contains(contentType) || !SignatureMatches(contentType, content)
            || (kind == EmployeeDocumentKind.Photo && contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException("hr.consent.scan_type",
                kind == EmployeeDocumentKind.Photo ? "Фотография: JPEG или PNG." : $"{EmployeeDocumentKinds.Name(kind)}: PDF, JPEG или PNG.");
        }

        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        return new ConsentScan
        {
            OrganizationId = organizationId,
            EmployeeId = employeeId,
            FileName = name.Length is 0 ? "consent" : name.Length > 200 ? name[..200] : name,
            ContentType = contentType.ToLowerInvariant(),
            SizeBytes = content.Length,
            Content = content,
            UploadedAtUtc = nowUtc,
            UploadedByUserId = userId,
            Kind = kind,
        };
    }

    private static bool SignatureMatches(string contentType, byte[] c) => contentType.ToLowerInvariant() switch
    {
        "application/pdf" => c.Length >= 5 && c[0] == 0x25 && c[1] == 0x50 && c[2] == 0x44 && c[3] == 0x46 && c[4] == 0x2D,
        "image/jpeg" => c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF,
        "image/png" => c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47,
        _ => false,
    };
}

/// <summary>
/// Заполненность карточки сотрудника (D226): что из обязательного для кадров есть. Только факт наличия — без самих значений,
/// поэтому показатель виден и тем, кто не читает ПДн, но ведёт сотрудников.
/// </summary>
public sealed record EmployeeCompleteness(IReadOnlyList<(string Item, bool Done)> Items)
{
    public int Percent => Items.Count == 0 ? 0 : Items.Count(i => i.Done) * 100 / Items.Count;

    public IReadOnlyList<string> Missing => Items.Where(i => !i.Done).Select(i => i.Item).ToList();

    public static EmployeeCompleteness Of(bool birthDate, bool passport, bool snils, bool inn, bool registrationAddress, bool phone, bool consent,
        bool consentScan, bool passportScan, bool registrationScan, bool photo, bool schedule, bool hireOrder, bool contractScan, bool agreements) =>
        new([
            ("Дата рождения", birthDate), ("Паспорт: номер, кем и когда выдан", passport), ("СНИЛС", snils), ("ИНН", inn),
            ("Адрес регистрации", registrationAddress), ("Телефон", phone), ("Действующее согласие на обработку ПДн", consent),
            ("Скан согласия на обработку ПДн", consentScan), ("Скан паспорта", passportScan), ("Скан регистрации", registrationScan),
            ("Фотография", photo), ("Скан трудового договора", contractScan), ("График работы", schedule), ("Приказ о приёме", hireOrder),
            ("Подписаны согласие, правила работы и обязательство о конфиденциальности", agreements),
        ]);
}
