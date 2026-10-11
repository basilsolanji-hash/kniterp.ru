using KnitErp.Domain.Common;

namespace KnitErp.Domain.Warehousing;

public enum OpeningBalanceStatus : byte
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Cancelled = 9,
}

/// <summary>
/// Документ начальных остатков склада. Черновик заполняет Старший кладовщик, утверждает другой человек —
/// Владелец или Руководитель (допущение D05). Утверждённый документ не меняется: он создаёт движения регистра.
/// Документ не удаляется — черновик можно только отменить.
/// </summary>
public sealed class OpeningBalance
{
    public const int CommentMaxLength = 1000;
    public const int ReasonMaxLength = 500;
    public const string NumberPrefix = "НО";

    private readonly List<OpeningBalanceLine> _lines = [];

    private OpeningBalance()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public long WarehouseId { get; private set; }

    /// <summary>Дата, на которую фиксируются остатки (начало учёта в системе).</summary>
    public DateOnly AsOfDate { get; private set; }

    public OpeningBalanceStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public long? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }

    /// <summary>Причина последнего возврата на доработку — видна автору черновика.</summary>
    public string? ReturnReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Растёт при правке разбивки по партиям (D100): разбивка — отдельная таблица, а версия документа должна меняться.</summary>
    public int BatchRevision { get; private set; }
    public IReadOnlyCollection<OpeningBalanceLine> Lines => _lines;

    public static OpeningBalance Create(
        long organizationId, string number, long warehouseId, DateOnly asOfDate, string? comment, long createdBy, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        Number = number,
        WarehouseId = warehouseId,
        AsOfDate = asOfDate,
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
        Status = OpeningBalanceStatus.Draft,
        CreatedByUserId = createdBy,
        CreatedAtUtc = nowUtc,
    };

    public void UpdateHeader(DateOnly asOfDate, string? comment)
    {
        EnsureDraft();
        AsOfDate = asOfDate;
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
    }

    /// <summary>Строка по позиции: новая или замена количества. Одна позиция — одна строка.</summary>
    /// <summary>Разбивка по партиям изменилась (D100) — только у черновика.</summary>
    public void MarkBatchesChanged()
    {
        EnsureDraft();
        BatchRevision++;
    }

    public void SetLine(long itemId, decimal quantity)
    {
        EnsureDraft();
        Quantities.EnsurePositive(quantity);
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is null)
        {
            _lines.Add(new OpeningBalanceLine(itemId, quantity));
        }
        else
        {
            line.SetQuantity(quantity);
        }
    }

    public void RemoveLine(long itemId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId)
                   ?? throw new BusinessRuleException("stock.opening.line_missing", "Такой строки в документе нет.");
        _lines.Remove(line);
    }

    /// <summary>Замена всех строк (загрузка из файла). Повторов позиций быть не должно — их отсекает проверка файла.</summary>
    public void ReplaceLines(IReadOnlyList<(long ItemId, decimal Quantity)> lines)
    {
        EnsureDraft();
        if (lines.Select(l => l.ItemId).Distinct().Count() != lines.Count)
        {
            throw new BusinessRuleException("stock.opening.duplicate_item", "Позиция повторяется в документе.");
        }

        foreach (var (_, quantity) in lines)
        {
            Quantities.EnsurePositive(quantity);
        }

        _lines.Clear();
        _lines.AddRange(lines.Select(l => new OpeningBalanceLine(l.ItemId, l.Quantity)));
    }

    public void Submit(DateTime nowUtc)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("stock.opening.empty", "В документе нет строк.");
        }

        Status = OpeningBalanceStatus.Submitted;
        SubmittedAtUtc = nowUtc;
        ReturnReason = null;
    }

    /// <summary>Возврат на доработку: документ снова черновик, причина видна автору.</summary>
    public void ReturnToDraft(string? reason)
    {
        EnsureStatus(OpeningBalanceStatus.Submitted, "Вернуть можно только документ на утверждении.");
        ReturnReason = DomainText.Require(reason, ReasonMaxLength, "Причина возврата");
        Status = OpeningBalanceStatus.Draft;
        SubmittedAtUtc = null;
    }

    /// <summary>Утверждение. Принцип четырёх глаз: автор не утверждает свой документ.</summary>
    public void Approve(long approverUserId, DateTime nowUtc)
    {
        EnsureStatus(OpeningBalanceStatus.Submitted, "Утвердить можно только документ на утверждении.");
        if (approverUserId == CreatedByUserId)
        {
            throw new BusinessRuleException("stock.opening.self_approval", "Автор документа не может его утвердить — нужен другой человек.");
        }

        Status = OpeningBalanceStatus.Approved;
        ApprovedByUserId = approverUserId;
        ApprovedAtUtc = nowUtc;
    }

    public void Cancel()
    {
        EnsureDraft();
        Status = OpeningBalanceStatus.Cancelled;
    }

    public static string StatusName(OpeningBalanceStatus status) => status switch
    {
        OpeningBalanceStatus.Draft => "Черновик",
        OpeningBalanceStatus.Submitted => "На утверждении",
        OpeningBalanceStatus.Approved => "Утверждён",
        OpeningBalanceStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };

    private void EnsureDraft() => EnsureStatus(OpeningBalanceStatus.Draft, "Документ уже не черновик — изменения запрещены.");

    private void EnsureStatus(OpeningBalanceStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new BusinessRuleException("stock.opening.status", message);
        }
    }
}

public sealed class OpeningBalanceLine
{
    private OpeningBalanceLine()
    {
    }

    internal OpeningBalanceLine(long itemId, decimal quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public long Id { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }

    internal void SetQuantity(decimal quantity) => Quantity = quantity;
}

/// <summary>Откуда пришло движение. Числа хранятся в базе.</summary>
public enum StockSource : byte
{
    OpeningBalance = 1,
    StockDocument = 2,

    /// <summary>Сторно складского документа: те же строки с обратным знаком.</summary>
    StockDocumentReversal = 3,
    Inventory = 4,
}

/// <summary>
/// Движение по складскому регистру: приход (+) или расход (−) позиции на складе на дату.
/// Только вставка: исправление — новым документом, а не правкой движения (как журнал аудита, защищено триггером).
/// Остаток = сумма движений.
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long WarehouseId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public StockSource Source { get; private set; }
    public long SourceId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Подпись записи в цепочке организации (как у журнала аудита) — ставит хранилище при вставке.</summary>
    public byte[]? ChainHash { get; private set; }

    /// <summary>Номер записи в цепочке организации: пропуск номера — удалённое движение.</summary>
    public long? ChainSeq { get; private set; }

    public static StockMovement Create(
        long organizationId, long warehouseId, long itemId, decimal quantity, DateOnly occurredOn, StockSource source, long sourceId, DateTime nowUtc)
    {
        if (quantity == 0)
        {
            throw new BusinessRuleException("stock.quantity.zero", "Нулевое движение не записывается.");
        }

        return new StockMovement
        {
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            ItemId = itemId,
            Quantity = quantity,
            OccurredOn = occurredOn,
            Source = source,
            SourceId = sourceId,
            CreatedAtUtc = nowUtc,
        };
    }
}
