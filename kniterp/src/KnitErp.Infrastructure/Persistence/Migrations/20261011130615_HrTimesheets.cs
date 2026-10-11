using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrTimesheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_timesheets",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: false),
                    Year = table.Column<short>(type: "smallint", nullable: false),
                    Month = table.Column<byte>(type: "tinyint", nullable: false),
                    Version = table.Column<byte>(type: "tinyint", nullable: false),
                    CorrectsTimesheetId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_timesheets", x => x.Id);
                    table.UniqueConstraint("ak_hr_timesheets_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_timesheets_closed", "([Status] = 2 AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL) OR ([Status] = 1 AND [ClosedAtUtc] IS NULL AND [ClosedByUserId] IS NULL)");
                    table.CheckConstraint("ck_hr_timesheets_period", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2100");
                    table.CheckConstraint("ck_hr_timesheets_status", "[Status] IN (1, 2)");
                    table.CheckConstraint("ck_hr_timesheets_version", "([Version] = 0 AND [CorrectsTimesheetId] IS NULL) OR ([Version] > 0 AND [CorrectsTimesheetId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_hr_timesheets_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_timesheets_users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_timesheets_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_timesheets_corrects",
                        columns: x => new { x.OrganizationId, x.CorrectsTimesheetId },
                        principalSchema: "kniterp",
                        principalTable: "hr_timesheets",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_timesheets_department",
                        columns: x => new { x.OrganizationId, x.DepartmentId },
                        principalSchema: "kniterp",
                        principalTable: "departments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_work_schedules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ShiftHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    NightHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    WorkDays = table.Column<byte>(type: "tinyint", nullable: false),
                    RestDays = table.Column<byte>(type: "tinyint", nullable: false),
                    CycleStart = table.Column<DateOnly>(type: "date", nullable: true),
                    Summarized = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_work_schedules", x => x.Id);
                    table.UniqueConstraint("ak_hr_work_schedules_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_work_schedules_cycle", "[Kind] = 1 OR ([WorkDays] > 0 AND [RestDays] > 0 AND [CycleStart] IS NOT NULL)");
                    table.CheckConstraint("ck_hr_work_schedules_hours", "[ShiftHours] > 0 AND [ShiftHours] <= 24 AND [NightHours] >= 0 AND [NightHours] <= [ShiftHours]");
                    table.CheckConstraint("ck_hr_work_schedules_kind", "[Kind] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_hr_work_schedules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_timesheet_days",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    TimesheetId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Hours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    NightHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    OvertimeHours = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    IsManual = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_timesheet_days", x => x.Id);
                    table.CheckConstraint("ck_hr_timesheet_days_hours", "[Hours] >= 0 AND [NightHours] >= 0 AND [OvertimeHours] >= 0 AND [Hours] + [OvertimeHours] <= 24 AND [NightHours] <= [Hours] + [OvertimeHours]");
                    table.ForeignKey(
                        name: "fk_hr_timesheet_days_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_timesheet_days_timesheet",
                        columns: x => new { x.OrganizationId, x.TimesheetId },
                        principalSchema: "kniterp",
                        principalTable: "hr_timesheets",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_employee_schedules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduleId = table.Column<long>(type: "bigint", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employee_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_hr_employee_schedules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_schedules_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_schedules_schedule",
                        columns: x => new { x.OrganizationId, x.ScheduleId },
                        principalSchema: "kniterp",
                        principalTable: "hr_work_schedules",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employee_schedules_OrganizationId_ScheduleId",
                schema: "kniterp",
                table: "hr_employee_schedules",
                columns: new[] { "OrganizationId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_employee_schedules_from",
                schema: "kniterp",
                table: "hr_employee_schedules",
                columns: new[] { "OrganizationId", "EmployeeId", "ValidFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hr_timesheet_days_employee",
                schema: "kniterp",
                table: "hr_timesheet_days",
                columns: new[] { "OrganizationId", "EmployeeId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_timesheet_days_OrganizationId_TimesheetId",
                schema: "kniterp",
                table: "hr_timesheet_days",
                columns: new[] { "OrganizationId", "TimesheetId" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_timesheet_days",
                schema: "kniterp",
                table: "hr_timesheet_days",
                columns: new[] { "TimesheetId", "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hr_timesheets_ClosedByUserId",
                schema: "kniterp",
                table: "hr_timesheets",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_hr_timesheets_CreatedByUserId",
                schema: "kniterp",
                table: "hr_timesheets",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_hr_timesheets_OrganizationId_CorrectsTimesheetId",
                schema: "kniterp",
                table: "hr_timesheets",
                columns: new[] { "OrganizationId", "CorrectsTimesheetId" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_timesheets_period",
                schema: "kniterp",
                table: "hr_timesheets",
                columns: new[] { "OrganizationId", "DepartmentId", "Year", "Month", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_hr_work_schedules_name",
                schema: "kniterp",
                table: "hr_work_schedules",
                columns: new[] { "OrganizationId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            // Закрытый табель не меняется (D208): правка — исправительным табелем. Табели не удаляются.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_timesheets_closed_immutable]
                ON [kniterp].[hr_timesheets]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted d WHERE d.[Status] = 2)
                       OR (NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted))
                        THROW 51000, N''Закрытый табель неизменяем, табели не удаляются: исправление — исправительным табелем.'', 1;
                END');
                """);

            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_timesheet_days_closed_immutable]
                ON [kniterp].[hr_timesheet_days]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted i JOIN [kniterp].[hr_timesheets] t ON t.[Id] = i.[TimesheetId] WHERE t.[Status] = 2)
                       OR EXISTS (SELECT 1 FROM deleted d JOIN [kniterp].[hr_timesheets] t ON t.[Id] = d.[TimesheetId] WHERE t.[Status] = 2)
                        THROW 51000, N''Закрытый табель неизменяем: исправление — исправительным табелем.'', 1;
                END');
                """);

            // D208: права табеля системным ролям существующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES ('owner', 'hr.timesheet.edit', 3), ('owner', 'hr.timesheet.close', 3),
                             ('department_head', 'hr.timesheet.edit', 2), ('department_head', 'hr.timesheet.close', 2),
                             ('employee', 'hr.timesheet.edit', 11), ('hr_officer', 'hr.timesheet.edit', 3)) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('hr.timesheet.edit', 'hr.timesheet.close');
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_timesheet_days_closed_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_timesheets_closed_immutable];
                """);
            migrationBuilder.DropTable(
                name: "hr_employee_schedules",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_timesheet_days",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_work_schedules",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_timesheets",
                schema: "kniterp");
        }
    }
}
