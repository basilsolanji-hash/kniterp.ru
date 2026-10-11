using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items");

            migrationBuilder.CreateTable(
                name: "bundle_components",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    BundleItemId = table.Column<long>(type: "bigint", nullable: false),
                    ComponentItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bundle_components", x => x.Id);
                    table.CheckConstraint("ck_bundle_components_quantity", "[Quantity] > 0");
                    table.CheckConstraint("ck_bundle_components_self", "[BundleItemId] <> [ComponentItemId]");
                    table.ForeignKey(
                        name: "fk_bundle_components_bundle",
                        columns: x => new { x.OrganizationId, x.BundleItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bundle_components_component",
                        columns: x => new { x.OrganizationId, x.ComponentItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_bundle",
                schema: "kniterp",
                table: "items",
                sql: "[Type] <> 11 OR ([MinStock] IS NULL AND [ParentItemId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_type",
                schema: "kniterp",
                table: "items",
                sql: "[Type] IN (1, 2, 3, 4, 5, 6, 9, 10, 11)");

            migrationBuilder.CreateIndex(
                name: "ix_bundle_components_component",
                schema: "kniterp",
                table: "bundle_components",
                columns: new[] { "OrganizationId", "ComponentItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_bundle_components_OrganizationId_BundleItemId",
                schema: "kniterp",
                table: "bundle_components",
                columns: new[] { "OrganizationId", "BundleItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_bundle_components_pair",
                schema: "kniterp",
                table: "bundle_components",
                columns: new[] { "BundleItemId", "ComponentItemId" },
                unique: true);

            // D302: комплект не хранится на складе — триггеры склада и смены типа расширены на тип 11.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_movements_stocked_items]
                ON [kniterp].[stock_movements]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted m JOIN [kniterp].[items] i ON i.[Id] = m.[ItemId] WHERE i.[Type] IN (10, 11))
                        THROW 51300, N''Услуги и комплекты не хранятся на складе: движения по ним запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_batches_stocked_items]
                ON [kniterp].[stock_batches]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted b JOIN [kniterp].[items] i ON i.[Id] = b.[ItemId] WHERE i.[Type] IN (10, 11))
                        THROW 51300, N''Услуги и комплекты не хранятся на складе: партий у них нет.'', 1;
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
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_bundle_components_types]
                ON [kniterp].[bundle_components]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted c JOIN [kniterp].[items] i ON i.[Id] = c.[BundleItemId] WHERE i.[Type] <> 11)
                        THROW 51300, N''Состав бывает только у комплекта.'', 1;
                    IF EXISTS (SELECT 1 FROM inserted c JOIN [kniterp].[items] i ON i.[Id] = c.[ComponentItemId] WHERE i.[Type] IN (10, 11))
                        THROW 51300, N''В комплект входят только товары — не услуги и не комплекты.'', 1;
                END');
                """);

            // D302: право «Номенклатура: состав комплектов» — Владельцу и Администратору действующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], 'catalog.bundle.edit', 3
                FROM [kniterp].[roles] r
                WHERE r.[IsSystem] = 1 AND r.[Code] IN ('owner', 'admin')
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = 'catalog.bundle.edit');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = 'catalog.bundle.edit';");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_bundle_components_types]');");
            migrationBuilder.DropTable(
                name: "bundle_components",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_bundle",
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
                sql: "[Type] IN (1, 2, 3, 4, 5, 6, 9, 10)");

            // Триггеры склада — как в CatServices (только услуги).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_movements_stocked_items]
                ON [kniterp].[stock_movements] AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted m JOIN [kniterp].[items] i ON i.[Id] = m.[ItemId] WHERE i.[Type] = 10)
                        THROW 51300, N''Услуга не хранится на складе: движения по ней запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_batches_stocked_items]
                ON [kniterp].[stock_batches] AFTER INSERT, UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted b JOIN [kniterp].[items] i ON i.[Id] = b.[ItemId] WHERE i.[Type] = 10)
                        THROW 51300, N''Услуга не хранится на складе: партий у неё нет.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_items_service_without_stock]
                ON [kniterp].[items] AFTER UPDATE AS
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
    }
}
