using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BaseAutomationWave2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_mail_templates_kind",
                schema: "kniterp",
                table: "sales_mail_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_mail_messages_kind",
                schema: "kniterp",
                table: "sales_mail_messages");

            migrationBuilder.AddColumn<bool>(
                name: "NoPaymentReminders",
                schema: "kniterp",
                table: "customer_mail_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReminderToken",
                schema: "kniterp",
                table: "customer_mail_settings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "automation_settings",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PaymentRemindersEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RemindDaysBefore = table.Column<int>(type: "int", nullable: false),
                    OverdueRepeatDays = table.Column<int>(type: "int", nullable: false),
                    AutoCloseSalesOrders = table.Column<bool>(type: "bit", nullable: false),
                    AutoClosePurchaseOrders = table.Column<bool>(type: "bit", nullable: false),
                    RecurringOperationsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_settings", x => x.OrganizationId);
                    table.CheckConstraint("ck_automation_settings_remind_days", "[RemindDaysBefore] BETWEEN 0 AND 30");
                    table.CheckConstraint("ck_automation_settings_repeat_days", "[OverdueRepeatDays] BETWEEN 0 AND 60");
                    table.ForeignKey(
                        name: "FK_automation_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_automation_settings_updated_by",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "currency_rates",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyCode = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Nominal = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_currency_rates", x => x.Id);
                    table.CheckConstraint("ck_currency_rates_code", "LEN([CurrencyCode]) = 3");
                    table.CheckConstraint("ck_currency_rates_source", "([Source] = 1 AND [OrganizationId] IS NULL) OR ([Source] = 2 AND [OrganizationId] IS NOT NULL)");
                    table.CheckConstraint("ck_currency_rates_value", "[Value] > 0 AND [Nominal] >= 1");
                    table.ForeignKey(
                        name: "FK_currency_rates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_currency_rates_updated_by",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recurring_operations",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    CashFlowItemId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Party = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Basis = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Period = table.Column<byte>(type: "tinyint", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_operations", x => x.Id);
                    table.UniqueConstraint("ak_recurring_operations_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_recurring_operations_amount", "[Amount] > 0");
                    table.CheckConstraint("ck_recurring_operations_day", "([Period] = 2 AND [Day] BETWEEN 1 AND 7) OR ([Period] <> 2 AND [Day] BETWEEN 1 AND 31)");
                    table.CheckConstraint("ck_recurring_operations_end", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("ck_recurring_operations_kind", "[Kind] IN (1, 2)");
                    table.CheckConstraint("ck_recurring_operations_period", "[Period] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_recurring_operations_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operations_account",
                        columns: x => new { x.OrganizationId, x.AccountId },
                        principalSchema: "kniterp",
                        principalTable: "legal_entity_accounts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operations_cash_flow_item",
                        columns: x => new { x.OrganizationId, x.CashFlowItemId },
                        principalSchema: "kniterp",
                        principalTable: "cash_flow_items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operations_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_upd_imports",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SellerInn = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PurchaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ReceiptDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_upd_imports", x => x.Id);
                    table.CheckConstraint("ck_supplier_upd_imports_hash", "LEN([FileHash]) = 64");
                    table.ForeignKey(
                        name: "FK_supplier_upd_imports_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_upd_imports_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_upd_imports_order",
                        columns: x => new { x.OrganizationId, x.PurchaseOrderId },
                        principalSchema: "kniterp",
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_upd_imports_receipt",
                        columns: x => new { x.OrganizationId, x.ReceiptDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recurring_operation_runs",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RecurringOperationId = table.Column<long>(type: "bigint", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    MoneyOperationId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_operation_runs", x => x.Id);
                    table.CheckConstraint("ck_recurring_operation_runs_amount", "[Amount] > 0");
                    table.CheckConstraint("ck_recurring_operation_runs_status", "([Status] = 1 AND [MoneyOperationId] IS NULL AND [DecidedAtUtc] IS NULL) OR ([Status] = 2 AND [MoneyOperationId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL) OR ([Status] = 3 AND [MoneyOperationId] IS NULL AND [DecidedAtUtc] IS NOT NULL AND [SkipReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_recurring_operation_runs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operation_runs_decided_by",
                        column: x => x.DecidedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operation_runs_money",
                        columns: x => new { x.OrganizationId, x.MoneyOperationId },
                        principalSchema: "kniterp",
                        principalTable: "money_operations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_recurring_operation_runs_template",
                        columns: x => new { x.OrganizationId, x.RecurringOperationId },
                        principalSchema: "kniterp",
                        principalTable: "recurring_operations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_mail_templates_kind",
                schema: "kniterp",
                table: "sales_mail_templates",
                sql: "[Kind] IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_mail_messages_kind",
                schema: "kniterp",
                table: "sales_mail_messages",
                sql: "[Kind] IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.CreateIndex(
                name: "ux_customer_mail_settings_reminder_token",
                schema: "kniterp",
                table: "customer_mail_settings",
                column: "ReminderToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_settings_UpdatedByUserId",
                schema: "kniterp",
                table: "automation_settings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_currency_rates_UpdatedByUserId",
                schema: "kniterp",
                table: "currency_rates",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_currency_rates_cbr",
                schema: "kniterp",
                table: "currency_rates",
                columns: new[] { "CurrencyCode", "RateDate" },
                unique: true,
                filter: "[OrganizationId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_currency_rates_manual",
                schema: "kniterp",
                table: "currency_rates",
                columns: new[] { "OrganizationId", "CurrencyCode", "RateDate" },
                unique: true,
                filter: "[OrganizationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_operation_runs_DecidedByUserId",
                schema: "kniterp",
                table: "recurring_operation_runs",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_operation_runs_OrganizationId_MoneyOperationId",
                schema: "kniterp",
                table: "recurring_operation_runs",
                columns: new[] { "OrganizationId", "MoneyOperationId" });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_operation_runs_status",
                schema: "kniterp",
                table: "recurring_operation_runs",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_recurring_operation_runs_due",
                schema: "kniterp",
                table: "recurring_operation_runs",
                columns: new[] { "OrganizationId", "RecurringOperationId", "DueDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_operations_CreatedByUserId",
                schema: "kniterp",
                table: "recurring_operations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_operations_next",
                schema: "kniterp",
                table: "recurring_operations",
                columns: new[] { "OrganizationId", "IsArchived", "NextDate" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_operations_OrganizationId_AccountId",
                schema: "kniterp",
                table: "recurring_operations",
                columns: new[] { "OrganizationId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_operations_OrganizationId_CashFlowItemId",
                schema: "kniterp",
                table: "recurring_operations",
                columns: new[] { "OrganizationId", "CashFlowItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_upd_imports_CreatedByUserId",
                schema: "kniterp",
                table: "supplier_upd_imports",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_upd_imports_OrganizationId_PurchaseOrderId",
                schema: "kniterp",
                table: "supplier_upd_imports",
                columns: new[] { "OrganizationId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_upd_imports_OrganizationId_ReceiptDocumentId",
                schema: "kniterp",
                table: "supplier_upd_imports",
                columns: new[] { "OrganizationId", "ReceiptDocumentId" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_upd_imports_file",
                schema: "kniterp",
                table: "supplier_upd_imports",
                columns: new[] { "OrganizationId", "FileHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "currency_rates",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "recurring_operation_runs",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "supplier_upd_imports",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "recurring_operations",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_mail_templates_kind",
                schema: "kniterp",
                table: "sales_mail_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_mail_messages_kind",
                schema: "kniterp",
                table: "sales_mail_messages");

            migrationBuilder.DropIndex(
                name: "ux_customer_mail_settings_reminder_token",
                schema: "kniterp",
                table: "customer_mail_settings");

            migrationBuilder.DropColumn(
                name: "NoPaymentReminders",
                schema: "kniterp",
                table: "customer_mail_settings");

            migrationBuilder.DropColumn(
                name: "ReminderToken",
                schema: "kniterp",
                table: "customer_mail_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_mail_templates_kind",
                schema: "kniterp",
                table: "sales_mail_templates",
                sql: "[Kind] IN (1, 2, 3, 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_mail_messages_kind",
                schema: "kniterp",
                table: "sales_mail_messages",
                sql: "[Kind] IN (1, 2, 3, 4)");
        }
    }
}
