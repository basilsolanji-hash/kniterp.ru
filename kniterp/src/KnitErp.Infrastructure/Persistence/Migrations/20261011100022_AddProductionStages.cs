using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_operations",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ProductionOrderId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<byte>(type: "tinyint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    ReturnToStage = table.Column<byte>(type: "tinyint", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_operations", x => x.Id);
                    table.CheckConstraint("ck_production_operations_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_production_operations_qc", "[Kind] IN (1, 2) OR [Stage] = 4");
                    table.CheckConstraint("ck_production_operations_quantity", "([Kind] = 2 AND [Quantity] < 0) OR ([Kind] <> 2 AND [Quantity] > 0)");
                    table.CheckConstraint("ck_production_operations_reason", "[Kind] NOT IN (2, 4) OR [Reason] IS NOT NULL");
                    table.CheckConstraint("ck_production_operations_return", "([Kind] = 3 AND [ReturnToStage] IN (1, 2, 3)) OR ([Kind] <> 3 AND [ReturnToStage] IS NULL)");
                    table.CheckConstraint("ck_production_operations_stage", "[Stage] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_production_operations_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_operations_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_operations_order",
                        columns: x => new { x.OrganizationId, x.ProductionOrderId },
                        principalSchema: "kniterp",
                        principalTable: "production_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_operations_CreatedByUserId",
                schema: "kniterp",
                table: "production_operations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_production_operations_org_order",
                schema: "kniterp",
                table: "production_operations",
                columns: new[] { "OrganizationId", "ProductionOrderId" });

            // Отметки этапов только добавляются: ошибку исправляет запись «Исправление» с причиной (D92).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_production_operations_immutable]
                ON [kniterp].[production_operations]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51092, N''Отметки этапов не меняются и не удаляются — исправление вносится новой записью.'', 1;
                END');
                """);

            // Допущения D92, D93: права этапов и материалов системным ролям существующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES
                    ('owner', 'production.stage.mark', 3), ('admin', 'production.stage.mark', 3), ('department_head', 'production.stage.mark', 3),
                    ('employee', 'production.stage.mark', 10),
                    ('owner', 'production.material.move', 3), ('senior_storekeeper', 'production.material.move', 2),
                    ('storekeeper', 'production.material.move', 2)
                ) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('production.stage.mark', 'production.material.move');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_production_operations_immutable]');");

            migrationBuilder.DropTable(
                name: "production_operations",
                schema: "kniterp");
        }
    }
}
