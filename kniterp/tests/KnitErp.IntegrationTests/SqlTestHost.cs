using KnitErp.Application.Access;
using KnitErp.Application.Audit;
using KnitErp.Application.Catalog;
using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Structure;
using KnitErp.Application.Warehousing;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Тест пропускается, если не задан SQL Server (переменная KNITERP_TEST_SQL). В CI он задан всегда.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestHost.EnvVar)))
        {
            Skip = $"Не задан {SqlTestHost.EnvVar}: интеграционные тесты SQL Server пропущены.";
        }
    }
}

public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
}

public sealed class FakeEmailSender : IEmailSender
{
    public bool IsConfigured { get; set; } = true;
    public string? PublicUrl => "https://erp.test";
    public System.Collections.Concurrent.ConcurrentQueue<(string To, string Subject, string Text)> Sent { get; } = new();

    public Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct = default)
    {
        Sent.Enqueue((to, subject, text));
        return Task.FromResult(true);
    }

    /// <summary>Письмо уходит в фоне — ждём его не дольше 5 секунд.</summary>
    public async Task<(string To, string Subject, string Text)?> WaitForAsync(string to)
    {
        for (var i = 0; i < 50; i++)
        {
            var hit = Sent.LastOrDefault(m => m.To == to);
            if (hit.To is not null)
            {
                return hit;
            }

            await Task.Delay(100);
        }

        return null;
    }
}

public sealed class TestUser : ICurrentUser
{
    public long? UserId { get; set; }
    public long? OrganizationId { get; set; }
    public string? CorrelationId => "test";
    public string? ClientAddress { get; set; }
    public IReadOnlyCollection<string>? ApiScope { get; set; }
    public long? ApiTokenId { get; set; }
}

/// <summary>Отдельная база на каждый тест-класс; создаётся с нуля теми же миграциями, что и рабочая, и удаляется после.</summary>
public sealed class SqlTestHost : IAsyncLifetime
{
    public const string EnvVar = "KNITERP_TEST_SQL";

    private readonly string _connectionString;

    public SqlTestHost()
    {
        // Мастер-ключ тестов — случайный на каждый запуск: ключей в репозитории нет (CLAUDE.md).
        if (!KnitErp.Infrastructure.Security.KeyRing.IsConfigured)
        {
            KnitErp.Infrastructure.Security.KeyRing.Configure(
                new KnitErp.Infrastructure.Security.KeyRing(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        }

        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvVar) ?? "Server=.")
        {
            InitialCatalog = "kniterp_test_" + Guid.NewGuid().ToString("N")[..12],
        };
        _connectionString = builder.ConnectionString;
    }

    public TestClock Clock { get; } = new();

    /// <summary>Почта тестов: письма складываются в память.</summary>
    public FakeEmailSender Mail { get; } = new();

    /// <summary>Адрес клиента для журнала входов (как у HTTP-запроса страницы входа).</summary>
    public string? ClientAddress { get; set; }

    /// <summary>Модель ИИ-помощника для тестов: по умолчанию не подключена.</summary>
    public IAssistantModel AssistantModel { get; set; } = new DisabledAssistantModel();

    public IUiText UiText { get; set; } = new RussianUiText();

    /// <summary>Кэш главного экрана — общий для всех сессий тест-класса, как в приложении общий на процесс.</summary>
    public Microsoft.Extensions.Caching.Memory.IMemoryCache DashboardCache { get; } =
        new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    /// <summary>Фабрика контекстов для инструментов панели — как в приложении, свой контекст на операцию.</summary>
    public IKnitErpDbContextFactory Factory => new TestDbFactory(this);

    private sealed class TestDbFactory(SqlTestHost host) : IKnitErpDbContextFactory
    {
        public IKnitErpDbContext Create() => host.NewDb();
    }

    public KnitErpDbContext NewDb() =>
        new(new DbContextOptionsBuilder<KnitErpDbContext>()
            .UseSqlServer(_connectionString, KnitErp.Infrastructure.DependencyInjection.ConfigureSqlServer).Options);

