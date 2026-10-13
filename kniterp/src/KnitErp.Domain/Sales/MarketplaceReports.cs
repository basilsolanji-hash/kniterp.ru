using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

public enum MarketplaceReportStatus : byte
{
    Active = 1,
    Cancelled = 9,
}

/// <summary>Вид строки отчёта маркетплейса (D1168): продажа, возврат или услуга (логистика, штраф, хранение — без товара).</summary>
public enum MarketplaceLineKind : byte
{
    Sale = 1,
    Return = 2,
    Service = 3,
}

/// <summary>Как найдена позиция строки (D1169).</summary>
public enum MarketplaceMatch : byte
{
    None = 0,
    ExternalLink = 1,
    Barcode = 2,
    Manual = 3,
}

/// <summary>
/// Поле отчёта маркетплейса, которое берётся из столбца файла (D1168). Столбец ищется по заголовку; заголовки по умолчанию —
/// <c>MarketplaceFormats</c>, своё сопоставление — <see cref="MarketplaceColumnMap"/>.
/// </summary>
public enum MarketplaceField : byte
{
    Article = 1,
    ExternalCode = 2,
    Barcode = 3,
    Name = 4,
    DocumentType = 5,
    PaymentReason = 6,
    Date = 7,
    Quantity = 8,
    Amount = 9,
    ReturnQuantity = 10,
    ReturnAmount = 11,
    Commission = 12,
    Logistics = 13,
    Payout = 14,
}

/// <summary>
/// Загруженный отчёт о реализации маркетплейса (D1168–D1170): канал, период, итоги и строки. Не удаляется; ошибочный —
/// аннулируется с причиной (тогда тот же файл можно загрузить снова). Отпечаток — SHA-256 разобранных строк: повтор файла
/// не создаёт второй отчёт (уникальный индекс по действующим).
/// </summary>
public sealed class MarketplaceReport
{
    public const int FileNameMaxLength = 255;
    public const int FingerprintLength = 64;
    public const int ReasonMaxLength = 500;
    public const int MaxLines = 50_000;

    private readonly List<MarketplaceReportLine> _lines = [];

