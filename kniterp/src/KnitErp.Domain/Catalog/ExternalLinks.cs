using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Внешняя система обмена (D310). Расширяемый список: сейчас загрузка из МойСклад; остальные — места под будущие адаптеры
/// (1С, маркетплейсы, сайт). Числа хранятся в базе.
/// </summary>
public enum ExternalSystem : byte
{
    MoySklad = 1,
    OneC = 2,
    Wildberries = 3,
    Ozon = 4,
    YandexMarket = 5,
    Uzum = 6,
    Kaspi = 7,
    Website = 8,
}

/// <summary>Что связано с внешней системой.</summary>
public enum ExternalObjectType : byte
{
    Item = 1,
    ItemGroup = 2,
    PriceType = 3,
    UnitOfMeasure = 4,
    Counterparty = 5,
    Characteristic = 6,
}

public static class ExternalSystems
{
    public static string Name(ExternalSystem system) => system switch
    {
        ExternalSystem.MoySklad => "МойСклад",
        ExternalSystem.OneC => "1С",
        ExternalSystem.Wildberries => "Wildberries",
        ExternalSystem.Ozon => "Ozon",
        ExternalSystem.YandexMarket => "Яндекс Маркет",
        ExternalSystem.Uzum => "Uzum Market",
        ExternalSystem.Kaspi => "Kaspi.kz",
        _ => "Сайт",
    };
}

/// <summary>
/// Связь объекта knitERP с объектом внешней системы (D310): внешний id (UUID МойСклад), externalCode, версия во внешней системе
/// (поле updated) и отпечаток значений, записанных при последней загрузке. Отпечаток отличается от текущих значений в knitERP —
/// объект правили вручную, и повторная загрузка его не затирает без флага «перезаписать».
/// Уникальность: (организация, система, тип, внешний id) и (организация, система, тип, id в knitERP).
/// </summary>
public sealed class ExternalLink
{
    public const int ExternalIdMaxLength = 64;
    public const int ExternalCodeMaxLength = 255;
    public const int VersionMaxLength = 40;
    public const int HashLength = 64;

