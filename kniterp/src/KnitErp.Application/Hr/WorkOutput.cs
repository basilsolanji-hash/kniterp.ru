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
