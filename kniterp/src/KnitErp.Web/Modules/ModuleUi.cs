namespace KnitErp.Web.Modules;

/// <summary>
/// D117: точки подключения интерфейса модулей (CRM, «Разработка» и следующих) без правки страниц базы.
/// Модуль регистрирует свои компоненты один раз при запуске (в <c>Program.cs</c> или в своём методе подключения):
/// <code>
/// ModuleUi.AddDashboardBlock("crm", typeof(CrmDashboardBlock), order: 10);
/// ModuleUi.AddCounterpartyTab("crm", ModuleUi.Text(…русское название…), typeof(CrmCounterpartyTab), "crm.view");
/// </code>
/// Правила для компонентов модулей:
/// <list type="bullet">
/// <item>Права проверяет сервис модуля; нет прав — компонент ничего не выводит. Право вкладки здесь только скрывает кнопку.</item>
/// <item>Данные — через свой короткоживущий контекст (<c>IDbContextFactory</c>) или после первой отрисовки: страница базы уже
///   работает со своим контекстом вкладки, параллельные запросы в один контекст запрещены.</item>
/// <item>Тексты — через L(…) в своём компоненте; название вкладки — через <see cref="Text"/>, чтобы его нашёл тест переводов.</item>
/// </list>
/// </summary>
public static class ModuleUi
{
    private static readonly object Gate = new();
    private static List<DashboardBlock> _blocks = [];
    private static List<CounterpartyTab> _tabs = [];

    /// <summary>Блок модуля на главном экране. Компонент без параметров; блоки идут в порядке <see cref="DashboardBlock.Order"/>.</summary>
    public sealed record DashboardBlock(string Key, Type Component, int Order);

    /// <summary>Вкладка карточки контрагента. Компонент получает параметр <c>CounterpartyId</c> (long).</summary>
    public sealed record CounterpartyTab(string Key, string Title, Type Component, string? Permission);

    public static IReadOnlyList<DashboardBlock> DashboardBlocks => _blocks;

    public static IReadOnlyList<CounterpartyTab> CounterpartyTabs => _tabs;

    /// <summary>Пометка русского текста для перевода (тест переводов собирает русские строки из вызовов ModuleUi.Text).</summary>
    public static string Text(string russian) => russian;

    public static void AddDashboardBlock(string key, Type component, int order = 0)
    {
        EnsureComponent(component);
        lock (Gate)
        {
            if (_blocks.Any(b => b.Key == key))
            {
                return;
            }

            _blocks = [.. _blocks.Append(new DashboardBlock(key, component, order)).OrderBy(b => b.Order).ThenBy(b => b.Key, StringComparer.Ordinal)];
        }
    }

    public static void AddCounterpartyTab(string key, string title, Type component, string? permission)
    {
        EnsureComponent(component);
        lock (Gate)
        {
            if (_tabs.Any(t => t.Key == key))
            {
                return;
            }

            _tabs = [.. _tabs, new CounterpartyTab(key, title, component, permission)];
        }
    }

    private static void EnsureComponent(Type component)
    {
        if (!typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(component))
        {
            throw new ArgumentException($"{component.FullName} — не компонент Blazor.", nameof(component));
        }
    }
}
