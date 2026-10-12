using System.Security.Cryptography;
using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>Документ, который сотрудник подписывает перед доступом к системе (D227).</summary>
public enum AgreementKind : byte
{
    /// <summary>Согласие на обработку персональных данных (ст. 9 152-ФЗ).</summary>
    PersonalDataConsent = 1,

    /// <summary>Правила работы в информационной системе и соглашение о простой электронной подписи (ст. 6, 9 63-ФЗ).</summary>
    SystemRules = 2,

    /// <summary>Обязательство о неразглашении конфиденциальной информации (98-ФЗ, ст. 81, 90 ТК РФ).</summary>
    Confidentiality = 3,
}

/// <summary>Как подписан документ.</summary>
public enum AgreementMethod : byte
{
    /// <summary>Простая электронная подпись: сотрудник подтвердил подписание своим паролем.</summary>
    Electronic = 1,

    /// <summary>На бумаге собственноручно; отметку ставит кадровик (скан — в документах сотрудника).</summary>
    Paper = 2,
}

/// <summary>Данные для заполнения документа: организация и сотрудник. Пустое поле печатается линией для заполнения от руки.</summary>
public sealed record AgreementData(
    string OrganizationName, string OrganizationInn, string? OrganizationAddress, string EmployeeName, string PersonnelNumber, string? Position,
    string? PassportNumber, string? PassportIssuer, DateOnly? PassportIssuedOn, string? RegistrationAddress, DateOnly Date);

/// <summary>Готовый текст документа и его хэш — подпись фиксирует именно этот текст.</summary>
public sealed record AgreementText(AgreementKind Kind, string Version, string Title, IReadOnlyList<string> Paragraphs)
{
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Title + "\n" + string.Join("\n", Paragraphs)))).ToLowerInvariant();
}

/// <summary>
/// Шаблоны документов перед доступом (D227). Тексты — типовые для РФ; перед использованием организация проверяет их у юриста и при
/// необходимости заменяет локальными актами. Версия меняется при правке текста — тогда сотрудники подписывают новую редакцию.
/// </summary>
public static class AgreementTemplates
{
    public const string Version = "2026-10-11";

    public static readonly IReadOnlyList<AgreementKind> All = [AgreementKind.PersonalDataConsent, AgreementKind.SystemRules, AgreementKind.Confidentiality];

    private const string Blank = "________________";

    public static string Title(AgreementKind kind) => kind switch
    {
        AgreementKind.PersonalDataConsent => "Согласие на обработку персональных данных",
        AgreementKind.SystemRules => "Правила работы в информационной системе knitERP и соглашение об электронной подписи",
        AgreementKind.Confidentiality => "Обязательство о неразглашении конфиденциальной информации",
        _ => kind.ToString(),
    };

