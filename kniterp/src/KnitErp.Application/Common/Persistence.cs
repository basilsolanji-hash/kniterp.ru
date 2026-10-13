using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Common;

public static class Persistence
{
    /// <summary>Версия строки из формы должна совпасть с текущей — иначе данные изменил кто-то другой.</summary>
    public static T EnsureVersion<T>(this T entity, byte[] actual, byte[] expected) =>
        actual.AsSpan().SequenceEqual(expected) ? entity : throw new ConcurrencyConflictException();

    /// <summary>
    /// Сохранение, где одновременная правка возвращается пользователю как конфликт, а не теряется. D129 (аудит 12.10.2026, п. 5):
    /// при конфликте отслеживаемые сущности сбрасываются — иначе долгоживущий контекст вкладки Blazor держит устаревший экземпляр
    /// со старой версией строки, и повтор с новой версией из формы снова получает «конфликт».
    /// </summary>
    public static async Task SaveOrConflictAsync(this IKnitErpDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
    }
}
