using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Gtin",
                schema: "kniterp",
                table: "items",
                type: "varchar(14)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMarked",
                schema: "kniterp",
                table: "items",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MinPrice",
                schema: "kniterp",
                table: "items",
                type: "decimal(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "PaymentSubject",
                schema: "kniterp",
                table: "items",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte>(
                name: "TrackingType",
                schema: "kniterp",
                table: "items",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            // D305: у действующих услуг признак предмета расчёта — «услуга» (до CHECK ck_items_payment_subject).
            // Через EXEC: в идемпотентном скрипте пакет компилируется до добавления колонки.
            migrationBuilder.Sql("EXEC(N'UPDATE [kniterp].[items] SET [PaymentSubject] = 4 WHERE [Type] = 10;');");

            migrationBuilder.CreateTable(
                name: "price_revaluations",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<byte>(type: "tinyint", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Rounding = table.Column<byte>(type: "tinyint", nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Items = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_revaluations", x => x.Id);
                    table.CheckConstraint("ck_price_revaluations_mode", "[Mode] IN (1, 2) AND [Rounding] IN (0, 1, 2, 3) AND [Items] >= 0");
                    table.ForeignKey(
                        name: "FK_price_revaluations_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_revaluations_type",
                        columns: x => new { x.OrganizationId, x.PriceTypeId },
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_price_history",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: false),
                    OldPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    NewPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    RevaluationId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_price_history", x => x.Id);
                    table.CheckConstraint("ck_item_price_history_prices", "([OldPrice] IS NULL OR [OldPrice] >= 0) AND ([NewPrice] IS NULL OR [NewPrice] >= 0)");
                    table.CheckConstraint("ck_item_price_history_source", "[Source] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "fk_item_price_history_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_price_history_revaluation",
                        column: x => x.RevaluationId,
                        principalSchema: "kniterp",
                        principalTable: "price_revaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_price_history_type",
                        columns: x => new { x.OrganizationId, x.PriceTypeId },
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_items_org_gtin",
                schema: "kniterp",
                table: "items",
                columns: new[] { "OrganizationId", "Gtin" },
                unique: true,
                filter: "[Gtin] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_gtin",
                schema: "kniterp",
                table: "items",
                sql: "[Gtin] IS NULL OR (LEN([Gtin]) = 14 AND [Gtin] NOT LIKE '%[^0-9]%')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_marking",
                schema: "kniterp",
                table: "items",
                sql: "([IsMarked] = 0 AND [TrackingType] = 0) OR ([IsMarked] = 1 AND [TrackingType] IN (1, 2, 3, 9) AND [Type] <> 10)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_min_price",
                schema: "kniterp",
                table: "items",
                sql: "[MinPrice] IS NULL OR [MinPrice] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_payment_subject",
                schema: "kniterp",
                table: "items",
                sql: "([Type] = 10 AND [PaymentSubject] = 4) OR ([Type] <> 10 AND [PaymentSubject] IN (1, 2))");

            migrationBuilder.CreateIndex(
                name: "ix_item_price_history_item_time",
                schema: "kniterp",
                table: "item_price_history",
                columns: new[] { "ItemId", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_item_price_history_OrganizationId_ItemId",
                schema: "kniterp",
                table: "item_price_history",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_item_price_history_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "item_price_history",
                columns: new[] { "OrganizationId", "PriceTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_item_price_history_RevaluationId",
                schema: "kniterp",
                table: "item_price_history",
                column: "RevaluationId");

            migrationBuilder.CreateIndex(
                name: "ix_price_revaluations_org_time",
                schema: "kniterp",
                table: "price_revaluations",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_price_revaluations_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "price_revaluations",
                columns: new[] { "OrganizationId", "PriceTypeId" });

            // D305, D306: история цен и журнал переоценок — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_item_price_history_immutable]
                ON [kniterp].[item_price_history]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51301, N''История цен неизменяема: исправление — новой ценой.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_price_revaluations_immutable]
                ON [kniterp].[price_revaluations]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51301, N''Журнал переоценок неизменяем.'', 1;
                END');
                """);

            // D306: право «Цены: переоценка» — Владельцу и Администратору действующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], 'catalog.price.edit', 3
                FROM [kniterp].[roles] r
                WHERE r.[IsSystem] = 1 AND r.[Code] IN ('owner', 'admin')
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = 'catalog.price.edit');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = 'catalog.price.edit';");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_price_revaluations_immutable]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_item_price_history_immutable]');");

            migrationBuilder.DropTable(
                name: "item_price_history",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "price_revaluations",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ux_items_org_gtin",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_gtin",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_marking",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_min_price",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_payment_subject",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "Gtin",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "IsMarked",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "MinPrice",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "PaymentSubject",
                schema: "kniterp",
                table: "items");

            migrationBuilder.DropColumn(
                name: "TrackingType",
                schema: "kniterp",
                table: "items");
        }
    }
}
