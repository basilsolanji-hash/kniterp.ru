using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AgreementId",
                schema: "kniterp",
                table: "sales_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PriceOverrideAtUtc",
                schema: "kniterp",
                table: "sales_orders",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PriceOverrideByUserId",
                schema: "kniterp",
                table: "sales_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceOverrideReason",
                schema: "kniterp",
                table: "sales_orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_roles_org_id",
                schema: "kniterp",
                table: "roles",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "customer_sales_profiles",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: true),
                    ManagerUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_sales_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_sales_profiles_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_sales_profiles_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_sales_profiles_manager",
                        column: x => x.ManagerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_sales_profiles_price_type",
                        columns: x => new { x.OrganizationId, x.PriceTypeId },
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_agreements",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: true),
                    TagId = table.Column<long>(type: "bigint", nullable: true),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    PrepaymentPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    PaymentDays = table.Column<int>(type: "int", nullable: true),
                    CreditLimit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: true),
                    ContractId = table.Column<long>(type: "bigint", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_agreements", x => x.Id);
                    table.UniqueConstraint("ak_sales_agreements_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_sales_agreements_contract", "[ContractId] IS NULL OR [CounterpartyId] IS NOT NULL");
                    table.CheckConstraint("ck_sales_agreements_days", "[PaymentDays] IS NULL OR [PaymentDays] BETWEEN 0 AND 365");
                    table.CheckConstraint("ck_sales_agreements_discount", "[DiscountPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_sales_agreements_limit", "[CreditLimit] IS NULL OR [CreditLimit] >= 0");
                    table.CheckConstraint("ck_sales_agreements_period", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
                    table.CheckConstraint("ck_sales_agreements_prepayment", "[PrepaymentPercent] IS NULL OR [PrepaymentPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_sales_agreements_scope", "[CounterpartyId] IS NULL OR [TagId] IS NULL");
                    table.CheckConstraint("ck_sales_agreements_status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_sales_agreements_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_agreements_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_agreements_contract",
                        columns: x => new { x.OrganizationId, x.ContractId },
                        principalSchema: "kniterp",
                        principalTable: "counterparty_contracts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_agreements_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_agreements_price_type",
                        columns: x => new { x.OrganizationId, x.PriceTypeId },
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_agreements_tag",
                        columns: x => new { x.OrganizationId, x.TagId },
                        principalSchema: "kniterp",
                        principalTable: "tags",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_agreements_warehouse",
                        columns: x => new { x.OrganizationId, x.WarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_role_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    MaxDiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    MinPriceTypeId = table.Column<long>(type: "bigint", nullable: true),
                    Visibility = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)1),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_role_settings", x => x.Id);
                    table.CheckConstraint("ck_sales_role_settings_discount", "[MaxDiscountPercent] IS NULL OR [MaxDiscountPercent] BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_sales_role_settings_visibility", "[Visibility] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_sales_role_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_role_settings_price_type",
                        columns: x => new { x.OrganizationId, x.MinPriceTypeId },
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_role_settings_role",
                        columns: x => new { x.OrganizationId, x.RoleId },
                        principalSchema: "kniterp",
                        principalTable: "roles",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_org_agreement",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "AgreementId" },
                filter: "[AgreementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sales_orders_PriceOverrideByUserId",
                schema: "kniterp",
                table: "sales_orders",
                column: "PriceOverrideByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_orders_price_override",
                schema: "kniterp",
                table: "sales_orders",
                sql: "([PriceOverrideReason] IS NULL AND [PriceOverrideByUserId] IS NULL AND [PriceOverrideAtUtc] IS NULL) OR ([PriceOverrideReason] IS NOT NULL AND [PriceOverrideByUserId] IS NOT NULL AND [PriceOverrideAtUtc] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_customer_sales_profiles_manager",
                schema: "kniterp",
                table: "customer_sales_profiles",
                columns: new[] { "OrganizationId", "ManagerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_sales_profiles_ManagerUserId",
                schema: "kniterp",
                table: "customer_sales_profiles",
                column: "ManagerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_sales_profiles_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "customer_sales_profiles",
                columns: new[] { "OrganizationId", "PriceTypeId" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_sales_profiles_counterparty",
                schema: "kniterp",
                table: "customer_sales_profiles",
                columns: new[] { "OrganizationId", "CounterpartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_CreatedByUserId",
                schema: "kniterp",
                table: "sales_agreements",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_OrganizationId_ContractId",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_OrganizationId_CounterpartyId",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "PriceTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_OrganizationId_TagId",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_agreements_OrganizationId_WarehouseId",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_agreements_pick",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "Status", "CounterpartyId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_agreements_org_number",
                schema: "kniterp",
                table: "sales_agreements",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_role_settings_OrganizationId_MinPriceTypeId",
                schema: "kniterp",
                table: "sales_role_settings",
                columns: new[] { "OrganizationId", "MinPriceTypeId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_role_settings_role",
                schema: "kniterp",
                table: "sales_role_settings",
                columns: new[] { "OrganizationId", "RoleId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_sales_orders_agreement",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "AgreementId" },
                principalSchema: "kniterp",
                principalTable: "sales_agreements",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sales_orders_price_override_user",
                schema: "kniterp",
                table: "sales_orders",
                column: "PriceOverrideByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // D1100: новые права продаж системным ролям существующих организаций (новые получают их из RoleMatrixP0 и CrmRoleMatrix).
            migrationBuilder.Sql("""
                EXEC(N'
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES (''owner'', ''sales.price.override'', 3),
                             (''owner'', ''sales.credit.override'', 3),
                             (''owner'', ''sales.agreement.edit'', 3),
                             (''owner'', ''sales.discount.edit'', 3),
                             (''owner'', ''sales.report.view'', 3),
                             (''owner'', ''sales.plan.edit'', 3),
                             (''owner'', ''sales.marking.edit'', 3),
                             (''admin'', ''sales.agreement.edit'', 3),
                             (''admin'', ''sales.discount.edit'', 3),
                             (''admin'', ''sales.report.view'', 3),
                             (''accountant'', ''sales.agreement.edit'', 3),
                             (''accountant'', ''sales.discount.edit'', 3),
                             (''accountant'', ''sales.report.view'', 3),
                             (''auditor'', ''sales.report.view'', 3),
                             (''senior_storekeeper'', ''sales.marking.edit'', 2),
                             (''storekeeper'', ''sales.marking.edit'', 2),
                             (''sales_manager'', ''sales.report.view'', 3),
                             (''sales_head'', ''sales.price.override'', 3),
                             (''sales_head'', ''sales.credit.override'', 3),
                             (''sales_head'', ''sales.agreement.edit'', 3),
                             (''sales_head'', ''sales.discount.edit'', 3),
                             (''sales_head'', ''sales.report.view'', 3),
                             (''sales_head'', ''sales.plan.edit'', 3)) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                EXEC(N'DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN (''sales.price.override'', ''sales.credit.override'',
                    ''sales.agreement.edit'', ''sales.discount.edit'', ''sales.report.view'', ''sales.plan.edit'', ''sales.marking.edit'');');
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_sales_orders_agreement",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropForeignKey(
                name: "fk_sales_orders_price_override_user",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropTable(
                name: "customer_sales_profiles",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_agreements",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_role_settings",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ix_sales_orders_org_agreement",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropIndex(
                name: "IX_sales_orders_PriceOverrideByUserId",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_orders_price_override",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_roles_org_id",
                schema: "kniterp",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "AgreementId",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "PriceOverrideAtUtc",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "PriceOverrideByUserId",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "PriceOverrideReason",
                schema: "kniterp",
                table: "sales_orders");
        }
    }
}