    private MarketplaceReport()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ChannelId { get; private set; }
    public MarketplaceKind Marketplace { get; private set; }
    public DateOnly PeriodFrom { get; private set; }
    public DateOnly PeriodTo { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public MarketplaceReportStatus Status { get; private set; }
    public decimal SalesQuantity { get; private set; }
    public decimal SalesAmount { get; private set; }
    public decimal ReturnsQuantity { get; private set; }
    public decimal ReturnsAmount { get; private set; }
    public decimal Commission { get; private set; }
    public decimal Logistics { get; private set; }
    public decimal Payout { get; private set; }
    public int LineCount { get; private set; }
    public int UnmatchedCount { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<MarketplaceReportLine> Lines => _lines;

    public static MarketplaceReport Create(long organizationId, long channelId, MarketplaceKind marketplace, DateOnly from, DateOnly to, string? fileName,
        string fingerprint, IReadOnlyList<MarketplaceReportLine> lines, long userId, DateTime nowUtc)
    {
        if (to < from)
        {
            throw new BusinessRuleException("marketplace.period", "Период отчёта: дата «по» не раньше даты «с».");
        }

        if (lines.Count == 0)
        {
            throw new BusinessRuleException("marketplace.empty", "В отчёте нет строк с продажами, возвратами или услугами.");
        }

        if (lines.Count > MaxLines)
        {
            throw new BusinessRuleException("marketplace.too_many_lines", $"В отчёте больше {MaxLines} строк. Разбейте его на части.");
        }

        if (fingerprint.Length != FingerprintLength)
        {
            throw new ArgumentException("Отпечаток — 64 шестнадцатеричных символа.", nameof(fingerprint));
        }

        var name = string.IsNullOrWhiteSpace(fileName) ? "отчёт.xlsx" : fileName.Trim();
        var report = new MarketplaceReport
        {
            OrganizationId = organizationId,
            ChannelId = channelId,
            Marketplace = marketplace,
            PeriodFrom = from,
            PeriodTo = to,
            FileName = name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name,
            Fingerprint = fingerprint,
            Status = MarketplaceReportStatus.Active,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        report._lines.AddRange(lines);
        report.Recalculate();
        return report;
    }

    /// <summary>Итоги: продажи и возвраты — суммы «реализовано»; комиссия и к перечислению — продажи минус возвраты плюс услуги.</summary>
    public void Recalculate()
    {
        var sales = _lines.Where(l => l.Kind == MarketplaceLineKind.Sale).ToList();
        var returns = _lines.Where(l => l.Kind == MarketplaceLineKind.Return).ToList();
        var services = _lines.Where(l => l.Kind == MarketplaceLineKind.Service).ToList();
        SalesQuantity = sales.Sum(l => l.Quantity);
        SalesAmount = Money.Round(sales.Sum(l => l.Amount));
        ReturnsQuantity = returns.Sum(l => l.Quantity);
        ReturnsAmount = Money.Round(returns.Sum(l => l.Amount));
        Commission = Money.Round(sales.Sum(l => l.Commission) - returns.Sum(l => l.Commission) + services.Sum(l => l.Commission));
        Logistics = Money.Round(_lines.Sum(l => l.Logistics));
        Payout = Money.Round(sales.Sum(l => l.Payout) - returns.Sum(l => l.Payout) + services.Sum(l => l.Payout));
        LineCount = _lines.Count;
        UnmatchedCount = _lines.Count(l => l.NeedsItem);
    }

    /// <summary>Позиция для строк с этим ключом (D1169) — решение пользователя по несопоставленным.</summary>
    public int Resolve(string key, long itemId)
    {
        EnsureActive();
        var hits = _lines.Where(l => l.ItemId is null && l.Key == key).ToList();
        foreach (var line in hits)
        {
            line.Match(itemId, MarketplaceMatch.Manual);
        }

        UnmatchedCount = _lines.Count(l => l.NeedsItem);
        return hits.Count;
    }

    public void Cancel(string? reason, long userId, DateTime nowUtc)
    {
        EnsureActive();
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new BusinessRuleException("marketplace.cancel_reason", "Укажите причину аннулирования отчёта.");
        }

        if (text.Length > ReasonMaxLength)
        {
            throw new BusinessRuleException("marketplace.cancel_reason_length", $"Причина — не длиннее {ReasonMaxLength} символов.");
        }

        Status = MarketplaceReportStatus.Cancelled;
        CancelReason = text;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }

    private void EnsureActive()
    {
        if (Status != MarketplaceReportStatus.Active)
        {
            throw new BusinessRuleException("marketplace.cancelled", "Отчёт аннулирован — изменить его нельзя.");
        }
    }
}

/// <summary>Строка отчёта маркетплейса (D1168): товар (артикул, код маркетплейса, штрихкод), количество и деньги.</summary>
public sealed class MarketplaceReportLine
{
    public const int ArticleMaxLength = 100;
    public const int CodeMaxLength = 64;
    public const int BarcodeMaxLength = 64;
    public const int NameMaxLength = 300;
    public const int NoteMaxLength = 100;

    private MarketplaceReportLine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ReportId { get; private set; }

    /// <summary>Номер строки в файле — чтобы найти её в исходном отчёте.</summary>
    public int RowNumber { get; private set; }

    public MarketplaceLineKind Kind { get; private set; }
    public string? Article { get; private set; }
    public string? ExternalCode { get; private set; }
    public string? Barcode { get; private set; }
    public string? Name { get; private set; }

    /// <summary>Обоснование услуги («Логистика», «Штраф») или тип документа — как в файле.</summary>
    public string? Note { get; private set; }

    public DateOnly? SaleDate { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Commission { get; private set; }
    public decimal Logistics { get; private set; }
    public decimal Payout { get; private set; }
    public long? ItemId { get; private set; }
    public MarketplaceMatch MatchedBy { get; private set; }

    /// <summary>Продажа или возврат без позиции — «нужно решение».</summary>
    public bool NeedsItem => ItemId is null && Kind != MarketplaceLineKind.Service;

    /// <summary>Ключ товара для решения по несопоставленным: код маркетплейса, артикул и штрихкод.</summary>
    public string Key => KeyOf(ExternalCode, Article, Barcode);

    public static string KeyOf(string? code, string? article, string? barcode) => $"{code}|{article}|{barcode}";

    public static MarketplaceReportLine Create(long organizationId, int rowNumber, MarketplaceLineKind kind, string? article, string? externalCode,
        string? barcode, string? name, string? note, DateOnly? date, decimal quantity, decimal amount, decimal commission, decimal logistics, decimal payout) =>
        new()
        {
            OrganizationId = organizationId,
            RowNumber = rowNumber,
            Kind = kind,
            Article = Cut(article, ArticleMaxLength),
            ExternalCode = Cut(externalCode, CodeMaxLength),
            Barcode = Cut(barcode, BarcodeMaxLength),
            Name = Cut(name, NameMaxLength),
            Note = Cut(note, NoteMaxLength),
            SaleDate = date,
            Quantity = decimal.Round(quantity, 6),
            Amount = decimal.Round(amount, 4),
            Commission = decimal.Round(commission, 4),
            Logistics = decimal.Round(logistics, 4),
            Payout = decimal.Round(payout, 4),
        };

    public void Match(long itemId, MarketplaceMatch how)
    {
        ItemId = itemId;
        MatchedBy = how;
    }

    private static string? Cut(string? value, int max)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}

/// <summary>
/// Своё сопоставление столбца файла маркетплейса (D1168): для площадки и поля — заголовок столбца в файле. Нет записи —
/// заголовки по умолчанию; пустой заголовок — поле не берётся из файла.
/// </summary>
public sealed class MarketplaceColumnMap
{
    public const int HeaderMaxLength = 200;

    private MarketplaceColumnMap()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public MarketplaceKind Marketplace { get; private set; }
    public MarketplaceField Field { get; private set; }
    public string Header { get; private set; } = string.Empty;
    public byte[] RowVersion { get; private set; } = [];

    public static MarketplaceColumnMap Create(long organizationId, MarketplaceKind marketplace, MarketplaceField field) =>
        new() { OrganizationId = organizationId, Marketplace = marketplace, Field = field };

    public void Set(string? header)
    {
        var t = (header ?? string.Empty).Trim();
        if (t.Length > HeaderMaxLength)
        {
            throw new BusinessRuleException("marketplace.header_length", $"Заголовок столбца — не длиннее {HeaderMaxLength} символов.");
        }

        Header = t;
    }
}
