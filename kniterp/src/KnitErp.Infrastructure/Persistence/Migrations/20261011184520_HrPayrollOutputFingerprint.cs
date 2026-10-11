using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPayrollOutputFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "OutputFingerprint",
                schema: "kniterp",
                table: "hr_payroll_runs",
                type: "binary(32)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutputFingerprint",
                schema: "kniterp",
                table: "hr_payroll_runs");
        }
    }
}
