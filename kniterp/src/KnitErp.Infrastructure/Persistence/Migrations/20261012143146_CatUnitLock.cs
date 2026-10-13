using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatUnitLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D339: единица позиции в учёте не меняется — количества в движениях и документах записаны в её единице.
            // Защита в базе дополняет проверку сервиса (запрос в обход сервиса тоже не пройдёт).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_items_unit_locked]
                ON [kniterp].[items]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([UnitId]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE i.[UnitId] <> d.[UnitId]
                          AND (EXISTS (SELECT 1 FROM [kniterp].[stock_movements] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_batches] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_batch_movements] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_document_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_document_batch_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[opening_balance_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[opening_balance_batch_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[inventory_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[inventory_batch_counts] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[sales_order_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[purchase_order_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[customer_invoice_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[tech_card_lines] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[tech_cards] x WHERE x.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[bundle_components] x WHERE x.[ComponentItemId] = i.[Id])))
                        THROW 51310, N''Позиция уже в учёте: единицу измерения менять нельзя.'', 1;
                END');
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_items_unit_locked]');");

        }
    }
}
