using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KnitErp.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Кто выполняет запрос. OrganizationId берётся из сессии пользователя, а не из параметров запроса (ТЗ §4.6 п.3).
/// </summary>
public interface ICurrentUser
{
    long? UserId { get; }
    long? OrganizationId { get; }
    string? CorrelationId { get; }

    /// <summary>IP-адрес клиента обычного HTTP-запроса (страницы входа) — для журнала входов. В Blazor-цепи и тестах — null.</summary>
    string? ClientAddress => null;

    /// <summary>Запрос по токену API (D88): права ограничены этими кодами (пересечение с правами пользователя). null — обычная сессия.</summary>
    IReadOnlyCollection<string>? ApiScope => null;

    /// <summary>Токен API, от имени которого идёт запрос; null — обычная сессия.</summary>
    long? ApiTokenId => null;
}

/// <summary>
/// Отдельный DbContext на одну операцию. Нужен инструментам панели быстрого доступа: они работают поверх открытой
/// страницы, а DbContext вкладки нельзя использовать параллельно.
/// </summary>
public interface IKnitErpDbContextFactory
{
    IKnitErpDbContext Create();
}

public interface IKnitErpDbContext : IAsyncDisposable
{
    DbSet<Organization> Organizations { get; }
    DbSet<UserAccount> Users { get; }
    DbSet<RecoveryCode> RecoveryCodes { get; }
    DbSet<TrustedDevice> TrustedDevices { get; }
    DbSet<Characteristic> Characteristics { get; }
    DbSet<ItemCharacteristicValue> ItemCharacteristicValues { get; }
    DbSet<ItemPhoto> ItemPhotos { get; }

