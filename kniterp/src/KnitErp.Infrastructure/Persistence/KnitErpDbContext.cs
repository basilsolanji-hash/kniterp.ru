using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using KnitErp.Domain.Warehousing;
using KnitErp.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KnitErp.Infrastructure.Persistence;

public sealed class KnitErpDbContext(DbContextOptions<KnitErpDbContext> options) : DbContext(options), IKnitErpDbContext, IDataProtectionKeyContext
{
    public const string Schema = "kniterp";

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<Characteristic> Characteristics => Set<Characteristic>();
    public DbSet<ItemCharacteristicValue> ItemCharacteristicValues => Set<ItemCharacteristicValue>();
    public DbSet<ItemPhoto> ItemPhotos => Set<ItemPhoto>();
    public DbSet<BundleComponent> BundleComponents => Set<BundleComponent>();
    public DbSet<ItemPriceHistory> ItemPriceHistory => Set<ItemPriceHistory>();
    public DbSet<PriceRevaluation> PriceRevaluations => Set<PriceRevaluation>();
    public DbSet<ItemPack> ItemPacks => Set<ItemPack>();
    public DbSet<ItemSupplier> ItemSuppliers => Set<ItemSupplier>();
    public DbSet<KnitErp.Domain.Finance.MoneyOperation> MoneyOperations => Set<KnitErp.Domain.Finance.MoneyOperation>();
    public DbSet<KnitErp.Domain.Integrations.ApiToken> ApiTokens => Set<KnitErp.Domain.Integrations.ApiToken>();
    public DbSet<KnitErp.Domain.Integrations.WebhookSubscription> WebhookSubscriptions => Set<KnitErp.Domain.Integrations.WebhookSubscription>();
    public DbSet<KnitErp.Domain.Integrations.WebhookDelivery> WebhookDeliveries => Set<KnitErp.Domain.Integrations.WebhookDelivery>();
    public DbSet<KnitErp.Domain.Integrations.ApiIdempotencyRecord> ApiIdempotency => Set<KnitErp.Domain.Integrations.ApiIdempotencyRecord>();
    public DbSet<KnitErp.Domain.Finance.CashFlowItem> CashFlowItems => Set<KnitErp.Domain.Finance.CashFlowItem>();
    public DbSet<KnitErp.Domain.Finance.ExpenseReport> ExpenseReports => Set<KnitErp.Domain.Finance.ExpenseReport>();
    public DbSet<KnitErp.Domain.Finance.CustomerPaymentAllocation> CustomerPaymentAllocations => Set<KnitErp.Domain.Finance.CustomerPaymentAllocation>();
    public DbSet<KnitErp.Domain.Finance.SupplierPaymentAllocation> SupplierPaymentAllocations => Set<KnitErp.Domain.Finance.SupplierPaymentAllocation>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<UnitOfMeasure> Units => Set<UnitOfMeasure>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<CounterpartyContract> CounterpartyContracts => Set<CounterpartyContract>();
    public DbSet<CounterpartyContractFile> CounterpartyContractFiles => Set<CounterpartyContractFile>();
    public DbSet<VatRate> VatRates => Set<VatRate>();
    public DbSet<KnitErp.Domain.Production.TechCard> TechCards => Set<KnitErp.Domain.Production.TechCard>();
    public DbSet<KnitErp.Domain.Production.ProductionOrder> ProductionOrders => Set<KnitErp.Domain.Production.ProductionOrder>();
    public DbSet<KnitErp.Domain.Production.ProductionOperation> ProductionOperations => Set<KnitErp.Domain.Production.ProductionOperation>();
    public DbSet<KnitErp.Domain.Production.ShiftTask> ShiftTasks => Set<KnitErp.Domain.Production.ShiftTask>();
    public DbSet<KnitErp.Domain.Production.ProductionSettings> ProductionSettings => Set<KnitErp.Domain.Production.ProductionSettings>();
    public DbSet<KnitErp.Domain.Production.ProductionOrderDocument> ProductionOrderDocuments => Set<KnitErp.Domain.Production.ProductionOrderDocument>();
    public DbSet<KnitErp.Domain.Purchasing.PurchaseOrder> PurchaseOrders => Set<KnitErp.Domain.Purchasing.PurchaseOrder>();
    public DbSet<KnitErp.Domain.Purchasing.SupplierPayment> SupplierPayments => Set<KnitErp.Domain.Purchasing.SupplierPayment>();
    public DbSet<KnitErp.Domain.Sales.SalesOrder> SalesOrders => Set<KnitErp.Domain.Sales.SalesOrder>();
    public DbSet<KnitErp.Domain.Sales.CustomerPayment> CustomerPayments => Set<KnitErp.Domain.Sales.CustomerPayment>();
    public DbSet<KnitErp.Domain.Sales.CustomerInvoice> CustomerInvoices => Set<KnitErp.Domain.Sales.CustomerInvoice>();
    public DbSet<KnitErp.Domain.Sales.SalesOrderStage> SalesOrderStages => Set<KnitErp.Domain.Sales.SalesOrderStage>();
    public DbSet<KnitErp.Domain.Sales.SalesStageRule> SalesStageRules => Set<KnitErp.Domain.Sales.SalesStageRule>();
    public DbSet<Lookup> Lookups => Set<Lookup>();
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<ItemGroup> ItemGroups => Set<ItemGroup>();
    public DbSet<ItemBarcode> ItemBarcodes => Set<ItemBarcode>();
    public DbSet<PriceType> PriceTypes => Set<PriceType>();
    public DbSet<ItemPrice> ItemPrices => Set<ItemPrice>();
    public DbSet<UserCatalog> UserCatalogs => Set<UserCatalog>();
    public DbSet<UserCatalogEntry> UserCatalogEntries => Set<UserCatalogEntry>();
    public DbSet<LegalEntityAccount> LegalEntityAccounts => Set<LegalEntityAccount>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<KnitErp.Domain.Purchasing.ReceivedVatInvoice> ReceivedVatInvoices => Set<KnitErp.Domain.Purchasing.ReceivedVatInvoice>();
    public DbSet<OperationReason> OperationReasons => Set<OperationReason>();
    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
    public DbSet<StockBatch> StockBatches => Set<StockBatch>();
    public DbSet<StockDocumentBatchLine> StockDocumentBatchLines => Set<StockDocumentBatchLine>();
    public DbSet<StockBatchMovement> StockBatchMovements => Set<StockBatchMovement>();
    public DbSet<KnitErp.Domain.Tagging.Tag> Tags => Set<KnitErp.Domain.Tagging.Tag>();
    public DbSet<KnitErp.Domain.Collaboration.DiscussionComment> DiscussionComments => Set<KnitErp.Domain.Collaboration.DiscussionComment>();
    public DbSet<KnitErp.Domain.Collaboration.DiscussionMention> DiscussionMentions => Set<KnitErp.Domain.Collaboration.DiscussionMention>();
    public DbSet<KnitErp.Domain.Tagging.TagLink> TagLinks => Set<KnitErp.Domain.Tagging.TagLink>();
    public DbSet<OpeningBalanceBatchLine> OpeningBalanceBatchLines => Set<OpeningBalanceBatchLine>();
    public DbSet<InventoryBatchCount> InventoryBatchCounts => Set<InventoryBatchCount>();
    public DbSet<InventoryCount> InventoryCounts => Set<InventoryCount>();
    public DbSet<PeriodClosure> PeriodClosures => Set<PeriodClosure>();
    public DbSet<KnitErp.Domain.Workspace.UserToolData> UserToolData => Set<KnitErp.Domain.Workspace.UserToolData>();
    public DbSet<KnitErp.Domain.Workspace.SupportTicket> SupportTickets => Set<KnitErp.Domain.Workspace.SupportTicket>();

