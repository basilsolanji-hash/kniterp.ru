using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_stage_rules_trigger",
                schema: "kniterp",
                table: "sales_stage_rules");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_money_operations_OrganizationId_Id",
                schema: "kniterp",
                table: "money_operations",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "customer_refunds",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RefundDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    PaymentId = table.Column<long>(type: "bigint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: true),
                    ReturnDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    MoneyOperationId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_refunds", x => x.Id);
                    table.CheckConstraint("ck_customer_refunds_amount", "[Amount] > 0");
                    table.CheckConstraint("ck_customer_refunds_cancelled", "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
                    table.CheckConstraint("ck_customer_refunds_status", "[Status] IN (2, 9)");
                    table.ForeignKey(
                        name: "FK_customer_refunds_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_cancelled_by",
                        column: x => x.CancelledByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_customer",
                        columns: x => new { x.OrganizationId, x.CustomerId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_money_operation",
                        columns: x => new { x.OrganizationId, x.MoneyOperationId },
                        principalSchema: "kniterp",
                        principalTable: "money_operations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_payment",
                        columns: x => new { x.OrganizationId, x.PaymentId },
                        principalSchema: "kniterp",
                        principalTable: "customer_payments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_refunds_return",
                        columns: x => new { x.OrganizationId, x.ReturnDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_delivery_methods",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_delivery_methods", x => x.Id);
                    table.UniqueConstraint("ak_sales_delivery_methods_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_sales_delivery_methods_kind", "[Kind] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_sales_delivery_methods_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_marking_codes",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    StockDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Gtin = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Serial = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, collation: "Latin1_General_BIN2"),
                    IsSold = table.Column<bool>(type: "bit", nullable: false),
                    SoldAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReleasedByDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    ScannedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ScannedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_marking_codes", x => x.Id);
                    table.CheckConstraint("ck_sales_marking_codes_gtin", "LEN([Gtin]) = 14 AND [Gtin] NOT LIKE '%[^0-9]%'");
                    table.CheckConstraint("ck_sales_marking_codes_serial", "LEN([Serial]) BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_sales_marking_codes_sold", "[IsSold] = 0 OR [SoldAtUtc] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_sales_marking_codes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_marking_codes_document",
                        columns: x => new { x.OrganizationId, x.StockDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_marking_codes_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_marking_codes_released_by",
                        columns: x => new { x.OrganizationId, x.ReleasedByDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_marking_codes_user",
                        column: x => x.ScannedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_pick_lists",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ListDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PickedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    PickedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LinesRevision = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_pick_lists", x => x.Id);
                    table.UniqueConstraint("ak_sales_pick_lists_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_sales_pick_lists_picked", "([Status] = 2 AND [PickedByUserId] IS NOT NULL AND [PickedAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [PickedByUserId] IS NULL)");
                    table.CheckConstraint("ck_sales_pick_lists_status", "[Status] IN (1, 2, 9)");
                    table.ForeignKey(
                        name: "FK_sales_pick_lists_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_pick_lists_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_pick_lists_picked_by",
                        column: x => x.PickedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_pick_lists_warehouse",
                        columns: x => new { x.OrganizationId, x.WarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_reasons",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return_reasons", x => x.Id);
                    table.UniqueConstraint("ak_sales_return_reasons_org_id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_sales_return_reasons_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_shipment_groups",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    GroupDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryMethodId = table.Column<long>(type: "bigint", nullable: true),
                    TrackingNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Packages = table.Column<int>(type: "int", nullable: true),
                    GrossWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PostedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_shipment_groups", x => x.Id);
                    table.UniqueConstraint("ak_sales_shipment_groups_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_sales_shipment_groups_packages", "[Packages] IS NULL OR [Packages] > 0");
                    table.CheckConstraint("ck_sales_shipment_groups_posted", "([Status] = 2 AND [PostedByUserId] IS NOT NULL AND [PostedAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [PostedByUserId] IS NULL)");
                    table.CheckConstraint("ck_sales_shipment_groups_status", "[Status] IN (1, 2, 9)");
                    table.CheckConstraint("ck_sales_shipment_groups_weight", "[GrossWeightKg] IS NULL OR [GrossWeightKg] > 0");
                    table.ForeignKey(
                        name: "FK_sales_shipment_groups_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_groups_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_groups_customer",
                        columns: x => new { x.OrganizationId, x.CustomerId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_groups_delivery",
                        columns: x => new { x.OrganizationId, x.DeliveryMethodId },
                        principalSchema: "kniterp",
                        principalTable: "sales_delivery_methods",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_groups_posted_by",
                        column: x => x.PostedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_groups_warehouse",
                        columns: x => new { x.OrganizationId, x.WarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_pick_list_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PickListId = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Picked = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_pick_list_lines", x => x.Id);
                    table.CheckConstraint("ck_sales_pick_list_lines_picked", "[Picked] >= 0 AND [Picked] <= [Quantity]");
                    table.CheckConstraint("ck_sales_pick_list_lines_quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "fk_sales_pick_list_lines_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_pick_list_lines_list",
                        columns: x => new { x.OrganizationId, x.PickListId },
                        principalSchema: "kniterp",
                        principalTable: "sales_pick_lists",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_pick_list_lines_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_details",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    StockDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ReasonId = table.Column<long>(type: "bigint", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    QuarantineWarehouseId = table.Column<long>(type: "bigint", nullable: true),
                    WriteOffReasonId = table.Column<long>(type: "bigint", nullable: true),
                    TransferDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    WriteOffDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return_details", x => x.Id);
                    table.UniqueConstraint("ak_sales_return_details_org_id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_sales_return_details_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_document",
                        columns: x => new { x.OrganizationId, x.StockDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_quarantine",
                        columns: x => new { x.OrganizationId, x.QuarantineWarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_reason",
                        columns: x => new { x.OrganizationId, x.ReasonId },
                        principalSchema: "kniterp",
                        principalTable: "sales_return_reasons",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_transfer",
                        columns: x => new { x.OrganizationId, x.TransferDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_writeoff",
                        columns: x => new { x.OrganizationId, x.WriteOffDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_details_writeoff_reason",
                        columns: x => new { x.OrganizationId, x.WriteOffReasonId },
                        principalSchema: "kniterp",
                        principalTable: "operation_reasons",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_shipment_group_documents",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    StockDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_shipment_group_documents", x => x.Id);
                    table.ForeignKey(
                        name: "fk_sales_shipment_group_documents_document",
                        columns: x => new { x.OrganizationId, x.StockDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_shipment_group_documents_group",
                        columns: x => new { x.OrganizationId, x.GroupId },
                        principalSchema: "kniterp",
                        principalTable: "sales_shipment_groups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_shipment_group_documents_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DetailsId = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quality = table.Column<byte>(type: "tinyint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_return_lines", x => x.Id);
                    table.CheckConstraint("ck_sales_return_lines_quality", "[Quality] IN (2, 3)");
                    table.CheckConstraint("ck_sales_return_lines_quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "fk_sales_return_lines_details",
                        columns: x => new { x.OrganizationId, x.DetailsId },
                        principalSchema: "kniterp",
                        principalTable: "sales_return_details",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_stage_rules_trigger",
                schema: "kniterp",
                table: "sales_stage_rules",
                sql: "[Trigger] BETWEEN 1 AND 9");

            migrationBuilder.CreateIndex(
                name: "IX_customer_refunds_CancelledByUserId",
                schema: "kniterp",
                table: "customer_refunds",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_refunds_CreatedByUserId",
                schema: "kniterp",
                table: "customer_refunds",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_customer_refunds_customer_date",
                schema: "kniterp",
                table: "customer_refunds",
                columns: new[] { "OrganizationId", "CustomerId", "RefundDate" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_refunds_money_operation",
                schema: "kniterp",
                table: "customer_refunds",
                columns: new[] { "OrganizationId", "MoneyOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_refunds_OrganizationId_ReturnDocumentId",
                schema: "kniterp",
                table: "customer_refunds",
                columns: new[] { "OrganizationId", "ReturnDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_refunds_OrganizationId_SalesOrderId",
                schema: "kniterp",
                table: "customer_refunds",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_refunds_payment",
                schema: "kniterp",
                table: "customer_refunds",
                columns: new[] { "OrganizationId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_delivery_methods_org_name",
                schema: "kniterp",
                table: "sales_delivery_methods",
                columns: new[] { "OrganizationId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_sales_marking_codes_OrganizationId_ItemId",
                schema: "kniterp",
                table: "sales_marking_codes",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_marking_codes_OrganizationId_ReleasedByDocumentId",
                schema: "kniterp",
                table: "sales_marking_codes",
                columns: new[] { "OrganizationId", "ReleasedByDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_marking_codes_OrganizationId_StockDocumentId",
                schema: "kniterp",
                table: "sales_marking_codes",
                columns: new[] { "OrganizationId", "StockDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_marking_codes_ScannedByUserId",
                schema: "kniterp",
                table: "sales_marking_codes",
                column: "ScannedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_marking_codes_document_code",
                schema: "kniterp",
                table: "sales_marking_codes",
                columns: new[] { "StockDocumentId", "Gtin", "Serial" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_marking_codes_sold",
                schema: "kniterp",
                table: "sales_marking_codes",
                columns: new[] { "OrganizationId", "Gtin", "Serial" },
                unique: true,
                filter: "[IsSold] = 1");

            migrationBuilder.CreateIndex(
                name: "ix_sales_pick_list_lines_order",
                schema: "kniterp",
                table: "sales_pick_list_lines",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_pick_list_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "sales_pick_list_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_pick_list_lines_OrganizationId_PickListId",
                schema: "kniterp",
                table: "sales_pick_list_lines",
                columns: new[] { "OrganizationId", "PickListId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_pick_list_lines_order_item",
                schema: "kniterp",
                table: "sales_pick_list_lines",
                columns: new[] { "PickListId", "SalesOrderId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_pick_lists_CreatedByUserId",
                schema: "kniterp",
                table: "sales_pick_lists",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_sales_pick_lists_org_status_date",
                schema: "kniterp",
                table: "sales_pick_lists",
                columns: new[] { "OrganizationId", "Status", "ListDate" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_pick_lists_OrganizationId_WarehouseId",
                schema: "kniterp",
                table: "sales_pick_lists",
                columns: new[] { "OrganizationId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_pick_lists_PickedByUserId",
                schema: "kniterp",
                table: "sales_pick_lists",
                column: "PickedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_pick_lists_org_number",
                schema: "kniterp",
                table: "sales_pick_lists",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_QuarantineWarehouseId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "QuarantineWarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_ReasonId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "ReasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_StockDocumentId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "StockDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_TransferDocumentId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "TransferDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_WriteOffDocumentId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "WriteOffDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_details_OrganizationId_WriteOffReasonId",
                schema: "kniterp",
                table: "sales_return_details",
                columns: new[] { "OrganizationId", "WriteOffReasonId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_details_document",
                schema: "kniterp",
                table: "sales_return_details",
                column: "StockDocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_lines_OrganizationId_DetailsId",
                schema: "kniterp",
                table: "sales_return_lines",
                columns: new[] { "OrganizationId", "DetailsId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_return_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "sales_return_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_lines_item",
                schema: "kniterp",
                table: "sales_return_lines",
                columns: new[] { "DetailsId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_return_reasons_org_name",
                schema: "kniterp",
                table: "sales_return_reasons",
                columns: new[] { "OrganizationId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_group_documents_OrganizationId_GroupId",
                schema: "kniterp",
                table: "sales_shipment_group_documents",
                columns: new[] { "OrganizationId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_group_documents_OrganizationId_SalesOrderId",
                schema: "kniterp",
                table: "sales_shipment_group_documents",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_group_documents_OrganizationId_StockDocumentId",
                schema: "kniterp",
                table: "sales_shipment_group_documents",
                columns: new[] { "OrganizationId", "StockDocumentId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_shipment_group_documents_document",
                schema: "kniterp",
                table: "sales_shipment_group_documents",
                column: "StockDocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_shipment_group_documents_group_order",
                schema: "kniterp",
                table: "sales_shipment_group_documents",
                columns: new[] { "GroupId", "SalesOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_groups_CreatedByUserId",
                schema: "kniterp",
                table: "sales_shipment_groups",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_sales_shipment_groups_org_date",
                schema: "kniterp",
                table: "sales_shipment_groups",
                columns: new[] { "OrganizationId", "GroupDate" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_groups_OrganizationId_CustomerId",
                schema: "kniterp",
                table: "sales_shipment_groups",
                columns: new[] { "OrganizationId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_groups_OrganizationId_DeliveryMethodId",
                schema: "kniterp",
                table: "sales_shipment_groups",
                columns: new[] { "OrganizationId", "DeliveryMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_groups_OrganizationId_WarehouseId",
                schema: "kniterp",
                table: "sales_shipment_groups",
                columns: new[] { "OrganizationId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_shipment_groups_PostedByUserId",
                schema: "kniterp",
                table: "sales_shipment_groups",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_shipment_groups_org_number",
                schema: "kniterp",
                table: "sales_shipment_groups",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            // CUSTOM SQL (D): D1150 — правило «Заказ собран на складе → Собран» существующим организациям (как в AddSalesStageRules),
            // если у них есть действующий этап «Собран» и правила для этого события ещё нет.
            migrationBuilder.Sql(@"EXEC(N'
INSERT INTO [kniterp].[sales_stage_rules] ([OrganizationId], [Trigger], [StageId])
SELECT s.[OrganizationId], 9, s.[Id]
FROM [kniterp].[sales_order_stages] s
WHERE s.[Name] = N''Собран'' AND s.[IsArchived] = 0
  AND NOT EXISTS (SELECT 1 FROM [kniterp].[sales_stage_rules] r WHERE r.[OrganizationId] = s.[OrganizationId] AND r.[Trigger] = 9);
')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CUSTOM SQL (D): правила события 9 удаляются до возврата CHECK «1–8».
            migrationBuilder.Sql("EXEC(N'DELETE FROM [kniterp].[sales_stage_rules] WHERE [Trigger] = 9;')");

            migrationBuilder.DropTable(
                name: "customer_refunds",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_marking_codes",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_pick_list_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_return_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_shipment_group_documents",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_pick_lists",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_return_details",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_shipment_groups",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_return_reasons",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_delivery_methods",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_stage_rules_trigger",
                schema: "kniterp",
                table: "sales_stage_rules");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_money_operations_OrganizationId_Id",
                schema: "kniterp",
                table: "money_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_stage_rules_trigger",
                schema: "kniterp",
                table: "sales_stage_rules",
                sql: "[Trigger] BETWEEN 1 AND 8");
        }
    }
}
