using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Вид рисунка (D726, ТЗ §7). Числа хранятся в базе.</summary>
public enum DevPatternKind : byte
{
    JacquardDouble = 1,
    JacquardFloat = 2,
    Intarsia = 3,
    Structural = 4,
    Stripes = 5,
    Print = 6,
}

/// <summary>Статус рисунка: черновик → на проверке → утверждён → в архиве (D726).</summary>
public enum DevPatternStatus : byte
{
    Draft = 1,
    Review = 2,
    Approved = 3,
    Archived = 4,
}

/// <summary>Файл версии рисунка: сетка или картинка рисунка, программа машины (D727).</summary>
public enum DevPatternFileKind : byte
{
    Pattern = 1,
    Program = 2,
}

public sealed record DevPatternValues(
    string? Name, DevPatternKind Kind, int? RapportWidth, int? RapportHeight, decimal? MaxFloatMm, long? MachineClassId, int? ProductWidthLoops,
    string? AlignmentRule, string? Note, long? DessinatorUserId, long? ProgrammerUserId);

/// <summary>
/// Рисунок (D726, ТЗ §7) — самостоятельный объект: один рисунок используют несколько моделей. Номер «Р-00001», вид, раппорт (петли × ряды),
/// цвета с долями и нитеводителями, колориты (раскладка цвет → пряжа), допустимая протяжка, класс машины (справочник, число —
/// нитеводителей), статус. Менять можно черновик и рисунок на проверке; утверждённый — только новой версией (<see cref="NextVersion"/>).
/// Версии и их файлы — только вставка (<see cref="DevPatternVersion"/>, <see cref="DevPatternFile"/>).
/// </summary>
public sealed class DevPattern
{
    public const int NameMaxLength = 150;
    public const int AlignmentMaxLength = 200;
    public const int NoteMaxLength = 1000;
    public const int MaxRapport = 2000;
    public const int MaxColors = 16;
    public const int MaxColorways = 30;

    private DevPattern()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public DevPatternKind Kind { get; private set; }

    /// <summary>Раппорт по ширине, петель.</summary>
    public int? RapportWidth { get; private set; }

    /// <summary>Раппорт по высоте, рядов.</summary>
    public int? RapportHeight { get; private set; }

    /// <summary>Допустимая длина протяжки, мм.</summary>
    public decimal? MaxFloatMm { get; private set; }

    /// <summary>Класс машины из справочника разработки (вид «Класс машины», число — нитеводителей).</summary>
    public long? MachineClassId { get; private set; }

    /// <summary>Ширина изделия (детали) в петлях — для проверки кратности раппорта.</summary>
    public int? ProductWidthLoops { get; private set; }

    /// <summary>Правило выравнивания, если раппорт не кратен ширине («центрировать по середине переда»).</summary>
    public string? AlignmentRule { get; private set; }

    public string? Note { get; private set; }
    public DevPatternStatus Status { get; private set; }
    public int CurrentVersion { get; private set; }
    public long? DessinatorUserId { get; private set; }
    public long? ProgrammerUserId { get; private set; }

    /// <summary>Дессинатор нажал «Рисунок готов к программированию» для текущей версии — задача программисту.</summary>
    public DateTime? ReadyForProgrammingAtUtc { get; private set; }

    /// <summary>Рисунок-источник, если создан копированием.</summary>
    public long? CopiedFromPatternId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Последняя утверждённая версия (ревью 13.10): её цвета, колориты и сетку больше не меняют — только новой версией.</summary>
    public int? ApprovedVersion { get; private set; }

    /// <summary>Время последней правки рисунка или его цветов и колоритов: правка меняет версию строки — утверждение с устаревшей версией получит конфликт.</summary>
    public DateTime? ChangedAtUtc { get; private set; }

    public bool IsEditable => Status is DevPatternStatus.Draft or DevPatternStatus.Review && ApprovedVersion != CurrentVersion;

    /// <summary>Отметить правку цветов, колоритов или файлов — меняет версию строки рисунка.</summary>
    public void Touch(DateTime nowUtc) =>
        ChangedAtUtc = ChangedAtUtc is { } last && nowUtc <= last ? last.AddMilliseconds(1) : nowUtc; // всегда новое значение — строка обновится

