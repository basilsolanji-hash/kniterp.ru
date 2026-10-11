namespace KnitErp.Domain.Access;

/// <summary>
/// Коды прав «модуль.объект.действие» (ТЗ §4.6 п.2). Новое право добавляется сюда и в <see cref="RoleMatrixP0"/>.
/// </summary>
public static class Permissions
{
    public const string OrganizationView = "organization.requisites.view";
    public const string OrganizationEdit = "organization.requisites.edit";
    public const string DepartmentView = "structure.department.view";
    public const string DepartmentEdit = "structure.department.edit";
    public const string EmployeeView = "hr.employee.view";
    public const string EmployeeEdit = "hr.employee.edit";
    public const string PersonalDataView = "hr.personal_data.view";
    public const string UserView = "access.user.view";
    public const string UserManage = "access.user.manage";
    public const string AdminGrant = "access.admin.grant";
    public const string CatalogView = "catalog.item.view";
    public const string CatalogEdit = "catalog.item.edit";
    public const string CatalogArchive = "catalog.item.archive";
    public const string WarehouseDocumentCreate = "warehouse.document.create";
    public const string WarehouseDocumentPost = "warehouse.document.post";
    public const string OpeningBalanceCreate = "warehouse.opening.create";
    public const string OpeningBalanceApprove = "warehouse.opening.approve";
    public const string ClosedPeriodReopen = "period.closed.reopen";
    public const string PriceView = "finance.price.view";
    public const string WarehouseReportView = "warehouse.report.view";
    public const string AuditLogView = "audit.log.view";
    public const string CatalogImport = "catalog.import.run";
    public const string PurchaseView = "purchase.document.view";
    public const string PurchaseEdit = "purchase.document.edit";
    public const string SalesView = "sales.document.view";
    public const string SalesEdit = "sales.document.edit";

    /// <summary>D302 (сессия № 4): состав комплектов.</summary>
    public const string BundleEdit = "catalog.bundle.edit";

    /// <summary>D306 (сессия № 4): массовая переоценка. Через API не выдаётся.</summary>
    public const string PriceEdit = "catalog.price.edit";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [OrganizationView] = "Организация: просмотр реквизитов",
        [OrganizationEdit] = "Организация: изменение",
        [DepartmentView] = "Подразделения и должности: просмотр",
        [DepartmentEdit] = "Подразделения и должности: изменение",
        [EmployeeView] = "Сотрудники: просмотр",
        [EmployeeEdit] = "Сотрудники: изменение",
        [PersonalDataView] = "Персональные данные сотрудников",
        [UserView] = "Пользователи и роли: просмотр",
        [UserManage] = "Пользователи и роли: изменение",
        [AdminGrant] = "Выдача административных прав",
        [CatalogView] = "Справочники: просмотр",
        [CatalogEdit] = "Справочники: изменение",
        [CatalogArchive] = "Справочники: архивирование",
        [WarehouseDocumentCreate] = "Складские документы: черновик",
        [WarehouseDocumentPost] = "Складские документы: проведение и сторно",
        [OpeningBalanceCreate] = "Начальные остатки: создание",
        [OpeningBalanceApprove] = "Начальные остатки: утверждение",
        [ClosedPeriodReopen] = "Открытие закрытого периода",
        [PriceView] = "Цены и суммы: просмотр",
        [WarehouseReportView] = "Отчёты склада: просмотр",
        [AuditLogView] = "Журнал аудита и входов: просмотр",
        [CatalogImport] = "Шаблонный импорт справочников",
        [PurchaseView] = "Закупки: просмотр",
        [PurchaseEdit] = "Закупки: заказы поставщикам и оплаты",
        [SalesView] = "Продажи: просмотр",
        [SalesEdit] = "Продажи: заказы покупателей и оплаты",
        [BundleEdit] = "Номенклатура: состав комплектов",
        [PriceEdit] = "Цены: переоценка",
    };

    public static IReadOnlyCollection<string> All => Labels.Keys;

    public static bool IsKnown(string code) => Labels.ContainsKey(code);

    public static string Describe(string code) => Labels.TryGetValue(code, out var label) ? label : code;

    /// <summary>Права, выдача которых считается выдачей административных прав (ТЗ §4.8 KA3634).</summary>
    public static bool IsAdministrative(string code) => code is UserManage or AdminGrant;
}
