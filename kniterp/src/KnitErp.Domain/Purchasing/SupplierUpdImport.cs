using KnitErp.Domain.Common;

namespace KnitErp.Domain.Purchasing;

/// <summary>
/// D149: загруженный XML УПД поставщика (формат 5.03) — по нему создан черновик поступления по заказу поставщику. Один файл
/// (SHA-256 содержимого) — одна загрузка в организации: повторная загрузка того же файла не создаёт второго поступления.
/// Сам файл не хранится — только реквизиты счёта-фактуры и отпечаток.
/// </summary>
public sealed class SupplierUpdImport
{
    public const int FileNameMaxLength = 255;
    public const int NumberMaxLength = 1000;
    public const int HashLength = 64;

    private SupplierUpdImport()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string FileHash { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string SellerInn { get; private set; } = string.Empty;
    public string InvoiceNumber { get; private set; } = string.Empty;
    public DateOnly InvoiceDate { get; private set; }
    public long PurchaseOrderId { get; private set; }
    public long ReceiptDocumentId { get; private set; }
    public int LineCount { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static SupplierUpdImport Create(long organizationId, string fileHash, string? fileName, string sellerInn, string number, DateOnly date,
        long purchaseOrderId, long receiptDocumentId, int lineCount, long userId, DateTime nowUtc)
    {
        if (fileHash.Length != HashLength)
        {
            throw new BusinessRuleException("purchase.upd.hash", "Неверный отпечаток файла.");
        }

        var name = string.IsNullOrWhiteSpace(fileName) ? "upd.xml" : fileName.Trim();
        return new SupplierUpdImport
        {
            OrganizationId = organizationId, FileHash = fileHash, FileName = name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name,
            SellerInn = sellerInn, InvoiceNumber = number.Length > NumberMaxLength ? number[..NumberMaxLength] : number, InvoiceDate = date,
            PurchaseOrderId = purchaseOrderId, ReceiptDocumentId = receiptDocumentId, LineCount = lineCount, CreatedByUserId = userId, CreatedAtUtc = nowUtc,
        };
    }
}
