using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrDepartmentSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_department_schedules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_department_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_hr_department_schedules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_department_schedules_department",
                        columns: x => new { x.OrganizationId, x.DepartmentId },
                        principalSchema: "kniterp",
                        principalTable: "departments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_department_schedules_schedule",
                        columns: x => new { x.OrganizationId, x.ScheduleId },
                        principalSchema: "kniterp",
                        principalTable: "hr_work_schedules",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hr_department_schedules_OrganizationId_ScheduleId",
                schema: "kniterp",
                table: "hr_department_schedules",
                columns: new[] { "OrganizationId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_department_schedules_department",
                schema: "kniterp",
                table: "hr_department_schedules",
                columns: new[] { "OrganizationId", "DepartmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hr_department_schedules",
                schema: "kniterp");
        }
    }
}
