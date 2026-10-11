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
    public const string UserInvited = "access.user.invited";
    public const string UserBlocked = "access.user.blocked";
    public const string UserUnblocked = "access.user.unblocked";
    public const string InvitationIssued = "access.invitation.issued";
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
    public const string CatalogCreated = "catalog.created";
    public const string CatalogChanged = "catalog.changed";
    public const string CatalogArchived = "catalog.archived";
    public const string CatalogRestored = "catalog.restored";
    public const string CatalogImported = "catalog.imported";
    public const string StockDocumentCreated = "stock.document.created";
    public const string StockDocumentChanged = "stock.document.changed";
    public const string StockDocumentSubmitted = "stock.document.submitted";
    public const string StockDocumentReturned = "stock.document.returned";
    public const string StockDocumentApproved = "stock.document.approved";
    public const string StockDocumentCancelled = "stock.document.cancelled";
    public const string StockDocumentPosted = "stock.document.posted";
    public const string StockDocumentReversed = "stock.document.reversed";
    public const string TechCardActivated = "production.techcard.activated";
    public const string PurchaseOrderCreated = "purchase.order.created";
    public const string PurchaseOrderChanged = "purchase.order.changed";
    public const string PurchaseOrderConfirmed = "purchase.order.confirmed";
    public const string PurchaseOrderCancelled = "purchase.order.cancelled";
    public const string SupplierPaymentCreated = "purchase.payment.created";
    public const string SupplierPaymentCancelled = "purchase.payment.cancelled";
    public const string SalesOrderCreated = "sales.order.created";
    public const string SalesOrderChanged = "sales.order.changed";
    public const string SalesOrderConfirmed = "sales.order.confirmed";
    public const string SalesOrderCancelled = "sales.order.cancelled";
    public const string CustomerPaymentCreated = "sales.payment.created";
    public const string CustomerPaymentCancelled = "sales.payment.cancelled";
    public const string CustomerInvoiceIssued = "sales.invoice.issued";
    public const string CustomerInvoiceCancelled = "sales.invoice.cancelled";
    public const string ReceivedVatInvoiceRegistered = "purchase.vat_invoice.registered";
    public const string ReceivedVatInvoiceCancelled = "purchase.vat_invoice.cancelled";
    public const string PeriodClosed = "period.closed";
    public const string PeriodReopened = "period.reopened";
    public const string SupportTicketCreated = "support.ticket.created";
    public const string SupportTicketChanged = "support.ticket.changed";
    public const string IntegrityChecked = "security.integrity.checked";
    public const string OrganizationCreated = "organization.created";
    public const string OrganizationRequisitesChanged = "organization.requisites.changed";
}
