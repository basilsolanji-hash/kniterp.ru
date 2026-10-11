using KnitErp.Domain.Common;

namespace KnitErp.Domain.Tagging;

/// <summary>Цвета тегов — как метки Finder в macOS: без цвета и семь цветов в том же порядке.</summary>
public enum TagColor : byte
{
    None = 0,
    Red = 1,
    Orange = 2,
    Yellow = 3,
    Green = 4,
    Blue = 5,
    Purple = 6,
    Gray = 7,
}

/// <summary>Объекты, которые можно отметить тегом (D103) и обсуждать (D107). Значение хранится в базе — не перенумеровывать.</summary>
public enum TaggedEntity : byte
{
    Item = 1,
    Counterparty = 2,
    SalesOrder = 3,
    PurchaseOrder = 4,
    StockDocument = 5,
    StockBatch = 6,
    InventoryCount = 7,
    OpeningBalance = 8,
    CustomerInvoice = 9,
    ExpenseReport = 10,

    /// <summary>Производственный заказ (D99).</summary>
    ProductionOrder = 11,
}

public static class TagColors
{
    public static readonly IReadOnlyList<TagColor> All =
        [TagColor.Red, TagColor.Orange, TagColor.Yellow, TagColor.Green, TagColor.Blue, TagColor.Purple, TagColor.Gray, TagColor.None];

    public static string Name(TagColor color) => color switch
    {
        TagColor.Red => "Красный",
        TagColor.Orange => "Оранжевый",
        TagColor.Yellow => "Жёлтый",
        TagColor.Green => "Зелёный",
        TagColor.Blue => "Синий",
        TagColor.Purple => "Фиолетовый",
        TagColor.Gray => "Серый",
        _ => "Без цвета",
    };

    /// <summary>Цвет точки — системная палитра Apple (светлая тема).</summary>
    public static string Css(TagColor color) => color switch
    {
        TagColor.Red => "#ff3b30",
        TagColor.Orange => "#ff9500",
        TagColor.Yellow => "#ffcc00",
        TagColor.Green => "#34c759",
        TagColor.Blue => "#007aff",
        TagColor.Purple => "#af52de",
        TagColor.Gray => "#8e8e93",
        _ => "transparent",
    };

    public static string EntityName(TaggedEntity entity) => entity switch
    {
        TaggedEntity.Item => "Номенклатура",
        TaggedEntity.Counterparty => "Контрагенты",
        TaggedEntity.SalesOrder => "Заказы покупателей",
        TaggedEntity.PurchaseOrder => "Заказы поставщикам",
        TaggedEntity.StockDocument => "Складские документы",
        TaggedEntity.StockBatch => "Партии",
        TaggedEntity.InventoryCount => "Инвентаризации",
        TaggedEntity.OpeningBalance => "Начальные остатки",
        TaggedEntity.CustomerInvoice => "Счета покупателям",
        TaggedEntity.ExpenseReport => "Авансовые отчёты",
        TaggedEntity.ProductionOrder => "Производственные заказы",
        _ => entity.ToString(),
    };
}

/// <summary>
/// Тег организации (D103): название и цвет, как метки Finder. Теги общие для всех сотрудников организации — по ним коллеги
/// находят одно и то же («Срочно», «Новый год», «Брак»). Избранные показываются в боковом меню. Не удаляется — архивируется,
/// отметки архивного тега скрываются.
/// </summary>
public sealed class Tag
{
    public const int NameMaxLength = 40;

    private Tag()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public TagColor Color { get; private set; }
    public bool IsFavorite { get; private set; }
    public bool IsArchived { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Tag Create(long organizationId, string? name, TagColor color, long userId, DateTime nowUtc)
    {
        var tag = new Tag { OrganizationId = organizationId, CreatedByUserId = userId, CreatedAtUtc = nowUtc, IsFavorite = true };
        tag.Set(name, color);
        return tag;
    }

    public void Update(string? name, TagColor color, bool favorite)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Тег «{Name}» в архиве.");
        }

        Set(name, color);
        IsFavorite = favorite;
    }

    public void Archive()
    {
        IsArchived = true;
        IsFavorite = false;
    }

    public void Restore() => IsArchived = false;

    /// <summary>Название без «#» в начале и лишних пробелов: «#Срочно » → «Срочно».</summary>
    public static string Normalize(string? name) =>
        string.Join(' ', (name ?? "").Trim().TrimStart('#').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void Set(string? name, TagColor color)
    {
        var text = Normalize(name);
        if (text.Length == 0)
        {
            throw new BusinessRuleException("tag.name.required", "Укажите название тега.");
        }

        if (text.Length > NameMaxLength)
        {
            throw new BusinessRuleException("tag.name.too_long", $"Название тега — не длиннее {NameMaxLength} символов.");
        }

        if (!Enum.IsDefined(color))
        {
            throw new BusinessRuleException("tag.color", "Неизвестный цвет тега.");
        }

        Name = text;
        Color = color;
    }
}

/// <summary>Отметка объекта тегом. Снимается удалением строки — это не учётная запись; кто и когда поставил — в журнале аудита.</summary>
public sealed class TagLink
{
    private TagLink()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long TagId { get; private set; }
    public TaggedEntity Entity { get; private set; }
    public long EntityId { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TagLink Create(long organizationId, long tagId, TaggedEntity entity, long entityId, long userId, DateTime nowUtc) =>
        Enum.IsDefined(entity)
            ? new TagLink { OrganizationId = organizationId, TagId = tagId, Entity = entity, EntityId = entityId, CreatedByUserId = userId, CreatedAtUtc = nowUtc }
            : throw new BusinessRuleException("tag.entity", "Этот объект нельзя отметить тегом.");
}