    public static string KindName(DevPatternKind kind) => kind switch
    {
        DevPatternKind.JacquardDouble => "Жаккард двухсторонний",
        DevPatternKind.JacquardFloat => "Жаккард с протяжками",
        DevPatternKind.Intarsia => "Интарсия",
        DevPatternKind.Structural => "Структурный (косы, араны, ажур)",
        DevPatternKind.Stripes => "Полосы",
        _ => "Принт по полотну",
    };

    public static string StatusName(DevPatternStatus status) => status switch
    {
        DevPatternStatus.Draft => "Черновик",
        DevPatternStatus.Review => "На проверке",
        DevPatternStatus.Approved => "Утверждён",
        _ => "В архиве",
    };

    public static readonly IReadOnlyList<DevPatternKind> Kinds = Enum.GetValues<DevPatternKind>();

    public static DevPattern Create(long organizationId, string number, DevPatternValues values, DateTime nowUtc, long userId, long? copiedFrom = null)
    {
        var p = new DevPattern
        {
            OrganizationId = organizationId,
            Number = number,
            Status = DevPatternStatus.Draft,
            CurrentVersion = 1,
            CreatedAtUtc = nowUtc,
            CreatedByUserId = userId,
            CopiedFromPatternId = copiedFrom,
        };
        p.Apply(values);
        return p;
    }

    /// <summary>Изменить свойства — только черновик или рисунок на проверке.</summary>
    public void Set(DevPatternValues values)
    {
        EnsureEditable();
        Apply(values);
    }

    public void EnsureEditable()
    {
        if (!IsEditable)
        {
            throw new BusinessRuleException("dev.pattern.locked",
                Status == DevPatternStatus.Archived
                    ? "Рисунок в архиве — сначала верните его из архива."
                    : "Эта версия рисунка утверждена — изменения вносятся новой версией рисунка.");
        }
    }

    private void ApplyApproval()
    {
        ApprovedVersion = CurrentVersion;
    }

    private void Apply(DevPatternValues v)
    {
        if (!Enum.IsDefined(v.Kind))
        {
            throw new BusinessRuleException("dev.pattern.kind", "Выберите вид рисунка.");
        }

        if (v.RapportWidth is < 1 or > MaxRapport || v.RapportHeight is < 1 or > MaxRapport || v.ProductWidthLoops is < 1 or > 10000)
        {
            throw new BusinessRuleException("dev.pattern.rapport", $"Раппорт — от 1 до {MaxRapport} петель и рядов, ширина изделия — до 10 000 петель.");
        }

        if (v.MaxFloatMm is < 0 or > 1000)
        {
            throw new BusinessRuleException("dev.pattern.float", "Длина протяжки — от 0 до 1000 мм.");
        }

        Name = DomainText.Require(v.Name, NameMaxLength, "Название");
        Kind = v.Kind;
        RapportWidth = v.RapportWidth;
        RapportHeight = v.RapportHeight;
        MaxFloatMm = v.MaxFloatMm;
        MachineClassId = v.MachineClassId;
        ProductWidthLoops = v.ProductWidthLoops;
        AlignmentRule = DomainText.Optional(v.AlignmentRule, AlignmentMaxLength, "Правило выравнивания");
        Note = DomainText.Optional(v.Note, NoteMaxLength, "Описание");
        DessinatorUserId = v.DessinatorUserId;
        ProgrammerUserId = v.ProgrammerUserId;
    }

    /// <summary>Перевести в другой статус по правилам: на проверку — из черновика; утвердить — из черновика или проверки (проверки — сервис);
    /// вернуть в черновик — с проверки или утверждённый; в архив — откуда угодно; из архива — в черновик.</summary>
    public void MoveTo(DevPatternStatus target)
    {
        var ok = (Status, target) switch
        {
            (DevPatternStatus.Draft, DevPatternStatus.Review) => true,
            (DevPatternStatus.Draft or DevPatternStatus.Review, DevPatternStatus.Approved) => true,
            (DevPatternStatus.Review or DevPatternStatus.Approved, DevPatternStatus.Draft) => true,
            (not DevPatternStatus.Archived, DevPatternStatus.Archived) => true,
            (DevPatternStatus.Archived, DevPatternStatus.Draft) => true,
            _ => false,
        };
        if (!ok)
        {
            throw new BusinessRuleException("dev.pattern.status", $"Из статуса «{StatusName(Status)}» в «{StatusName(target)}» перевести нельзя.");
        }

        if (target == DevPatternStatus.Approved)
        {
            ApplyApproval();
        }

        Status = target;
    }

