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

    /// <summary>Кадровик (D205) — роль модуля «Сотрудники», её права заданы в <see cref="HrRoleMatrix"/>, а не столбцом матрицы P0.</summary>
    public const string HrOfficer = "hr_officer";

    /// <summary>Порядок столбцов матрицы ТЗ §4.8.</summary>
    public static readonly IReadOnlyList<string> Ordered =
        [Owner, Administrator, DepartmentHead, SeniorStorekeeper, Storekeeper, Accountant, Auditor, Employee, HrOfficer];

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
        HrOfficer => "Кадровик",
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
        // Допущение D205: кадровые приказы и штатное расписание — Владелец; Кадровик — см. HrRoleMatrix; Аудитор и Бухгалтер видят приказы.
        (Permissions.HrOrderCreate,           [Full, None, None, None, None, None, None, None]),
        (Permissions.HrOrderPost,             [Full, None, None, None, None, None, None, None]),
        // Допущение D208: табель своего подразделения заполняет и закрывает Руководитель подразделения (мастер цеха);
        // Сотрудник видит только свой табель (уровень «С»); Кадровик заполняет, но не закрывает — см. HrRoleMatrix.
        (Permissions.TimesheetEdit,           [Full, None, Scoped, None, None, None, None, OwnOnly]),
        (Permissions.TimesheetClose,          [Full, None, Scoped, None, None, None, None, None]),
        // Допущение D212: заработок считает Владелец; Бухгалтеру — «по отдельному праву» (выдаёт Владелец: роль Бухгалтера может
        // выдать и Администратор, а зарплатных данных у него нет); Аудитор смотрит; Сотрудник — только свой расчётный листок.
        (Permissions.PayrollView,             [Full, None, None, None, None, ByGrant, ReadOnly, OwnOnly]),
        (Permissions.PayrollCalculate,        [Full, None, None, None, None, ByGrant, None, None]),
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
        // Допущение D91 (дополнение): Руководитель подразделения (начальник цеха) видит заказы покупателей — что и к какому сроку
        // производить; цены и суммы — только с правом «Цены и суммы», у этой роли оно выдаётся отдельно.
        (Permissions.SalesView,               [Full, Full, ReadOnly, Full, None, Full, Full, None]),
        (Permissions.SalesEdit,               [Full, Full, None, None, None, Full, None, None]),
        // Допущение D91: производственные заказы ведут Владелец, Администратор и Руководитель подразделения (начальник цеха);
        // кладовщики, Бухгалтер и Аудитор видят их. Плановая себестоимость видна только с правом «Цены и суммы».
        (Permissions.ProductionOrderView,     [Full, Full, Full, Full, Full, Full, Full, None]),
        (Permissions.ProductionOrderEdit,     [Full, Full, Full, None, None, None, None, None]),
        // Допущение D92: этапы отмечает начальник цеха, мастерам и контролёрам ОТК (роль «Сотрудник») право выдаётся отдельно.
        (Permissions.ProductionStageMark,     [Full, Full, Full, None, None, None, None, ByGrant]),
        // Допущение D412: виновника брака указывает тот, кто ведёт качество, — начальник цеха; контролёру ОТК право выдаётся отдельно
        // (виновник влияет на баллы и премии, поэтому право уже, чем отметка этапов).
        (Permissions.ProductionDefectCulprit, [Full, Full, Full, None, None, None, None, ByGrant]),
        // Допущение D93: выдачу, возврат, отходы и выпуск проводят кладовщики своих складов — нужны ещё права складских документов.
        (Permissions.ProductionMaterialMove,  [Full, None, None, Scoped, Scoped, None, None, None]),
        // Допущение D302 (каталог): состав комплектов меняют те, кто меняет справочники организации целиком.
        (Permissions.BundleEdit,              [Full, Full, None, None, None, None, None, None]),
        // Допущение D306: массовую переоценку проводят Владелец и Администратор (цены видят — право «Цены и суммы»).
        (Permissions.PriceEdit,               [Full, Full, None, None, None, None, None, None]),
    ];

    public static IReadOnlyList<string> PermissionCodes { get; } = Rows.Select(r => r.Code).ToArray();

    public static PermissionLevel LevelOf(string roleCode, string permissionCode)
    {
        if (roleCode == SystemRoles.HrOfficer)
        {
            return HrRoleMatrix.LevelOf(permissionCode);
        }

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
        if (roleCode == SystemRoles.HrOfficer)
        {
            return HrRoleMatrix.HrOfficer.Select(p => (p.Key, p.Value)).ToArray();
        }

        var col = IndexOfRole(roleCode);
        return Rows.Where(r => r.Cells[col] != None).Select(r => (r.Code, r.Cells[col])).ToArray();
    }

    private static int IndexOfRole(string roleCode)
    {
        for (var i = 0; i < Rows[0].Cells.Length; i++)
        {
            if (SystemRoles.Ordered[i] == roleCode)
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(roleCode), roleCode, "Неизвестная системная роль.");
    }
}

/// <summary>
/// Права роли «Кадровик» (D205): сотрудники, персональные данные, кадровые приказы и штатное расписание организации.
/// Отдельный список, а не девятый столбец матрицы P0: так модули добавляют строки матрицы, не задевая друг друга.
/// </summary>
public static class HrRoleMatrix
{
    public static readonly IReadOnlyDictionary<string, PermissionLevel> HrOfficer = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal)
    {
        [Permissions.DepartmentView] = Full,
        [Permissions.EmployeeView] = Full,
        [Permissions.EmployeeEdit] = Full,
        [Permissions.PersonalDataView] = Full,
        [Permissions.PersonalDataEdit] = Full,
        [Permissions.HrOrderCreate] = Full,
        [Permissions.HrOrderPost] = Full,
        [Permissions.TimesheetEdit] = Full,
        [Permissions.AuditLogView] = OwnOnly,
    };

    public static PermissionLevel LevelOf(string permissionCode) => HrOfficer.TryGetValue(permissionCode, out var level) ? level : None;
}
