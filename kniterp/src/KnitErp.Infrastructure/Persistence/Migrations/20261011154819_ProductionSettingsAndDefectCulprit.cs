using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionSettingsAndDefectCulprit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CulpritEmployeeId",
                schema: "kniterp",
                table: "production_operations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "production_settings",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    TolerancePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    PointsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PointsTaskDone = table.Column<int>(type: "int", nullable: false),
                    PointsTaskMissedWithReason = table.Column<int>(type: "int", nullable: false),
                    PointsTaskMissedNoReason = table.Column<int>(type: "int", nullable: false),
                    PointsPerDefectUnit = table.Column<int>(type: "int", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_settings", x => x.OrganizationId);
                    table.CheckConstraint("ck_production_settings_points", "ABS([PointsTaskDone]) <= 100 AND ABS([PointsTaskMissedWithReason]) <= 100 AND ABS([PointsTaskMissedNoReason]) <= 100 AND ABS([PointsPerDefectUnit]) <= 100");
                    table.CheckConstraint("ck_production_settings_tolerance", "[TolerancePercent] BETWEEN 0 AND 50");
                    table.ForeignKey(
                        name: "FK_production_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_settings_users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_production_operations_org_culprit_date",
                schema: "kniterp",
                table: "production_operations",
                columns: new[] { "OrganizationId", "CulpritEmployeeId", "OccurredOn" },
                filter: "[CulpritEmployeeId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_operations_culprit",
                schema: "kniterp",
                table: "production_operations",
                sql: "[CulpritEmployeeId] IS NULL OR [Kind] = 4");

            migrationBuilder.CreateIndex(
                name: "IX_production_settings_UpdatedByUserId",
                schema: "kniterp",
                table: "production_settings",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "fk_production_operations_culprit",
                schema: "kniterp",
                table: "production_operations",
                columns: new[] { "OrganizationId", "CulpritEmployeeId" },
                principalSchema: "kniterp",
                principalTable: "employees",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_production_operations_culprit",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropTable(
                name: "production_settings",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ix_production_operations_org_culprit_date",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_operations_culprit",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropColumn(
                name: "CulpritEmployeeId",
                schema: "kniterp",
                table: "production_operations");
        }
    }
}
