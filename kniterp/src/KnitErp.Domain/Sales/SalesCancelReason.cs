using KnitErp.Domain.Common;

namespace KnitErp.Domain.Sales;

/// <summary>Причина отмены заказа покупателя (D1137): справочник организации, не удаляется — архив.</summary>
public sealed class SalesCancelReason
{
    public const int NameMaxLength = 150;
    public const int MaxReasons = 100;

    public static readonly IReadOnlyList<string> Defaults =
        ["Покупатель передумал", "Нет товара и нет сроков производства", "Не согласовали цену или условия", "Дубль заказа", "Другое"];

    private SalesCancelReason()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static SalesCancelReason Create(long organizationId, string? name) =>
        new() { OrganizationId = organizationId, Name = DomainText.Require(name, NameMaxLength, "Причина отмены") };

    public void SetArchived(bool archived) => IsArchived = archived;
}
