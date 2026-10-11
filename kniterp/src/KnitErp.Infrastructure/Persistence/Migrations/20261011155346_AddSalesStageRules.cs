using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesStageRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sales_stage_rules",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<byte>(type: "tinyint", nullable: false),
                    StageId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_stage_rules", x => x.Id);
                    table.CheckConstraint("ck_sales_stage_rules_trigger", "[Trigger] BETWEEN 1 AND 8");
                    table.ForeignKey(
                        name: "FK_sales_stage_rules_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sales_stage_rules_sales_order_stages_OrganizationId_StageId",
                        columns: x => new { x.OrganizationId, x.StageId },
                        principalSchema: "kniterp",
                        principalTable: "sales_order_stages",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_stage_rules_OrganizationId_StageId",
                schema: "kniterp",
                table: "sales_stage_rules",
                columns: new[] { "OrganizationId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_stage_rules_org_trigger",
                schema: "kniterp",
                table: "sales_stage_rules",
                columns: new[] { "OrganizationId", "Trigger" },
                unique: true);
        
            // D110: правила по умолчанию для существующих организаций — к их этапам по умолчанию (по названию, действующим).
            migrationBuilder.Sql(@"EXEC(N'
INSERT INTO [kniterp].[sales_stage_rules] ([OrganizationId], [Trigger], [StageId])
SELECT s.[OrganizationId], d.[Trigger], s.[Id]
FROM (VALUES (1, N''В обработке''), (2, N''Ждём оплату''), (4, N''Оплачен — в производство''), (7, N''В работе''), (8, N''Собран''), (6, N''Отгружен''))
     AS d([Trigger], [Name])
JOIN [kniterp].[sales_order_stages] s ON s.[Name] = d.[Name] AND s.[IsArchived] = 0
WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[sales_stage_rules] r WHERE r.[OrganizationId] = s.[OrganizationId] AND r.[Trigger] = d.[Trigger]);
')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_stage_rules",
                schema: "kniterp");
        }
    }
}
