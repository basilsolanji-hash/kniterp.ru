using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionRouteByItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Route",
                schema: "kniterp",
                table: "tech_cards",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)31);

            migrationBuilder.AddColumn<byte>(
                name: "Route",
                schema: "kniterp",
                table: "production_orders",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)31);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tech_cards_route",
                schema: "kniterp",
                table: "tech_cards",
                sql: "([Route] & 9) = 9 AND [Route] <= 31");

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_orders_route",
                schema: "kniterp",
                table: "production_orders",
                sql: "([Route] & 9) = 9 AND [Route] <= 31");

            // Маршрут запущенного заказа (снимок техкарты, D401) защищён тем же триггером, что изделие, количество и техкарта.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_production_orders_fixed_after_release]
                ON [kniterp].[production_orders]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                               WHERE d.[Status] <> 1 AND (i.[ItemId] <> d.[ItemId] OR i.[Quantity] <> d.[Quantity] OR i.[TechCardId] <> d.[TechCardId] OR i.[Route] <> d.[Route]))
                        THROW 51091, N''Изделие, количество, техкарта и маршрут запущенного заказа не меняются.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_production_orders_fixed_after_release]
                ON [kniterp].[production_orders]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.[Id] = i.[Id]
                               WHERE d.[Status] <> 1 AND (i.[ItemId] <> d.[ItemId] OR i.[Quantity] <> d.[Quantity] OR i.[TechCardId] <> d.[TechCardId]))
                        THROW 51091, N''Изделие, количество и техкарта запущенного заказа не меняются.'', 1;
                END');
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_tech_cards_route",
                schema: "kniterp",
                table: "tech_cards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_orders_route",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "Route",
                schema: "kniterp",
                table: "tech_cards");

            migrationBuilder.DropColumn(
                name: "Route",
                schema: "kniterp",
                table: "production_orders");
        }
    }
}