    /// <summary>Каталог (сессия № 4): состав комплектов (D302).</summary>
    DbSet<BundleComponent> BundleComponents { get; }
    DbSet<ItemPriceHistory> ItemPriceHistory { get; }
    DbSet<PriceRevaluation> PriceRevaluations { get; }
    DbSet<ItemPack> ItemPacks { get; }
    DbSet<ItemSupplier> ItemSuppliers { get; }
    DbSet<ExternalLink> ExternalLinks { get; }
    DbSet<ExternalConnection> ExternalConnections { get; }
    DbSet<ExternalImportRun> ExternalImportRuns { get; }
    DbSet<ExternalImportIssue> ExternalImportIssues { get; }
    DbSet<CatalogSettings> CatalogSettings { get; }
    DbSet<CatalogFeed> CatalogFeeds { get; }
    DbSet<LabelTemplate> LabelTemplates { get; }
    DbSet<KnitErp.Domain.Finance.MoneyOperation> MoneyOperations { get; }
    DbSet<KnitErp.Domain.Integrations.ApiToken> ApiTokens { get; }
    DbSet<KnitErp.Domain.Integrations.WebhookSubscription> WebhookSubscriptions { get; }
    DbSet<KnitErp.Domain.Integrations.WebhookDelivery> WebhookDeliveries { get; }
    DbSet<KnitErp.Domain.Integrations.ApiIdempotencyRecord> ApiIdempotency { get; }
    DbSet<KnitErp.Domain.Finance.CashFlowItem> CashFlowItems { get; }
    DbSet<KnitErp.Domain.Finance.ExpenseReport> ExpenseReports { get; }
    DbSet<KnitErp.Domain.Finance.CustomerPaymentAllocation> CustomerPaymentAllocations { get; }
    DbSet<KnitErp.Domain.Finance.SupplierPaymentAllocation> SupplierPaymentAllocations { get; }
    DbSet<OrganizationMember> OrganizationMembers { get; }
    DbSet<Role> Roles { get; }
    DbSet<RoleAssignment> RoleAssignments { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<Department> Departments { get; }
    DbSet<Position> Positions { get; }
    DbSet<Employee> Employees { get; }
    DbSet<UnitOfMeasure> Units { get; }
    DbSet<Item> Items { get; }
    DbSet<Site> Sites { get; }
    DbSet<Warehouse> Warehouses { get; }
    DbSet<Counterparty> Counterparties { get; }
    DbSet<CounterpartyContract> CounterpartyContracts { get; }
    DbSet<CounterpartyContractFile> CounterpartyContractFiles { get; }
    DbSet<VatRate> VatRates { get; }
    DbSet<KnitErp.Domain.Production.TechCard> TechCards { get; }
    DbSet<KnitErp.Domain.Production.ProductionOrder> ProductionOrders { get; }
    DbSet<KnitErp.Domain.Production.ProductionOperation> ProductionOperations { get; }
    DbSet<KnitErp.Domain.Production.ShiftTask> ShiftTasks { get; }
    DbSet<KnitErp.Domain.Production.ProductionSettings> ProductionSettings { get; }
    DbSet<KnitErp.Domain.Production.ProductionOrderDocument> ProductionOrderDocuments { get; }
    DbSet<KnitErp.Domain.Purchasing.PurchaseOrder> PurchaseOrders { get; }
    DbSet<KnitErp.Domain.Purchasing.SupplierPayment> SupplierPayments { get; }
    DbSet<KnitErp.Domain.Sales.SalesOrder> SalesOrders { get; }
    DbSet<KnitErp.Domain.Sales.CustomerPayment> CustomerPayments { get; }
    DbSet<KnitErp.Domain.Finance.BankStatementLine> BankStatementLines { get; }
    DbSet<KnitErp.Domain.Sales.CustomerInvoice> CustomerInvoices { get; }
    DbSet<KnitErp.Domain.Sales.SalesOrderStage> SalesOrderStages { get; }
    DbSet<KnitErp.Domain.Sales.SalesStageRule> SalesStageRules { get; }
    DbSet<KnitErp.Domain.Catalog.Lookup> Lookups { get; }
    DbSet<KnitErp.Domain.Organizations.LegalEntity> LegalEntities { get; }
    DbSet<KnitErp.Domain.Catalog.ItemGroup> ItemGroups { get; }
    DbSet<KnitErp.Domain.Catalog.ItemBarcode> ItemBarcodes { get; }
    DbSet<KnitErp.Domain.Catalog.PriceType> PriceTypes { get; }
    DbSet<KnitErp.Domain.Catalog.ItemPrice> ItemPrices { get; }
    DbSet<KnitErp.Domain.Catalog.UserCatalog> UserCatalogs { get; }
    DbSet<KnitErp.Domain.Catalog.UserCatalogEntry> UserCatalogEntries { get; }
    DbSet<KnitErp.Domain.Organizations.LegalEntityAccount> LegalEntityAccounts { get; }
    DbSet<KnitErp.Domain.Common.CustomFieldDefinition> CustomFieldDefinitions { get; }
    DbSet<KnitErp.Domain.Common.CustomFieldValue> CustomFieldValues { get; }
    DbSet<KnitErp.Domain.Purchasing.ReceivedVatInvoice> ReceivedVatInvoices { get; }
    DbSet<OperationReason> OperationReasons { get; }
    DbSet<OpeningBalance> OpeningBalances { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<StockDocument> StockDocuments { get; }
    DbSet<StockBatch> StockBatches { get; }
    DbSet<StockDocumentBatchLine> StockDocumentBatchLines { get; }
    DbSet<StockBatchMovement> StockBatchMovements { get; }
    DbSet<KnitErp.Domain.Tagging.Tag> Tags { get; }
    DbSet<KnitErp.Domain.Collaboration.DiscussionComment> DiscussionComments { get; }
    DbSet<KnitErp.Domain.Collaboration.DiscussionMention> DiscussionMentions { get; }
    DbSet<KnitErp.Domain.Tagging.TagLink> TagLinks { get; }
    DbSet<OpeningBalanceBatchLine> OpeningBalanceBatchLines { get; }
    DbSet<InventoryBatchCount> InventoryBatchCounts { get; }
    DbSet<InventoryCount> InventoryCounts { get; }
    DbSet<PeriodClosure> PeriodClosures { get; }
    DbSet<KnitErp.Domain.Workspace.UserToolData> UserToolData { get; }
    DbSet<KnitErp.Domain.Workspace.SupportTicket> SupportTickets { get; }
    DbSet<DocumentCounter> DocumentCounters { get; }

    // Модуль «Сотрудники» (D200–D299).
    DbSet<KnitErp.Domain.Hr.EmployeePersonalData> EmployeePersonalData { get; }
    DbSet<KnitErp.Domain.Hr.ConsentScan> ConsentScans { get; }
    DbSet<KnitErp.Domain.Hr.StaffingPosition> StaffingPositions { get; }
    DbSet<KnitErp.Domain.Hr.PersonnelOrder> PersonnelOrders { get; }
    DbSet<KnitErp.Domain.Hr.EmploymentRecord> EmploymentHistory { get; }
    DbSet<KnitErp.Domain.Hr.WorkSchedule> WorkSchedules { get; }
    DbSet<KnitErp.Domain.Hr.EmployeeSchedule> EmployeeSchedules { get; }
    DbSet<KnitErp.Domain.Hr.DepartmentSchedule> DepartmentSchedules { get; }
    DbSet<KnitErp.Domain.Hr.EmployeeAgreement> EmployeeAgreements { get; }
    DbSet<KnitErp.Domain.Hr.Timesheet> Timesheets { get; }
    DbSet<KnitErp.Domain.Hr.TimesheetDay> TimesheetDays { get; }
    DbSet<KnitErp.Domain.Hr.PieceRate> PieceRates { get; }
    DbSet<KnitErp.Domain.Hr.PayrollRun> PayrollRuns { get; }
    DbSet<KnitErp.Domain.Hr.PayrollLine> PayrollLines { get; }
    DbSet<KnitErp.Domain.Hr.HrSettings> HrSettings { get; }

    // Модуль «Разработка» (D700–D799).
    DbSet<KnitErp.Domain.Development.DevModel> DevModels { get; }
    DbSet<KnitErp.Domain.Development.DevModelStageEntry> DevModelStages { get; }
    DbSet<KnitErp.Domain.Development.DevStageSetting> DevStageSettings { get; }
    DbSet<KnitErp.Domain.Development.DevFile> DevFiles { get; }
    DbSet<KnitErp.Domain.Development.DevLookup> DevLookups { get; }
    DbSet<KnitErp.Domain.Development.DevSettings> DevSettings { get; }
    DbSet<KnitErp.Domain.Development.DevCategoryTemplate> DevCategoryTemplates { get; }
    // CRM (D600–D699).
    DbSet<KnitErp.Domain.Crm.CrmPipeline> CrmPipelines { get; }
    DbSet<KnitErp.Domain.Crm.CrmStage> CrmStages { get; }
    DbSet<KnitErp.Domain.Crm.CrmReference> CrmReferences { get; }
    DbSet<KnitErp.Domain.Crm.CrmLead> CrmLeads { get; }
    DbSet<KnitErp.Domain.Crm.CrmContact> CrmContacts { get; }
    DbSet<KnitErp.Domain.Crm.CrmDeal> CrmDeals { get; }
    DbSet<KnitErp.Domain.Crm.CrmDealStageHistory> CrmDealStageHistory { get; }
    DbSet<KnitErp.Domain.Crm.CrmDealOrder> CrmDealOrders { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Забыть несохранённые изменения после отказа сценария: у вкладки Blazor один контекст, и без этого изменённая в памяти
    /// сущность (например, «проведённый» документ, проведение которого отклонено) ушла бы в базу со следующим сохранением.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// Исключительная блокировка складов до конца текущей транзакции: проверка остатка и запись расхода
    /// не перемежаются с другим проведением по тем же складам.
    /// </summary>
    Task LockWarehousesAsync(IEnumerable<long> warehouseIds, CancellationToken cancellationToken = default);

    /// <summary>Исключительная именованная блокировка до конца текущей транзакции (sp_getapplock).</summary>
    Task LockAsync(string resource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Лист таблицы для выгрузки: заголовок и строки как текст. NumericColumns — номера столбцов (с 0) с числами
/// в инвариантной записи «1250.5»: в Excel они станут числами, и их можно суммировать; остальные — текст.
/// </summary>
public sealed record SheetData(
    string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlySet<int>? NumericColumns = null);

/// <summary>Чтение и запись таблиц Excel (.xlsx). Реализация — в Infrastructure, сценарии от библиотеки не зависят.</summary>
public interface ISpreadsheetFormat
{
    /// <summary>Строки первого листа как текст, вместе с заголовком. Плохой файл — BusinessRuleException.</summary>
    IReadOnlyList<IReadOnlyList<string>> ReadFirstSheet(Stream stream, int maxRows);

    /// <summary>Строки листа по названию (без учёта регистра); листа нет — null (D308, сессия 4).</summary>
    IReadOnlyList<IReadOnlyList<string>>? ReadNamedSheet(Stream stream, string sheetName, int maxRows);

    byte[] Write(IReadOnlyList<SheetData> sheets);
}
