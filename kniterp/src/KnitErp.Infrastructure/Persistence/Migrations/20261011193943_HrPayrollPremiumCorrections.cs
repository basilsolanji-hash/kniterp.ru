using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPayrollPremiumCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.AddColumn<bool>(
                name: "FromPiece",
                schema: "kniterp",
                table: "hr_payroll_lines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "([CorrectsPeriod] IS NULL AND [Kind] <> 6) OR ([CorrectsPeriod] IS NOT NULL AND [Kind] IN (3, 4, 5, 6) AND DAY([CorrectsPeriod]) = 1 AND [Amount] > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_from_piece",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "[FromPiece] = 0 OR [Kind] IN (3, 4, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_from_piece",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.DropColumn(
                name: "FromPiece",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "([Kind] = 6 AND [CorrectsPeriod] IS NOT NULL AND DAY([CorrectsPeriod]) = 1 AND [Amount] > 0) OR ([Kind] <> 6 AND [CorrectsPeriod] IS NULL)");
        }
    }
}
