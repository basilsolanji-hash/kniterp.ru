using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_service",
                schema: "kniterp",
                table: "items",
                sql: "[Type] <> 10 OR ([VatRateId] IS NOT NULL AND [WeightKg] IS NULL AND [VolumeM3] IS NULL AND [MinStock] IS NULL AND [ParentItemId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items",
                sql: "[Type] IN (1, 2, 3, 4, 5, 6, 9, 10)");

            // D300: единицы для услуг — «усл. ед» (ОКЕИ 876) и «ч» (356) у действующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[units] ([OrganizationId], [Code], [Name], [Symbol], [Precision], [IsArchived])
                SELECT o.[Id], d.[Code], d.[Name], d.[Symbol], d.[Precision], 0
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES (N'876', N'Условная единица', N'усл. ед', 0), (N'356', N'Час', N'ч', 2)) AS d([Code], [Name], [Symbol], [Precision])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[units] u WHERE u.[OrganizationId] = o.[Id] AND u.[Code] = d.[Code]);
                """);

            // D300: услуга не хранится на складе — ни движения, ни партии; позицию со складской историей нельзя сделать услугой.
            // Сервисы проверяют то же с понятным сообщением; триггеры — защита в обход приложения.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_movements_stocked_items]
                ON [kniterp].[stock_movements]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted m JOIN [kniterp].[items] i ON i.[Id] = m.[ItemId] WHERE i.[Type] = 10)
                        THROW 51300, N''Услуга не хранится на складе: движения по ней запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_batches_stocked_items]
                ON [kniterp].[stock_batches]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted b JOIN [kniterp].[items] i ON i.[Id] = b.[ItemId] WHERE i.[Type] = 10)
                        THROW 51300, N''Услуга не хранится на складе: партий у неё нет.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_items_service_without_stock]
                ON [kniterp].[items]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([Type]) AND EXISTS (
                        SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                        WHERE i.[Type] = 10 AND d.[Type] <> 10
                          AND (EXISTS (SELECT 1 FROM [kniterp].[stock_movements] m WHERE m.[ItemId] = i.[Id])
                               OR EXISTS (SELECT 1 FROM [kniterp].[stock_batches] b WHERE b.[ItemId] = i.[Id])))
                        THROW 51300, N''Позицию со складской историей нельзя сделать услугой.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_items_service_without_stock]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_batches_stocked_items]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_movements_stocked_items]');");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_service",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items",
                sql: "[Type] IN (1, 2, 3, 4, 5, 6, 9)");
        }
    }
}