    /// <summary>Ключи защиты cookie и антиподделки; XML ключа зашифрован мастер-ключом (MasterKeyXmlEncryptor).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();

    // Модуль «Сотрудники» (D200–D299).
    public DbSet<KnitErp.Domain.Hr.EmployeePersonalData> EmployeePersonalData => Set<KnitErp.Domain.Hr.EmployeePersonalData>();
    public DbSet<KnitErp.Domain.Hr.ConsentScan> ConsentScans => Set<KnitErp.Domain.Hr.ConsentScan>();
    public DbSet<KnitErp.Domain.Hr.StaffingPosition> StaffingPositions => Set<KnitErp.Domain.Hr.StaffingPosition>();
    public DbSet<KnitErp.Domain.Hr.PersonnelOrder> PersonnelOrders => Set<KnitErp.Domain.Hr.PersonnelOrder>();
    public DbSet<KnitErp.Domain.Hr.EmploymentRecord> EmploymentHistory => Set<KnitErp.Domain.Hr.EmploymentRecord>();
    public DbSet<KnitErp.Domain.Hr.WorkSchedule> WorkSchedules => Set<KnitErp.Domain.Hr.WorkSchedule>();
    public DbSet<KnitErp.Domain.Hr.EmployeeSchedule> EmployeeSchedules => Set<KnitErp.Domain.Hr.EmployeeSchedule>();
    public DbSet<KnitErp.Domain.Hr.DepartmentSchedule> DepartmentSchedules => Set<KnitErp.Domain.Hr.DepartmentSchedule>();
    public DbSet<KnitErp.Domain.Hr.Timesheet> Timesheets => Set<KnitErp.Domain.Hr.Timesheet>();
    public DbSet<KnitErp.Domain.Hr.TimesheetDay> TimesheetDays => Set<KnitErp.Domain.Hr.TimesheetDay>();
    public DbSet<KnitErp.Domain.Hr.PieceRate> PieceRates => Set<KnitErp.Domain.Hr.PieceRate>();
    public DbSet<KnitErp.Domain.Hr.PayrollRun> PayrollRuns => Set<KnitErp.Domain.Hr.PayrollRun>();
    public DbSet<KnitErp.Domain.Hr.PayrollLine> PayrollLines => Set<KnitErp.Domain.Hr.PayrollLine>();
    public DbSet<KnitErp.Domain.Hr.HrSettings> HrSettings => Set<KnitErp.Domain.Hr.HrSettings>();