    private ExternalLink()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public ExternalSystem System { get; private set; }
    public ExternalObjectType ObjectType { get; private set; }
    public long LocalId { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string? ExternalCode { get; private set; }
    public string? ExternalVersion { get; private set; }
    public string? ContentHash { get; private set; }
    public DateTime SyncedAtUtc { get; private set; }

    public static ExternalLink Create(long organizationId, ExternalSystem system, ExternalObjectType type, long localId, string externalId,
        string? externalCode, string? version, string? contentHash, DateTime nowUtc)
    {
        var id = (externalId ?? string.Empty).Trim();
        if (id.Length is 0 or > ExternalIdMaxLength)
        {
            throw new BusinessRuleException("exchange.external_id", "Внешний идентификатор пустой или длиннее 64 символов.");
        }

        var link = new ExternalLink { OrganizationId = organizationId, System = system, ObjectType = type, LocalId = localId, ExternalId = id };
        link.Touch(externalCode, version, contentHash, nowUtc);
        return link;
    }

    public void Touch(string? externalCode, string? version, string? contentHash, DateTime nowUtc)
    {
        ExternalCode = Trim(externalCode, ExternalCodeMaxLength);
        ExternalVersion = Trim(version, VersionMaxLength);
        ContentHash = contentHash;
        SyncedAtUtc = nowUtc;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}

/// <summary>
/// Подключение организации к внешней системе (D310): токен хранится зашифрованным мастер-ключом, на экране — только последние
/// четыре символа. Одно подключение на систему.
/// </summary>
public sealed class ExternalConnection
{
    public const int TokenMaxLength = 400;
    public const int AccountMaxLength = 300;

    private ExternalConnection()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public ExternalSystem System { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public string TokenHint { get; private set; } = string.Empty;
    public string? AccountName { get; private set; }
    public long ConnectedByUserId { get; private set; }
    public DateTime ConnectedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Токен из ввода пользователя: убираются пробелы и слово «Bearer»; допускаются только печатные латинские символы без пробелов,
    /// 16–400 знаков. Сам токен не проверяется здесь — это делает запрос к системе.
    /// </summary>
    public static string NormalizeToken(string? token)
    {
        var t = (token ?? string.Empty).Trim();
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            t = t[7..].Trim();
        }

        if (t.Length is < 16 or > TokenMaxLength || t.Any(c => c is <= ' ' or > '~'))
        {
            throw new BusinessRuleException("exchange.token.format", "Токен — строка из латинских букв и цифр без пробелов (16–400 символов). Скопируйте его из МойСклад заново.");
        }

        return t;
    }

    public static ExternalConnection Create(long organizationId, ExternalSystem system, string token, string? accountName, long userId, DateTime nowUtc)
    {
        var c = new ExternalConnection { OrganizationId = organizationId, System = system };
        c.Replace(token, accountName, userId, nowUtc);
        return c;
    }

    public void Replace(string token, string? accountName, long userId, DateTime nowUtc)
    {
        Token = NormalizeToken(token);
        TokenHint = "…" + Token[^4..];
        AccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim() is var a && a.Length > AccountMaxLength ? a[..AccountMaxLength] : accountName.Trim();
        ConnectedByUserId = userId;
        ConnectedAtUtc = nowUtc;
    }
}

public enum ImportRunStatus : byte
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>
/// Загрузка справочников из внешней системы (D310): идёт в фоне по этапам (единицы, виды цен, группы, товары, услуги, комплекты,
/// модификации) страницами; Checkpoint — этап и смещение последней записанной страницы, чтобы прерванную загрузку продолжить.
/// </summary>
public sealed class ExternalImportRun
{
    public const int MessageMaxLength = 1000;

    private ExternalImportRun()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public ExternalSystem System { get; private set; }
    public ImportRunStatus Status { get; private set; }
    public bool Overwrite { get; private set; }

    /// <summary>Тип новых позиций для товаров внешней системы (у товара МойСклад типа нет): готовая продукция, пряжа и т. п.</summary>
    public ItemType DefaultItemType { get; private set; } = ItemType.Finished;

    public long RequestedByUserId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public string? Stage { get; private set; }
    public int StageOffset { get; private set; }
    public int Created { get; private set; }
    public int Updated { get; private set; }
    public int Skipped { get; private set; }
    public int Errors { get; private set; }
    public string? Message { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsActive => Status is ImportRunStatus.Queued or ImportRunStatus.Running;

    public static ExternalImportRun Queue(long organizationId, ExternalSystem system, bool overwrite, ItemType defaultItemType, long userId, DateTime nowUtc)
    {
        if (!ItemTypes.IsStocked(defaultItemType))
        {
            throw new BusinessRuleException("exchange.default_type", "Тип по умолчанию для товаров — складской (не услуга и не комплект).");
        }

        return new()
        {
            OrganizationId = organizationId, System = system, Overwrite = overwrite, DefaultItemType = defaultItemType, RequestedByUserId = userId,
            RequestedAtUtc = nowUtc, Status = ImportRunStatus.Queued,
        };
    }

    public void Start(DateTime nowUtc)
    {
        Status = ImportRunStatus.Running;
        StartedAtUtc ??= nowUtc;
        Message = null;
    }

    /// <summary>Записана страница этапа: смещение следующей страницы и счётчики.</summary>
    public void Checkpoint(string stage, int nextOffset, int created, int updated, int skipped, int errors)
    {
        Stage = stage;
        StageOffset = nextOffset;
        Created += created;
        Updated += updated;
        Skipped += skipped;
        Errors += errors;
    }

    public void Complete(DateTime nowUtc)
    {
        Status = ImportRunStatus.Completed;
        FinishedAtUtc = nowUtc;
        Stage = null;
        StageOffset = 0;
    }

    public void Fail(string message, DateTime nowUtc)
    {
        Status = ImportRunStatus.Failed;
        FinishedAtUtc = nowUtc;
        Message = message.Length > MessageMaxLength ? message[..MessageMaxLength] : message;
    }

    /// <summary>Продолжить прерванную загрузку с последней записанной страницы.</summary>
    public void Resume(DateTime nowUtc)
    {
        if (Status != ImportRunStatus.Failed)
        {
            throw new BusinessRuleException("exchange.run.not_failed", "Продолжить можно только прерванную загрузку.");
        }

        Status = ImportRunStatus.Queued;
        FinishedAtUtc = null;
        Message = null;
        RequestedAtUtc = nowUtc;
    }
}

/// <summary>Замечание загрузки по строке: что, откуда, почему пропущено или изменено (для отчёта и выгрузки в Excel).</summary>
public sealed class ExternalImportIssue
{
    public const int TextMaxLength = 500;

    private ExternalImportIssue()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long RunId { get; private set; }
    public string Entity { get; private set; } = string.Empty;
    public string? ExternalId { get; private set; }
    public string? Code { get; private set; }
    public string? Name { get; private set; }
    public bool IsError { get; private set; }
    public string Text { get; private set; } = string.Empty;

    public static ExternalImportIssue Create(long organizationId, long runId, string entity, string? externalId, string? code, string? name, bool isError,
        string text) =>
        new()
        {
            OrganizationId = organizationId, RunId = runId, Entity = Cut(entity, 40)!, ExternalId = Cut(externalId, ExternalLink.ExternalIdMaxLength),
            Code = Cut(code, 255), Name = Cut(name, 300), IsError = isError, Text = Cut(text, TextMaxLength)!,
        };

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}
