using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatCancelAndCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                schema: "kniterp",
                table: "external_import_runs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HiddenBlocks",
                schema: "kniterp",
                table: "catalog_settings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequireGroup",
                schema: "kniterp",
                table: "catalog_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireVat",
                schema: "kniterp",
                table: "catalog_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_catalog_settings_blocks",
                schema: "kniterp",
                table: "catalog_settings",
                sql: "[HiddenBlocks] BETWEEN 0 AND 31");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_catalog_settings_blocks",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "CancelRequested",
                schema: "kniterp",
                table: "external_import_runs");

            migrationBuilder.DropColumn(
                name: "HiddenBlocks",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "RequireGroup",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "RequireVat",
                schema: "kniterp",
                table: "catalog_settings");
        }
    }
}