    /// <summary>Новая версия: номер +1, статус — черновик, отметка «готов к программированию» снимается (программа — к новой версии).</summary>
    public int NextVersion()
    {
        if (Status == DevPatternStatus.Archived)
        {
            throw new BusinessRuleException("dev.pattern.locked", "Рисунок в архиве — сначала верните его из архива.");
        }

        CurrentVersion++;
        Status = DevPatternStatus.Draft;
        ReadyForProgrammingAtUtc = null;
        return CurrentVersion;
    }

    /// <summary>«Рисунок готов к программированию» (ТЗ §7): у текущей версии должен быть файл рисунка — проверяет сервис.</summary>
    public void MarkReadyForProgramming(DateTime nowUtc)
    {
        if (Status == DevPatternStatus.Archived)
        {
            throw new BusinessRuleException("dev.pattern.locked", "Рисунок в архиве — сначала верните его из архива.");
        }

        ReadyForProgrammingAtUtc = nowUtc;
    }

    /// <summary>Номер счётчика «dev.pattern-000001» → «Р-00001».</summary>
    public static string FormatNumber(string counter) =>
        "Р-" + long.Parse(counter[(counter.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture).ToString("D5");
}

/// <summary>Версия рисунка (D727): номер, что изменено, причина, кто и когда. Только вставка (триггер в базе).</summary>
public sealed class DevPatternVersion
{
    public const int TextMaxLength = 300;

    private DevPatternVersion()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PatternId { get; private set; }
    public int Version { get; private set; }
    public string Change { get; private set; } = string.Empty;
    public string? Reason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }

    public static DevPatternVersion Create(long organizationId, long patternId, int version, string? change, string? reason, DateTime nowUtc, long userId) => new()
    {
        OrganizationId = organizationId,
        PatternId = patternId,
        Version = version,
        Change = DomainText.Require(change, TextMaxLength, "Что изменено"),
        Reason = DomainText.Optional(reason, TextMaxLength, "Причина"),
        CreatedAtUtc = nowUtc,
        CreatedByUserId = userId,
    };
}

/// <summary>
/// Файл версии рисунка (D727): сетка рисунка (PNG, BMP или CSV/TXT, где клетка — петля) или программа машины с временем вязки одного
/// изделия. Только вставка: новый файл — новая строка, прежние остаются.
/// </summary>
public sealed class DevPatternFile
{
    public const int MaxKnitSeconds = 24 * 3600;

    private DevPatternFile()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PatternId { get; private set; }
    public int Version { get; private set; }
    public DevPatternFileKind Kind { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];

    /// <summary>Время вязки одного изделия по программе, секунд (отмечает программист).</summary>
    public int? KnitSeconds { get; private set; }

    public DateTime UploadedAtUtc { get; private set; }
    public long UploadedByUserId { get; private set; }

    public const string Csv = "text/csv";

    public static string Accept(DevPatternFileKind kind) => kind == DevPatternFileKind.Pattern ? ".png,.bmp,.csv,.txt" : ".sin,.dat";

    public static DevPatternFile Create(long organizationId, long patternId, int version, DevPatternFileKind kind, string? fileName, byte[] content,
        int? knitSeconds, DateTime nowUtc, long userId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("dev.pattern.file.kind", "Выберите вид файла рисунка.");
        }

        if (content.Length == 0 || content.Length > DevFile.MaxBytes)
        {
            throw new BusinessRuleException("dev.file.size", "Файл модели — до 50 МБ.");
        }

        if (knitSeconds is < 1 or > MaxKnitSeconds)
        {
            throw new BusinessRuleException("dev.pattern.knit", "Время вязки изделия — от 1 секунды до 24 часов.");
        }

