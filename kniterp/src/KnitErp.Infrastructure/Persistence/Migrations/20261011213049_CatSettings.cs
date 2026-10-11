using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ServicesEnabled = table.Column<bool>(type: "bit", nullable: false),
                    BundlesEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoCodePrefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    AutoCodeDigits = table.Column<byte>(type: "tinyint", nullable: false),
                    NextNumber = table.Column<int>(type: "int", nullable: false),
                    DefaultItemType = table.Column<byte>(type: "tinyint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_settings", x => x.Id);
                    table.CheckConstraint("ck_catalog_settings_digits", "[AutoCodeDigits] BETWEEN 3 AND 8");
                    table.CheckConstraint("ck_catalog_settings_next", "[NextNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_catalog_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_catalog_settings_org",
                schema: "kniterp",
                table: "catalog_settings",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_settings",
                schema: "kniterp");
        }
    }
}
