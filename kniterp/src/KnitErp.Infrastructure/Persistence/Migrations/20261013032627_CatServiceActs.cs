using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatServiceActs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_counterparty",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_kind",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_purchase_order",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_sales_order",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operation_reasons_kind",
                schema: "kniterp",
                table: "operation_reasons");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_counterparty",
                schema: "kniterp",
                table: "stock_documents",
                sql: "([Kind] = 1) OR ([Kind] IN (5, 6, 7, 8, 9) AND [CounterpartyId] IS NOT NULL) OR ([Kind] NOT IN (1, 5, 6, 7, 8, 9) AND [CounterpartyId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_kind",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[Kind] IN (1, 2, 3, 5, 6, 7, 8, 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_purchase_order",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[PurchaseOrderId] IS NULL OR [Kind] IN (1, 5, 9)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_sales_order",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[SalesOrderId] IS NULL OR [Kind] IN (6, 7, 8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_service_act_order",
                schema: "kniterp",
                table: "stock_documents",
                sql: "([Kind] <> 8 OR [SalesOrderId] IS NOT NULL) AND ([Kind] <> 9 OR [PurchaseOrderId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operation_reasons_kind",
                schema: "kniterp",
                table: "operation_reasons",
                sql: "[Kind] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");

            // D354 (сессия 4, Р1): организациям, созданным раньше, — причины актов услуг (как OperationReason.Defaults).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[operation_reasons] ([OrganizationId], [Kind], [Name], [RequiresComment], [IsArchived])
                SELECT o.[Id], d.[Kind], d.[Name], 0, 0
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES (8, N'Оказание услуг'), (9, N'Услуги поставщика')) AS d([Kind], [Name])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[operation_reasons] r WHERE r.[OrganizationId] = o.[Id] AND r.[Kind] = d.[Kind]);
                """);

            // D354: строка складского документа — хранимая позиция; строка акта услуг (8, 9) — только услуга своей организации,
            // без ссылки на комплект. Проверка комплектов (D352) — прежняя.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_document_lines_bundle]
                ON [kniterp].[stock_document_lines]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted l
                               JOIN [kniterp].[stock_documents] d ON d.[Id] = l.[DocumentId]
                               JOIN [kniterp].[items] i ON i.[Id] = l.[ItemId]
                               WHERE d.[Kind] NOT IN (8, 9) AND i.[Type] IN (10, 11))
                        THROW 51300, N''Услуги и комплекты не хранятся на складе: в строке документа — только хранимая позиция.'', 1;
                    IF EXISTS (SELECT 1 FROM inserted l
                               JOIN [kniterp].[stock_documents] d ON d.[Id] = l.[DocumentId]
                               JOIN [kniterp].[items] i ON i.[Id] = l.[ItemId]
                               WHERE d.[Kind] IN (8, 9)
                                 AND (i.[Type] <> 10 OR i.[OrganizationId] <> d.[OrganizationId] OR l.[BundleItemId] IS NOT NULL))
                        THROW 51300, N''В акте услуг — только услуги своей организации.'', 1;
                    IF EXISTS (SELECT 1 FROM inserted l
                               JOIN [kniterp].[stock_documents] d ON d.[Id] = l.[DocumentId]
                               JOIN [kniterp].[items] b ON b.[Id] = l.[BundleItemId]
                               WHERE b.[Type] <> 11 OR b.[OrganizationId] <> d.[OrganizationId]
                                  OR d.[Kind] NOT IN (6, 7) OR d.[SalesOrderId] IS NULL)
                        THROW 51300, N''Строка комплекта — только комплект своей организации в отгрузке или возврате по заказу покупателя.'', 1;
                END');
                """);

            // D354: вид документа не меняется — иначе строки-услуги акта оказались бы в складском документе в обход проверки строк.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_documents_kind_locked]
                ON [kniterp].[stock_documents]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([Kind]) AND EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id] WHERE i.[Kind] <> d.[Kind])
                        THROW 51300, N''Вид документа не меняется — оформите новый документ.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_documents_kind_locked]');");
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
            migrationBuilder.Sql("""
                DELETE r FROM [kniterp].[operation_reasons] r
                WHERE r.[Kind] IN (8, 9) AND NOT EXISTS (SELECT 1 FROM [kniterp].[stock_documents] d WHERE d.[ReasonId] = r.[Id]);
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_counterparty",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_kind",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_purchase_order",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_sales_order",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_service_act_order",
                schema: "kniterp",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operation_reasons_kind",
                schema: "kniterp",
                table: "operation_reasons");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_counterparty",
                schema: "kniterp",
                table: "stock_documents",
                sql: "([Kind] = 1) OR ([Kind] IN (5, 6, 7) AND [CounterpartyId] IS NOT NULL) OR ([Kind] NOT IN (1, 5, 6, 7) AND [CounterpartyId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_kind",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[Kind] IN (1, 2, 3, 5, 6, 7)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_purchase_order",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[PurchaseOrderId] IS NULL OR [Kind] IN (1, 5)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_sales_order",
                schema: "kniterp",
                table: "stock_documents",
                sql: "[SalesOrderId] IS NULL OR [Kind] IN (6, 7)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operation_reasons_kind",
                schema: "kniterp",
                table: "operation_reasons",
                sql: "[Kind] IN (1, 2, 3, 4, 5, 6, 7)");
        }
    }
}
