using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionShiftTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_shift_tasks",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    ProductionOrderId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<byte>(type: "tinyint", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortfallReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShortfallComment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortfallAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_shift_tasks", x => x.Id);
                    table.CheckConstraint("ck_production_shift_tasks_cancelled", "([CancelledAtUtc] IS NULL AND [CancelledByUserId] IS NULL) OR ([CancelledAtUtc] IS NOT NULL AND [CancelledByUserId] IS NOT NULL)");
                    table.CheckConstraint("ck_production_shift_tasks_planned", "[PlannedQuantity] > 0");
                    table.CheckConstraint("ck_production_shift_tasks_shortfall", "([ShortfallReason] IS NULL AND [ShortfallAtUtc] IS NULL) OR ([ShortfallReason] IS NOT NULL AND [ShortfallAtUtc] IS NOT NULL)");
                    table.CheckConstraint("ck_production_shift_tasks_stage", "[Stage] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_production_shift_tasks_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_shift_tasks_users_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_shift_tasks_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_shift_tasks_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_shift_tasks_order",
                        columns: x => new { x.OrganizationId, x.ProductionOrderId },
                        principalSchema: "kniterp",
                        principalTable: "production_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_shift_tasks_CancelledByUserId",
                schema: "kniterp",
                table: "production_shift_tasks",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_production_shift_tasks_CreatedByUserId",
                schema: "kniterp",
                table: "production_shift_tasks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_production_shift_tasks_org_date",
                schema: "kniterp",
                table: "production_shift_tasks",
                columns: new[] { "OrganizationId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_production_shift_tasks_OrganizationId_EmployeeId",
                schema: "kniterp",
                table: "production_shift_tasks",
                columns: new[] { "OrganizationId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_shift_tasks_OrganizationId_ProductionOrderId",
                schema: "kniterp",
                table: "production_shift_tasks",
                columns: new[] { "OrganizationId", "ProductionOrderId" });

            migrationBuilder.CreateIndex(
                name: "ux_production_shift_tasks_active",
                schema: "kniterp",
                table: "production_shift_tasks",
                columns: new[] { "OrganizationId", "Date", "EmployeeId", "ProductionOrderId", "Stage" },
                unique: true,
                filter: "[CancelledAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_shift_tasks",
                schema: "kniterp");
        }
    }
}
