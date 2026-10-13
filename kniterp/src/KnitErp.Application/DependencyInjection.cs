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
        services.AddScoped<SignInRestrictionService>(); // D127
        services.AddScoped<InvitationMailer>();
        services.AddScoped<RoleService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<StructureService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<KnitErp.Application.Hr.PersonalDataService>();
        services.AddScoped<KnitErp.Application.Hr.StaffingService>();
        services.AddScoped<KnitErp.Application.Hr.PersonnelOrderService>();
        services.AddScoped<KnitErp.Application.Hr.TimesheetService>();
        services.AddScoped<KnitErp.Application.Hr.LeaveStatusSync>();
        services.AddScoped<KnitErp.Application.Authentication.SessionCleanup>();
        services.AddScoped<KnitErp.Application.Hr.PayrollService>();
        services.AddScoped<KnitErp.Application.Hr.HrSettingsService>();
        services.AddScoped<KnitErp.Application.Hr.EmployeeAgreementService>(); // D227: документы перед доступом
        services.AddScoped<KnitErp.Application.Hr.EmployeeDataRequestService>(); // D125: анкета сотрудника по ссылке
        services.AddScoped<KnitErp.Application.Hr.PayrollEnterpriseDataExport>();
        // Граница с производством: настоящая выработка регистрируется модулем производства и заменяет заглушку.
        services.TryAddScoped<KnitErp.Application.Hr.IWorkOutputSource, KnitErp.Application.Hr.EmptyWorkOutputSource>();
        services.AddScoped<CatalogService>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<CounterpartyService>();
        services.AddScoped<CounterpartyCardService>();
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
        services.AddScoped<Production.SalesProductionService>();
        services.AddScoped<Production.MaterialPlanningService>();
        services.AddScoped<Production.ShopFloorService>();
        services.AddScoped<Production.ShiftTaskService>();
        services.AddScoped<Production.ProductionSettingsService>();
        services.AddScoped<Production.ProductionScoreService>();
        // Производство на главном экране (контракт D111 базовой ветки).
        services.AddScoped<Workspace.IProductionDashboardSource, Production.ProductionDashboardSource>();
        services.AddScoped<Workspace.IShiftStaffingSource, Hr.ShiftStaffingSource>(); // D221: «смена сегодня» на главном
        // Модуль «Разработка» (D700–D799).
        services.AddScoped<Development.DevModelService>();
        services.AddScoped<Development.DevSettingsService>();
        services.AddScoped<Development.DevExchangeService>();
        services.AddScoped<Development.DevTechService>();
        services.AddScoped<Development.DevShopService>();
        services.AddScoped<Development.DevHandoffService>();
        services.AddScoped<Development.DevYarnService>();
        services.AddScoped<Development.DevCostService>();
        Development.DevObjects.Register();
        // Выработка для «Сотрудников» (ТЗ §6): заменяет их заглушку EmptyWorkOutputSource, зарегистрированную через TryAdd.
        services.AddScoped<Hr.IWorkOutputSource, Production.ProductionWorkOutputSource>();
        services.AddScoped<Purchasing.PurchaseService>();
        services.AddScoped<Sales.SalesService>();
        services.AddScoped<Sales.CustomerInvoiceService>();
        services.AddScoped<Sales.SalesStageService>();
        services.AddScoped<Sales.SalesAgreementService>();
        services.AddScoped<Sales.SalesDiscountService>();
        services.AddScoped<Sales.SalesSettingsService>();
        // D1120–D1125 (продажи): оформление документов, письма клиенту, связанные документы.
        services.AddScoped<Sales.SalesDocumentSettingsService>();
        services.AddScoped<Sales.SalesDocumentPrintService>();
        services.AddScoped<Sales.SalesOrderDocumentsService>();
        services.AddScoped<Sales.SalesMailDispatcher>();
        // CRM (D600–D699).
        services.AddScoped<Crm.CrmSettingsService>();
        services.AddScoped<Crm.CrmLeadService>();
        services.AddScoped<Crm.CrmDealService>();
        services.AddScoped<Crm.CrmContactService>();
        services.AddScoped<Crm.CrmActivityService>();
        services.AddScoped<Crm.CrmOfferService>();
        services.AddScoped<Crm.CrmReportService>();
        services.AddScoped<Crm.CrmIntakeService>();
        services.AddScoped<Crm.CrmMailingService>();
        services.AddScoped<Crm.CrmImportService>();
        services.AddScoped<Crm.CrmMessengerService>();
        services.AddScoped<Crm.CrmDealSalesService>();
        services.AddScoped<Common.ISalesOrderEvents, Crm.CrmSalesOrderEvents>(); // D683: «заказ подтверждён» → сделка выиграна
        services.AddScoped<Crm.CrmEmailTemplateService>();
        services.AddScoped<Crm.CrmMailboxService>();
        services.AddScoped<Crm.ICrmDashboardSource, Crm.CrmDashboardSource>(); // ТЗ CRM §6: блок главного экрана подключает база
        services.AddScoped<Organizations.LegalEntityService>();
        services.AddScoped<Catalog.NomenclatureService>();
        services.AddScoped<Catalog.ModificationService>();
        services.AddScoped<Catalog.BundleService>(); // D302 (сессия 4)
        services.AddScoped<Catalog.ItemTradeService>(); // D305 (сессия 4)
        services.AddScoped<Catalog.RevaluationService>(); // D306 (сессия 4)
        services.AddScoped<Catalog.PackService>(); // D307 (сессия 4)
        services.AddScoped<Catalog.CatalogQueryService>(); // D307 (сессия 4)
        services.AddScoped<Catalog.ItemSupplierService>(); // D309 (сессия 4)
        services.AddScoped<Exchange.MoySklad.MoySkladImportService>(); // D310 (сессия 4)
        services.AddScoped<Catalog.CatalogSettingsService>(); // D312 (сессия 4)
        services.AddScoped<Catalog.CatalogFeedService>(); // D314 (сессия 4)
        services.AddScoped<Catalog.ItemCodesService>(); // D315 (сессия 4)
        services.AddScoped<Catalog.LabelService>(); // D316 (сессия 4)
        services.AddScoped<Catalog.GroupDefaultsService>(); // D317 (сессия 4)
        services.AddScoped<Catalog.BulkEditService>(); // D318 (сессия 4)
        services.AddScoped<Catalog.ItemCopyService>(); // D319 (сессия 4)
        services.AddScoped<Catalog.ValueSetService>(); // D321 (сессия 4)
        services.AddScoped<Catalog.PriceRuleService>(); // D322 (сессия 4)
        services.AddScoped<Catalog.SupplierPriceListService>(); // D323 (сессия 4)
        services.AddScoped<Finance.MoneyService>();
        services.AddScoped<Finance.MoneyOperationService>();
        services.AddScoped<Finance.BankStatementService>();
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
        services.AddScoped<AccountSecurityService>();
        // D123: отметка повторного подтверждения — на область DI (в Blazor — цепь вкладки), не в cookie и не у клиента.
        services.AddScoped<StepUpState>();
        services.AddScoped<StepUpGuard>();
        services.AddScoped<StepUpService>();
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
        // D115: PBKDF2-HMAC-SHA512, 210 000 итераций (рекомендация OWASP); прежние хеши (100 000) пересчитываются при входе.
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);
        services.TryAddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<SignInService>();
        return services;
    }
}
