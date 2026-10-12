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

    /// <summary>D122: заголовок User-Agent обычного HTTP-запроса (страницы входа) — для названия устройства в списке сессий.</summary>
    string? UserAgent => null;

    /// <summary>D122: внешний идентификатор сессии из cookie; null — запрос не из браузерной сессии (API, фоновые задачи, тесты).</summary>
    Guid? SessionId => null;

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

    /// <summary>D122: серверные сессии входа.</summary>
    DbSet<UserSession> UserSessions { get; }
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
    DbSet<ItemGroupDefaults> ItemGroupDefaults { get; }
    DbSet<CharacteristicValueSet> CharacteristicValueSets { get; }
    DbSet<PriceRule> PriceRules { get; }
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
    DbSet<KnitErp.Domain.Hr.EmployeeDataRequest> EmployeeDataRequests { get; }
    DbSet<KnitErp.Domain.Hr.EmployeeDataSubmission> EmployeeDataSubmissions { get; }
    DbSet<KnitErp.Domain.Hr.EmployeeDataSubmissionFile> EmployeeDataSubmissionFiles { get; }

    // Модуль «Разработка» (D700–D799).
    DbSet<KnitErp.Domain.Development.DevModel> DevModels { get; }
    DbSet<KnitErp.Domain.Development.DevModelStageEntry> DevModelStages { get; }
    DbSet<KnitErp.Domain.Development.DevStageSetting> DevStageSettings { get; }
    DbSet<KnitErp.Domain.Development.DevFile> DevFiles { get; }
    DbSet<KnitErp.Domain.Development.DevLookup> DevLookups { get; }
    DbSet<KnitErp.Domain.Development.DevSettings> DevSettings { get; }
    DbSet<KnitErp.Domain.Development.DevCategoryTemplate> DevCategoryTemplates { get; }
    DbSet<KnitErp.Domain.Development.DevMachine> DevMachines { get; }
    DbSet<KnitErp.Domain.Development.DevMachineNote> DevMachineNotes { get; }
    DbSet<KnitErp.Domain.Development.DevModelColor> DevModelColors { get; }
    DbSet<KnitErp.Domain.Development.DevDetail> DevDetails { get; }
    DbSet<KnitErp.Domain.Development.DevThreading> DevThreading { get; }
    DbSet<KnitErp.Domain.Development.DevModelSize> DevModelSizes { get; }
    DbSet<KnitErp.Domain.Development.DevDetailSize> DevDetailSizes { get; }
    DbSet<KnitErp.Domain.Development.DevMeasure> DevMeasures { get; }
    DbSet<KnitErp.Domain.Development.DevMeasureValue> DevMeasureValues { get; }
    DbSet<KnitErp.Domain.Development.DevSample> DevSamples { get; }
    DbSet<KnitErp.Domain.Development.DevSampleDetail> DevSampleDetails { get; }
    DbSet<KnitErp.Domain.Development.DevSampleColorShare> DevSampleColorShares { get; }
    DbSet<KnitErp.Domain.Development.DevSampleMeasure> DevSampleMeasures { get; }
    DbSet<KnitErp.Domain.Development.DevOperationType> DevOperationTypes { get; }
    DbSet<KnitErp.Domain.Development.DevModelOperation> DevModelOperations { get; }
    // CRM (D600–D699).
    DbSet<KnitErp.Domain.Crm.CrmPipeline> CrmPipelines { get; }
    DbSet<KnitErp.Domain.Crm.CrmStage> CrmStages { get; }
    DbSet<KnitErp.Domain.Crm.CrmReference> CrmReferences { get; }
    DbSet<KnitErp.Domain.Crm.CrmLead> CrmLeads { get; }
    DbSet<KnitErp.Domain.Crm.CrmContact> CrmContacts { get; }
    DbSet<KnitErp.Domain.Crm.CrmDeal> CrmDeals { get; }
    DbSet<KnitErp.Domain.Crm.CrmDealStageHistory> CrmDealStageHistory { get; }
    DbSet<KnitErp.Domain.Crm.CrmDealOrder> CrmDealOrders { get; }
    DbSet<KnitErp.Domain.Sales.SalesAgreement> SalesAgreements { get; }
    DbSet<KnitErp.Domain.Sales.CustomerSalesProfile> CustomerSalesProfiles { get; }
    DbSet<KnitErp.Domain.Sales.SalesRoleSettings> SalesRoleSettings { get; }
    DbSet<KnitErp.Domain.Crm.CrmActivity> CrmActivities { get; }
    DbSet<KnitErp.Domain.Crm.CrmOffer> CrmOffers { get; }
    DbSet<KnitErp.Domain.Crm.CrmSettings> CrmSettings { get; }
    DbSet<KnitErp.Domain.Crm.CrmMessengerChannel> CrmMessengerChannels { get; }
    DbSet<KnitErp.Domain.Crm.CrmEmailTemplate> CrmEmailTemplates { get; }
    DbSet<KnitErp.Domain.Crm.CrmMailbox> CrmMailboxes { get; }
    DbSet<KnitErp.Domain.Crm.CrmPlan> CrmPlans { get; }
    DbSet<KnitErp.Domain.Crm.CrmMailing> CrmMailings { get; }
    DbSet<KnitErp.Domain.Crm.CrmExternalLink> CrmExternalLinks { get; }
    DbSet<KnitErp.Domain.Sales.SalesDiscountRule> SalesDiscountRules { get; }
    DbSet<KnitErp.Domain.Sales.SalesDiscountSettings> SalesDiscountSettings { get; }
    DbSet<KnitErp.Domain.Sales.SalesCancelReason> SalesCancelReasons { get; }
    // D1120–D1125 (продажи): оформление документов, письма клиенту, связанные документы.
    DbSet<KnitErp.Domain.Sales.SalesDocumentImage> SalesDocumentImages { get; }
    DbSet<KnitErp.Domain.Sales.SalesDocumentSettings> SalesDocumentSettings { get; }
    DbSet<KnitErp.Domain.Sales.SalesMailTemplate> SalesMailTemplates { get; }
    DbSet<KnitErp.Domain.Sales.SalesMailStage> SalesMailStages { get; }
    DbSet<KnitErp.Domain.Sales.CustomerMailSettings> CustomerMailSettings { get; }
    DbSet<KnitErp.Domain.Sales.SalesMailMessage> SalesMailMessages { get; }
    DbSet<KnitErp.Domain.Sales.SalesMailAttachment> SalesMailAttachments { get; }
    DbSet<KnitErp.Domain.Sales.SalesDocumentLink> SalesDocumentLinks { get; }
    DbSet<KnitErp.Domain.Sales.SalesCreditApproval> SalesCreditApprovals { get; }
    // D1107, D1162, D1168 (продажи, аналитика): план продаж, свойства каналов, отчёты маркетплейсов.
    DbSet<KnitErp.Domain.Sales.SalesPlan> SalesPlans { get; }
    DbSet<KnitErp.Domain.Sales.SalesChannelSettings> SalesChannelSettings { get; }
    DbSet<KnitErp.Domain.Sales.MarketplaceReport> MarketplaceReports { get; }
    DbSet<KnitErp.Domain.Sales.MarketplaceReportLine> MarketplaceReportLines { get; }
    DbSet<KnitErp.Domain.Sales.MarketplaceColumnMap> MarketplaceColumnMaps { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Забыть несохранённые изменения после отказа сценария: у вкладки Blazor один контекст, и без этого изменённая в памяти
    /// сущность (например, «проведённый» документ, проведение которого отклонено) ушла бы в базу со следующим сохранением.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// Перечитать отслеживаемую сущность из базы (значения и rowversion): у вкладки Blazor контекст живёт долго, и запрос с отслеживанием
    /// вернул бы копию, загруженную раньше (например, счётчик автокодов, который уже сдвинул другой пользователь).
    /// </summary>
    Task ReloadAsync(object entity, CancellationToken cancellationToken = default);

    /// <summary>Точка сохранения внутри текущей транзакции: часть работы (например, строку загрузки) можно откатить, не теряя остального.</summary>
    Task SavepointAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Откат к точке сохранения (изменения после неё в базе отменяются; отслеживание сбрасывает вызывающий — DiscardChanges).</summary>
    Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// D1168 (сессия «Продажи»): первый лист широкой выгрузки чужой системы — отчёт Wildberries содержит около 80 столбцов,
    /// больше обычного предела шаблонов. Те же проверки архива; пределы столбцов и ячеек — шире.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> ReadWideSheet(Stream stream, int maxRows) => ReadFirstSheet(stream, maxRows);
}
