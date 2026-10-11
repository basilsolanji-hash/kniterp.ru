using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_packs",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    UnitId = table.Column<long>(type: "bigint", nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Barcode = table.Column<string>(type: "varchar(64)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_packs", x => x.Id);
                    table.CheckConstraint("ck_item_packs_factor", "[Factor] > 0");
                    table.ForeignKey(
                        name: "fk_item_packs_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_packs_unit",
                        columns: x => new { x.OrganizationId, x.UnitId },
                        principalSchema: "kniterp",
                        principalTable: "units",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_item_packs_OrganizationId_ItemId",
                schema: "kniterp",
                table: "item_packs",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_item_packs_OrganizationId_UnitId",
                schema: "kniterp",
                table: "item_packs",
                columns: new[] { "OrganizationId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "ux_item_packs_item_unit",
                schema: "kniterp",
                table: "item_packs",
                columns: new[] { "ItemId", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_item_packs_org_barcode",
                schema: "kniterp",
                table: "item_packs",
                columns: new[] { "OrganizationId", "Barcode" },
                unique: true,
                filter: "[Barcode] IS NOT NULL");

            // D307: позицию с упаковками нельзя сделать услугой или комплектом — триггер смены типа дополнен упаковками.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_items_service_without_stock]
                ON [kniterp].[items]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([Type]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE i.[Type] IN (10, 11) AND d.[Type] NOT IN (10, 11)
                          AND (EXISTS (SELECT 1 FROM [kniterp].[stock_movements] m WHERE m.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_batches] b WHERE b.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[bundle_components] c WHERE c.[ComponentItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[item_packs] p WHERE p.[ItemId] = i.[Id])))
                        THROW 51300, N''Позицию со складской историей, упаковками или из состава комплекта нельзя сделать услугой или комплектом.'', 1;
                    IF UPDATE([Type]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE d.[Type] = 11 AND i.[Type] <> 11
                          AND EXISTS (SELECT 1 FROM [kniterp].[bundle_components] c WHERE c.[BundleItemId] = i.[Id]))
                        THROW 51300, N''У комплекта есть состав — сначала уберите компоненты.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Триггер смены типа — как в CatBundles (без упаковок).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_items_service_without_stock]
                ON [kniterp].[items] AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([Type]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE i.[Type] IN (10, 11) AND d.[Type] NOT IN (10, 11)
                          AND (EXISTS (SELECT 1 FROM [kniterp].[stock_movements] m WHERE m.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_batches] b WHERE b.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[bundle_components] c WHERE c.[ComponentItemId] = i.[Id])))
                        THROW 51300, N''Позицию со складской историей или из состава комплекта нельзя сделать услугой или комплектом.'', 1;
                    IF UPDATE([Type]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE d.[Type] = 11 AND i.[Type] <> 11
                          AND EXISTS (SELECT 1 FROM [kniterp].[bundle_components] c WHERE c.[BundleItemId] = i.[Id]))
                        THROW 51300, N''У комплекта есть состав — сначала уберите компоненты.'', 1;
                END');
                """);

            migrationBuilder.DropTable(
                name: "item_packs",
                schema: "kniterp");
        }
    }
}
