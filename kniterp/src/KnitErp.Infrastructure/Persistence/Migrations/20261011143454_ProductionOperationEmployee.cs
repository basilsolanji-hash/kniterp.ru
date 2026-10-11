using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionOperationEmployee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EmployeeId",
                schema: "kniterp",
                table: "production_operations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_operations_org_employee_date",
                schema: "kniterp",
                table: "production_operations",
                columns: new[] { "OrganizationId", "EmployeeId", "OccurredOn" },
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_production_operations_employee",
                schema: "kniterp",
                table: "production_operations",
                columns: new[] { "OrganizationId", "EmployeeId" },
                principalSchema: "kniterp",
                principalTable: "employees",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_production_operations_employee",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropIndex(
                name: "ix_production_operations_org_employee_date",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                schema: "kniterp",
                table: "production_operations");
        }
    }
}
