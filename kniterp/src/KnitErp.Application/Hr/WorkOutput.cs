using KnitErp.Domain.Hr;

namespace KnitErp.Application.Hr;

/// <summary>Выработка сотрудника за день по операции производства (ТЗ «Сотрудники», §6).</summary>
public sealed record WorkOutputDto(long EmployeeId, DateOnly Date, long OperationId, string OperationName, long? ItemId, decimal Quantity, string Unit);

/// <summary>
/// Граница с производством (ТЗ «Сотрудники», §6): производство публикует выработку, модуль «Сотрудники» её только читает.
/// Реализацию пишет модуль производства у себя и регистрирует в DI — она заменит заглушку.
/// </summary>
public interface IWorkOutputSource
{
    Task<IReadOnlyList<WorkOutputDto>> OutputAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Заглушка до слияния с производством: выработки нет, сдельная часть расчёта — ноль.</summary>
public sealed class EmptyWorkOutputSource : IWorkOutputSource
{
    public Task<IReadOnlyList<WorkOutputDto>> OutputAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkOutputDto>>([]);
}

/// <summary>
/// Отпечаток выработки за период (D217): SHA-256 по строкам, упорядоченным независимо от порядка источника. Изменилась любая строка —
/// изменился отпечаток. Хранится в расчёте; производство ничего дополнительно не отдаёт.
/// </summary>
public static class WorkOutputFingerprint
{
    public static byte[] Of(IEnumerable<WorkOutputDto> rows)
    {
        var lines = rows.Select(r => string.Join('|', r.EmployeeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                r.OperationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.ItemId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                PieceRate.NormalizeUnit(r.Unit),
                r.Quantity.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))
            .Order(StringComparer.Ordinal);
        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }
}
