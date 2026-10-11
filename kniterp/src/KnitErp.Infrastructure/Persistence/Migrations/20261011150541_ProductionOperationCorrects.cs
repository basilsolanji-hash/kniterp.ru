using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionOperationCorrects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CorrectsOperationId",
                schema: "kniterp",
                table: "production_operations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations",
                column: "CorrectsOperationId",
                filter: "[CorrectsOperationId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations",
                sql: "[CorrectsOperationId] IS NULL OR ([Kind] = 2 AND [EmployeeId] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations",
                column: "CorrectsOperationId",
                principalSchema: "kniterp",
                principalTable: "production_operations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropIndex(
                name: "ix_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_operations_corrects",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropColumn(
                name: "CorrectsOperationId",
                schema: "kniterp",
                table: "production_operations");
        }
    }
}
