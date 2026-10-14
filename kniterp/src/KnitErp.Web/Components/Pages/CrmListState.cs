namespace KnitErp.Web.Components.Pages;

/// <summary>
/// D653 (ТЗ интерфейса, раздел 0 п. 7): возврат из карточки к списку CRM — с теми же фильтрами, видом и страницей. Живёт, пока открыта
/// вкладка (scoped на соединение Blazor), как <see cref="DevListState"/>; между входами не запоминается — это общий механизм сессии
/// интерфейса. Только отображение: права и область видимости по-прежнему проверяют сервисы.
/// </summary>
public sealed class CrmListState
{
    public CrmDealsListFilters? Deals { get; set; }

    public CrmLeadsListFilters? Leads { get; set; }

    public CrmContactsListFilters? Contacts { get; set; }
}

public sealed record CrmDealsListFilters(string View, string Pipeline, long? TagId, string Responsible, string Source, DateTime? From, DateTime? To,
    string? Search, bool Mine, int Page);

public sealed record CrmLeadsListFilters(string Status, string Source, long? TagId, string? Search, bool Mine, int Page);

public sealed record CrmContactsListFilters(string? Search, bool Archived, int Page);
