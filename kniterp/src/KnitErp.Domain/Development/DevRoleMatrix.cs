using KnitErp.Domain.Access;
using static KnitErp.Domain.Access.PermissionLevel;

namespace KnitErp.Domain.Development;

/// <summary>
/// Системные роли отдела разработки (D700): Главный технолог, Разработчик (технолог), Дессинатор, Образцовщица.
/// Отдельный список, как у Кадровика (D205): модули добавляют свои роли, не задевая столбцы матрицы P0.
/// На небольшой фабрике один человек совмещает роли — Владелец объединяет права в своей роли (D112).
/// </summary>
public static class DevRoleMatrix
{
    private static readonly Dictionary<string, PermissionLevel> Chief = new(StringComparer.Ordinal)
    {
        [Permissions.DevView] = Full,
        [Permissions.DevEdit] = Full,
        [Permissions.DevApprove] = Full,
        [Permissions.DevCostView] = Full,
        [Permissions.DevSettings] = Full,
        [Permissions.CatalogView] = Full,
    };

    private static readonly Dictionary<string, PermissionLevel> Technologist = new(StringComparer.Ordinal)
    {
        [Permissions.DevView] = Full,
        [Permissions.DevEdit] = Full,
        [Permissions.CatalogView] = Full,
    };

    private static readonly Dictionary<string, PermissionLevel> Dessinator = new(StringComparer.Ordinal)
    {
        [Permissions.DevView] = Full,
        [Permissions.DevEdit] = Full,
        [Permissions.DevPattern] = Full,
        [Permissions.CatalogView] = Full,
    };

    private static readonly Dictionary<string, PermissionLevel> Sampler = new(StringComparer.Ordinal)
    {
        [Permissions.DevView] = Full,
        [Permissions.DevSample] = Full,
        [Permissions.CatalogView] = Full,
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, PermissionLevel>> Roles =
        new Dictionary<string, IReadOnlyDictionary<string, PermissionLevel>>(StringComparer.Ordinal)
        {
            [SystemRoles.DevChief] = Chief,
            [SystemRoles.DevTechnologist] = Technologist,
            [SystemRoles.DevDessinator] = Dessinator,
            [SystemRoles.DevSampler] = Sampler,
        };
}
