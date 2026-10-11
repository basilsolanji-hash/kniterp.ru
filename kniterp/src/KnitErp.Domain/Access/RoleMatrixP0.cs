using static KnitErp.Domain.Access.PermissionLevel;

namespace KnitErp.Domain.Access;

/// <summary>Системные роли пилота P0 (ТЗ §4.7, KA3626).</summary>
public static class SystemRoles
{
    public const string Owner = "owner";
    public const string Administrator = "admin";
    public const string DepartmentHead = "department_head";
    public const string SeniorStorekeeper = "senior_storekeeper";
    public const string Storekeeper = "storekeeper";
    public const string Accountant = "accountant";
    public const string Auditor = "auditor";
    public const string Employee = "employee";

    /// <summary>Порядок столбцов матрицы ТЗ §4.8.</summary>
    public static readonly IReadOnlyList<string> Ordered =
        [Owner, Administrator, DepartmentHead, SeniorStorekeeper, Storekeeper, Accountant, Auditor, Employee];

    public static string NameOf(string code) => code switch
    {
        Owner => "Владелец организации",
        Administrator => "Администратор организации",
        DepartmentHead => "Руководитель подразделения",
        SeniorStorekeeper => "Старший кладовщик",
        Storekeeper => "Кладовщик",
        Accountant => "Бухгалтер",
        Auditor => "Аудитор",
        Employee => "Сотрудник",
        _ => code,
    };

    /// <summary>Роль даёт административные права (выдавать их может только Владелец).</summary>
    public static bool IsAdministrative(string code) => code is Owner or Administrator;
}

/// <summary>
/// Матрица прав P0 из ТЗ §4.8 (KA3630–KA3645). Единственный источник для начального наполнения ролей и тестов.
/// Порядок значений — как в <see cref="SystemRoles.Ordered"/>.
/// </summary>
public static class RoleMatrixP0
{
    private static readonly (string Code, PermissionLevel[] Cells)[] Rows =
    [
        (Permissions.OrganizationView,        [Full, Full, ReadOnly, None, None, ReadOnly, ReadOnly, None]),
        (Permissions.OrganizationEdit,        [Full, Full, None, None, None, None, None, None]),
        (Permissions.DepartmentView,          [Full, Full, Scoped, ReadOnly, ReadOnly, ReadOnly, ReadOnly, None]),
        (Permissions.DepartmentEdit,          [Full, Full, None, None, None, None, None, None]),
        (Permissions.EmployeeView,            [Full, Full, Scoped, ReadOnly, ReadOnly, ReadOnly, ReadOnly, OwnOnly]),
        (Permissions.EmployeeEdit,            [Full, Full, None, None, None, None, None, None]),
        (Permissions.PersonalDataView,        [Full, ByGrant, None, None, None, None, ByGrant, OwnOnly]),
        // Допущение D200: изменяет ПДн Владелец; Администратору — только индивидуальной выдачей, как и чтение.
        (Permissions.PersonalDataEdit,        [Full, ByGrant, None, None, None, None, None, None]),
        (Permissions.UserView,                [Full, Full, None, None, None, None, ReadOnly, None]),
        (Permissions.UserManage,              [Full, Full, None, None, None, None, None, None]),
        (Permissions.AdminGrant,              [Full, None, None, None, None, None, None, None]),
        (Permissions.CatalogView,             [Full, Full, Full, Full, Full, Full, Full, ReadOnly]),
        (Permissions.CatalogEdit,             [Full, Full, None, Scoped, None, None, None, None]),
        (Permissions.CatalogArchive,          [Full, Full, None, None, None, None, None, None]),
        (Permissions.WarehouseDocumentCreate, [Full, None, None, Scoped, Scoped, None, None, None]),
        // Кладовщик проводит только при выключенном разделении обязанностей — это отдельная выдача.
        (Permissions.WarehouseDocumentPost,   [Full, None, None, Scoped, ByGrant, None, None, None]),
        // Допущение D05: создаёт Старший кладовщик, утверждает Владелец или Руководитель (A §69.3).
        (Permissions.OpeningBalanceCreate,    [None, None, None, Full, None, None, None, None]),
        (Permissions.OpeningBalanceApprove,   [Full, None, Full, None, None, None, None, None]),
        (Permissions.ClosedPeriodReopen,      [Full, None, None, None, None, None, None, None]),
        (Permissions.PriceView,               [Full, Full, ByGrant, ByGrant, None, Full, Full, None]),
        (Permissions.WarehouseReportView,     [Full, Full, Scoped, Scoped, Scoped, Full, Full, None]),
        (Permissions.AuditLogView,            [Full, Full, None, None, None, None, Full, OwnOnly]),
        (Permissions.CatalogImport,           [Full, Full, None, None, None, None, None, None]),
        // Допущение D64 (после P0): закупки ведут Владелец, Администратор и Бухгалтер; Старший кладовщик и Аудитор видят заказы.
        // Цены и суммы в них видны только с правом «Цены и суммы». Администратору права нужны и для выдачи роли Бухгалтера.
        (Permissions.PurchaseView,            [Full, Full, None, Full, None, Full, Full, None]),
        (Permissions.PurchaseEdit,            [Full, Full, None, None, None, Full, None, None]),
        // Допущение D65: продажи — так же, как закупки.
        (Permissions.SalesView,               [Full, Full, None, Full, None, Full, Full, None]),
        (Permissions.SalesEdit,               [Full, Full, None, None, None, Full, None, None]),
    ];

    public static IReadOnlyList<string> PermissionCodes { get; } = Rows.Select(r => r.Code).ToArray();

    public static PermissionLevel LevelOf(string roleCode, string permissionCode)
    {
        var col = IndexOfRole(roleCode);
        foreach (var row in Rows)
        {
            if (row.Code == permissionCode)
            {
                return row.Cells[col];
            }
        }

        return None;
    }

    /// <summary>Все ненулевые права роли, включая «П» и «С» — они хранятся, но доступа сами не дают.</summary>
    public static IReadOnlyList<(string PermissionCode, PermissionLevel Level)> PermissionsOf(string roleCode)
    {
        var col = IndexOfRole(roleCode);
        return Rows.Where(r => r.Cells[col] != None).Select(r => (r.Code, r.Cells[col])).ToArray();
    }

    private static int IndexOfRole(string roleCode)
    {
        for (var i = 0; i < SystemRoles.Ordered.Count; i++)
        {
            if (SystemRoles.Ordered[i] == roleCode)
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(roleCode), roleCode, "Неизвестная системная роль.");
    }
}