        var name = DevFile.CleanName(fileName);
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var type = kind == DevPatternFileKind.Program
            ? DevFileTypes.Detect(DevFileKind.MachineProgram, name, content)
            : ext is "csv" or "txt" ? (DevPatternGrid.LooksLikeTextGrid(content) ? Csv : null) : DevFileTypes.Detect(DevFileKind.Pattern, name, content);
        if (type is null)
        {
            throw new BusinessRuleException("dev.pattern.file.type", $"Подходят файлы: {Accept(kind)}. Содержимое должно соответствовать расширению.");
        }

        return new DevPatternFile
        {
            OrganizationId = organizationId,
            PatternId = patternId,
            Version = version,
            Kind = kind,
            FileName = name,
            ContentType = type,
            SizeBytes = content.Length,
            Content = content,
            KnitSeconds = kind == DevPatternFileKind.Program ? knitSeconds : null,
            UploadedAtUtc = nowUtc,
            UploadedByUserId = userId,
        };
    }
}

/// <summary>
/// Цвет рисунка (D726): номер 1…16, название («Фон», «Узор 1»), доля в раппорте, %, нитеводитель; «цвет сетки» — цвет пикселя PNG
/// (#RRGGBB) или символ клетки CSV, по нему доли считаются из файла-сетки.
/// </summary>
public sealed class DevPatternColor
{
    public const int NameMaxLength = 60;
    public const int GridKeyMaxLength = 10;

