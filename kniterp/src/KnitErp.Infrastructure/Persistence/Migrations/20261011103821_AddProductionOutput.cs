using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionOutput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                schema: "kniterp",
                table: "production_orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                schema: "kniterp",
                table: "production_orders",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClosedByUserId",
                schema: "kniterp",
                table: "production_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_ClosedByUserId",
                schema: "kniterp",
                table: "production_orders",
                column: "ClosedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_orders_closed",
                schema: "kniterp",
                table: "production_orders",
                sql: "([Status] = 5 AND [ClosedByUserId] IS NOT NULL AND [ClosedAtUtc] IS NOT NULL) OR ([Status] <> 5 AND [ClosedByUserId] IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_production_orders_users_ClosedByUserId",
                schema: "kniterp",
                table: "production_orders",
                column: "ClosedByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_orders_users_ClosedByUserId",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropIndex(
                name: "IX_production_orders_ClosedByUserId",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_orders_closed",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ClosedByUserId",
                schema: "kniterp",
                table: "production_orders");
        }
    }
}
