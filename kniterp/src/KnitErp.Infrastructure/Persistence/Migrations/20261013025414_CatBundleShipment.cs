using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatBundleShipment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_doc_item",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.AddColumn<long>(
                name: "BundleItemId",
                schema: "kniterp",
                table: "stock_document_lines",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BundleQuantity",
                schema: "kniterp",
                table: "stock_document_lines",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines",
                column: "BundleItemId",
                filter: "[BundleItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_doc_item_bundle",
                schema: "kniterp",
                table: "stock_document_lines",
                columns: new[] { "DocumentId", "ItemId", "BundleItemId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines",
                sql: "([BundleItemId] IS NULL AND [BundleQuantity] IS NULL) OR ([BundleItemId] IS NOT NULL AND [BundleQuantity] IS NOT NULL AND [BundleQuantity] > 0)");

            migrationBuilder.AddForeignKey(
                name: "fk_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines",
                column: "BundleItemId",
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // D352 (сессия 4, Р2): строка складского документа — всегда хранимая позиция (компонент), комплект указывается только
            // ссылкой BundleItemId: комплект своей организации и только в отгрузке или возврате по заказу покупателя.
            // Сам комплект движений не получает — это держит и tr_stock_movements_stocked_items (CatBundles).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_document_lines_bundle]
                ON [kniterp].[stock_document_lines]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted l JOIN [kniterp].[items] i ON i.[Id] = l.[ItemId] WHERE i.[Type] IN (10, 11))
                        THROW 51300, N''Услуги и комплекты не хранятся на складе: в строке документа — только хранимая позиция.'', 1;
                    IF EXISTS (SELECT 1 FROM inserted l
                               JOIN [kniterp].[stock_documents] d ON d.[Id] = l.[DocumentId]
                               JOIN [kniterp].[items] b ON b.[Id] = l.[BundleItemId]
                               WHERE b.[Type] <> 11 OR b.[OrganizationId] <> d.[OrganizationId]
                                  OR d.[Kind] NOT IN (6, 7) OR d.[SalesOrderId] IS NULL)
                        THROW 51300, N''Строка комплекта — только комплект своей организации в отгрузке или возврате по заказу покупателя.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_document_lines_bundle]');");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.DropIndex(
                name: "ix_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_doc_item_bundle",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_document_lines_bundle",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.DropColumn(
                name: "BundleItemId",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.DropColumn(
                name: "BundleQuantity",
                schema: "kniterp",
                table: "stock_document_lines");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_doc_item",
                schema: "kniterp",
                table: "stock_document_lines",
                columns: new[] { "DocumentId", "ItemId" },
                unique: true);
        }
    }
}
