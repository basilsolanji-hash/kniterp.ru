using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCashLimitAndAdvanceDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AdvanceDueDate",
                schema: "kniterp",
                table: "money_operations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashLimit",
                schema: "kniterp",
                table: "legal_entity_accounts",
                type: "decimal(18,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdvanceDueDate",
                schema: "kniterp",
                table: "money_operations");

            migrationBuilder.DropColumn(
                name: "CashLimit",
                schema: "kniterp",
                table: "legal_entity_accounts");
        }
    }
}