    /// <summary>Контекст с перехватчиками EF — например, чтобы считать SQL-команды.</summary>
    public KnitErpDbContext NewDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<KnitErpDbContext>()
            .UseSqlServer(_connectionString, KnitErp.Infrastructure.DependencyInjection.ConfigureSqlServer).AddInterceptors(interceptors).Options);

    /// <summary>Набор сервисов от имени пользователя. Каждый вызов — новый DbContext, как отдельный HTTP-запрос.</summary>
    public Services As(long? userId, long? organizationId, IReadOnlyCollection<string>? apiScope = null)
    {
        var db = NewDb();
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = ClientAddress, ApiScope = apiScope };
        var guard = new AccessGuard(db, user, Clock);
        var access = new UserAccessService(db, guard, user, Clock);
        return new Services(db,
            new OrganizationService(db, guard, user, Clock),
            access,
            new AuditQueryService(db, guard),
            new SignInService(db, Hasher, user, Clock),
            new StructureService(db, guard, user, Clock),
            new EmployeeService(db, guard, access, user, Clock),
            new CatalogService(db, guard, user, Clock),
            new WarehouseService(db, guard, user, Clock),
            new CounterpartyService(db, guard, user, Clock),
            new OperationReasonService(db, guard, user, Clock),
            new ItemExchangeService(db, guard, Spreadsheet, user, Clock),
            new OpeningBalanceService(db, guard, Spreadsheet, user, Clock),
            new StockService(db, guard),
            new StockDocumentService(db, guard, Spreadsheet, user, Clock),
            new InventoryService(db, guard, Spreadsheet, user, Clock),
            new PeriodService(db, guard, user, Clock),
            new StockReportService(db, guard),
            new PersonalToolsService(Factory, user, Clock),
            new SupportService(Factory, user, Clock),
            new NotificationService(Factory, new PersonalToolsService(Factory, user, Clock), user, Clock),
            new QuickSearchService(Factory, user, Clock, UiText),
            new AssistantService(AssistantModel, Factory, user, Clock, UiText),
            new KnitErp.Application.Security.IntegrityService(db, guard, new KnitErp.Infrastructure.Security.IntegrityVerifier(Factory), user, Clock),
            new ReconciliationService(db, guard),
            new CounterpartyExchangeService(db, guard, Spreadsheet, user, Clock),
            new EmployeeExchangeService(db, guard, Spreadsheet, user, Clock),
            new LaunchReadinessService(db, guard, Clock),
            new RecoveryCodeService(db, user, Clock),
            new VatRateService(db, guard, user, Clock),
            new KnitErp.Application.Production.TechCardService(db, guard, user, Clock),
            new DashboardService(db, guard, Clock, new LaunchReadinessService(db, guard, Clock), DashboardCache),
            new KnitErp.Application.Purchasing.PurchaseService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock)),
            new KnitErp.Application.Sales.SalesService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock)),
            new KnitErp.Application.Sales.CustomerInvoiceService(db, guard, user, Clock),
            new KnitErp.Application.Printing.PrintService(db, guard),
            new KnitErp.Application.Sales.SalesAnalyticsService(db, guard),
            new KnitErp.Application.Taxes.VatInvoiceService(db, guard, user, Clock),
            new KnitErp.Application.Sales.SalesStageService(db, guard, user, Clock),
            new KnitErp.Application.Sales.SalesSettingsService(db, guard, user, Clock),
            new KnitErp.Application.Organizations.LegalEntityService(db, guard, user, Clock),
            new KnitErp.Application.Common.RequisitesLookupService(new FakeRequisitesLookup(), guard),
            new KnitErp.Application.Catalog.NomenclatureService(db, guard, user, Clock),
            new KnitErp.Application.Finance.MoneyService(db, guard),
            new PasswordResetService(db, Hasher, Mail, user, Clock),
            new TrustedDeviceService(db, user, Clock),
            new KnitErp.Application.Catalog.ModificationService(db, guard, user, Clock),
            new KnitErp.Application.Finance.MoneyOperationService(db, guard, user, Clock),
            new KnitErp.Application.Finance.SettlementStatementService(db, guard),
            new KnitErp.Application.Finance.CashFlowItemService(db, guard, user, Clock),
            new KnitErp.Application.Finance.AccountableService(db, guard, user, Clock),
            new KnitErp.Application.Finance.CashReportsService(db, guard),
            new KnitErp.Application.Integrations.IntegrationService(db, guard, user, Clock, new KnitErp.Application.Integrations.IntegrationSettings()),
            new KnitErp.Application.Warehousing.StockBatchService(db, guard, user, Clock),
            new KnitErp.Application.Warehousing.StockBatchBreakdownService(db, guard),
            new KnitErp.Application.Tagging.TagService(Factory, user, Clock),
            new KnitErp.Application.Exchange.EnterpriseDataExportService(db, guard, user, Clock),
            new KnitErp.Application.Collaboration.DiscussionService(Factory, user, Clock),
            new RoleService(db, guard, user, Clock));
    }

    private static readonly IPasswordHasher<UserAccount> Hasher = new PasswordHasher<UserAccount>();

    public static readonly ISpreadsheetFormat Spreadsheet = new KnitErp.Infrastructure.Spreadsheets.ClosedXmlSpreadsheet();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            return;
        }

        await using var db = NewDb();
        await KnitErp.Infrastructure.DependencyInjection.MigrateDatabaseAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            return;
        }

        await using var db = NewDb();
        SqlConnection.ClearAllPools();
        await db.Database.EnsureDeletedAsync();
    }
}

