using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Domain.Warehousing;

/// <summary>
/// Партия материала (D89), прежде всего пряжи: номер партии поставщика, цвет и номер крашения (dye lot). Пряжа разных крашений
/// одного цвета даёт заметную разницу тона в полотне — поэтому её учитывают раздельно и не смешивают в одном изделии.
/// Партия принадлежит позиции номенклатуры; остаток партии — сумма движений партийного регистра. Не удаляется — архивируется
/// при нулевом остатке.
/// </summary>
public sealed class StockBatch
{
    public const int NumberMaxLength = 50;
    public const int ColorMaxLength = 100;
    public const int DyeLotMaxLength = 50;
    public const int CommentMaxLength = 500;

    private StockBatch()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }

    /// <summary>Номер партии — с ярлыка поставщика или свой; уникален в пределах позиции.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Цвет словами или номер цвета по карте поставщика.</summary>
    public string? Color { get; private set; }

    /// <summary>Номер крашения (партия крашения, dye lot).</summary>
    public string? DyeLot { get; private set; }

    /// <summary>Поставщик партии — подсказка для повторного заказа того же крашения.</summary>
    public long? SupplierId { get; private set; }

    /// <summary>Вес нетто одного места (бобины, мотка, коробки), кг — для пересчёта кг ↔ места.</summary>
    public decimal? PackageWeightKg { get; private set; }

    public string? Comment { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static StockBatch Create(long organizationId, long itemId, StockBatchData data, DateTime nowUtc)
    {
        var batch = new StockBatch { OrganizationId = organizationId, ItemId = itemId, CreatedAtUtc = nowUtc };
        batch.Apply(data);
        return batch;
    }

    public IReadOnlyList<FieldChange> Update(StockBatchData data)
    {
        EnsureActive();
        var before = (Number, Color, DyeLot, SupplierId, PackageWeightKg, Comment);
        Apply(data);
        var changes = new List<FieldChange>();
        void Add(string field, object? a, object? b)
        {
            if (!Equals(a, b))
            {
                changes.Add(new FieldChange(field, a?.ToString(), b?.ToString()));
            }
        }

        Add("Номер партии", before.Number, Number);
        Add("Цвет", before.Color, Color);
        Add("Крашение", before.DyeLot, DyeLot);
        Add(nameof(SupplierId), before.SupplierId, SupplierId);
        Add("Вес места, кг", before.PackageWeightKg, PackageWeightKg);
        Add("Комментарий", before.Comment, Comment);
        return changes;
    }

    /// <summary>В архив — только пустая партия: иначе её остаток пропал бы из выбора при расходе.</summary>
    public void Archive(decimal balance)
    {
        EnsureActive();
        if (balance != 0)
        {
            throw new BusinessRuleException("stock.batch.has_balance", $"У партии {Number} есть остаток {Quantities.Format(balance)} — в архив нельзя.");
        }

        IsArchived = true;
    }

    public void Restore()
    {
        if (!IsArchived)
        {
            throw new BusinessRuleException("stock.batch.not_archived", "Партия не в архиве.");
        }

        IsArchived = false;
    }

    /// <summary>Префикс кода партии на ярлыке (D101): «KB» и номер записи — уникален, не зависит от номера партии поставщика.</summary>
    public const string CodePrefix = "KB";

    /// <summary>Код партии для штрихкода Code 128 на ярлыке: KB00000123.</summary>
    public static string Code(long id) => CodePrefix + id.ToString("D8");

    /// <summary>Разбор кода со сканера: регистр и пробелы не важны; не код партии — null.</summary>
    public static long? ParseCode(string? scanned)
    {
        var text = scanned?.Trim().ToUpperInvariant();
        return text is { Length: > 2 } && text.StartsWith(CodePrefix, StringComparison.Ordinal) && text[2..].All(char.IsAsciiDigit)
               && long.TryParse(text[2..], out var id) && id > 0
            ? id
            : null;
    }

    /// <summary>Подпись партии в списках: «П-117 · бордо · кр. 2231».</summary>
    public static string Describe(string number, string? color, string? dyeLot) =>
        string.Join(" · ", new[] { number, color, dyeLot is null ? null : "кр. " + dyeLot }.Where(s => !string.IsNullOrEmpty(s)));

    private void Apply(StockBatchData d)
    {
        Number = DomainText.Require(d.Number, NumberMaxLength, "Номер партии");
        Color = DomainText.Optional(d.Color, ColorMaxLength, "Цвет");
        DyeLot = DomainText.Optional(d.DyeLot, DyeLotMaxLength, "Номер крашения");
        SupplierId = d.SupplierId;
        if (d.PackageWeightKg is { } w && (w <= 0 || w > 1000 || decimal.Round(w, 3) != w))
        {
            throw new BusinessRuleException("stock.batch.package_weight", "Вес места — положительное число до 1000 кг, не больше 3 знаков после запятой.");
        }

        PackageWeightKg = d.PackageWeightKg;
        Comment = DomainText.Optional(d.Comment, CommentMaxLength, "Комментарий");
    }

    private void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Партия {Number} в архиве.");
        }
    }
}

