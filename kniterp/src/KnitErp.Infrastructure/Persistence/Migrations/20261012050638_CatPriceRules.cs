using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatPriceRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_price_revaluations_mode",
                schema: "kniterp",
                table: "price_revaluations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_price_history_source",
                schema: "kniterp",
                table: "item_price_history");

            migrationBuilder.CreateTable(
                name: "price_rules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    Rounding = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_rules", x => x.Id);
                    table.CheckConstraint("ck_price_rules_factor", "[Factor] > 0 AND [Factor] <= 100");
                    table.CheckConstraint("ck_price_rules_rounding", "[Rounding] IN (0, 1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_price_rules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_price_type",
                        column: x => x.PriceTypeId,
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_price_revaluations_mode",
                schema: "kniterp",
                table: "price_revaluations",
                sql: "[Mode] IN (1, 2) AND [Rounding] IN (0, 1, 2, 3, 4, 5) AND [Items] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_price_history_source",
                schema: "kniterp",
                table: "item_price_history",
                sql: "[Source] IN (1, 2, 3, 4, 5, 6)");

            migrationBuilder.CreateIndex(
                name: "IX_price_rules_OrganizationId",
                schema: "kniterp",
                table: "price_rules",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "ux_price_rules_price_type",
                schema: "kniterp",
                table: "price_rules",
                column: "PriceTypeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_rules",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_price_revaluations_mode",
                schema: "kniterp",
                table: "price_revaluations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_price_history_source",
                schema: "kniterp",
                table: "item_price_history");

            migrationBuilder.AddCheckConstraint(
                name: "ck_price_revaluations_mode",
                schema: "kniterp",
                table: "price_revaluations",
                sql: "[Mode] IN (1, 2) AND [Rounding] IN (0, 1, 2, 3) AND [Items] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_price_history_source",
                schema: "kniterp",
                table: "item_price_history",
                sql: "[Source] IN (1, 2, 3, 4, 5)");
        }
    }
}
