using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModuleRangeRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D117/D118: миграции CRM (CrmLeadsDeals) и «Разработки» (DevModels) созданы до общей миграции ModuleExtensionPoints и сужают
            // ограничение номеров объектов тегов и обсуждений до 39 и 49. На сервере, где ModuleExtensionPoints уже применена, они применятся
            // позже и сузили бы диапазон — эта миграция идёт последней и всегда возвращает 1…99 (объекты всех модулей, D117).
            foreach (var (table, constraint, column) in new[]
                     {
                         ("tag_links", "ck_tag_links_entity", "Entity"),
                         ("discussion_comments", "ck_discussion_comments_target", "Target"),
                     })
            {
                migrationBuilder.Sql($"""
                    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'{constraint}')
                        EXEC(N'ALTER TABLE [kniterp].[{table}] DROP CONSTRAINT [{constraint}]');
                    EXEC(N'ALTER TABLE [kniterp].[{table}] ADD CONSTRAINT [{constraint}] CHECK ([{column}] BETWEEN 1 AND 99)');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Диапазон 1…99 нужен всем модулям — откат его не сужает.
        }
    }
}