public sealed record StockBatchData(string? Number, string? Color, string? DyeLot, long? SupplierId, decimal? PackageWeightKg, string? Comment);

/// <summary>
/// Разбивка строки складского документа по партиям (D89): сколько из количества строки приходится на партию, сколько мест
/// и какой вес брутто. Правится только в черновике. Сумма разбивки по позиции при проведении равна количеству строки;
/// позиция без разбивки при расходе списывается сначала «без партии», затем с самых старых партий.
/// </summary>
public sealed class StockDocumentBatchLine
{
    private StockDocumentBatchLine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }
    public long BatchId { get; private set; }
    public decimal Quantity { get; private set; }

    /// <summary>Мест (бобин, мотков, коробок) — для пересчёта и сверки с накладной.</summary>
    public int? Packages { get; private set; }

    /// <summary>Вес брутто, кг (с тарой: шпули, коробки). Тара = брутто − нетто.</summary>
    public decimal? GrossWeightKg { get; private set; }

    public static StockDocumentBatchLine Create(
        long organizationId, long documentId, long itemId, long batchId, decimal quantity, int? packages, decimal? grossWeightKg)
    {
        var line = new StockDocumentBatchLine { OrganizationId = organizationId, DocumentId = documentId, ItemId = itemId, BatchId = batchId };
        line.Set(quantity, packages, grossWeightKg);
        return line;
    }

    public void Set(decimal quantity, int? packages, decimal? grossWeightKg)
    {
        Quantities.EnsurePositive(quantity);
        if (packages is <= 0 or > 1_000_000)
        {
            throw new BusinessRuleException("stock.batch.packages", "Мест — целое положительное число.");
        }

        if (grossWeightKg is { } g && (g <= 0 || decimal.Round(g, 3) != g))
        {
            throw new BusinessRuleException("stock.batch.gross_weight", "Вес брутто — положительное число, не больше 3 знаков после запятой.");
        }

        Quantity = quantity;
        Packages = packages;
        GrossWeightKg = grossWeightKg;
    }
}

/// <summary>Разбивка строки начальных остатков по партиям (D100): сумма по позиции равна количеству строки.</summary>
public sealed class OpeningBalanceBatchLine
{
    private OpeningBalanceBatchLine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }
    public long BatchId { get; private set; }
    public decimal Quantity { get; private set; }

    public static OpeningBalanceBatchLine Create(long organizationId, long documentId, long itemId, long batchId, decimal quantity)
    {
        var line = new OpeningBalanceBatchLine { OrganizationId = organizationId, DocumentId = documentId, ItemId = itemId, BatchId = batchId };
        line.SetQuantity(quantity);
        return line;
    }

    public void SetQuantity(decimal quantity)
    {
        Quantities.EnsurePositive(quantity);
        Quantity = quantity;
    }
}

