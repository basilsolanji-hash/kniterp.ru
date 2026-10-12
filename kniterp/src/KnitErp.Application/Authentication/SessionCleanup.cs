using KnitErp.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

/// <summary>
/// D126: записи сессий (D122) не копятся бесконечно. Сессия, завершённая или не использованная дольше <see cref="Retention"/>,
/// войти уже не даёт (простой больше 12 ч), а хранит устройство и сеть сотрудника — персональные данные без цели хранения (152-ФЗ, ст. 5 ч. 7).
/// Завершения остаются в журнале аудита. Удаляется пачками, чтобы не держать долгую блокировку таблицы входа.
/// </summary>
public sealed class SessionCleanup(IKnitErpDbContext db, IClock clock)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int BatchSize = 5_000;

    /// <returns>Сколько записей удалено.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var before = clock.UtcNow - Retention;
        var total = 0;
        while (true)
        {
            var ids = await db.UserSessions.AsNoTracking()
                .Where(s => s.LastSeenAtUtc < before || (s.RevokedAtUtc != null && s.RevokedAtUtc < before))
                .OrderBy(s => s.Id).Select(s => s.Id).Take(BatchSize).ToListAsync(ct);
            if (ids.Count == 0)
            {
                return total;
            }

            total += await db.UserSessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize)
            {
                return total;
            }
        }
    }
}
