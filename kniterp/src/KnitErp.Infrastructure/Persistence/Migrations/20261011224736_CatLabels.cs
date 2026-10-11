using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "label_templates",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WidthMm = table.Column<decimal>(type: "decimal(5,1)", nullable: false),
                    HeightMm = table.Column<decimal>(type: "decimal(5,1)", nullable: false),
                    Sheet = table.Column<byte>(type: "tinyint", nullable: false),
                    Columns = table.Column<byte>(type: "tinyint", nullable: false),
                    Rows = table.Column<byte>(type: "tinyint", nullable: false),
                    PaddingMm = table.Column<decimal>(type: "decimal(4,1)", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: true),
                    ElementsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_label_templates", x => x.Id);
                    table.CheckConstraint("ck_label_templates_grid", "[Columns] BETWEEN 1 AND 8 AND [Rows] BETWEEN 1 AND 30 AND [Sheet] IN (1, 2)");
                    table.CheckConstraint("ck_label_templates_size", "[WidthMm] BETWEEN 15 AND 150 AND [HeightMm] BETWEEN 15 AND 150 AND [PaddingMm] BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "FK_label_templates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_label_templates_price_type",
                        column: x => x.PriceTypeId,
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_label_templates_org",
                schema: "kniterp",
                table: "label_templates",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_label_templates_PriceTypeId",
                schema: "kniterp",
                table: "label_templates",
                column: "PriceTypeId");

            migrationBuilder.CreateIndex(
                name: "ux_label_templates_default",
                schema: "kniterp",
                table: "label_templates",
                column: "OrganizationId",
                unique: true,
                filter: "[IsDefault] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "label_templates",
                schema: "kniterp");
        }
    }
}
