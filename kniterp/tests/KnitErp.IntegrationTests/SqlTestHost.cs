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

    /// <summary>Письма CRM целиком (HTML, Reply-To, List-Unsubscribe).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<EmailMessage> Messages { get; } = new();

    public Task<bool> SendAsync(string to, string subject, string text, CancellationToken ct = default)
    {
        Sent.Enqueue((to, subject, text));
        return Task.FromResult(true);
    }

    /// <summary>D1123: письма с вложениями (документы продаж).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<EmailMessage> SentMessages { get; } = new();

    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Messages.Enqueue(message);
        SentMessages.Enqueue(message);
        return SendAsync(message.To, message.Subject, message.Text, ct);
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
    public string? UserAgent { get; set; }
    public Guid? SessionId { get; set; }
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

    /// <summary>D122: заголовок User-Agent «браузера» тестов — для названия устройства сессии.</summary>
    public string? UserAgent { get; set; }

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

    /// <summary>
    /// D123: по умолчанию каждый набор сервисов получает свежую отметку повторного подтверждения — как администратор, только что
    /// подтвердивший действие паролем; так опасные действия в прежних тестах проходят. Тесты D123 выключают это и проверяют порядок.
    /// </summary>
    public bool AutoStepUp { get; set; } = true;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, StepUpState> _stepUp = new();

    /// <summary>Отметка повторного подтверждения «сессии» пользователя — общая для всех его наборов сервисов в тест-классе.</summary>
    public StepUpState StepUpStateOf(long? userId) => userId is { } id ? _stepUp.GetOrAdd(id, _ => new StepUpState()) : new StepUpState();

    public StepUpGuard StepUpGuardFor(ICurrentUser user)
    {
        var state = StepUpStateOf(user.UserId);
        if (AutoStepUp && user.UserId is { } id)
        {
            state.Grant(id, Clock.UtcNow);
        }

        return new StepUpGuard(state, user, Clock);
    }

    /// <summary>Набор сервисов от имени пользователя. Каждый вызов — новый DbContext, как отдельный HTTP-запрос.</summary>
    public Services As(long? userId, long? organizationId, IReadOnlyCollection<string>? apiScope = null, Guid? sessionId = null)
    {
        var db = NewDb();
        var user = new TestUser
        {
            UserId = userId, OrganizationId = organizationId, ClientAddress = ClientAddress, UserAgent = UserAgent, SessionId = sessionId, ApiScope = apiScope,
        };
        var guard = new AccessGuard(db, user, Clock);
        var stepUp = StepUpGuardFor(user);
        var access = new UserAccessService(db, guard, user, Clock, stepUp);
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
            new KnitErp.Application.Integrations.IntegrationService(db, guard, user, Clock, new KnitErp.Application.Integrations.IntegrationSettings(), stepUp),
            new KnitErp.Application.Warehousing.StockBatchService(db, guard, user, Clock),
            new KnitErp.Application.Warehousing.StockBatchBreakdownService(db, guard),
            new KnitErp.Application.Tagging.TagService(Factory, user, Clock),
            new KnitErp.Application.Exchange.EnterpriseDataExportService(db, guard, user, Clock),
            new KnitErp.Application.Production.ProductionOrderService(db, guard, user, Clock),
            new KnitErp.Application.Production.ProductionFloorService(db, guard, user, Clock),
            new KnitErp.Application.Production.ProductionMaterialService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock)),
            new KnitErp.Application.Production.ProductionPrintService(db, guard),
            new KnitErp.Application.Production.ProductionReportService(db, guard, Spreadsheet, Clock),
            new KnitErp.Application.Production.MaterialPlanningService(db, guard, Clock,
                new KnitErp.Application.Purchasing.PurchaseService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock))),
            new KnitErp.Application.Collaboration.DiscussionService(Factory, user, Clock),
            new KnitErp.Application.Production.ShopFloorService(db, guard, Clock, new KnitErp.Application.Production.ProductionFloorService(db, guard, user, Clock)),
            new KnitErp.Application.Production.ShiftTaskService(db, guard, user, Clock),
            new KnitErp.Application.Production.ProductionSettingsService(db, guard, user, Clock),
            new KnitErp.Application.Production.ProductionScoreService(db, guard, Clock, user, Spreadsheet),
            new RoleService(db, guard, user, Clock, stepUp),
            new KnitErp.Application.Catalog.BundleService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.ItemTradeService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.RevaluationService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.PackService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.CatalogQueryService(db, guard, new KnitErp.Application.Catalog.BundleService(db, guard, user, Clock)),
            new KnitErp.Application.Catalog.ItemSupplierService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.CatalogSettingsService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.CatalogFeedService(db, guard, user, Clock, new PlainFeedPhotoLinks()),
            new KnitErp.Application.Catalog.ItemCodesService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.LabelService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.GroupDefaultsService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.BulkEditService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.ItemCopyService(db, guard, user, Clock, new CatalogService(db, guard, user, Clock),
                new KnitErp.Application.Catalog.NomenclatureService(db, guard, user, Clock)),
            new KnitErp.Application.Catalog.ValueSetService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.PriceRuleService(db, guard, user, Clock),
            new KnitErp.Application.Catalog.SupplierPriceListService(db, guard, Spreadsheet, user, Clock),
            new AccountSecurityService(db, Hasher, Mail, user, Clock),
            new KnitErp.Application.Finance.BankStatementService(db, guard, user, Clock,
                new KnitErp.Application.Sales.SalesService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock)),
                new KnitErp.Application.Purchasing.PurchaseService(db, guard, user, Clock, new StockDocumentService(db, guard, Spreadsheet, user, Clock))),
            new KnitErp.Application.Sales.SalesAgreementService(db, guard, user, Clock),
            new StepUpService(Factory, Hasher, user, Clock, StepUpStateOf(userId)),
            new KnitErp.Application.Sales.SalesDiscountService(db, guard, user, Clock),
            new KnitErp.Application.Sales.SalesDocumentSettingsService(db, guard, user, Clock, Mail),
            new KnitErp.Application.Sales.SalesDocumentPrintService(db, guard),
            new KnitErp.Application.Sales.SalesOrderDocumentsService(db, guard, user, Clock, Mail),
            new KnitErp.Application.Sales.SalesMailDispatcher(db, Mail, PdfRenderer, Clock),
            new KnitErp.Application.Sales.SalesPriceListService(db, guard, Clock),
            new KnitErp.Application.Sales.SalesReportService(db, guard, user, Clock),
            new KnitErp.Application.Sales.MarketplaceReportService(db, guard, Spreadsheet, user, Clock));
    }

    /// <summary>D1123: PDF документов продаж — настоящий (PDFsharp/MigraDoc), без сети и системных шрифтов.</summary>
    public static readonly KnitErp.Application.Sales.ISalesPdfRenderer PdfRenderer = new KnitErp.Infrastructure.Documents.SalesPdfRenderer();

    public static readonly IPasswordHasher<UserAccount> Hasher = new PasswordHasher<UserAccount>();

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
    KnitErp.Application.Production.ProductionOrderService ProductionOrders,
    KnitErp.Application.Production.ProductionFloorService Floor,
    KnitErp.Application.Production.ProductionMaterialService Materials,
    KnitErp.Application.Production.ProductionPrintService ProductionPrint,
    KnitErp.Application.Production.ProductionReportService ProductionReports,
    KnitErp.Application.Production.MaterialPlanningService Planning,
    KnitErp.Application.Collaboration.DiscussionService Discussions,
    KnitErp.Application.Production.ShopFloorService ShopFloor,
    KnitErp.Application.Production.ShiftTaskService ShiftTasks,
    KnitErp.Application.Production.ProductionSettingsService ProductionSettings,
    KnitErp.Application.Production.ProductionScoreService Scores,
    RoleService Roles,
    KnitErp.Application.Catalog.BundleService Bundles,
    KnitErp.Application.Catalog.ItemTradeService Trade,
    KnitErp.Application.Catalog.RevaluationService Revaluations,
    KnitErp.Application.Catalog.PackService Packs,
    KnitErp.Application.Catalog.CatalogQueryService CatalogQuery,
    KnitErp.Application.Catalog.ItemSupplierService Suppliers,
    KnitErp.Application.Catalog.CatalogSettingsService CatalogSettings,
    KnitErp.Application.Catalog.CatalogFeedService Feeds,
    KnitErp.Application.Catalog.ItemCodesService Codes,
    KnitErp.Application.Catalog.LabelService Labels,
    KnitErp.Application.Catalog.GroupDefaultsService GroupDefaults,
    KnitErp.Application.Catalog.BulkEditService Bulk,
    KnitErp.Application.Catalog.ItemCopyService Copies,
    KnitErp.Application.Catalog.ValueSetService ValueSets,
    KnitErp.Application.Catalog.PriceRuleService PriceRules,
    KnitErp.Application.Catalog.SupplierPriceListService PriceLists,
    AccountSecurityService Account,
    KnitErp.Application.Finance.BankStatementService BankStatements,
    KnitErp.Application.Sales.SalesAgreementService Agreements,
    StepUpService StepUp,
    KnitErp.Application.Sales.SalesDiscountService Discounts,
    KnitErp.Application.Sales.SalesDocumentSettingsService DocumentSettings,
    KnitErp.Application.Sales.SalesDocumentPrintService DocumentPrint,
    KnitErp.Application.Sales.SalesOrderDocumentsService OrderDocuments,
    KnitErp.Application.Sales.SalesMailDispatcher MailDispatcher,
    KnitErp.Application.Sales.SalesPriceListService PriceList,
    KnitErp.Application.Sales.SalesReportService SalesReports,
    KnitErp.Application.Sales.MarketplaceReportService Marketplace) : IAsyncDisposable
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

/// <summary>
/// Ссылки на фото фида для тестов без подписи (D324): «фид.фото» открытым текстом — так тест сам подделывает токен и проверяет,
/// что сервис всё равно не отдаст фото выключенного фида или чужой организации. Подпись — в Web (Data Protection), проверяется e2e.
/// </summary>
public sealed class PlainFeedPhotoLinks : KnitErp.Application.Catalog.IFeedPhotoLinks
{
    public string Token(long feedId, long photoId) => $"{feedId}.{photoId}";

    public bool TryRead(string token, out long feedId, out long photoId)
    {
        photoId = 0;
        var parts = token.Split('.');
        return long.TryParse(parts[0], out feedId) & parts.Length == 2 && long.TryParse(parts[1], out photoId);
    }
}
