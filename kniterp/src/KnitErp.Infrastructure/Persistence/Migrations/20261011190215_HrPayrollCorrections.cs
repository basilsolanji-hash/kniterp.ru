using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPayrollCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_kind",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.AddColumn<DateOnly>(
                name: "CorrectsPeriod",
                schema: "kniterp",
                table: "hr_payroll_lines",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_hr_payroll_lines_corrects",
                schema: "kniterp",
                table: "hr_payroll_lines",
                columns: new[] { "OrganizationId", "CorrectsPeriod" },
                filter: "[CorrectsPeriod] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "([Kind] = 6 AND [CorrectsPeriod] IS NOT NULL AND DAY([CorrectsPeriod]) = 1 AND [Amount] > 0) OR ([Kind] <> 6 AND [CorrectsPeriod] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_kind",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "[Kind] BETWEEN 1 AND 6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_hr_payroll_lines_corrects",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_correction",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_payroll_lines_kind",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.DropColumn(
                name: "CorrectsPeriod",
                schema: "kniterp",
                table: "hr_payroll_lines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_payroll_lines_kind",
                schema: "kniterp",
                table: "hr_payroll_lines",
                sql: "[Kind] BETWEEN 1 AND 5");
        }
    }
}