/// <summary>
/// Факт по партии в инвентаризации (D100). Если у позиции указан факт хотя бы по одной партии, позиция считается пересчитанной
/// по партиям полностью: партия с учётным остатком без факта — недостача до нуля, остаток сверх суммы партий — «без партии».
/// </summary>
public sealed class InventoryBatchCount
{
    private InventoryBatchCount()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DocumentId { get; private set; }
    public long ItemId { get; private set; }
    public long BatchId { get; private set; }
    public decimal CountedQuantity { get; private set; }

    public static InventoryBatchCount Create(long organizationId, long documentId, long itemId, long batchId, decimal counted)
    {
        var line = new InventoryBatchCount { OrganizationId = organizationId, DocumentId = documentId, ItemId = itemId, BatchId = batchId };
        line.SetCounted(counted);
        return line;
    }

    public void SetCounted(decimal counted)
    {
        if (counted < 0)
        {
            throw new BusinessRuleException("stock.quantity.negative", "Количество не может быть отрицательным.");
        }

        CountedQuantity = counted;
    }
}

/// <summary>
/// Движение партийного регистра (D89): приход (+) или расход (−) партии на складе. Ведётся рядом с общим регистром
/// <see cref="StockMovement"/> теми же документами и только на вставку (триггер). Остаток партии = сумма её движений;
/// «без партии» = общий остаток − сумма остатков партий.
/// </summary>
public sealed class StockBatchMovement
{
    private StockBatchMovement()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long WarehouseId { get; private set; }
    public long ItemId { get; private set; }
    public long BatchId { get; private set; }
    public decimal Quantity { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public StockSource Source { get; private set; }
    public long SourceId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static StockBatchMovement Create(
        long organizationId, long warehouseId, long itemId, long batchId, decimal quantity, DateOnly occurredOn, StockSource source, long sourceId,
        DateTime nowUtc)
    {
        if (quantity == 0)
        {
            throw new BusinessRuleException("stock.quantity.zero", "Нулевое движение не записывается.");
        }

        return new StockBatchMovement
        {
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            ItemId = itemId,
            BatchId = batchId,
            Quantity = quantity,
            OccurredOn = occurredOn,
            Source = source,
            SourceId = sourceId,
            CreatedAtUtc = nowUtc,
        };
    }
}

/// <summary>
/// Пересчёт количества в вес и обратно (D89). Единицы веса узнаются по коду ОКЕИ: 166 — кг, 163 — г, 168 — т.
/// Штучная позиция пересчитывается через вес единицы из карточки номенклатуры; весовая (пряжа в кг) в места — через вес места партии.
/// </summary>
public static class WeightConversion
{
    public const string Kilogram = "166";
    public const string Gram = "163";
    public const string Tonne = "168";

    /// <summary>Сколько килограммов в одной единице учёта: для весовых единиц — по определению, для прочих — вес единицы из карточки.</summary>
    public static decimal? KgPerUnit(string unitCode, decimal? itemWeightKg) => unitCode switch
    {
        Kilogram => 1m,
        Gram => 0.001m,
        Tonne => 1000m,
        _ => itemWeightKg is > 0 ? itemWeightKg : null,
    };

    public static bool IsWeightUnit(string unitCode) => unitCode is Kilogram or Gram or Tonne;

    /// <summary>Количество в единицах учёта → кг; null — пересчёт невозможен (не указан вес единицы).</summary>
    public static decimal? ToKg(decimal quantity, string unitCode, decimal? itemWeightKg) =>
        KgPerUnit(unitCode, itemWeightKg) is { } k ? decimal.Round(quantity * k, 3) : null;

    /// <summary>Кг → единицы учёта с точностью единицы; null — пересчёт невозможен.</summary>
    public static decimal? FromKg(decimal kg, string unitCode, decimal? itemWeightKg, byte precision) =>
        KgPerUnit(unitCode, itemWeightKg) is { } k ? decimal.Round(kg / k, precision, MidpointRounding.AwayFromZero) : null;

    /// <summary>Сколько мест (бобин) в весе; дробная часть — неполное место.</summary>
    public static decimal? Packages(decimal kg, decimal? packageWeightKg) =>
        packageWeightKg is > 0 ? decimal.Round(kg / packageWeightKg.Value, 1, MidpointRounding.AwayFromZero) : null;
}
