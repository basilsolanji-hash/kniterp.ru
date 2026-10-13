using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>
/// Фото к сделке (D692): снимок с камеры телефона у клиента — образцы, витрина, упаковка, замеры. JPEG, PNG или WebP до 5 МБ, тип —
/// по сигнатуре файла (заголовку браузера не верим), не больше 30 действующих фото на сделку. Хранится в базе, как фото позиций
/// каталога (<see cref="ItemPhoto"/>). Удаление — архивом: снимок остаётся в базе, в сделке не показывается.
/// </summary>
public sealed class CrmDealPhoto
{
    // Допущение D692: хранится в базе без шифрования, как фото каталога: снимки образцов и товара — не персональные данные.
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxPerDeal = 30;
    public const int CaptionMaxLength = 200;

    private CrmDealPhoto()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DealId { get; private set; }
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content { get; private set; } = [];
    public int SizeBytes { get; private set; }
    public string? Caption { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }
    public long? ArchivedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmDealPhoto Create(long organizationId, long dealId, byte[] content, string? caption, long userId, DateTime nowUtc)
    {
        var type = Validate(content);
        return new CrmDealPhoto
        {
            OrganizationId = organizationId,
            DealId = dealId,
            Content = content,
            SizeBytes = content.Length,
            ContentType = type,
            Caption = DomainText.Optional(caption, CaptionMaxLength, "Подпись"),
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Проверка файла до записи: не пустой, не больше 5 МБ, сигнатура JPEG, PNG или WebP. Возвращает тип содержимого.</summary>
    public static string Validate(byte[] content)
    {
        if (content.Length == 0)
        {
            throw new BusinessRuleException("crm.photo.empty", "Файл пустой.");
        }

        if (content.Length > MaxBytes)
        {
            throw new BusinessRuleException("crm.photo.too_large", "Фото — не больше 5 МБ. Уменьшите размер снимка.");
        }

        return Detect(content) ?? throw new BusinessRuleException("crm.photo.type", "Подходят только фото JPEG, PNG или WebP.");
    }

    /// <summary>Тип по первым байтам файла (как у фото каталога); null — не разрешённое изображение.</summary>
    public static string? Detect(ReadOnlySpan<byte> content) => ItemPhoto.Detect(content);

    public void SetCaption(string? caption)
    {
        EnsureActive();
        Caption = DomainText.Optional(caption, CaptionMaxLength, "Подпись");
    }

    public void Archive(long userId, DateTime nowUtc)
    {
        EnsureActive();
        IsArchived = true;
        ArchivedAtUtc = nowUtc;
        ArchivedByUserId = userId;
    }

    private void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("crm.photo.archived", "Фото уже удалено из сделки.");
        }
    }
}
