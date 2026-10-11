using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Поставщик позиции (D309): у кого закупаем, его артикул и наименование (как в его счетах и прайсе), основной ли он. Последняя цена
/// закупки не хранится, а берётся из заказов поставщику — так она не расходится с документами. Пара (позиция, поставщик) уникальна.
/// Не у комплектов: закупаются компоненты.
/// </summary>
public sealed class ItemSupplier
{
    public const int ArticleMaxLength = 64;
    public const int NameMaxLength = 300;
    public const int MaxPerItem = 20;

    private ItemSupplier()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ItemId { get; private set; }
    public long SupplierId { get; private set; }
    public string? SupplierArticle { get; private set; }
    public string? SupplierItemName { get; private set; }
    public bool IsMain { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static ItemSupplier Create(long organizationId, Item item, long supplierId, string? article, string? name, bool isMain)
    {
        if (item.Type == ItemType.Bundle)
        {
            throw new BusinessRuleException("catalog.supplier.bundle", "У комплекта нет поставщиков — их задают у компонентов.");
        }

        var s = new ItemSupplier { OrganizationId = organizationId, ItemId = item.Id, SupplierId = supplierId };
        s.Set(article, name, isMain);
        return s;
    }

    public void Set(string? article, string? name, bool isMain)
    {
        SupplierArticle = DomainText.Optional(article, ArticleMaxLength, "Артикул поставщика");
        SupplierItemName = DomainText.Optional(name, NameMaxLength, "Наименование у поставщика");
        IsMain = isMain;
    }

    public void SetMain(bool isMain) => IsMain = isMain;

    /// <summary>Проверка длины артикула и наименования — до любых изменений (сервис меняет основного поставщика раньше записи строки).</summary>
    public static void Validate(string? article, string? name)
    {
        DomainText.Optional(article, ArticleMaxLength, "Артикул поставщика");
        DomainText.Optional(name, NameMaxLength, "Наименование у поставщика");
    }
}
