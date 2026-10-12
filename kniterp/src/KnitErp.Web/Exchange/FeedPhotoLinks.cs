using System.Globalization;
using KnitErp.Application.Catalog;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace KnitErp.Web.Exchange;

/// <summary>
/// D324 (сессия 4): подписанные ссылки на фото в фиде каталога. Токен — Data Protection с ограниченным сроком (30 дней): подделать или
/// подставить чужое фото нельзя, а выключенный фид перестаёт отдавать фото сразу (проверка в CatalogFeedService.PhotoAsync).
/// </summary>
public sealed class FeedPhotoLinks(IDataProtectionProvider provider) : IFeedPhotoLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("knitERP.FeedPhotos.v1").ToTimeLimitedDataProtector();

    public string Token(long feedId, long photoId) =>
        WebEncoders.Base64UrlEncode(protector.Protect(System.Text.Encoding.UTF8.GetBytes(
            feedId.ToString(CultureInfo.InvariantCulture) + ":" + photoId.ToString(CultureInfo.InvariantCulture)), DateTimeOffset.UtcNow.Add(Lifetime)));

    public bool TryRead(string token, out long feedId, out long photoId)
    {
        feedId = photoId = 0;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(protector.Unprotect(WebEncoders.Base64UrlDecode(token), out _));
            var parts = text.Split(':');
            return parts.Length == 2
                   && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out feedId)
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out photoId);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}
