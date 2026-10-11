using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionRepeatWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RepeatQuantity",
                schema: "kniterp",
                table: "production_operations",
                type: "decimal(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_operations_repeat",
                schema: "kniterp",
                table: "production_operations",
                sql: "([Kind] = 1 AND [RepeatQuantity] >= 0 AND [RepeatQuantity] <= [Quantity]) OR ([Kind] = 2 AND [RepeatQuantity] <= 0 AND [RepeatQuantity] >= [Quantity]) OR [RepeatQuantity] = 0");

            // Допущение D412: право «указание виновника брака» системным ролям существующих организаций (как в RoleMatrixP0).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES
                    ('owner', 'production.defect.culprit', 3), ('admin', 'production.defect.culprit', 3),
                    ('department_head', 'production.defect.culprit', 3), ('employee', 'production.defect.culprit', 10)
                ) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = 'production.defect.culprit';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_operations_repeat",
                schema: "kniterp",
                table: "production_operations");

            migrationBuilder.DropColumn(
                name: "RepeatQuantity",
                schema: "kniterp",
                table: "production_operations");
        }
    }
}
