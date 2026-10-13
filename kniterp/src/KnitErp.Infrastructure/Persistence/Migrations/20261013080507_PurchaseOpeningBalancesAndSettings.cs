using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseOpeningBalancesAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_settings",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    OverReceiptPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_settings", x => x.OrganizationId);
                    table.CheckConstraint("ck_purchase_settings_over_receipt", "[OverReceiptPercent] BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "FK_purchase_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_settings_updated_by",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_opening_balances",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    LegalEntityId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    BalanceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsCancelled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_opening_balances", x => x.Id);
                    table.CheckConstraint("ck_supplier_opening_balances_amount", "[Amount] <> 0");
                    table.CheckConstraint("ck_supplier_opening_balances_cancelled", "([IsCancelled] = 1 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([IsCancelled] = 0 AND [CancelledByUserId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_supplier_opening_balances_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_opening_balances_cancelled_by",
                        column: x => x.CancelledByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_opening_balances_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_opening_balances_entity",
                        columns: x => new { x.OrganizationId, x.LegalEntityId },
                        principalSchema: "kniterp",
                        principalTable: "legal_entities",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_opening_balances_supplier",
                        columns: x => new { x.OrganizationId, x.SupplierId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_settings_UpdatedByUserId",
                schema: "kniterp",
                table: "purchase_settings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_opening_balances_CancelledByUserId",
                schema: "kniterp",
                table: "supplier_opening_balances",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_opening_balances_CreatedByUserId",
                schema: "kniterp",
                table: "supplier_opening_balances",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_opening_balances_OrganizationId_LegalEntityId",
                schema: "kniterp",
                table: "supplier_opening_balances",
                columns: new[] { "OrganizationId", "LegalEntityId" });

            migrationBuilder.CreateIndex(
                name: "ux_supplier_opening_balances_active",
                schema: "kniterp",
                table: "supplier_opening_balances",
                columns: new[] { "OrganizationId", "SupplierId", "LegalEntityId" },
                unique: true,
                filter: "[IsCancelled] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "purchase_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "supplier_opening_balances",
                schema: "kniterp");
        }
    }
}
