using System.Text.Json;

namespace KnitErp.Application.Workspace;

/// <summary>
/// Личная настройка главного экрана: порядок блоков и скрытые блоки. Хранится в личных данных пользователя
/// (<see cref="PersonalToolsService"/>), у каждого пользователя в каждой организации своя.
/// </summary>
public sealed record DashboardLayout(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden, IReadOnlyList<string>? Actions = null)
{
    /// <summary>Быстрые действия в порядке пользователя (D108); не задано — набор по умолчанию.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ActionKeys => Actions ?? DashboardActions.Default;

    public bool Shows(string block) => !Hidden.Contains(block);

    /// <summary>Видимые блоки основной части экрана в порядке пользователя.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> Visible => Order.Where(Shows);
}

/// <summary>Блоки главного экрана. Названия для экрана — в DashboardPage (переводятся там).</summary>
public static class DashboardBlocks
{
    public const string Kpi = "kpi";

    /// <summary>Быстрые действия — настраиваемые кнопки «Новый заказ», «Поступление», «Приход», «Расход»… (D108).</summary>
    public const string Actions = "actions";

    /// <summary>Деньги на счетах и в кассах всех юрлиц (D80).</summary>
    public const string Money = "money";
    public const string Attention = "attention";
    public const string Debtors = "debtors";
    public const string Charts = "charts";
    public const string Recent = "recent";
    public const string Month = "month";
    public const string Reference = "reference";

    /// <summary>Производство (D111): заказы в работе, готовность, выпуск, смена, что требует внимания.</summary>
    public const string Production = "production";

    /// <summary>D117: блоки модулей (CRM, «Разработка»…) — компоненты, подключённые через ModuleUi в Web; нет модулей — блока нет.</summary>
    public const string Modules = "modules";

    /// <summary>«Готовность к запуску» — в шапке экрана: её можно скрыть, но не переставить.</summary>
    public const string Readiness = "readiness";

    /// <summary>Порядок по умолчанию (D113): динамика за 30 дней — выше всего, затем быстрые действия, показатели, деньги и производство.</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = [Charts, Actions, Kpi, Money, Production, Modules, Attention, Debtors, Recent, Month, Reference];

    public static readonly IReadOnlyList<string> All = [.. DefaultOrder, Readiness];

    public static DashboardLayout Default { get; } = new(DefaultOrder, []);

    /// <summary>
    /// Неизвестные и повторные блоки отбрасываются; блоки, которых нет в сохранённом порядке (например, новые), встают в конец.
    /// </summary>
    public static DashboardLayout Clean(DashboardLayout? layout)
    {
        if (layout is null)
        {
            return Default;
        }

        var order = (layout.Order ?? []).Where(DefaultOrder.Contains).Distinct().ToList();
        // Новый блок встаёт на своё место по умолчанию (быстрые действия — наверх), а не в конец.
        foreach (var missing in DefaultOrder.Where(b => !order.Contains(b)).ToList())
        {
            var at = DefaultOrder.IndexOf(missing);
            order.Insert(Math.Min(at, order.Count), missing);
        }

        var hidden = (layout.Hidden ?? []).Where(All.Contains).Distinct().OrderBy(b => All.IndexOf(b)).ToList();
        var actions = layout.Actions?.Where(DashboardActions.Keys.Contains).Distinct().Take(DashboardActions.MaxShown).ToList();
        return new DashboardLayout(order, hidden, actions);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(DashboardLayout layout) => JsonSerializer.Serialize(Clean(layout), Json);

    /// <summary>Повреждённая или пустая запись — настройка по умолчанию: экран не должен ломаться.</summary>
    public static DashboardLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return Clean(JsonSerializer.Deserialize<DashboardLayout>(json, Json));
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Быстрое действие главного экрана: куда ведёт и какое право нужно, чтобы кнопка была видна (сервер проверяет сам).</summary>
public sealed record DashboardAction(string Key, string Title, string Href, string Icon, string Permission);

/// <summary>Каталог быстрых действий (D108). Ссылки с «?new=…» сразу открывают форму создания на странице.</summary>
public static class DashboardActions
{
    public const int MaxShown = 12;

    public static readonly IReadOnlyList<DashboardAction> All =
    [
        new("sales-order", "Новый заказ покупателя", "sales-orders?new=1", "sales", KnitErp.Domain.Access.Permissions.SalesEdit),
        new("receipt", "Поступление на склад", "stock-documents?kind=1&new=1", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("shipment", "Отгрузка", "stock-documents?kind=6&new=6", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("money-in", "Приход денег", "money?new=in", "money", KnitErp.Domain.Access.Permissions.PriceView),
        new("money-out", "Расход денег", "money?new=out", "money", KnitErp.Domain.Access.Permissions.PriceView),
        new("purchase-order", "Заказ поставщику", "purchase-orders?new=1", "purchases", KnitErp.Domain.Access.Permissions.PurchaseEdit),
        new("write-off", "Списание", "stock-documents?kind=2&new=2", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("transfer", "Перемещение", "stock-documents?kind=3&new=3", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("inventory", "Инвентаризация", "inventory?new=1", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("counterparty", "Новый контрагент", "counterparties?new=1", "crm", KnitErp.Domain.Access.Permissions.CatalogEdit),
        new("item", "Новая позиция", "catalog?new=1", "catalogs", KnitErp.Domain.Access.Permissions.CatalogEdit),
        new("batch", "Партии и ярлыки", "stock-batches", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseDocumentCreate),
        new("stock", "Остатки", "stock", "warehouse", KnitErp.Domain.Access.Permissions.WarehouseReportView),
    ];

    public static readonly IReadOnlyList<string> Keys = All.Select(a => a.Key).ToList();

    /// <summary>По умолчанию — то, что делают каждый день: заказ, поступление, отгрузка, приход и расход денег, заказ поставщику.</summary>
    public static readonly IReadOnlyList<string> Default = ["sales-order", "receipt", "shipment", "money-in", "money-out", "purchase-order"];

    public static DashboardAction? Find(string key) => All.FirstOrDefault(a => a.Key == key);
}
