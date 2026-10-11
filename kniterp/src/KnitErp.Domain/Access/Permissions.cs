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
    public const string PersonalDataEdit = "hr.personal_data.edit";
    public const string HrOrderCreate = "hr.order.create";
    public const string HrOrderPost = "hr.order.post";
    public const string TimesheetEdit = "hr.timesheet.edit";
    public const string TimesheetClose = "hr.timesheet.close";
    public const string PayrollView = "hr.payroll.view";
    public const string PayrollCalculate = "hr.payroll.calculate";
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
    public const string ProductionOrderView = "production.order.view";
    public const string ProductionOrderEdit = "production.order.edit";
    public const string ProductionStageMark = "production.stage.mark";
    public const string ProductionMaterialMove = "production.material.move";

    /// <summary>D302 (сессия № 4): состав комплектов.</summary>
    public const string BundleEdit = "catalog.bundle.edit";

    /// <summary>D306 (сессия № 4): массовая переоценка. Через API не выдаётся.</summary>
    public const string PriceEdit = "catalog.price.edit";

    /// <summary>D600 (CRM): свои лиды, сделки и контакты — где пользователь ответственный или наблюдатель.</summary>
    public const string CrmView = "crm.view";

    /// <summary>D600 (CRM): создавать и менять свои лиды, сделки, контакты, активности.</summary>
    public const string CrmEdit = "crm.edit";

    /// <summary>D600 (CRM): все лиды и сделки организации, переназначение ответственного, объединение дубликатов.</summary>
    public const string CrmAll = "crm.all";

    /// <summary>D600 (CRM): воронки, этапы, источники, причины отказа, план продаж.</summary>
    public const string CrmSettings = "crm.settings";

    /// <summary>D600 (CRM): выгрузка в Excel (персональные данные контактов).</summary>
    public const string CrmExport = "crm.export";

    /// <summary>D640 (CRM): приём заявок через API v1 (токен сайта или маркетплейса) и событий телефонии.</summary>
    public const string CrmLeadsCreate = "crm.leads.create";

    /// <summary>D615 (CRM): рассылки контактам с согласием на рекламу.</summary>
    public const string CrmMailing = "crm.mailing";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [OrganizationView] = "Организация: просмотр реквизитов",
        [OrganizationEdit] = "Организация: изменение",
        [DepartmentView] = "Подразделения и должности: просмотр",
        [DepartmentEdit] = "Подразделения и должности: изменение",
        [EmployeeView] = "Сотрудники: просмотр",
        [EmployeeEdit] = "Сотрудники: изменение",
        [PersonalDataView] = "Персональные данные сотрудников",
        [PersonalDataEdit] = "Персональные данные сотрудников: изменение",
        [HrOrderCreate] = "Кадровые приказы и штатное расписание: черновик",
        [HrOrderPost] = "Кадровые приказы: проведение, штатное расписание: изменение",
        [TimesheetEdit] = "Табель и графики работы: заполнение",
        [TimesheetClose] = "Табель: закрытие",
        [PayrollView] = "Заработок и расчётные листки: просмотр",
        [PayrollCalculate] = "Заработок: расценки, расчёт и утверждение",
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
        [ProductionOrderView] = "Производство: просмотр заказов",
        [ProductionOrderEdit] = "Производство: заказы — создание, запуск и отмена",
        [ProductionStageMark] = "Производство: отметка этапов и контроль качества",
        [ProductionMaterialMove] = "Производство: выдача, возврат и списание материалов, выпуск",
        [BundleEdit] = "Номенклатура: состав комплектов",
        [PriceEdit] = "Цены: переоценка",
        [CrmView] = "CRM: свои лиды, сделки и контакты",
        [CrmEdit] = "CRM: создание и изменение своих лидов, сделок и контактов",
        [CrmAll] = "CRM: все лиды и сделки, переназначение ответственного",
        [CrmSettings] = "CRM: воронки, этапы, справочники и план",
        [CrmExport] = "CRM: выгрузка в Excel",
        [CrmLeadsCreate] = "CRM: приём заявок и звонков через API",
        [CrmMailing] = "CRM: рассылки клиентам",
    };

    public static IReadOnlyCollection<string> All => Labels.Keys;

    public static bool IsKnown(string code) => Labels.ContainsKey(code);

    public static string Describe(string code) => Labels.TryGetValue(code, out var label) ? label : code;

    /// <summary>Группы прав для редактора ролей (D112), в порядке разделов меню.</summary>
    public static readonly IReadOnlyList<string> Groups =
        ["Продажи", "Закупки", "CRM", "Склад", "Деньги и цены", "Справочники", "Производство", "Организация и сотрудники", "Пользователи и доступ", "Журнал и контроль", "Прочее"];

    /// <summary>Группа права по его коду — так новые права модулей (производство, кадры) сами попадают в свой раздел.</summary>
    public static string GroupOf(string code) => code.Split('.')[0] switch
    {
        "sales" => "Продажи",
        "purchase" => "Закупки",
        "crm" => "CRM",
        "warehouse" or "period" => "Склад",
        "finance" or "money" => "Деньги и цены",
        "catalog" => "Справочники",
        "production" => "Производство",
        "organization" or "structure" or "hr" => "Организация и сотрудники",
        "access" => "Пользователи и доступ",
        "audit" => "Журнал и контроль",
        _ => "Прочее",
    };

    /// <summary>Права, выдача которых считается выдачей административных прав (ТЗ §4.8 KA3634).</summary>
    public static bool IsAdministrative(string code) => code is UserManage or AdminGrant;
}
