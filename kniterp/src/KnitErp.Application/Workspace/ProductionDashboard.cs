using KnitErp.Application.Access;
using KnitErp.Application.Common;

namespace KnitErp.Application.Workspace;

/// <summary>Производственный заказ на главном экране: готовность в процентах, срок, просрочка (D111).</summary>
public sealed record ProductionOrderProgressDto(long Id, string Number, string Product, decimal Percent, DateOnly? DueDate, bool Late, string Href,
    string? Operation = null);

/// <summary>Что требует внимания в производстве: брак, нехватка пряжи, простой машины, просрочка этапа.</summary>
public sealed record ProductionAlertDto(string Text, string Href, bool Critical, int Count = 1);

/// <summary>Данные модуля производства для главного экрана. Права проверяет сам модуль по <see cref="AccessContext"/>.</summary>
public sealed record ProductionDashboardData(
    int OrdersInWork, int OrdersLate, IReadOnlyList<ProductionOrderProgressDto> Orders, decimal? OutputToday, string? OutputUnit,
    IReadOnlyList<ProductionAlertDto> Alerts);

/// <summary>Смена сегодня: сколько людей вышло из запланированных (D111, данные модуля «Сотрудники»).</summary>
public sealed record ShiftStaffingDto(int OnShift, int Planned, int Absent, string Href, IReadOnlyList<string>? AbsentNames = null);

/// <summary>
/// Источник производственных показателей главного экрана (D111). Реализует модуль производства; не зарегистрирован — блок показывает
/// только воронку по этапам заказов покупателей. Вызывается с контекстом базы главного экрана, без параллельных запросов;
/// null — у пользователя нет прав на производство.
/// </summary>
public interface IProductionDashboardSource
{
    Task<ProductionDashboardData?> GetAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct);
}

/// <summary>Источник «смена сегодня» (D111). Реализует модуль «Сотрудники»; null — нет прав или табель не ведётся.</summary>
public interface IShiftStaffingSource
{
    Task<ShiftStaffingDto?> TodayAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct);
}

/// <summary>Шаг производственной воронки по этапам заказов покупателей: этап, число заказов, сумма, из них просрочено.</summary>
public sealed record ProductionFunnelStepDto(long StageId, string Stage, string Color, int Orders, decimal? Amount, int Overdue);

/// <summary>
/// Блок «Производство» главного экрана (D111): воронка заказов покупателей по этапам «очередь → в работе → собрано» (правила D110),
/// готовность производственных заказов, выпуск за день, требующее внимания и смена — из модулей производства и сотрудников.
/// </summary>
public sealed record DashboardProductionDto(
    IReadOnlyList<ProductionFunnelStepDto> Funnel, ProductionDashboardData? Production, ShiftStaffingDto? Shift)
{
    public int OverdueTotal => Funnel.Sum(f => f.Overdue) + (Production?.OrdersLate ?? 0);
}
