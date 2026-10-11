using KnitErp.Application.Access;
using KnitErp.Application.Audit;
using KnitErp.Application.Catalog;
using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Structure;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KnitErp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddKnitErpApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddScoped<IAccessGuard, AccessGuard>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<UserAccessService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<StructureService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<CounterpartyService>();
        services.AddScoped<OperationReasonService>();
        services.AddScoped<ItemExchangeService>();
        services.AddScoped<CounterpartyExchangeService>();
        services.AddScoped<VatRateService>();
        services.AddScoped<Production.TechCardService>();
        services.AddScoped<Production.ProductionOrderService>();
        services.AddScoped<Production.ProductionFloorService>();
        services.AddScoped<Production.ProductionMaterialService>();
        services.AddScoped<Production.ProductionPrintService>();
        services.AddScoped<Production.ProductionReportService>();
        services.AddScoped<Purchasing.PurchaseService>();
        services.AddScoped<Sales.SalesService>();
        services.AddScoped<Sales.CustomerInvoiceService>();
        services.AddScoped<Sales.SalesStageService>();
        services.AddScoped<Sales.SalesSettingsService>();
        services.AddScoped<Organizations.LegalEntityService>();
        services.AddScoped<Catalog.NomenclatureService>();
        services.AddScoped<Catalog.ModificationService>();
        services.AddScoped<Finance.MoneyService>();
        services.AddScoped<Finance.MoneyOperationService>();
        services.AddScoped<Finance.SettlementStatementService>();
        services.AddScoped<Finance.CashFlowItemService>();
        services.AddScoped<Finance.AccountableService>();
        services.AddScoped<Finance.CashReportsService>();
        services.AddScoped<Integrations.IntegrationService>();
        services.AddScoped<Integrations.WebhookDispatcher>();
        services.AddScoped<Common.RequisitesLookupService>();
        services.AddScoped<Sales.SalesAnalyticsService>();
        services.AddScoped<Taxes.VatInvoiceService>();
        services.AddScoped<Printing.PrintService>();
        services.AddScoped<RecoveryCodeService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<TrustedDeviceService>();
        services.AddScoped<Structure.EmployeeExchangeService>();
        services.AddScoped<Organizations.LaunchReadinessService>();
        // Кэш главного экрана (DashboardService): общий на процесс, ключ — организация, пользователь и его права.
        services.AddMemoryCache();
        services.AddScoped<Organizations.DashboardService>();
        services.AddScoped<OpeningBalanceService>();
        services.AddScoped<StockDocumentService>();
        services.AddScoped<StockBatchService>();
        services.AddScoped<StockBatchBreakdownService>();
        services.AddScoped<KnitErp.Application.Tagging.TagService>();
        services.AddScoped<KnitErp.Application.Collaboration.DiscussionService>();
        services.AddScoped<KnitErp.Application.Exchange.EnterpriseDataExportService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<PeriodService>();
        services.AddScoped<StockReportService>();
        services.AddScoped<Workspace.PersonalToolsService>();
        services.AddScoped<Workspace.WorkdayService>();
        services.AddScoped<Workspace.SupportService>();
        services.AddScoped<Workspace.NotificationService>();
        services.AddScoped<Workspace.QuickSearchService>();
        services.AddScoped<Workspace.AssistantService>();
        services.AddScoped<Security.IntegrityService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<StockService>();
        services.TryAddScoped<Workspace.IUiText, Workspace.RussianUiText>();
        services.TryAddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<SignInService>();
        return services;
    }
}
