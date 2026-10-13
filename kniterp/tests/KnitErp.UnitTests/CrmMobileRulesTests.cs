using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;

namespace KnitErp.UnitTests;

/// <summary>Фото к сделке (D692): тип — только по сигнатуре файла, размер до 5 МБ, подпись, архив.</summary>
public sealed class CrmMobileRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, null)] // RIFF, но WAVE — не фото
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, null)] // PDF
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E }, null)] // <html>
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, null)] // GIF — не входит в разрешённые
    [InlineData(new byte[] { 0xFF, 0xD8 }, null)] // обрезанный JPEG
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)] // обрезанный PNG
    public void Photo_type_is_detected_by_signature(byte[] content, string? expected) => Assert.Equal(expected, CrmDealPhoto.Detect(content));

    [Fact]
    public void Photo_is_validated_before_storing()
    {
        Assert.Equal("crm.photo.empty", Assert.Throws<BusinessRuleException>(() => CrmDealPhoto.Validate([])).Code);
        var html = "<!DOCTYPE html><script>alert(1)</script>"u8.ToArray();
        Assert.Equal("crm.photo.type", Assert.Throws<BusinessRuleException>(() => CrmDealPhoto.Create(1, 2, html, null, 3, Now)).Code);
        var big = new byte[CrmDealPhoto.MaxBytes + 1];
        big[0] = 0xFF;
        big[1] = 0xD8;
        big[2] = 0xFF;
        Assert.Equal("crm.photo.too_large", Assert.Throws<BusinessRuleException>(() => CrmDealPhoto.Validate(big)).Code);
        Assert.Equal("image/jpeg", CrmDealPhoto.Validate(big[..CrmDealPhoto.MaxBytes]));
    }

    [Fact]
    public void Photo_caption_and_archive()
    {
        var photo = CrmDealPhoto.Create(1, 2, [0xFF, 0xD8, 0xFF, 0xDB], "  Витрина клиента  ", 3, Now);
        Assert.Equal(("image/jpeg", 4, "Витрина клиента", false), (photo.ContentType, photo.SizeBytes, photo.Caption, photo.IsArchived));
        photo.SetCaption("  ");
        Assert.Null(photo.Caption);
        Assert.Throws<BusinessRuleException>(() => photo.SetCaption(new string('я', CrmDealPhoto.CaptionMaxLength + 1)));
        photo.Archive(5, Now);
        Assert.Equal((true, Now, 5L), (photo.IsArchived, photo.ArchivedAtUtc!.Value, photo.ArchivedByUserId!.Value));
        Assert.Equal("crm.photo.archived", Assert.Throws<BusinessRuleException>(() => photo.Archive(5, Now)).Code);
        Assert.Equal("crm.photo.archived", Assert.Throws<BusinessRuleException>(() => photo.SetCaption("Новая")).Code);
    }
}
