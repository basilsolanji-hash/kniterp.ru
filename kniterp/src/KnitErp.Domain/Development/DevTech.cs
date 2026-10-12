using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Цвет палитры модели (D712): дизайнер выбирает, какой пряжей из номенклатуры будет «Основной», «доп.1», «доп.2».
/// Дессинатор в заправке выбирает только обозначение — пряжа и цвет подставляются из палитры; цех видит одно и то же.
/// </summary>
public sealed class DevModelColor
{
    public const int LabelMaxLength = 40;
    public const int NoteMaxLength = 200;
    public const int MaxPerModel = 12;

    private DevModelColor()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public string Label { get; private set; } = string.Empty;

    /// <summary>Пряжа из номенклатуры (сырьё или материал, можно модификацию цвета).</summary>
    public long? ItemId { get; private set; }

    /// <summary>Цвет для экрана (#RRGGBB) — только для наглядности на техкарте.</summary>
    public string? Swatch { get; private set; }

    public string? Note { get; private set; }
    public int SortOrder { get; private set; }

    public static DevModelColor Create(long organizationId, long modelId, string? label, long? itemId, string? swatch, string? note, int sortOrder)
    {
        var c = new DevModelColor { OrganizationId = organizationId, ModelId = modelId, SortOrder = sortOrder };
        c.Set(label, itemId, swatch, note);
        return c;
    }

    public void Set(string? label, long? itemId, string? swatch, string? note)
    {
        var s = swatch?.Trim();
        if (!string.IsNullOrEmpty(s) && !System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new BusinessRuleException("dev.color.swatch", "Цвет — в виде #RRGGBB.");
        }

        Label = DomainText.Require(label, LabelMaxLength, "Обозначение цвета");
        ItemId = itemId;
        Swatch = string.IsNullOrEmpty(s) ? null : s.ToUpperInvariant();
        Note = DomainText.Optional(note, NoteMaxLength, "Комментарий");
    }
}

/// <summary>Сторона нитеводителя в заправке (как в техкарте: слева и справа от номера).</summary>
public enum DevFeederSide : byte
{
    Left = 1,
    Right = 2,
}

/// <summary>
/// Деталь модели (D711): перед, спинка, рукав, подвяз… У каждой — своё техзадание дессинатора: станок, переплетение, программа,
/// заправка нитеводителей, плановые время вязания и вес, важные комментарии для цеха.
/// </summary>
public sealed class DevDetail
{
    public const int NameMaxLength = 100;
    public const int NotesMaxLength = 2000;
    public const int MaxPerModel = 30;
    public const int MaxQuantity = 20;

    private DevDetail()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Сколько таких деталей в изделии (рукава — 2).</summary>
    public int Quantity { get; private set; } = 1;

    public int SortOrder { get; private set; }
    public long? MachineId { get; private set; }
    public long? WeaveId { get; private set; }

    /// <summary>Программа машины, по которой вяжут деталь (файл модели вида «Программа машины»).</summary>
    public long? ProgramFileId { get; private set; }

    /// <summary>Плановое время вязания одной детали, секунды.</summary>
    public int? PlanKnitSeconds { get; private set; }

    /// <summary>Плановый вес одной детали, граммы.</summary>
    public decimal? PlanWeightGrams { get; private set; }

    /// <summary>Важные комментарии для вязальщицы («если хлопок 20/1 — заправляем в 4 конца»).</summary>
    public string? Notes { get; private set; }

