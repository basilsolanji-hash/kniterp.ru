using KnitErp.Domain.Common;

namespace KnitErp.Domain.Warehousing;

public enum InventoryStatus : byte
{
    Draft = 1,
    Posted = 2,
    Cancelled = 9,
}

/// <summary>
/// Инвентаризация склада: учётное количество (сумма движений) сравнивается с фактически пересчитанным.
/// Проведение пишет разницу движениями: излишек — приход, недостача — расход. Учётное количество фиксируется
/// заново в момент проведения (допущение D42). Проведённая инвентаризация не сторнируется: ошибку исправляет
/// следующая инвентаризация (допущение D43).
/// </summary>
public sealed class InventoryCount
{
    public const int CommentMaxLength = 1000;
    public const string NumberPrefix = "ИН";

    private readonly List<InventoryLine> _lines = [];

    private InventoryCount()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public long WarehouseId { get; private set; }
    public DateOnly CountDate { get; private set; }
    public InventoryStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? PostedByUserId { get; private set; }
    public DateTime? PostedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Растёт при правке разбивки по партиям (D100): разбивка — отдельная таблица, а версия документа должна меняться.</summary>
    public int BatchRevision { get; private set; }
    public IReadOnlyCollection<InventoryLine> Lines => _lines;

    public static InventoryCount Create(
        long organizationId, string number, long warehouseId, DateOnly countDate, string? comment, long createdBy, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        Number = number,
        WarehouseId = warehouseId,
        CountDate = countDate,
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
        Status = InventoryStatus.Draft,
        CreatedByUserId = createdBy,
        CreatedAtUtc = nowUtc,
    };

    public void UpdateHeader(DateOnly countDate, string? comment)
    {
        EnsureDraft();
        CountDate = countDate;
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
    }

    /// <summary>
    /// «Заполнить по учёту»: строки для всех позиций с остатком; у существующих строк обновляется учётное
    /// количество, пересчитанное не трогается. Позиции, которых на складе по учёту нет, остаются, если их уже пересчитали.
    /// </summary>
    public int FillFromBook(IReadOnlyDictionary<long, decimal> book)
    {
        EnsureDraft();
        var added = 0;
        foreach (var (itemId, quantity) in book)
        {
            var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
            if (line is null)
            {
                _lines.Add(new InventoryLine(itemId, quantity, null));
                added++;
            }
            else
            {
                line.SetBook(quantity);
            }
        }

        foreach (var line in _lines.Where(l => !book.ContainsKey(l.ItemId)))
        {
            line.SetBook(0);
        }

        return added;
    }

    /// <summary>Фактическое количество по позиции; null — ещё не пересчитано. Новая позиция получает учётное количество.</summary>
    /// <summary>Разбивка по партиям изменилась (D100) — только у черновика.</summary>
    public void MarkBatchesChanged()
    {
        EnsureDraft();
        BatchRevision++;
    }

    public void SetCounted(long itemId, decimal? counted, decimal bookIfNew)
    {
        EnsureDraft();
        if (counted is { } c && c < 0)
        {
            throw new BusinessRuleException("stock.quantity.negative", "Количество не может быть отрицательным.");
        }

        if (counted is { } positive && positive > 0)
        {
            Quantities.EnsurePositive(positive);
        }

        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is null)
        {
            _lines.Add(new InventoryLine(itemId, bookIfNew, counted));
        }
        else
        {
            line.SetCounted(counted);
        }
    }

    public void RemoveLine(long itemId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId)
                   ?? throw new BusinessRuleException("stock.inventory.line_missing", "Такой строки в инвентаризации нет.");
        _lines.Remove(line);
    }

    /// <summary>
    /// Проведение: учётные количества — на момент проведения, все строки пересчитаны; недостача требует пояснения.
    /// Возвращает разницы (факт − учёт) для движений.
    /// </summary>
    public IReadOnlyList<(long ItemId, decimal Difference)> Post(IReadOnlyDictionary<long, decimal> book, long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("stock.inventory.empty", "В инвентаризации нет строк.");
        }

        var notCounted = _lines.Count(l => l.CountedQuantity is null);
        if (notCounted > 0)
        {
            throw new BusinessRuleException("stock.inventory.not_counted", $"Не пересчитано позиций: {notCounted}. Укажите факт, хотя бы 0.");
        }

        foreach (var line in _lines)
        {
            line.SetBook(book.GetValueOrDefault(line.ItemId));
        }

        var differences = _lines.Where(l => l.Difference != 0).Select(l => (l.ItemId, l.Difference)).ToList();
        if (differences.Any(d => d.Difference < 0) && Comment is null)
        {
            throw new BusinessRuleException("stock.inventory.comment_required", "Есть недостача — укажите пояснение в комментарии.");
        }

        Status = InventoryStatus.Posted;
        PostedByUserId = userId;
        PostedAtUtc = nowUtc;
        return differences;
    }

    public void Cancel()
    {
        EnsureDraft();
        Status = InventoryStatus.Cancelled;
    }

    public static string StatusName(InventoryStatus status) => status switch
    {
        InventoryStatus.Draft => "Черновик",
        InventoryStatus.Posted => "Проведена",
        InventoryStatus.Cancelled => "Отменена",
        _ => status.ToString(),
    };

    private void EnsureDraft()
    {
        if (Status != InventoryStatus.Draft)
        {
            throw new BusinessRuleException("stock.inventory.status", "Инвентаризация уже не черновик — изменения запрещены.");
        }
    }
}

