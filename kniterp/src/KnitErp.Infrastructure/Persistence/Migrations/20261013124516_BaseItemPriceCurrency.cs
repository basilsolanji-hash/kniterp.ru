using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BaseItemPriceCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PriceNote",
                schema: "kniterp",
                table: "sales_order_lines",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseCurrency",
                schema: "kniterp",
                table: "items",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "RUB");

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                schema: "kniterp",
                table: "item_prices",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: false,
                defaultValue: "RUB");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_prices_currency",
                schema: "kniterp",
                table: "item_prices",
                sql: "LEN([CurrencyCode]) = 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_item_prices_currency",
                schema: "kniterp",
                table: "item_prices");

            migrationBuilder.DropColumn(
                name: "PriceNote",
                schema: "kniterp",
                table: "sales_order_lines");

            migrationBuilder.DropColumn(
                name: "PurchaseCurrency",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                schema: "kniterp",
                table: "item_prices");
        }
    }
}
