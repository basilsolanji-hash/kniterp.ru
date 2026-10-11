namespace KnitErp.Domain.Access;

/// <summary>Одна строка прав пользователя после раскрытия ролей в права.</summary>
public sealed record PermissionGrant(
    string PermissionCode,
    PermissionLevel Level,
    bool IsDeny,
    long? DepartmentId = null,
    long? WarehouseId = null);

/// <summary>Область данных права. Пустая область означает «ничего», а не «всё» (ТЗ §4.9).</summary>
public sealed class DataScope
{
    public static readonly DataScope Nothing = new(false, new HashSet<long>(), new HashSet<long>());
    public static readonly DataScope Everything = new(true, new HashSet<long>(), new HashSet<long>());

    public DataScope(bool all, IReadOnlySet<long> departmentIds, IReadOnlySet<long> warehouseIds)
    {
        All = all;
        DepartmentIds = departmentIds;
        WarehouseIds = warehouseIds;
    }

    public bool All { get; }
    public IReadOnlySet<long> DepartmentIds { get; }
    public IReadOnlySet<long> WarehouseIds { get; }
    public bool IsEmpty => !All && DepartmentIds.Count == 0 && WarehouseIds.Count == 0;

    public bool CoversDepartment(long departmentId) => All || DepartmentIds.Contains(departmentId);
    public bool CoversWarehouse(long warehouseId) => All || WarehouseIds.Contains(warehouseId);
}

/// <summary>
/// Итоговые права = объединение прав ролей и индивидуальных прав минус явные запреты; запрет сильнее (ТЗ §4.6 п.4).
/// Расчёт чистый и не зависит от базы — его проверяют юнит-тесты матрицы.
/// </summary>
public sealed class EffectivePermissionSet
{
    private readonly Dictionary<string, PermissionLevel> _levels;
    private readonly HashSet<string> _denied;
    private readonly HashSet<string> _ownOnly;
    private readonly Dictionary<string, (HashSet<long> Departments, HashSet<long> Warehouses)> _scopes;

    private EffectivePermissionSet(
        Dictionary<string, PermissionLevel> levels,
        HashSet<string> denied,
        HashSet<string> ownOnly,
        Dictionary<string, (HashSet<long>, HashSet<long>)> scopes)
    {
        _levels = levels;
        _denied = denied;
        _ownOnly = ownOnly;
        _scopes = scopes;
    }

    public static EffectivePermissionSet Empty { get; } = Compute([]);

    /// <summary>Коды прав, которые дают доступ (без «П», «С» и запрещённых).</summary>
    public IEnumerable<string> GrantedCodes => _levels.Keys.Where(Has);

    public static EffectivePermissionSet Compute(IEnumerable<PermissionGrant> grants)
    {
        var levels = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        var ownOnly = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new Dictionary<string, (HashSet<long>, HashSet<long>)>(StringComparer.Ordinal);

        foreach (var g in grants)
        {
            if (g.IsDeny)
            {
                denied.Add(g.PermissionCode);
                continue;
            }

            if (g.Level == PermissionLevel.OwnOnly)
            {
                ownOnly.Add(g.PermissionCode);
                continue;
            }

            if (!g.Level.Grants())
            {
                continue;
            }

            // Назначение, ограниченное подразделением или складом, превращает право в «своя область».
            var level = g.DepartmentId is not null || g.WarehouseId is not null ? PermissionLevel.Scoped : g.Level;

            if (!levels.TryGetValue(g.PermissionCode, out var current) || Rank(level) > Rank(current))
            {
                levels[g.PermissionCode] = level;
            }

            if (level == PermissionLevel.Scoped)
            {
                if (!scopes.TryGetValue(g.PermissionCode, out var s))
                {
                    s = (new HashSet<long>(), new HashSet<long>());
                    scopes[g.PermissionCode] = s;
                }

                if (g.DepartmentId is { } d)
                {
                    s.Item1.Add(d);
                }

                if (g.WarehouseId is { } w)
                {
                    s.Item2.Add(w);
                }
            }
        }

        return new EffectivePermissionSet(levels, denied, ownOnly, scopes);
    }

    /// <summary>
    /// Права, ограниченные списком кодов (токен API, D88): остаются только перечисленные, с их уровнем и областью; запреты сохраняются.
    /// Пересечение не может дать больше, чем было.
    /// </summary>
    public EffectivePermissionSet Restrict(IEnumerable<string> codes)
    {
        var allowed = codes.ToHashSet(StringComparer.Ordinal);
        return new EffectivePermissionSet(
            _levels.Where(l => allowed.Contains(l.Key)).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
            new HashSet<string>(_denied, StringComparer.Ordinal),
            _ownOnly.Where(allowed.Contains).ToHashSet(StringComparer.Ordinal),
            _scopes.Where(s => allowed.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
    }

    public bool Has(string permissionCode) =>
        !_denied.Contains(permissionCode) && _levels.TryGetValue(permissionCode, out var l) && l.Grants();

    public bool IsDenied(string permissionCode) => _denied.Contains(permissionCode);

    /// <summary>Право только на собственные данные («С»). Запрет его тоже снимает.</summary>
    public bool HasOwnOnly(string permissionCode) =>
        !_denied.Contains(permissionCode) && (_ownOnly.Contains(permissionCode) || Has(permissionCode));

    public PermissionLevel LevelOf(string permissionCode) =>
        Has(permissionCode) ? _levels[permissionCode] : PermissionLevel.None;

    public DataScope ScopeOf(string permissionCode)
    {
        return LevelOf(permissionCode) switch
        {
            PermissionLevel.Full or PermissionLevel.ReadOnly => DataScope.Everything,
            PermissionLevel.Scoped when _scopes.TryGetValue(permissionCode, out var s) => new DataScope(false, s.Item1, s.Item2),
            _ => DataScope.Nothing,
        };
    }

    private static int Rank(PermissionLevel level) => level switch
    {
        PermissionLevel.Full => 3,
        PermissionLevel.Scoped => 2,
        PermissionLevel.ReadOnly => 1,
        _ => 0,
    };
}
