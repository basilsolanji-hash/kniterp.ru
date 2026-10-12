using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Вид справочника разработки (D709). Числа хранятся в базе — не перенумеровывать.</summary>
public enum DevLookupKind : byte
{
    /// <summary>Коллекция или сезон: «Осень-зима 2026», «Школа».</summary>
    Collection = 1,

    /// <summary>Переплетение: гладь, ластик 1×1, жаккард… (этап 3).</summary>
    Weave = 2,

    /// <summary>Класс машины; число — нитеводителей (этапы 2–3).</summary>
    MachineClass = 3,

    /// <summary>Причина доработки образца (этап 3).</summary>
    ReworkReason = 4,

    /// <summary>Точка измерения табеля мер (этап 4).</summary>
    MeasurePoint = 5,
}

/// <summary>
/// Запись справочника разработки (D709): коллекции, переплетения, классы машин, причины доработок, точки измерения.
/// Не удаляется — уходит в архив; меняет пользователь с правом «Разработка: настройки».
/// </summary>
public sealed class DevLookup
{
    public const int NameMaxLength = 100;
    public const int MaxNumber = 1000;

    private DevLookup()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public DevLookupKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>Число справочника: у класса машины — число нитеводителей.</summary>
    public int? Number { get; private set; }

    public int SortOrder { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static string KindName(DevLookupKind kind) => kind switch
    {
        DevLookupKind.Collection => "Коллекции и сезоны",
        DevLookupKind.Weave => "Переплетения",
        DevLookupKind.MachineClass => "Классы машин",
        DevLookupKind.ReworkReason => "Причины доработок",
        _ => "Точки измерения",
    };

    public static DevLookup Create(long organizationId, DevLookupKind kind, string? name, int? number, int sortOrder)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("dev.lookup.kind", "Выберите справочник.");
        }

        var x = new DevLookup { OrganizationId = organizationId, Kind = kind, SortOrder = sortOrder };
        x.Rename(name, number);
        return x;
    }

    public void Rename(string? name, int? number)
    {
        if (number is < 0 or > MaxNumber)
        {
            throw new BusinessRuleException("dev.lookup.number", $"Число — от 0 до {MaxNumber}.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название");
        Number = number;
    }

    public void SetArchived(bool archived) => IsArchived = archived;
}
