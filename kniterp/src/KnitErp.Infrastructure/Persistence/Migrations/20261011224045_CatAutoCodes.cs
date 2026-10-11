using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatAutoCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "ArticleDigits",
                schema: "kniterp",
                table: "catalog_settings",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)5);

            migrationBuilder.AddColumn<string>(
                name: "ArticleTemplateText",
                schema: "kniterp",
                table: "catalog_settings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoArticle",
                schema: "kniterp",
                table: "catalog_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoBarcode",
                schema: "kniterp",
                table: "catalog_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte>(
                name: "BarcodePrefix",
                schema: "kniterp",
                table: "catalog_settings",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)20);

            migrationBuilder.AddColumn<int>(
                name: "NextArticleNumber",
                schema: "kniterp",
                table: "catalog_settings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "NextBarcodeNumber",
                schema: "kniterp",
                table: "catalog_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_catalog_settings_article",
                schema: "kniterp",
                table: "catalog_settings",
                sql: "[ArticleDigits] BETWEEN 3 AND 8 AND [NextArticleNumber] >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_catalog_settings_barcode",
                schema: "kniterp",
                table: "catalog_settings",
                sql: "[BarcodePrefix] BETWEEN 20 AND 29 AND [NextBarcodeNumber] BETWEEN 1 AND 9999999999");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_catalog_settings_article",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_catalog_settings_barcode",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "ArticleDigits",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "ArticleTemplateText",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "AutoArticle",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "AutoBarcode",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "BarcodePrefix",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "NextArticleNumber",
                schema: "kniterp",
                table: "catalog_settings");

            migrationBuilder.DropColumn(
                name: "NextBarcodeNumber",
                schema: "kniterp",
                table: "catalog_settings");
        }
    }
}