    public static AgreementText Render(AgreementKind kind, AgreementData d)
    {
        var passport = d.PassportNumber is null
            ? $"паспорт {Blank}, выдан {Blank}"
            : $"паспорт {d.PassportNumber}, выдан {d.PassportIssuer ?? Blank} {d.PassportIssuedOn?.ToString("dd.MM.yyyy") ?? Blank}";
        var operatorName = $"{d.OrganizationName}, ИНН {d.OrganizationInn}, адрес: {d.OrganizationAddress ?? Blank}";
        IReadOnlyList<string> paragraphs = kind switch
        {
            AgreementKind.PersonalDataConsent =>
            [
                $"Я, {d.EmployeeName}, {passport}, зарегистрирован(а) по адресу: {d.RegistrationAddress ?? Blank}, в соответствии со статьёй 9 Федерального закона от 27.07.2006 № 152-ФЗ «О персональных данных» свободно, своей волей и в своём интересе даю согласие оператору — {operatorName} — на обработку моих персональных данных.",
                "Цели обработки: заключение и исполнение трудового договора; кадровый, бухгалтерский и налоговый учёт; расчёт и выплата заработной платы; исполнение требований трудового, налогового и пенсионного законодательства; предоставление доступа к информационной системе knitERP.",
                "Перечень персональных данных: фамилия, имя, отчество; дата и место рождения; пол; гражданство; паспортные данные; адреса регистрации и проживания; номер телефона; СНИЛС; ИНН; сведения об образовании, профессии, семейном положении; сведения о трудовой деятельности, рабочем времени, выработке и заработной плате; фотография.",
                "Действия с персональными данными: сбор, запись, систематизация, накопление, хранение, уточнение (обновление, изменение), извлечение, использование, передача (предоставление, доступ) в случаях, предусмотренных законодательством, обезличивание, блокирование, удаление, уничтожение — с использованием средств автоматизации и без них.",
                "Согласие действует со дня подписания до его отзыва. Согласие может быть отозвано письменным заявлением оператору; после отзыва оператор вправе продолжить обработку без согласия в случаях, предусмотренных Федеральным законом № 152-ФЗ.",
            ],
            AgreementKind.SystemRules =>
            [
                $"Я, {d.EmployeeName}, табельный номер {d.PersonnelNumber}, {d.Position ?? Blank}, получаю доступ к информационной системе knitERP организации {d.OrganizationName} и обязуюсь соблюдать следующие правила.",
                "1. Доступ личный: логин и пароль не передаю другим лицам и не записываю в доступных местах. О подозрении, что пароль стал известен постороннему, сразу сообщаю руководителю и меняю пароль.",
                "2. Пользуюсь системой только для выполнения трудовых обязанностей и в пределах выданных мне прав. Не пытаюсь получить доступ к сведениям, которые мне не предназначены.",
                "3. Мне известно, что мои действия в системе (вход, просмотр персональных данных, изменения, выгрузки) записываются в журнал с указанием пользователя и времени.",
                "4. Не копирую и не выгружаю сведения из системы за пределы организации без служебной необходимости и разрешения руководителя.",
                "5. Соглашение об электронной подписи. Подтверждение действия в системе вводом моего пароля признаётся простой электронной подписью (статья 9 Федерального закона от 06.04.2011 № 63-ФЗ «Об электронной подписи»). Ключ простой электронной подписи — мой логин и пароль; я храню его в тайне. Документ, подписанный так, признаётся равнозначным документу на бумаге, подписанному собственноручной подписью (часть 2 статьи 6 Закона № 63-ФЗ).",
            ],
            AgreementKind.Confidentiality =>
            [
                $"Я, {d.EmployeeName}, табельный номер {d.PersonnelNumber}, {d.Position ?? Blank}, в период работы в {d.OrganizationName} и после её окончания обязуюсь:",
                "1. Не разглашать сведения, составляющие коммерческую тайну организации (Федеральный закон от 29.07.2004 № 98-ФЗ «О коммерческой тайне»), персональные данные работников, покупателей и поставщиков и иную конфиденциальную информацию, ставшую мне известной в связи с работой.",
                "2. Использовать такие сведения только для выполнения трудовых обязанностей; не передавать их третьим лицам и не публиковать без письменного разрешения организации.",
                "3. При увольнении вернуть организации все носители с конфиденциальной информацией; об утрате носителей и о попытках получить от меня такие сведения — сообщить руководителю.",
                "4. Мне разъяснено, что за разглашение охраняемой законом тайны предусмотрена ответственность, в том числе увольнение по подпункту «в» пункта 6 части первой статьи 81 Трудового кодекса РФ, ответственность за нарушение правил обработки персональных данных работников (статья 90 ТК РФ), административная и уголовная ответственность в случаях, предусмотренных законом.",
            ],
            _ => [],
        };
        return new AgreementText(kind, Version, Title(kind), [.. paragraphs, $"Дата: {d.Date:dd.MM.yyyy}"]);
    }
}

/// <summary>
/// Подпись сотрудника под документом перед доступом (D227). Только вставка (триггер): хранится версия и SHA-256 подписанного текста,
/// время, кто подписал и как — электронно паролем или отметка кадровика о бумажной подписи.
/// </summary>
public sealed class EmployeeAgreement
{
    private EmployeeAgreement()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long EmployeeId { get; private set; }
    public AgreementKind Kind { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public string TextHash { get; private set; } = string.Empty;
    public AgreementMethod Method { get; private set; }
    public DateTime SignedAtUtc { get; private set; }
    public long SignedByUserId { get; private set; }

    public static EmployeeAgreement Sign(long organizationId, long employeeId, AgreementText text, AgreementMethod method, long userId, DateTime nowUtc)
    {
        if (!Enum.IsDefined(text.Kind) || !Enum.IsDefined(method))
        {
            throw new BusinessRuleException("hr.agreement.kind", "Неизвестный документ.");
        }

        return new EmployeeAgreement
        {
            OrganizationId = organizationId, EmployeeId = employeeId, Kind = text.Kind, Version = text.Version, TextHash = text.Hash, Method = method,
            SignedAtUtc = nowUtc, SignedByUserId = userId,
        };
    }
}
