using Microsoft.Extensions.Caching.Memory;

namespace KnitErp.Web.Exchange;

/// <summary>
/// Кэш файлов фида каталога (D314): свой экземпляр с ограничением объёма 256 МБ (фид на 50 000 позиций ~16 МБ), чтобы большие
/// файлы не вытесняли и не ломали общий кэш приложения. Файл живёт 5 минут.
/// </summary>
public sealed class FeedCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256L * 1024 * 1024 });

    public bool TryGet(string key, out byte[]? xml) => _cache.TryGetValue(key, out xml);

    public void Set(string key, byte[] xml) =>
        _cache.Set(key, xml, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = xml.Length });

    public void Dispose() => _cache.Dispose();
}
