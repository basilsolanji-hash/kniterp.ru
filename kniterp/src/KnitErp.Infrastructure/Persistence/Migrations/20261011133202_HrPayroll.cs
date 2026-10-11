using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_payroll_runs",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Year = table.Column<short>(type: "smallint", nullable: false),
                    Month = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    TimesheetsClosed = table.Column<bool>(type: "bit", nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CalculatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_payroll_runs", x => x.Id);
                    table.UniqueConstraint("ak_hr_payroll_runs_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_payroll_runs_approved", "([Status] = 2 AND [ApprovedAtUtc] IS NOT NULL AND [ApprovedByUserId] IS NOT NULL AND [TimesheetsClosed] = 1) OR ([Status] = 1 AND [ApprovedAtUtc] IS NULL AND [ApprovedByUserId] IS NULL)");
                    table.CheckConstraint("ck_hr_payroll_runs_period", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2100");
                    table.CheckConstraint("ck_hr_payroll_runs_status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_hr_payroll_runs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_payroll_runs_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_payroll_runs_users_CalculatedByUserId",
                        column: x => x.CalculatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_piece_rates",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<long>(type: "bigint", nullable: false),
                    OperationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_piece_rates", x => x.Id);
                    table.CheckConstraint("ck_hr_piece_rates_price", "[Price] > 0");
                    table.CheckConstraint("ck_hr_piece_rates_unit", "[Unit] IN (N'шт', N'кг')");
                    table.ForeignKey(
                        name: "FK_hr_piece_rates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_piece_rates_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_payroll_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_payroll_lines", x => x.Id);
                    table.CheckConstraint("ck_hr_payroll_lines_kind", "[Kind] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_hr_payroll_lines_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_payroll_lines_run",
                        columns: x => new { x.OrganizationId, x.RunId },
                        principalSchema: "kniterp",
                        principalTable: "hr_payroll_runs",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hr_payroll_lines_OrganizationId_EmployeeId",
                schema: "kniterp",
                table: "hr_payroll_lines",
                columns: new[] { "OrganizationId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_payroll_lines_OrganizationId_RunId",
                schema: "kniterp",
                table: "hr_payroll_lines",
                columns: new[] { "OrganizationId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "ix_hr_payroll_lines_run_employee",
                schema: "kniterp",
                table: "hr_payroll_lines",
                columns: new[] { "RunId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_payroll_runs_ApprovedByUserId",
                schema: "kniterp",
                table: "hr_payroll_runs",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_hr_payroll_runs_CalculatedByUserId",
                schema: "kniterp",
                table: "hr_payroll_runs",
                column: "CalculatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_payroll_runs_period",
                schema: "kniterp",
                table: "hr_payroll_runs",
                columns: new[] { "OrganizationId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hr_piece_rates_CreatedByUserId",
                schema: "kniterp",
                table: "hr_piece_rates",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_piece_rates_from",
                schema: "kniterp",
                table: "hr_piece_rates",
                columns: new[] { "OrganizationId", "OperationId", "ItemId", "ValidFrom" },
                unique: true,
                filter: "[ItemId] IS NOT NULL");

            // Расценки — только вставка (D211): новая цена — новая строка с новой датой.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_piece_rates_immutable]
                ON [kniterp].[hr_piece_rates]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''История расценок неизменяема: новая цена — новой строкой с новой датой.'', 1;
                END');
                """);

            // Утверждённый расчёт и его строки не меняются и не удаляются (D212); черновик пересчитывается.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_payroll_runs_approved_immutable]
                ON [kniterp].[hr_payroll_runs]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted d WHERE d.[Status] = 2)
                       OR (NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted))
                        THROW 51000, N''Утверждённый расчёт заработка неизменяем, расчёты не удаляются.'', 1;
                END');
                """);

            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_payroll_lines_approved_immutable]
                ON [kniterp].[hr_payroll_lines]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted i JOIN [kniterp].[hr_payroll_runs] r ON r.[Id] = i.[RunId] WHERE r.[Status] = 2)
                       OR EXISTS (SELECT 1 FROM deleted d JOIN [kniterp].[hr_payroll_runs] r ON r.[Id] = d.[RunId] WHERE r.[Status] = 2)
                        THROW 51000, N''Утверждённый расчёт заработка неизменяем.'', 1;
                END');
                """);

            // D212: права заработка системным ролям существующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES ('owner', 'hr.payroll.view', 3), ('owner', 'hr.payroll.calculate', 3),
                             ('accountant', 'hr.payroll.view', 10), ('accountant', 'hr.payroll.calculate', 10),
                             ('auditor', 'hr.payroll.view', 1), ('employee', 'hr.payroll.view', 11)) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('hr.payroll.view', 'hr.payroll.calculate');
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_payroll_lines_approved_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_payroll_runs_approved_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_piece_rates_immutable];
                """);
            migrationBuilder.DropTable(
                name: "hr_payroll_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_piece_rates",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_payroll_runs",
                schema: "kniterp");
        }
    }
}
