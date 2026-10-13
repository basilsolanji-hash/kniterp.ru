namespace KnitErp.Domain.Audit;

/// <summary>
/// Запись журнала аудита. Неизменяема: после вставки не меняется и не удаляется никем (ТЗ §4.8 KA3644, §4.13).
/// </summary>
public sealed class AuditEntry
{
    public const int ActionMaxLength = 100;
    public const int EntityTypeMaxLength = 100;
    public const int EntityIdMaxLength = 64;
    public const int ValueMaxLength = 4000;
    public const int ReasonMaxLength = 500;

    private AuditEntry()
    {
    }

    public long Id { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public long? OrganizationId { get; private set; }
    public long? ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string? EntityId { get; private set; }
    public string? Before { get; private set; }
    public string? After { get; private set; }
    public string? Reason { get; private set; }
    public string? CorrelationId { get; private set; }

    /// <summary>
    /// Подпись записи в цепочке организации: HMAC от подписи предыдущей записи и полей этой. Ставит хранилище при вставке;
    /// изменение, удаление или вставка записи в обход приложения обнаруживается проверкой целостности.
    /// </summary>
    public byte[]? ChainHash { get; private set; }

    /// <summary>Номер записи в цепочке организации (1, 2, 3…): пропуск номера — удалённая запись.</summary>
    public long? ChainSeq { get; private set; }

    public static AuditEntry Create(
        DateTime nowUtc,
        long? organizationId,
        long? actorUserId,
        string action,
        string entityType,
        string? entityId,
        string? before = null,
        string? after = null,
        string? reason = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        return new AuditEntry
        {
            OccurredAtUtc = nowUtc,
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            Action = Cut(action, ActionMaxLength)!,
            EntityType = Cut(entityType, EntityTypeMaxLength)!,
            EntityId = Cut(entityId, EntityIdMaxLength),
            Before = Cut(before, ValueMaxLength),
            After = Cut(after, ValueMaxLength),
            Reason = Cut(reason, ReasonMaxLength),
            CorrelationId = Cut(correlationId, 64),
        };
    }

    private static string? Cut(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Стабильные коды действий журнала аудита.</summary>
public static class AuditActions
{
    public const string AccessDenied = "access.denied";
    public const string RoleGranted = "access.role.granted";
    public const string AdminRoleGranted = "access.admin_role.granted";
    public const string PermissionGranted = "access.permission.granted";
    public const string AssignmentRevoked = "access.assignment.revoked";
    public const string PermissionDenied = "access.permission.denied";
    public const string RoleCreated = "access.role.created";
    public const string RoleChanged = "access.role.changed";
    public const string UserInvited = "access.user.invited";
    public const string UserBlocked = "access.user.blocked";
    public const string UserUnblocked = "access.user.unblocked";
    public const string InvitationIssued = "access.invitation.issued";
    public const string InvitationEmailed = "access.invitation.emailed";
    public const string TwoFactorReset = "access.2fa.reset";
    public const string SignedIn = "auth.signed_in";
    public const string SignInFailed = "auth.sign_in.failed";
    public const string LockedOut = "auth.locked_out";
    public const string SignedOut = "auth.signed_out";
    public const string InvitationAccepted = "auth.invitation.accepted";
    public const string TwoFactorEnabled = "auth.2fa.enabled";
    public const string RecoveryCodesIssued = "auth.recovery_codes.issued";
    public const string RecoveryCodeUsed = "auth.recovery_code.used";
    public const string DeviceTrusted = "auth.device.trusted";
    public const string MoneyOperationCreated = "money.operation.created";
    public const string MoneyOperationCancelled = "money.operation.cancelled";
    public const string PaymentAllocated = "payment.allocated";
    public const string PaymentAllocationRemoved = "payment.allocation.removed";
    public const string ExpenseReportCreated = "expense_report.created";
    public const string ExpenseReportChanged = "expense_report.changed";
    public const string ExpenseReportApproved = "expense_report.approved";
    public const string ExpenseReportCancelled = "expense_report.cancelled";
    public const string StockBatchCreated = "stock.batch.created";
    public const string TagCreated = "tag.created";
    public const string CommentAdded = "discussion.comment.added";
    public const string CommentEdited = "discussion.comment.edited";
    public const string CommentDeleted = "discussion.comment.deleted";
    public const string ExchangeExported = "exchange.exported";
    public const string TagChanged = "tag.changed";
    public const string TagArchived = "tag.archived";
    public const string TagAssigned = "tag.assigned";
    public const string TagRemoved = "tag.removed";
    public const string StockBatchChanged = "stock.batch.changed";
    public const string StockBatchArchived = "stock.batch.archived";
    public const string ApiTokenIssued = "api.token.issued";
    public const string ApiTokenRevoked = "api.token.revoked";
    public const string WebhookCreated = "webhook.created";
    public const string WebhookChanged = "webhook.changed";
    public const string DeviceRevoked = "auth.device.revoked";
    public const string PasswordResetRequested = "auth.password_reset.requested";
    public const string PasswordReset = "auth.password.reset";
    public const string PasswordChanged = "auth.password.changed";
    public const string BankStatementImported = "money.bank_statement.imported";
    public const string SessionsRevoked = "auth.sessions.revoked";

    /// <summary>D123: повторное подтверждение опасного действия (step-up) — успех и неверный пароль или код.</summary>
    public const string StepUpConfirmed = "auth.stepup.confirmed";
    public const string StepUpFailed = "auth.stepup.failed";
    public const string SessionRevoked = "auth.session.revoked";

    /// <summary>D127: изменены ограничения входа участника (до и после).</summary>
    public const string SignInRestrictionsChanged = "access.sign_in_restrictions.changed";

    /// <summary>D127: вход или сессия отклонены ограничением (адрес, время работы).</summary>
    public const string SignInRestricted = "auth.sign_in.restricted";

    /// <summary>D127: сессия закроется через 30 с (вход с другого устройства, конец времени работы).</summary>
    public const string SessionEnding = "auth.session.ending";
    public const string PasswordResetIssued = "access.password_reset.issued";
    public const string EmergencyAccess = "access.emergency";
    public const string DepartmentCreated = "structure.department.created";
    public const string DepartmentChanged = "structure.department.changed";
    public const string DepartmentArchived = "structure.department.archived";
    public const string PositionCreated = "structure.position.created";
    public const string PositionChanged = "structure.position.changed";
    public const string PositionArchived = "structure.position.archived";
    public const string EmployeeHired = "hr.employee.hired";
    public const string EmployeeChanged = "hr.employee.changed";
    public const string EmployeeDismissed = "hr.employee.dismissed";
    public const string EmployeeLinkedToUser = "hr.employee.linked_user";
    public const string EmployeesImported = "hr.employee.imported";
    public const string PersonalDataViewed = "hr.personal_data.viewed";
    public const string PersonalDataChanged = "hr.personal_data.changed";
    public const string PersonalDataPrinted = "hr.personal_data.printed";
    public const string ConsentChanged = "hr.consent.changed";
    public const string ConsentScanAdded = "hr.consent.scan_added";
    public const string ConsentScanViewed = "hr.consent.scan_viewed";

    /// <summary>Загружен документ сотрудника: скан паспорта, регистрации или фотография (D226).</summary>
    public const string EmployeeDocumentAdded = "hr.document.added";

    /// <summary>Просмотр документа сотрудника (D226).</summary>
    public const string EmployeeDocumentViewed = "hr.document.viewed";

    /// <summary>Подписаны документы перед доступом: согласие, правила, обязательство (D227) — или неудачная попытка.</summary>
    public const string AgreementsSigned = "hr.agreement.signed";

    /// <summary>Печать документа сотрудника с его данными (D227).</summary>
    public const string AgreementPrinted = "hr.agreement.printed";

    // D125: анкета сотрудника по ссылке — выдача, письмо, отправка, просмотр, принятие и отказ (без значений ПДн).
    public const string EmployeeDataLinkIssued = "hr.employee_data.link_issued";
    public const string EmployeeDataLinkEmailed = "hr.employee_data.link_emailed";
    public const string EmployeeDataSubmitted = "hr.employee_data.submitted";
    public const string EmployeeDataViewed = "hr.employee_data.viewed";
    public const string EmployeeDataAccepted = "hr.employee_data.accepted";
    public const string EmployeeDataRejected = "hr.employee_data.rejected";
    public const string HrOrderCreated = "hr.order.created";
    public const string HrOrderChanged = "hr.order.changed";
    public const string HrOrderPosted = "hr.order.posted";
    public const string HrOrderCancelled = "hr.order.cancelled";
    public const string StaffingChanged = "hr.staffing.changed";
    public const string ScheduleChanged = "hr.schedule.changed";
    public const string TimesheetCreated = "hr.timesheet.created";
    public const string TimesheetDayChanged = "hr.timesheet.day_changed";
    public const string TimesheetClosed = "hr.timesheet.closed";
    public const string PieceRateAdded = "hr.piece_rate.added";
    public const string PayrollCalculated = "hr.payroll.calculated";
    public const string PayrollApproved = "hr.payroll.approved";
    public const string PayslipViewed = "hr.payslip.viewed";
    public const string PayrollExported = "hr.payroll.exported";

    /// <summary>Выгрузка баллов и показателей сотрудников в Excel (D223, сессия «Сотрудники»: массовая выгрузка ФИО и брака по вине — в журнал).</summary>
    public const string ProductionScoresExported = "production.scores.exported";
    public const string HrSettingsChanged = "hr.settings.changed";
    public const string CatalogCreated = "catalog.created";
    public const string CatalogChanged = "catalog.changed";
    public const string CatalogArchived = "catalog.archived";
    public const string CatalogRestored = "catalog.restored";
    public const string CatalogImported = "catalog.imported";
    public const string ContractCreated = "catalog.contract.created";
    public const string ContractChanged = "catalog.contract.changed";
    public const string ContractFileAdded = "catalog.contract.file_added";
    public const string ContractFileViewed = "catalog.contract.file_viewed";
    public const string StockDocumentCreated = "stock.document.created";
    public const string StockDocumentChanged = "stock.document.changed";
    public const string StockDocumentSubmitted = "stock.document.submitted";
    public const string StockDocumentReturned = "stock.document.returned";
    public const string StockDocumentApproved = "stock.document.approved";
    public const string StockDocumentCancelled = "stock.document.cancelled";
    public const string StockDocumentPosted = "stock.document.posted";
    public const string StockDocumentReversed = "stock.document.reversed";
    public const string TechCardActivated = "production.techcard.activated";
    public const string ProductionOrderCreated = "production.order.created";
    public const string ProductionOrderChanged = "production.order.changed";
    public const string ProductionOrderReleased = "production.order.released";
    public const string ProductionOrderCancelled = "production.order.cancelled";
    public const string ProductionStageMarked = "production.stage.marked";
    public const string ProductionMaterialMoved = "production.material.moved";
    public const string ProductionOutputReleased = "production.output.released";
    public const string ProductionOrderClosed = "production.order.closed";
    public const string ProductionDocumentReversed = "production.document.reversed";
    public const string ProductionShiftTaskAssigned = "production.shift.assigned";
    public const string ProductionShiftTaskCancelled = "production.shift.cancelled";
    public const string ProductionShiftShortfall = "production.shift.shortfall";
    public const string ProductionSettingsChanged = "production.settings.changed";
    public const string PurchaseOrderCreated = "purchase.order.created";
    public const string PurchaseOrderChanged = "purchase.order.changed";
    public const string PurchaseOrderConfirmed = "purchase.order.confirmed";
    public const string PurchaseOrderCancelled = "purchase.order.cancelled";
    public const string SupplierPaymentCreated = "purchase.payment.created";
    public const string SupplierPaymentCancelled = "purchase.payment.cancelled";
    public const string SupplierOpeningBalanceChanged = "purchase.opening_balance.changed"; // D133
    public const string PurchaseSettingsChanged = "purchase.settings.changed"; // D133
    public const string SalesOrderCreated = "sales.order.created";
    public const string SalesOrderChanged = "sales.order.changed";
    public const string SalesOrderConfirmed = "sales.order.confirmed";
    public const string SalesOrderCancelled = "sales.order.cancelled";
    public const string SalesAgreementCreated = "sales.agreement.created";
    public const string SalesAgreementChanged = "sales.agreement.changed";
    public const string SalesCustomerProfileChanged = "sales.customer_profile.changed";
    public const string SalesRoleSettingsChanged = "sales.role_settings.changed";
    public const string SalesPriceOverride = "sales.price.override";
    public const string SalesDiscountChanged = "sales.discount.changed";
    public const string SalesDiscountsReapplied = "sales.discount.reapplied";
    public const string SalesCreditOverride = "sales.credit.override";
    public const string CustomerPaymentCreated = "sales.payment.created";
    public const string CustomerPaymentCancelled = "sales.payment.cancelled";
    public const string CustomerInvoiceIssued = "sales.invoice.issued";
    public const string CustomerInvoiceCancelled = "sales.invoice.cancelled";
    // D1120–D1125 (продажи): оформление документов, письма клиенту, связанные документы.
    public const string SalesDocumentSettingsChanged = "sales.document_settings.changed";
    public const string SalesDocumentImageChanged = "sales.document_image.changed";
    public const string SalesMailSettingsChanged = "sales.mail_settings.changed";
    public const string CustomerMailSettingsChanged = "sales.customer_mail.changed";
    public const string SalesMailQueued = "sales.mail.queued";
    public const string SalesMailChanged = "sales.mail.changed";
    public const string SalesDocumentLinked = "sales.document.linked";
    public const string SalesDocumentUnlinked = "sales.document.unlinked";
    // D1150–D1159, D1108 (продажи): сборка, отгрузка по нескольким заказам, возвраты, возврат денег, коды маркировки.
    public const string SalesPickListCreated = "sales.pick_list.created";
    public const string SalesPickListChanged = "sales.pick_list.changed";
    public const string SalesShipmentGroupCreated = "sales.shipment_group.created";
    public const string SalesShipmentGroupChanged = "sales.shipment_group.changed";
    public const string SalesShippingSettingsChanged = "sales.shipping_settings.changed";
    public const string SalesReturnChanged = "sales.return.changed";
    public const string CustomerRefundCreated = "sales.refund.created";
    public const string CustomerRefundCancelled = "sales.refund.cancelled";
    public const string SalesAdvanceReleased = "sales.advance.released";
    public const string CustomerOpeningBalanceChanged = "sales.opening_balance.changed"; // D1146
    public const string SalesMarkingChanged = "sales.marking.changed";
    // D1107, D1162, D1168–D1170 (продажи, аналитика): план продаж, каналы, отчёты маркетплейсов.
    public const string SalesPlanChanged = "sales.plan.changed";
    public const string SalesChannelChanged = "sales.channel.changed";
    public const string MarketplaceColumnsChanged = "sales.marketplace_columns.changed";
    public const string MarketplaceReportImported = "sales.marketplace_report.imported";
    public const string MarketplaceReportResolved = "sales.marketplace_report.resolved";
    public const string MarketplaceReportCancelled = "sales.marketplace_report.cancelled";
    public const string ReceivedVatInvoiceRegistered = "purchase.vat_invoice.registered";
    public const string ReceivedVatInvoiceCancelled = "purchase.vat_invoice.cancelled";
    public const string PeriodClosed = "period.closed";
    public const string PeriodReopened = "period.reopened";
    public const string SupportTicketCreated = "support.ticket.created";
    public const string SupportTicketChanged = "support.ticket.changed";
    public const string IntegrityChecked = "security.integrity.checked";
    public const string OrganizationCreated = "organization.created";
    public const string OrganizationRequisitesChanged = "organization.requisites.changed";

    // Модуль «Разработка» (D700–D799).
    public const string DevModelCreated = "dev.model.created";
    public const string DevModelChanged = "dev.model.changed";
    public const string DevBriefChanged = "dev.brief.changed";
    public const string DevStageChanged = "dev.stage.changed";
    public const string DevModelCancelled = "dev.model.cancelled";
    public const string DevModelRestored = "dev.model.restored";
    public const string DevFileAdded = "dev.file.added";
    public const string DevFileViewed = "dev.file.viewed";
    public const string DevSettingsChanged = "dev.settings.changed";
    public const string DevTechChanged = "dev.tech.changed";
    public const string DevMachineNoted = "dev.machine.noted";
    public const string DevSampleRecorded = "dev.sample.recorded";
    public const string DevSampleCustody = "dev.sample.custody";
    public const string DevPatternChanged = "dev.pattern.changed";
    // CRM (D600–D699). Телефоны и почты — только маской (D612).
    public const string CrmLeadCreated = "crm.lead.created";
    public const string CrmLeadChanged = "crm.lead.changed";
    public const string CrmLeadQualified = "crm.lead.qualified";
    public const string CrmLeadRejected = "crm.lead.rejected";
    public const string CrmLeadMerged = "crm.lead.merged";
    public const string CrmDealCreated = "crm.deal.created";
    public const string CrmDealChanged = "crm.deal.changed";
    public const string CrmDealStageChanged = "crm.deal.stage_changed";
    public const string CrmDealReassigned = "crm.deal.reassigned";
    public const string CrmDealOrderCreated = "crm.deal.order_created";
    public const string CrmContactCreated = "crm.contact.created";
    public const string CrmContactChanged = "crm.contact.changed";
    public const string CrmSettingsChanged = "crm.settings.changed";
    public const string CrmDealWon = "crm.deal.won";
    public const string CrmDealLost = "crm.deal.lost";
    public const string CrmContactConsent = "crm.contact.consent";
    public const string CrmContactErased = "crm.contact.erased";
    public const string CrmExported = "crm.exported";
    public const string CrmActivityCreated = "crm.activity.created";
    public const string CrmActivityChanged = "crm.activity.changed";
    public const string CrmActivityDone = "crm.activity.done";
    public const string CrmActivityReopened = "crm.activity.reopened";
    public const string CrmOfferCreated = "crm.offer.created";
    public const string CrmEmailSent = "crm.email.sent";
    public const string CrmMailingSent = "crm.mailing.sent";
    public const string CrmUnsubscribed = "crm.contact.unsubscribed";
    public const string CrmImported = "crm.imported";
    public const string CrmPlanChanged = "crm.plan.changed";
    public const string CrmMailingQueued = "crm.mailing.queued";
    public const string CrmMailingScheduled = "crm.mailing.scheduled";
    public const string CrmEmailReceived = "crm.email.received";
    public const string CrmDealPhotoAdded = "crm.deal_photo.added";
    public const string CrmDealPhotoChanged = "crm.deal_photo.changed";
    public const string CrmDealPhotoArchived = "crm.deal_photo.archived";
}
