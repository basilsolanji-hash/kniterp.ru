using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevModelDealFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Аудит 2026-10-12: ссылки на несуществующие или чужие сделки не теряются — они переносятся в таблицу-копию
            // (Down возвращает их обратно), в модели поле очищается, после чего составной FK можно создать.
            migrationBuilder.Sql(@"EXEC(N'
IF OBJECT_ID(N''kniterp.dev_models_deal_backup'', N''U'') IS NULL
    CREATE TABLE kniterp.dev_models_deal_backup (ModelId bigint NOT NULL PRIMARY KEY, OrganizationId bigint NOT NULL, DealId bigint NOT NULL,
        MovedAtUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
INSERT INTO kniterp.dev_models_deal_backup (ModelId, OrganizationId, DealId)
SELECT m.Id, m.OrganizationId, m.DealId FROM kniterp.dev_models m
WHERE m.DealId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM kniterp.crm_deals d WHERE d.Id = m.DealId AND d.OrganizationId = m.OrganizationId)
  AND NOT EXISTS (SELECT 1 FROM kniterp.dev_models_deal_backup b WHERE b.ModelId = m.Id);
UPDATE m SET DealId = NULL FROM kniterp.dev_models m
WHERE m.DealId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM kniterp.crm_deals d WHERE d.Id = m.DealId AND d.OrganizationId = m.OrganizationId);
')");

            migrationBuilder.CreateIndex(
                name: "ix_dev_samples_created",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_DealId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "DealId" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_model_stages_at",
                schema: "kniterp",
                table: "dev_model_stages",
                columns: new[] { "OrganizationId", "AtUtc" });

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_deal",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "DealId" },
                principalSchema: "kniterp",
                principalTable: "crm_deals",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_deal",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "ix_dev_samples_created",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_DealId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "ix_dev_model_stages_at",
                schema: "kniterp",
                table: "dev_model_stages");

            migrationBuilder.Sql(@"EXEC(N'
IF OBJECT_ID(N''kniterp.dev_models_deal_backup'', N''U'') IS NOT NULL
BEGIN
    UPDATE m SET DealId = b.DealId FROM kniterp.dev_models m JOIN kniterp.dev_models_deal_backup b ON b.ModelId = m.Id WHERE m.DealId IS NULL;
    DROP TABLE kniterp.dev_models_deal_backup;
END
')");
        }
    }
}
