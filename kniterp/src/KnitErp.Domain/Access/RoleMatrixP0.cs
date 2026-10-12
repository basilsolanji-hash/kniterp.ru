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

    /// <summary>Роли модуля «Разработка» (D700) — права в <see cref="KnitErp.Domain.Development.DevRoleMatrix"/>.</summary>
    public const string DevChief = "dev_chief";
    public const string DevTechnologist = "dev_technologist";
    public const string DevDessinator = "dev_dessinator";
    public const string DevSampler = "dev_sampler";

    /// <summary>Менеджер продаж (D600, CRM) — права в <see cref="CrmRoleMatrix"/>.</summary>
    public const string SalesManager = "sales_manager";

    /// <summary>Руководитель продаж (D600, CRM) — права в <see cref="CrmRoleMatrix"/>.</summary>
    public const string SalesHead = "sales_head";

    /// <summary>Порядок столбцов матрицы ТЗ §4.8.</summary>
    public static readonly IReadOnlyList<string> Ordered =
        [Owner, Administrator, DepartmentHead, SeniorStorekeeper, Storekeeper, Accountant, Auditor, Employee, HrOfficer, SalesManager, SalesHead,
            DevChief, DevTechnologist, DevDessinator, DevSampler];

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
        DevChief => "Главный технолог",
        DevTechnologist => "Разработчик (технолог)",
        DevDessinator => "Дессинатор",
        DevSampler => "Образцовщица",
        SalesManager => "Менеджер продаж",
        SalesHead => "Руководитель продаж",
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
        // Допущение D700 (разработка): Владелец — всё; Администратор настраивает справочники и видит модели; начальник цеха и Аудитор
        // смотрят; Бухгалтер (экономист) видит модели и себестоимость (Администратор — тоже, иначе не смог бы выдать роль Бухгалтера).
        // Отдел разработки — роли DevRoleMatrix.
        (Permissions.DevView,                 [Full, Full, ReadOnly, None, None, ReadOnly, ReadOnly, None]),
        (Permissions.DevEdit,                 [Full, None, None, None, None, None, None, None]),
        (Permissions.DevPattern,              [Full, None, None, None, None, None, None, None]),
        (Permissions.DevSample,               [Full, None, None, None, None, None, None, None]),
        (Permissions.DevApprove,              [Full, None, None, None, None, None, None, None]),
        (Permissions.DevCostView,             [Full, Full, None, None, None, Full, ReadOnly, None]),
        (Permissions.DevSettings,             [Full, Full, None, None, None, None, None, None]),
        // Допущение D600 (CRM): Владелец — всё; Администратор видит CRM и настраивает воронки (сделки ведут менеджеры).
        // Менеджер и руководитель продаж — роли CRM, их права в CrmRoleMatrix.
        (Permissions.CrmView,                 [Full, Full, None, None, None, None, None, None]),
        (Permissions.CrmEdit,                 [Full, None, None, None, None, None, None, None]),
        (Permissions.CrmAll,                  [Full, None, None, None, None, None, None, None]),
        (Permissions.CrmSettings,             [Full, Full, None, None, None, None, None, None]),
        (Permissions.CrmExport,               [Full, None, None, None, None, None, None, None]),
        (Permissions.CrmLeadsCreate,          [Full, None, None, None, None, None, None, None]),
        (Permissions.CrmMailing,              [Full, None, None, None, None, None, None, None]),
        // Допущение D1100 (продажи): обходы ограничений цены и кредита — Владелец; соглашения и скидки — Владелец, Администратор
        // и Бухгалтер (договорные условия); отчёты продаж — кто видит продажи и цены; план — Владелец; коды маркировки сканируют
        // кладовщики своих складов. Менеджер и руководитель продаж — CrmRoleMatrix.
        (Permissions.SalesPriceOverride,      [Full, None, None, None, None, None, None, None]),
        (Permissions.SalesCreditOverride,     [Full, None, None, None, None, None, None, None]),
        (Permissions.SalesAgreementEdit,      [Full, Full, None, None, None, Full, None, None]),
        (Permissions.SalesDiscountEdit,       [Full, Full, None, None, None, Full, None, None]),
        (Permissions.SalesReportView,         [Full, Full, None, None, None, Full, Full, None]),
        (Permissions.SalesPlanEdit,           [Full, None, None, None, None, None, None, None]),
        (Permissions.SalesMarkingEdit,        [Full, None, None, Scoped, Scoped, None, None, None]),
        // Допущение D310: МойСклад подключают Владелец и Администратор (ТЗ каталога, этап 4).
        (Permissions.MoySkladManage,          [Full, Full, None, None, None, None, None, None]),
        (Permissions.CrmLeadsCreate,          [Full, None, None, None, None, None, None, None]),
        (Permissions.CrmMailing,              [Full, None, None, None, None, None, None, None]),
    ];

    public static IReadOnlyList<string> PermissionCodes { get; } = Rows.Select(r => r.Code).ToArray();

    public static PermissionLevel LevelOf(string roleCode, string permissionCode)
    {
        if (roleCode == SystemRoles.HrOfficer)
        {
            return HrRoleMatrix.LevelOf(permissionCode);
        }

        if (KnitErp.Domain.Development.DevRoleMatrix.Roles.TryGetValue(roleCode, out var dev))
        {
            return dev.TryGetValue(permissionCode, out var devLevel) ? devLevel : None;
        }

        if (CrmRoleMatrix.Of(roleCode) is { } crm)
        {
            return crm.TryGetValue(permissionCode, out var level) ? level : None;
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

        if (KnitErp.Domain.Development.DevRoleMatrix.Roles.TryGetValue(roleCode, out var dev))
        {
            return dev.Select(p => (p.Key, p.Value)).ToArray();
        }

        if (CrmRoleMatrix.Of(roleCode) is { } crm)
        {
            return crm.Select(p => (p.Key, p.Value)).ToArray();
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

/// <summary>
/// Права ролей CRM (D600): «Менеджер продаж» ведёт свои лиды и сделки, «Руководитель продаж» — все, настраивает воронки
/// и выгружает. Обоим нужны продажи и цены базы: из сделки создаётся заказ покупателя, а его сумма видна только с правом цен.
/// Отдельный список, как у «Кадровика» (D205), чтобы модули не задевали столбцы матрицы P0.
/// </summary>
public static class CrmRoleMatrix
{
    public static readonly IReadOnlyDictionary<string, PermissionLevel> SalesManager = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal)
    {
        [Permissions.CrmView] = Full,
        [Permissions.CrmEdit] = Full,
        [Permissions.SalesView] = Full,
        [Permissions.SalesEdit] = Full,
        [Permissions.PriceView] = Full,
        [Permissions.CatalogView] = Full,
        // D1100: свои отчёты продаж (выручка, дебиторка своих клиентов).
        [Permissions.SalesReportView] = Full,
    };

    public static readonly IReadOnlyDictionary<string, PermissionLevel> SalesHead = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal)
    {
        [Permissions.CrmView] = Full,
        [Permissions.CrmEdit] = Full,
        [Permissions.CrmAll] = Full,
        [Permissions.CrmSettings] = Full,
        [Permissions.CrmExport] = Full,
        [Permissions.CrmLeadsCreate] = Full,
        [Permissions.CrmMailing] = Full,
        [Permissions.SalesView] = Full,
        [Permissions.SalesEdit] = Full,
        [Permissions.PriceView] = Full,
        [Permissions.CatalogView] = Full,
        // D1100: руководитель продаж разрешает обходы, ведёт соглашения, скидки, план и отчёты.
        [Permissions.SalesPriceOverride] = Full,
        [Permissions.SalesCreditOverride] = Full,
        [Permissions.SalesAgreementEdit] = Full,
        [Permissions.SalesDiscountEdit] = Full,
        [Permissions.SalesReportView] = Full,
        [Permissions.SalesPlanEdit] = Full,
    };

    public static IReadOnlyDictionary<string, PermissionLevel>? Of(string roleCode) => roleCode switch
    {
        SystemRoles.SalesManager => SalesManager,
        SystemRoles.SalesHead => SalesHead,
        _ => null,
    };
}
