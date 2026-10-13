using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D130: право «Теги: управление справочником» (tag.manage) системным ролям существующих организаций — как в RoleMatrixP0,
    /// HrRoleMatrix, CrmRoleMatrix и DevRoleMatrix (новые организации получают его из матриц). Только данные, модель не меняется.
    /// </summary>
    public partial class TagManagePermission : Migration
    {
        /// <summary>SQL выдачи права — открыт для интеграционного теста (повтор не создаёт дублей).</summary>
        public const string GrantSql = """
            EXEC(N'
            INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
            SELECT r.[Id], N''tag.manage'', 3
            FROM [kniterp].[roles] r
            WHERE r.[IsSystem] = 1
              AND r.[Code] IN (N''owner'', N''admin'', N''department_head'', N''senior_storekeeper'', N''accountant'',
                               N''hr_officer'', N''sales_head'', N''dev_chief'')
              AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = N''tag.manage'');
            ');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                EXEC(N'DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = N''tag.manage'';
                DELETE FROM [kniterp].[role_assignments] WHERE [PermissionCode] = N''tag.manage'';');
                """);
        }
    }
}
