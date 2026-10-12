using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Технологическая операция — справочник технолога (D715): название, единица, расценка, норма времени, этап производства (1–5:
/// вязание, сборка, ВТО, контроль качества, упаковка — те же коды, что у производства и сдельных расценок «Сотрудников»).
/// Расценка видна только с правом себестоимости разработки. Не удаляется — архив.
/// </summary>
public sealed class DevOperationType
{
    public const int NameMaxLength = 100;
    public const int UnitMaxLength = 10;

    private DevOperationType()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Unit { get; private set; } = "шт";
    public decimal? Price { get; private set; }
    public decimal? NormMinutes { get; private set; }

    /// <summary>Этап производства 1–5 (коды D401) или null — операция вне маршрута производства.</summary>
    public byte? ProductionStage { get; private set; }

    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevOperationType Create(long organizationId, DevOperationValues values)
    {
        var o = new DevOperationType { OrganizationId = organizationId };
        o.Set(values);
        return o;
    }

    public void Set(DevOperationValues v)
    {
        if (v.Price is < 0 or > 1_000_000m)
        {
            throw new BusinessRuleException("dev.operation.price", "Расценка — от 0 до 1 000 000.");
        }

        if (v.NormMinutes is <= 0 or > 10_000m)
        {
            throw new BusinessRuleException("dev.operation.norm", "Норма — больше 0 и не больше 10 000 минут.");
        }

        if (v.ProductionStage is < 1 or > 5)
        {
            throw new BusinessRuleException("dev.operation.stage", "Этап производства — от 1 до 5.");
        }

        Name = DomainText.Require(v.Name, NameMaxLength, "Операция");
        Unit = DomainText.Optional(v.Unit, UnitMaxLength, "Единица") ?? "шт";
        Price = v.Price is { } p ? decimal.Round(p, 4) : null;
        NormMinutes = v.NormMinutes is { } n ? decimal.Round(n, 4) : null;
        ProductionStage = v.ProductionStage;
    }

    public void SetArchived(bool archived) => IsArchived = archived;
}

public sealed record DevOperationValues(string? Name, string? Unit, decimal? Price, decimal? NormMinutes, byte? ProductionStage);

/// <summary>
/// Операция в технологии модели (D715): порядок, операция из справочника, пояснение («на второй день парить 2 секунды»), своя норма
/// и расценка, если отличаются от справочника.
/// </summary>
public sealed class DevModelOperation
{
    public const int NoteMaxLength = 300;
    public const int MaxPerModel = 60;

    private DevModelOperation()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public int Seq { get; private set; }
    public long OperationTypeId { get; private set; }
    public string? Note { get; private set; }
    public decimal? NormMinutes { get; private set; }
    public decimal? Price { get; private set; }

    public static DevModelOperation Create(long organizationId, long modelId, int seq, long operationTypeId, string? note, decimal? norm, decimal? price)
    {
        var o = new DevModelOperation { OrganizationId = organizationId, ModelId = modelId, Seq = seq, OperationTypeId = operationTypeId };
        o.Set(note, norm, price);
        return o;
    }

    public void Set(string? note, decimal? norm, decimal? price)
    {
        if (norm is <= 0 or > 10_000m)
        {
            throw new BusinessRuleException("dev.operation.norm", "Норма — больше 0 и не больше 10 000 минут.");
        }

        if (price is < 0 or > 1_000_000m)
        {
            throw new BusinessRuleException("dev.operation.price", "Расценка — от 0 до 1 000 000.");
        }

        Note = DomainText.Optional(note, NoteMaxLength, "Пояснение");
        NormMinutes = norm is { } n ? decimal.Round(n, 4) : null;
        Price = price is { } p ? decimal.Round(p, 4) : null;
    }

    public void Move(int seq) => Seq = seq;

    /// <summary>Итог технологии изделия: минуты и стоимость работы (своя норма и расценка важнее справочника).</summary>
    public static (decimal Minutes, decimal Cost) Totals(IEnumerable<(decimal? Norm, decimal? Price, decimal? TypeNorm, decimal? TypePrice)> rows) =>
        rows.Aggregate((Minutes: 0m, Cost: 0m), (acc, r) => (acc.Minutes + (r.Norm ?? r.TypeNorm ?? 0m), acc.Cost + (r.Price ?? r.TypePrice ?? 0m)));
}

/// <summary>Показатели заполнения модели (D716): какие разделы техдокументации готовы — по факту данных, а не по отметкам.</summary>
public sealed record DevCompleteness(IReadOnlyList<(string Section, bool Done)> Sections)
{
    public int Done => Sections.Count(s => s.Done);
    public int Total => Sections.Count;

    /// <summary>Готово разделов в процентах — «сколько разделов из списка заполнено».</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Done * 100m / Total, MidpointRounding.AwayFromZero);

    public const int AllSections = (1 << 10) - 1;

    /// <summary>Только разделы, которые организация включила в показатель (D719): бит i — раздел SectionNames[i].</summary>
    public DevCompleteness Only(int sections) => new(Sections.Where((_, i) => (sections & (1 << i)) != 0).ToList());

    public static readonly IReadOnlyList<string> SectionNames =
        ["Задание", "Эскиз", "Палитра цветов", "Детали", "Заправка", "Программы", "Размеры деталей", "Табель мер", "Операции", "Образец"];

    public static DevCompleteness From(bool brief, bool sketch, bool palette, bool details, bool threading, bool programs, bool detailSizes, bool measures,
        bool operations, bool sample) => new(
    [
        ("Задание", brief), ("Эскиз", sketch), ("Палитра цветов", palette), ("Детали", details), ("Заправка", threading), ("Программы", programs),
        ("Размеры деталей", detailSizes), ("Табель мер", measures), ("Операции", operations), ("Образец", sample),
    ]);
}
