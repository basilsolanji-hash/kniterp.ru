using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DepartmentHeadSalesView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Допущение D91 (дополнение): Руководителю подразделения — просмотр заказов покупателей (уровень «Ч»), без цен.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], 'sales.document.view', 1
                FROM [kniterp].[roles] r
                WHERE r.[IsSystem] = 1 AND r.[Code] = 'department_head'
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = 'sales.document.view');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE p FROM [kniterp].[role_permissions] p
                JOIN [kniterp].[roles] r ON r.[Id] = p.[RoleId]
                WHERE r.[IsSystem] = 1 AND r.[Code] = 'department_head' AND p.[PermissionCode] = 'sales.document.view';
                """);
        }
    }
}