public sealed record Services(
    KnitErpDbContext Db,
    OrganizationService Organizations,
    UserAccessService Access,
    AuditQueryService Audit,
    SignInService SignIn,
    StructureService Structure,
    EmployeeService Employees,
    CatalogService Catalog,
    WarehouseService Warehouses,
    CounterpartyService Counterparties,
    OperationReasonService Reasons,
    ItemExchangeService ItemExchange,
    OpeningBalanceService OpeningBalances,
    StockService Stock,
    StockDocumentService Documents,
    InventoryService Inventory,
    PeriodService Period,
    StockReportService Reports,
    PersonalToolsService Personal,
    SupportService Support,
    NotificationService Notifications,
    QuickSearchService Search,
    AssistantService Assistant,
    KnitErp.Application.Security.IntegrityService Integrity,
    ReconciliationService Reconciliation,
    CounterpartyExchangeService CounterpartyExchange,
    EmployeeExchangeService EmployeeExchange,
    LaunchReadinessService Readiness,
    RecoveryCodeService Recovery,
    VatRateService VatRates,
    KnitErp.Application.Production.TechCardService TechCards,
    DashboardService Dashboard,
    KnitErp.Application.Purchasing.PurchaseService Purchases,
    KnitErp.Application.Sales.SalesService Sales,
    KnitErp.Application.Sales.CustomerInvoiceService Invoices,
    KnitErp.Application.Printing.PrintService Print,
    KnitErp.Application.Sales.SalesAnalyticsService Analytics,
    KnitErp.Application.Taxes.VatInvoiceService VatInvoices,
    KnitErp.Application.Sales.SalesStageService Stages,
    KnitErp.Application.Sales.SalesSettingsService SalesSettings,
    KnitErp.Application.Organizations.LegalEntityService LegalEntities,
    KnitErp.Application.Common.RequisitesLookupService Requisites,
    KnitErp.Application.Catalog.NomenclatureService Nomenclature,
    KnitErp.Application.Finance.MoneyService Money,
    PasswordResetService PasswordReset,
    TrustedDeviceService Devices,
    KnitErp.Application.Catalog.ModificationService Modifications,
    KnitErp.Application.Finance.MoneyOperationService MoneyOperations,
    KnitErp.Application.Finance.SettlementStatementService Settlements,
    KnitErp.Application.Finance.CashFlowItemService CashFlowItems,
    KnitErp.Application.Finance.AccountableService Accountables,
    KnitErp.Application.Finance.CashReportsService CashReports,
    KnitErp.Application.Integrations.IntegrationService Integrations,
    KnitErp.Application.Warehousing.StockBatchService Batches,
    KnitErp.Application.Warehousing.StockBatchBreakdownService Breakdowns,
    KnitErp.Application.Tagging.TagService Tags,
    KnitErp.Application.Exchange.EnterpriseDataExportService Exchange,
    KnitErp.Application.Collaboration.DiscussionService Discussions,
    RoleService Roles) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>Справочник реквизитов для тестов: без сети, один известный ИНН (данные вымышленные).</summary>
public sealed class FakeRequisitesLookup : KnitErp.Application.Common.IRequisitesLookup
{
    public bool IsConfigured => true;

    public Task<KnitErp.Application.Common.CompanyRequisites?> FindByInnAsync(string inn, CancellationToken ct = default) =>
        Task.FromResult(inn == "7707083893"
            ? new KnitErp.Application.Common.CompanyRequisites(false, "ООО «ТЕСТОВАЯ ФАБРИКА»", "ООО «ТФ»", inn, "773601001", "1027700132195",
                "г. Москва, ул. Тестовая, д. 1", "ГЕНЕРАЛЬНЫЙ ДИРЕКТОР", "Тестов Тест Тестович", true, "ACTIVE")
            : null);
}
