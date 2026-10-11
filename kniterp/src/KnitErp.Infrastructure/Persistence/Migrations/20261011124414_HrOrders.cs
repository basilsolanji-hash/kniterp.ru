using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_orders",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: true),
                    PersonnelNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MiddleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: true),
                    PositionId = table.Column<long>(type: "bigint", nullable: true),
                    Rate = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: true),
                    Salary = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    TrialMonths = table.Column<byte>(type: "tinyint", nullable: true),
                    LeaveKind = table.Column<byte>(type: "tinyint", nullable: true),
                    DismissalGround = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Basis = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PostedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_orders", x => x.Id);
                    table.UniqueConstraint("ak_hr_orders_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_orders_fields", "([Kind] = 1 AND [PersonnelNumber] IS NOT NULL AND [LastName] IS NOT NULL AND [FirstName] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL AND ([Status] <> 2 OR [EmployeeId] IS NOT NULL)) OR ([Kind] = 2 AND [EmployeeId] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL) OR ([Kind] = 3 AND [EmployeeId] IS NOT NULL AND [LeaveKind] IS NOT NULL AND [EffectiveTo] >= [EffectiveFrom]) OR ([Kind] = 4 AND [EmployeeId] IS NOT NULL AND [DismissalGround] IS NOT NULL)");
                    table.CheckConstraint("ck_hr_orders_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_hr_orders_posted", "([Status] = 2 AND [PostedAtUtc] IS NOT NULL AND [PostedByUserId] IS NOT NULL) OR ([Status] <> 2 AND [PostedAtUtc] IS NULL AND [PostedByUserId] IS NULL)");
                    table.CheckConstraint("ck_hr_orders_rate", "[Rate] IS NULL OR ([Rate] > 0 AND [Rate] <= 1.5)");
                    table.CheckConstraint("ck_hr_orders_status", "[Status] IN (1, 2, 9)");
                    table.ForeignKey(
                        name: "FK_hr_orders_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_orders_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_orders_users_PostedByUserId",
                        column: x => x.PostedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_orders_department",
                        columns: x => new { x.OrganizationId, x.DepartmentId },
                        principalSchema: "kniterp",
                        principalTable: "departments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_orders_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_orders_position",
                        columns: x => new { x.OrganizationId, x.PositionId },
                        principalSchema: "kniterp",
                        principalTable: "positions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_staffing",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: false),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    Rates = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: false),
                    Salary = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Allowance = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_staffing", x => x.Id);
                    table.CheckConstraint("ck_hr_staffing_money", "[Salary] >= 0 AND [Allowance] >= 0");
                    table.CheckConstraint("ck_hr_staffing_rates", "[Rates] > 0");
                    table.ForeignKey(
                        name: "FK_hr_staffing_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_staffing_department",
                        columns: x => new { x.OrganizationId, x.DepartmentId },
                        principalSchema: "kniterp",
                        principalTable: "departments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_staffing_position",
                        columns: x => new { x.OrganizationId, x.PositionId },
                        principalSchema: "kniterp",
                        principalTable: "positions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_employment_history",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: false),
                    PositionId = table.Column<long>(type: "bigint", nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(4,2)", precision: 4, scale: 2, nullable: false),
                    Salary = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    LeaveKind = table.Column<byte>(type: "tinyint", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employment_history", x => x.Id);
                    table.CheckConstraint("ck_hr_employment_history_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_hr_employment_history_rate", "[Rate] > 0");
                    table.ForeignKey(
                        name: "FK_hr_employment_history_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employment_history_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employment_history_order",
                        columns: x => new { x.OrganizationId, x.OrderId },
                        principalSchema: "kniterp",
                        principalTable: "hr_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hr_employment_history_employee",
                schema: "kniterp",
                table: "hr_employment_history",
                columns: new[] { "OrganizationId", "EmployeeId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employment_history_OrganizationId_OrderId",
                schema: "kniterp",
                table: "hr_employment_history",
                columns: new[] { "OrganizationId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_orders_CreatedByUserId",
                schema: "kniterp",
                table: "hr_orders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_hr_orders_employee",
                schema: "kniterp",
                table: "hr_orders",
                columns: new[] { "OrganizationId", "EmployeeId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_orders_OrganizationId_DepartmentId",
                schema: "kniterp",
                table: "hr_orders",
                columns: new[] { "OrganizationId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_orders_OrganizationId_PositionId",
                schema: "kniterp",
                table: "hr_orders",
                columns: new[] { "OrganizationId", "PositionId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_orders_PostedByUserId",
                schema: "kniterp",
                table: "hr_orders",
                column: "PostedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_orders_number",
                schema: "kniterp",
                table: "hr_orders",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_hr_staffing_OrganizationId_PositionId",
                schema: "kniterp",
                table: "hr_staffing",
                columns: new[] { "OrganizationId", "PositionId" });

            migrationBuilder.CreateIndex(
                name: "ux_hr_staffing_active",
                schema: "kniterp",
                table: "hr_staffing",
                columns: new[] { "OrganizationId", "DepartmentId", "PositionId" },
                unique: true,
                filter: "[IsArchived] = 0");

            // Проведённый приказ не меняется и не удаляется (D204); черновик можно изменить или аннулировать.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_orders_posted_immutable]
                ON [kniterp].[hr_orders]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted d WHERE d.[Status] = 2)
                       OR (NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted))
                        THROW 51000, N''Проведённый кадровый приказ неизменяем, приказы не удаляются: исправление — новым приказом.'', 1;
                END');
                """);

            // История назначений — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_employment_history_immutable]
                ON [kniterp].[hr_employment_history]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''История назначений неизменяема: изменение и удаление запрещены.'', 1;
                END');
                """);

            // D205: права приказов Владельцу и роль «Кадровик» в существующих организациях (новые получают её из HrRoleMatrix).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                CROSS JOIN (VALUES ('hr.order.create'), ('hr.order.post')) AS p([Code])
                WHERE r.[IsSystem] = 1 AND r.[Code] = 'owner'
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);

                INSERT INTO [kniterp].[roles] ([OrganizationId], [Code], [Name], [IsSystem])
                SELECT o.[Id], 'hr_officer', N'Кадровик', 1
                FROM [kniterp].[organizations] o
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[roles] r WHERE r.[OrganizationId] = o.[Id] AND r.[Code] = 'hr_officer');

                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                CROSS JOIN (VALUES ('structure.department.view', 3), ('hr.employee.view', 3), ('hr.employee.edit', 3),
                                   ('hr.personal_data.view', 3), ('hr.personal_data.edit', 3), ('hr.order.create', 3),
                                   ('hr.order.post', 3), ('audit.log.view', 11)) AS p([Code], [Level])
                WHERE r.[IsSystem] = 1 AND r.[Code] = 'hr_officer'
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE p FROM [kniterp].[role_permissions] p JOIN [kniterp].[roles] r ON r.[Id] = p.[RoleId] WHERE r.[Code] = 'hr_officer';
                DELETE FROM [kniterp].[roles] WHERE [Code] = 'hr_officer' AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_assignments] a WHERE a.[RoleId] = [kniterp].[roles].[Id]);
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('hr.order.create', 'hr.order.post');
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_employment_history_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_hr_orders_posted_immutable];
                """);
            migrationBuilder.DropTable(
                name: "hr_employment_history",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_staffing",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_orders",
                schema: "kniterp");
        }
    }
}
