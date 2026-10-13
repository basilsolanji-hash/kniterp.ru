namespace KnitErp.Web.Components.Pages;

/// <summary>
/// ui-review §0 п. 7: возврат к списку — с теми же фильтрами. Фильтры списков разработки живут, пока открыта вкладка
/// (scoped на соединение Blazor); карточка ведёт «назад» на тот же адрес, список восстанавливает фильтры отсюда.
/// </summary>
public sealed class DevListState
{
    public DevModelsListFilters? Models { get; set; }

    public DevPatternsListFilters? Patterns { get; set; }
}

public sealed record DevModelsListFilters(string Search, string Collection, string Stage, string Assignee, string Customer, bool Overdue, bool Table, int Page);

public sealed record DevPatternsListFilters(string? Search, string Kind, int? Colors, int? Rapport, string MachineClass, bool Archived);
