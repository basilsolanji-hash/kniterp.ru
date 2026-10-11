using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    NightPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    OvertimeFirst = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: false),
                    OvertimeNext = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: false),
                    HolidayMultiplier = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: false),
                    SalaryBase = table.Column<byte>(type: "tinyint", nullable: false),
                    PieceHourlyRate = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ZupVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZupPieceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZupSalaryCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZupNightCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZupOvertimeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZupHolidayCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HrOfficerClosesTimesheet = table.Column<bool>(type: "bit", nullable: false),
                    LeaveOnlyByOrder = table.Column<bool>(type: "bit", nullable: false),
                    BlockPersonalDataWithoutConsent = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_settings", x => x.Id);
                    table.CheckConstraint("ck_hr_settings_minimums", "[NightPercent] >= 20 AND [OvertimeFirst] >= 1.5 AND [OvertimeNext] >= 2 AND [HolidayMultiplier] >= 2");
                    table.CheckConstraint("ck_hr_settings_piece_rate", "[PieceHourlyRate] IS NULL OR [PieceHourlyRate] > 0");
                    table.CheckConstraint("ck_hr_settings_salary_base", "[SalaryBase] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_hr_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_hr_settings_org",
                schema: "kniterp",
                table: "hr_settings",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hr_settings",
                schema: "kniterp");
        }
    }
}
