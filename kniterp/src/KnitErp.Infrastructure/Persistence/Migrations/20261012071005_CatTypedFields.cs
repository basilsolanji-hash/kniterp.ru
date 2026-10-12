using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatTypedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_custom_field_definitions_type",
                schema: "kniterp",
                table: "custom_field_definitions");

            migrationBuilder.AddColumn<long>(
                name: "CatalogId",
                schema: "kniterp",
                table: "characteristics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Type",
                schema: "kniterp",
                table: "characteristics",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_custom_field_definitions_type",
                schema: "kniterp",
                table: "custom_field_definitions",
                sql: "[Type] IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.CreateIndex(
                name: "IX_characteristics_OrganizationId_CatalogId",
                schema: "kniterp",
                table: "characteristics",
                columns: new[] { "OrganizationId", "CatalogId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_characteristics_catalog",
                schema: "kniterp",
                table: "characteristics",
                sql: "([Type] = 4 AND [CatalogId] IS NOT NULL) OR ([Type] <> 4 AND [CatalogId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_characteristics_type",
                schema: "kniterp",
                table: "characteristics",
                sql: "[Type] IN (1, 2, 3, 4)");

            migrationBuilder.AddForeignKey(
                name: "FK_characteristics_user_catalogs_OrganizationId_CatalogId",
                schema: "kniterp",
                table: "characteristics",
                columns: new[] { "OrganizationId", "CatalogId" },
                principalSchema: "kniterp",
                principalTable: "user_catalogs",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_characteristics_user_catalogs_OrganizationId_CatalogId",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_custom_field_definitions_type",
                schema: "kniterp",
                table: "custom_field_definitions");

            migrationBuilder.DropIndex(
                name: "IX_characteristics_OrganizationId_CatalogId",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_characteristics_catalog",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.DropCheckConstraint(
                name: "ck_characteristics_type",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.DropColumn(
                name: "CatalogId",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "kniterp",
                table: "characteristics");

            migrationBuilder.AddCheckConstraint(
                name: "ck_custom_field_definitions_type",
                schema: "kniterp",
                table: "custom_field_definitions",
                sql: "[Type] IN (1, 2, 3, 4, 5)");
        }
    }
}