    /// <summary>Номер правки заправки: растёт при каждой замене — меняет версию строки детали (конфликт двух вкладок).</summary>
    public int ThreadingRevision { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static DevDetail Create(long organizationId, long modelId, string? name, int quantity, int sortOrder)
    {
        var d = new DevDetail { OrganizationId = organizationId, ModelId = modelId, SortOrder = sortOrder };
        d.Rename(name, quantity);
        return d;
    }

    public void Rename(string? name, int quantity)
    {
        if (quantity is < 1 or > MaxQuantity)
        {
            throw new BusinessRuleException("dev.detail.quantity", $"Деталей в изделии — от 1 до {MaxQuantity}.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название детали");
        Quantity = quantity;
    }

    /// <summary>Техзадание дессинатора: станок, переплетение, план времени и веса, комментарии.</summary>
    public void SetKnitting(long? machineId, long? weaveId, int? planKnitSeconds, decimal? planWeightGrams, string? notes)
    {
        if (planKnitSeconds is < 1 or > 86_400)
        {
            throw new BusinessRuleException("dev.detail.time", "Время вязания — от 1 секунды до 24 часов.");
        }

        if (planWeightGrams is <= 0 or > 100_000)
        {
            throw new BusinessRuleException("dev.detail.weight", "Вес детали — больше 0 и не больше 100 кг.");
        }

        MachineId = machineId;
        WeaveId = weaveId;
        PlanKnitSeconds = planKnitSeconds;
        PlanWeightGrams = planWeightGrams is { } w ? decimal.Round(w, 2) : null;
        Notes = DomainText.Optional(notes, NotesMaxLength, "Важные комментарии");
    }

    public void SetProgram(long? fileId) => ProgramFileId = fileId;

    public void TouchThreading() => ThreadingRevision++;

    public void Move(int sortOrder) => SortOrder = sortOrder;
}

/// <summary>Одна позиция заправки (D711): нитеводитель, сторона, цвет палитры, число нитей, парафин, режим, эластан.</summary>
public sealed class DevThreading
{
    public const int ModeMaxLength = 10;
    public const int MaxEnds = 20;

    private DevThreading()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DetailId { get; private set; }
    public int Feeder { get; private set; }
    public DevFeederSide Side { get; private set; }

    /// <summary>Цвет палитры модели; пусто — нить без цвета (разделитель).</summary>
    public long? ColorId { get; private set; }

    /// <summary>Подпись, если цвет палитры не выбран: «Разделитель (нейлон)», «отработка в цвет основы».</summary>
    public string? Label { get; private set; }

    /// <summary>Число нитей (концов).</summary>
    public int Ends { get; private set; }

    public bool Paraffin { get; private set; }

    /// <summary>Режим подачи нити на машине («N» и т. п.).</summary>
    public string? Mode { get; private set; }

    /// <summary>Эластан (спандекс) — на техкарте выделяется цветом.</summary>
    public bool Elastic { get; private set; }

    public static DevThreading Create(long organizationId, long detailId, DevThreadingRow row, int feederCount)
    {
        if (row.Feeder < 1 || row.Feeder > feederCount)
        {
            throw new BusinessRuleException("dev.threading.feeder", $"Нитеводитель — от 1 до {feederCount} (столько на станке).");
        }

        if (!Enum.IsDefined(row.Side))
        {
            throw new BusinessRuleException("dev.threading.side", "Укажите сторону нитеводителя.");
        }

        if (row.Ends is < 1 or > MaxEnds)
        {
            throw new BusinessRuleException("dev.threading.ends", $"Число нитей — от 1 до {MaxEnds}.");
        }

        if (row.ColorId is null && string.IsNullOrWhiteSpace(row.Label))
        {
            throw new BusinessRuleException("dev.threading.yarn", "Для каждого нитеводителя выберите цвет палитры или напишите, какая нить.");
        }

        return new DevThreading
        {
            OrganizationId = organizationId,
            DetailId = detailId,
            Feeder = row.Feeder,
            Side = row.Side,
            ColorId = row.ColorId,
            Label = DomainText.Optional(row.Label, DevModelColor.LabelMaxLength + 20, "Нить"),
            Ends = row.Ends,
            Paraffin = row.Paraffin,
            Mode = DomainText.Optional(row.Mode, ModeMaxLength, "Режим"),
            Elastic = row.Elastic,
        };
    }

    /// <summary>Проверка набора заправки: одна позиция на нитеводитель и сторону.</summary>
    public static void EnsureUnique(IEnumerable<DevThreadingRow> rows)
    {
        var dup = rows.GroupBy(r => (r.Feeder, r.Side)).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            throw new BusinessRuleException("dev.threading.duplicate",
                $"Нитеводитель {dup.Key.Feeder} ({(dup.Key.Side == DevFeederSide.Left ? "слева" : "справа")}) указан дважды.");
        }
    }
}

public sealed record DevThreadingRow(int Feeder, DevFeederSide Side, long? ColorId, string? Label, int Ends, bool Paraffin, string? Mode, bool Elastic);