public sealed class InventoryLine
{
    private InventoryLine()
    {
    }

    internal InventoryLine(long itemId, decimal bookQuantity, decimal? countedQuantity)
    {
        ItemId = itemId;
        BookQuantity = bookQuantity;
        CountedQuantity = countedQuantity;
    }

    public long Id { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }

    /// <summary>Учётное количество: сумма движений по складу (в черновике — на момент заполнения, после проведения — на момент проведения).</summary>
    public decimal BookQuantity { get; private set; }

    public decimal? CountedQuantity { get; private set; }

    /// <summary>Факт − учёт: плюс — излишек, минус — недостача.</summary>
    public decimal Difference => (CountedQuantity ?? BookQuantity) - BookQuantity;

    internal void SetBook(decimal quantity) => BookQuantity = quantity;

    internal void SetCounted(decimal? quantity) => CountedQuantity = quantity;
}

/// <summary>
/// Закрытый период склада организации: движений с датой по <see cref="ClosedThrough"/> включительно быть не может —
/// проверяют сервисы и триггер базы. Закрывает и открывает период Владелец (право <c>period.closed.reopen</c>, допущение D44).
/// </summary>
public sealed class PeriodClosure
{
    public const int ReasonMaxLength = 500;

    private PeriodClosure()
    {
    }

    public long OrganizationId { get; private set; }
    public DateOnly? ClosedThrough { get; private set; }
    public long ChangedByUserId { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static PeriodClosure Start(long organizationId, long userId, DateTime nowUtc) =>
        new() { OrganizationId = organizationId, ChangedByUserId = userId, ChangedAtUtc = nowUtc };

    public bool IsClosed(DateOnly date) => ClosedThrough is { } c && date <= c;

    /// <summary>Закрытие идёт только вперёд; сдвинуть границу назад — это открытие с причиной.</summary>
    public void CloseThrough(DateOnly date, long userId, DateTime nowUtc)
    {
        if (ClosedThrough is { } current && date <= current)
        {
            throw new BusinessRuleException("period.close.backwards", $"Период уже закрыт по {current:dd.MM.yyyy}. Чтобы сдвинуть границу назад, откройте период.");
        }

        ClosedThrough = date;
        ChangedByUserId = userId;
        ChangedAtUtc = nowUtc;
    }

    /// <summary>Открытие: граница сдвигается назад (или период открыт полностью). Причина обязательна — она идёт в журнал.</summary>
    public string Reopen(DateOnly? newClosedThrough, string? reason, long userId, DateTime nowUtc)
    {
        if (ClosedThrough is not { } current)
        {
            throw new BusinessRuleException("period.not_closed", "Закрытого периода нет.");
        }

        if (newClosedThrough is { } d && d >= current)
        {
            throw new BusinessRuleException("period.reopen.forward", "Новая граница должна быть раньше текущей.");
        }

        var text = DomainText.Require(reason, ReasonMaxLength, "Причина открытия");
        ClosedThrough = newClosedThrough;
        ChangedByUserId = userId;
        ChangedAtUtc = nowUtc;
        return text;
    }
}
