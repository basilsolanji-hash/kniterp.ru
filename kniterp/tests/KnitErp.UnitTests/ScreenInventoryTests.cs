using System.Text.RegularExpressions;

namespace KnitErp.UnitTests;

/// <summary>
/// Опись экранов (сессия «Интерфейс», этап 1): каждый адрес <c>@page</c> из Web есть в <c>docs/ui/screens.md</c>, а в описи нет
/// адресов удалённых экранов. Новый экран — новая строка описи (раздел, кто видит, главные действия, телефон).
/// </summary>
public sealed partial class ScreenInventoryTests
{
    [Fact]
    public void Every_page_route_is_in_the_screen_inventory()
    {
        var root = FindRoot();
        var routes = Directory.EnumerateFiles(Path.Combine(root, "src", "KnitErp.Web", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Page().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var inventory = File.ReadAllText(Path.Combine(root, "docs", "ui", "screens.md"));
        var listed = Row().Matches(inventory).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(routes.Count > 100 && listed.Count > 100, $"Найдено адресов: {routes.Count}, в описи: {listed.Count}");
        Assert.Empty(routes.Except(listed).Order());
        Assert.Empty(listed.Except(routes).Order());
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnitErp.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не найден KnitErp.slnx");
    }

    [GeneratedRegex("""@page "([^"]+)"[\r\n]""")]
    private static partial Regex Page();

    /// <summary>Второй столбец строки таблицы — адреса экрана через пробел.</summary>
    [GeneratedRegex(@"^\| [^|]+ \| (/[^|]*?) \|", RegexOptions.Multiline)]
    private static partial Regex Row();
}