    private DevPatternColor()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PatternId { get; private set; }
    public int No { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal? SharePercent { get; private set; }
    public int? Feeder { get; private set; }
    public string? GridKey { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevPatternColor Create(long organizationId, long patternId, int no, string? name, decimal? share, int? feeder, string? gridKey)
    {
        var c = new DevPatternColor { OrganizationId = organizationId, PatternId = patternId, No = no };
        c.Set(name, share, feeder, gridKey);
        return c;
    }

    public void Set(string? name, decimal? share, int? feeder, string? gridKey)
    {
        if (share is < 0 or > 100)
        {
            throw new BusinessRuleException("dev.pattern.share", "Доля цвета — от 0 до 100 %.");
        }

        if (feeder is < 1 or > DevMachine.MaxFeeders)
        {
            throw new BusinessRuleException("dev.pattern.feeder", $"Нитеводитель — от 1 до {DevMachine.MaxFeeders}.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Цвет");
        SharePercent = share is { } s ? Math.Round(s, 2) : null;
        Feeder = feeder;
        GridKey = DomainText.Optional(gridKey, GridKeyMaxLength, "Цвет сетки");
    }

    /// <summary>Доля по файлу-сетке; <paramref name="gridKey"/> — новый цвет сетки, <paramref name="clearKey"/> — снять его (цвета нет в сетке).</summary>
    public void SetShare(decimal? share, string? gridKey, bool clearKey = false)
    {
        SharePercent = share is { } s ? Math.Round(Math.Clamp(s, 0, 100), 2) : null;
        GridKey = clearKey ? null : gridKey is null ? GridKey : DomainText.Optional(gridKey, GridKeyMaxLength, "Цвет сетки");
    }
}

/// <summary>Колорит (D726): одно сочетание цветов рисунка — своя раскладка цвет → пряжа; будущие модификации «цвет» в каталоге.</summary>
public sealed class DevPatternColorway
{
    public const int NameMaxLength = 100;

    private DevPatternColorway()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PatternId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevPatternColorway Create(long organizationId, long patternId, string? name, int sortOrder) => new()
    {
        OrganizationId = organizationId,
        PatternId = patternId,
        Name = DomainText.Require(name, NameMaxLength, "Колорит"),
        SortOrder = sortOrder,
    };

    public void Rename(string? name) => Name = DomainText.Require(name, NameMaxLength, "Колорит");
}

/// <summary>Пряжа цвета в колорите (D726): цвет рисунка → позиция номенклатуры (сырьё или материал) и цвет на экране.</summary>
public sealed class DevPatternColorwayYarn
{
    private DevPatternColorwayYarn()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ColorwayId { get; private set; }
    public long ColorId { get; private set; }
    public long? ItemId { get; private set; }
    public string? Swatch { get; private set; }

    public static DevPatternColorwayYarn Create(long organizationId, long colorwayId, long colorId) =>
        new() { OrganizationId = organizationId, ColorwayId = colorwayId, ColorId = colorId };

    public void Set(long? itemId, string? swatch)
    {
        if (swatch is not null && !System.Text.RegularExpressions.Regex.IsMatch(swatch, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new BusinessRuleException("dev.color.swatch", "Цвет на экране — в виде #RRGGBB.");
        }

        ItemId = itemId;
        Swatch = swatch;
    }
}

/// <summary>Проверки при утверждении рисунка (ТЗ §7) — каждая отдельным кодом с переводимым сообщением.</summary>
public static class DevPatternRules
{
    /// <param name="colors">Цвета рисунка.</param>
    /// <param name="colorwayYarns">По каждому колориту — цвета, у которых выбрана пряжа.</param>
    /// <param name="feeders">Нитеводителей у класса машины (null — класс не выбран).</param>
    public static void EnsureApprovable(DevPattern p, IReadOnlyCollection<long> colors, IReadOnlyCollection<(string Colorway, IReadOnlyCollection<long> ColorsWithYarn)> colorwayYarns,
        int? feeders)
    {
        if (colors.Count == 0)
        {
            throw new BusinessRuleException("dev.pattern.approve.colors", "Добавьте цвета рисунка.");
        }

        if (colorwayYarns.Count == 0)
        {
            throw new BusinessRuleException("dev.pattern.approve.colorway", "Добавьте хотя бы один колорит с пряжей для каждого цвета.");
        }

        foreach (var (colorway, withYarn) in colorwayYarns)
        {
            if (colors.Any(c => !withYarn.Contains(c)))
            {
                throw new BusinessRuleException("dev.pattern.approve.yarn", $"В колорите «{colorway}» не у каждого цвета выбрана пряжа.");
            }
        }

        if (feeders is not { } f)
        {
            throw new BusinessRuleException("dev.pattern.approve.machine", "Выберите класс машины, для которого разработан рисунок.");
        }

        if (colors.Count > f)
        {
            throw new BusinessRuleException("dev.pattern.approve.feeders", $"Цветов ({colors.Count}) больше, чем нитеводителей у класса машины ({f}).");
        }

        if (p.RapportWidth is not { } rw)
        {
            throw new BusinessRuleException("dev.pattern.approve.rapport", "Укажите раппорт по ширине.");
        }

        if (!(p.ProductWidthLoops is { } w && w % rw == 0) && p.AlignmentRule is null)
        {
            throw new BusinessRuleException("dev.pattern.approve.align",
                "Ширина изделия в петлях должна быть кратна раппорту — или укажите правило выравнивания.");
        }
    }
}

/// <summary>
/// Снимок цвета версии рисунка (ревью 13.10): при новой версии сохраняется, какими были цвета, доли, нитеводители и пряжа колоритов прежней
/// версии, — паспорт модели, привязанной к старой версии, печатает их, а не текущие. Только вставка (триггер).
/// </summary>
public sealed class DevPatternVersionColor
{
    private DevPatternVersionColor()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long PatternId { get; private set; }
    public int Version { get; private set; }
    public int ColorNo { get; private set; }
    public string ColorName { get; private set; } = string.Empty;
    public decimal? SharePercent { get; private set; }
    public int? Feeder { get; private set; }
    public long? ColorwayId { get; private set; }
    public string? Colorway { get; private set; }
    public string? Yarn { get; private set; }

    public static DevPatternVersionColor Create(long organizationId, long patternId, int version, int colorNo, string colorName, decimal? share, int? feeder,
        long? colorwayId, string? colorway, string? yarn) => new()
    {
        OrganizationId = organizationId,
        PatternId = patternId,
        Version = version,
        ColorNo = colorNo,
        ColorName = colorName,
        SharePercent = share,
        Feeder = feeder,
        ColorwayId = colorwayId,
        Colorway = colorway,
        Yarn = yarn is { Length: > 300 } ? yarn[..300] : yarn,
    };
}
