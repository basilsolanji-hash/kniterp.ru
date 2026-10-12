using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_column_maps",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Marketplace = table.Column<byte>(type: "tinyint", nullable: false),
                    Field = table.Column<byte>(type: "tinyint", nullable: false),
                    Header = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_column_maps", x => x.Id);
                    table.CheckConstraint("ck_marketplace_column_maps_field", "[Field] BETWEEN 1 AND 14");
                    table.CheckConstraint("ck_marketplace_column_maps_marketplace", "[Marketplace] IN (1, 2, 9)");
                    table.ForeignKey(
                        name: "FK_marketplace_column_maps_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_reports",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    Marketplace = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    SalesQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    SalesAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ReturnsQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReturnsAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Logistics = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Payout = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    UnmatchedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_reports", x => x.Id);
                    table.UniqueConstraint("ak_marketplace_reports_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_marketplace_reports_cancelled", "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] <> 9 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelReason] IS NULL)");
                    table.CheckConstraint("ck_marketplace_reports_counts", "[LineCount] > 0 AND [UnmatchedCount] BETWEEN 0 AND [LineCount]");
                    table.CheckConstraint("ck_marketplace_reports_fingerprint", "LEN([Fingerprint]) = 64");
                    table.CheckConstraint("ck_marketplace_reports_marketplace", "[Marketplace] IN (1, 2, 9)");
                    table.CheckConstraint("ck_marketplace_reports_period", "[PeriodTo] >= [PeriodFrom]");
                    table.CheckConstraint("ck_marketplace_reports_status", "[Status] IN (1, 9)");
                    table.ForeignKey(
                        name: "FK_marketplace_reports_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_reports_cancelled_by",
                        column: x => x.CancelledByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_reports_channel",
                        columns: x => new { x.OrganizationId, x.ChannelId },
                        principalSchema: "kniterp",
                        principalTable: "lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_reports_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_channel_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    Marketplace = table.Column<byte>(type: "tinyint", nullable: true),
                    Scheme = table.Column<byte>(type: "tinyint", nullable: true),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_channel_settings", x => x.Id);
                    table.CheckConstraint("ck_sales_channel_settings_marketplace", "[Marketplace] IS NULL OR [Marketplace] IN (1, 2, 9)");
                    table.CheckConstraint("ck_sales_channel_settings_scheme", "([Scheme] IS NULL OR [Scheme] IN (1, 2)) AND ([Marketplace] IS NOT NULL OR ([Scheme] IS NULL AND [CounterpartyId] IS NULL))");
                    table.ForeignKey(
                        name: "FK_sales_channel_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_channel_settings_channel",
                        columns: x => new { x.OrganizationId, x.ChannelId },
                        principalSchema: "kniterp",
                        principalTable: "lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_channel_settings_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_plans",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    ManagerUserId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_plans", x => x.Id);
                    table.CheckConstraint("ck_sales_plans_amount", "[Amount] >= 0");
                    table.CheckConstraint("ck_sales_plans_month", "DAY([Month]) = 1");
                    table.ForeignKey(
                        name: "FK_sales_plans_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_plans_manager",
                        column: x => x.ManagerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_plans_updated_by",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_report_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Article = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SaleDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Logistics = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Payout = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedBy = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_report_lines", x => x.Id);
                    table.CheckConstraint("ck_marketplace_report_lines_kind", "[Kind] IN (1, 2, 3)");
                    table.CheckConstraint("ck_marketplace_report_lines_match", "[MatchedBy] IN (0, 1, 2, 3) AND (([ItemId] IS NULL) = ([MatchedBy] = 0))");
                    table.ForeignKey(
                        name: "fk_marketplace_report_lines_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_report_lines_report",
                        columns: x => new { x.OrganizationId, x.ReportId },
                        principalSchema: "kniterp",
                        principalTable: "marketplace_reports",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_sales_analytics",
                schema: "kniterp",
                table: "stock_documents",
                columns: new[] { "OrganizationId", "Kind", "Status", "DocumentDate", "SalesOrderId" },
                filter: "[SalesOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_responsible_date",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "ResponsibleUserId", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_status_ship_date",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "Status", "ShipDate" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_column_maps_field",
                schema: "kniterp",
                table: "marketplace_column_maps",
                columns: new[] { "OrganizationId", "Marketplace", "Field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_report_lines_item",
                schema: "kniterp",
                table: "marketplace_report_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_report_lines_report",
                schema: "kniterp",
                table: "marketplace_report_lines",
                columns: new[] { "OrganizationId", "ReportId", "RowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_reports_CancelledByUserId",
                schema: "kniterp",
                table: "marketplace_reports",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_reports_CreatedByUserId",
                schema: "kniterp",
                table: "marketplace_reports",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_reports_OrganizationId_ChannelId",
                schema: "kniterp",
                table: "marketplace_reports",
                columns: new[] { "OrganizationId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_reports_period",
                schema: "kniterp",
                table: "marketplace_reports",
                columns: new[] { "OrganizationId", "PeriodTo" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_reports_fingerprint",
                schema: "kniterp",
                table: "marketplace_reports",
                columns: new[] { "OrganizationId", "Fingerprint" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_sales_channel_settings_OrganizationId_CounterpartyId",
                schema: "kniterp",
                table: "sales_channel_settings",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_channel_settings_channel",
                schema: "kniterp",
                table: "sales_channel_settings",
                columns: new[] { "OrganizationId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_plans_ManagerUserId",
                schema: "kniterp",
                table: "sales_plans",
                column: "ManagerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_plans_UpdatedByUserId",
                schema: "kniterp",
                table: "sales_plans",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_plans_month_manager",
                schema: "kniterp",
                table: "sales_plans",
                columns: new[] { "OrganizationId", "Month", "ManagerUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_column_maps",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "marketplace_report_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_channel_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_plans",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "marketplace_reports",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ix_stock_documents_sales_analytics",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_sales_orders_responsible_date",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropIndex(
                name: "ix_sales_orders_status_ship_date",
                schema: "kniterp",
                table: "sales_orders");
        }
    }
}
