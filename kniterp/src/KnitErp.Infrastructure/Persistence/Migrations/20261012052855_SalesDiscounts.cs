using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscountSources",
                schema: "kniterp",
                table: "sales_order_lines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ManualDiscountPercent",
                schema: "kniterp",
                table: "sales_order_lines",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "sales_discount_rules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    MinOrderAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    MinQuantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ItemGroupId = table.Column<long>(type: "bigint", nullable: true),
                    TagId = table.Column<long>(type: "bigint", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_discount_rules", x => x.Id);
                    table.CheckConstraint("ck_sales_discount_rules_kind", "[Kind] IN (2, 3, 4)");
                    table.CheckConstraint("ck_sales_discount_rules_percent", "[Percent] > 0 AND [Percent] <= 100");
                    table.CheckConstraint("ck_sales_discount_rules_period", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
                    table.CheckConstraint("ck_sales_discount_rules_terms", "([Kind] = 2 AND [MinOrderAmount] > 0) OR ([Kind] = 3 AND [MinQuantity] > 0) OR ([Kind] = 4 AND [ItemGroupId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_sales_discount_rules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_discount_rules_group",
                        columns: x => new { x.OrganizationId, x.ItemGroupId },
                        principalSchema: "kniterp",
                        principalTable: "item_groups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_discount_rules_tag",
                        columns: x => new { x.OrganizationId, x.TagId },
                        principalSchema: "kniterp",
                        principalTable: "tags",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_discount_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<byte>(type: "tinyint", nullable: false),
                    KindOrder = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_discount_settings", x => x.Id);
                    table.CheckConstraint("ck_sales_discount_settings_mode", "[Mode] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_sales_discount_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_order_lines_manual_discount",
                schema: "kniterp",
                table: "sales_order_lines",
                sql: "[ManualDiscountPercent] BETWEEN 0 AND 100");

            migrationBuilder.CreateIndex(
                name: "ix_sales_discount_rules_active",
                schema: "kniterp",
                table: "sales_discount_rules",
                columns: new[] { "OrganizationId", "IsArchived", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_discount_rules_OrganizationId_ItemGroupId",
                schema: "kniterp",
                table: "sales_discount_rules",
                columns: new[] { "OrganizationId", "ItemGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_discount_rules_OrganizationId_TagId",
                schema: "kniterp",
                table: "sales_discount_rules",
                columns: new[] { "OrganizationId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_discount_settings_org",
                schema: "kniterp",
                table: "sales_discount_settings",
                column: "OrganizationId",
                unique: true);

            // D1106: у существующих строк прежняя скидка — ручная.
            migrationBuilder.Sql("EXEC(N'UPDATE [kniterp].[sales_order_lines] SET [ManualDiscountPercent] = [DiscountPercent] WHERE [DiscountPercent] > 0');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_discount_rules",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_discount_settings",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_order_lines_manual_discount",
                schema: "kniterp",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "DiscountSources",
                schema: "kniterp",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "ManualDiscountPercent",
                schema: "kniterp",
                table: "sales_order_lines");
        }
    }
}