    /// <summary>
    /// Транзакция сценария. Если сценарий вызван из другого (увольнение блокирует учётную запись), он присоединяется к внешней
    /// транзакции: фиксирует и откатывает её только внешний сценарий.
    /// </summary>
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Database.CurrentTransaction is { } outer
            ? Task.FromResult<IDbContextTransaction>(new JoinedTransaction(outer))
            : Database.BeginTransactionAsync(cancellationToken);

    public void DiscardChanges() => ChangeTracker.Clear();

    /// <summary>Участие во внешней транзакции: фиксация и откат — дело внешнего сценария.</summary>
    private sealed class JoinedTransaction(IDbContextTransaction outer) : IDbContextTransaction
    {
        public Guid TransactionId => outer.TransactionId;

        public void Commit()
        {
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Rollback()
        {
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async Task LockWarehousesAsync(IEnumerable<long> warehouseIds, CancellationToken cancellationToken = default)
    {
        // Одинаковый порядок захвата у всех запросов — без взаимных блокировок.
        foreach (var id in warehouseIds.Distinct().Order())
        {
            await LockAsync($"kniterp.stock.warehouse.{id}", cancellationToken);
        }
    }

    public async Task LockAsync(string resource, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Блокировка действует только внутри транзакции.");
        }

        // sp_getapplock при таймауте не бросает ошибку, а возвращает отрицательный код — превращаем его в ошибку.
        await Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @result < 0 THROW 51003, N'Не удалось дождаться блокировки, повторите действие.', 1;
            """, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableAudit();
        if (IntegrityChain.HasChained(this))
        {
            // Подпись цепочки требует блокировки и чтения последней подписи — это делает только асинхронное сохранение.
            throw new InvalidOperationException("Журнал аудита и движения склада сохраняются только через SaveChangesAsync.");
        }

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Новые записи журнала аудита и движений подписываются в той же транзакции, что и сохраняются: если транзакции
    /// нет, она открывается здесь — подпись и вставка не могут разойтись.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableAudit();
        if (!IntegrityChain.HasChained(this))
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        if (Database.CurrentTransaction is not null)
        {
            await IntegrityChain.SealAsync(this, cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var tx = await Database.BeginTransactionAsync(cancellationToken);
        await IntegrityChain.SealAsync(this, cancellationToken);
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return saved;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KnitErpDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Время — UTC в datetime2(3) (ТЗ §5.13, §5.15).
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("datetime2(3)");
    }

    /// <summary>
    /// Журнал аудита (ТЗ §4.8 KA3644) и движения склада только добавляются. Второй рубеж — триггеры в базе.
    /// </summary>
    private void GuardImmutableAudit()
    {
        foreach (var entry in ChangeTracker.Entries<AuditEntry>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Журнал аудита неизменяем: изменение и удаление записей запрещены.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<StockMovement>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Движения склада неизменяемы: исправление — новым документом.");
            }
        }
    }
}
