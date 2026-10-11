using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_orders",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: true),
                    WipWarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    OutputWarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    TechCardId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReleasedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_orders", x => x.Id);
                    table.UniqueConstraint("ak_production_orders_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_production_orders_cancelled", "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] <> 9 AND [CancelledByUserId] IS NULL AND [CancelReason] IS NULL)");
                    table.CheckConstraint("ck_production_orders_due", "[DueDate] IS NULL OR [DueDate] >= [OrderDate]");
                    table.CheckConstraint("ck_production_orders_quantity", "[Quantity] > 0");
                    table.CheckConstraint("ck_production_orders_released", "([Status] = 1 AND [ReleasedByUserId] IS NULL) OR ([Status] IN (2, 3, 4, 5) AND [ReleasedByUserId] IS NOT NULL AND [ReleasedAtUtc] IS NOT NULL) OR [Status] = 9");
                    table.CheckConstraint("ck_production_orders_source", "([Source] = 1 AND [SalesOrderId] IS NULL) OR ([Source] = 2 AND [SalesOrderId] IS NOT NULL)");
                    table.CheckConstraint("ck_production_orders_status", "[Status] IN (1, 2, 3, 4, 5, 9)");
                    table.CheckConstraint("ck_production_orders_warehouses", "[WipWarehouseId] <> [OutputWarehouseId]");
                    table.ForeignKey(
                        name: "FK_production_orders_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_orders_users_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_orders_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_orders_users_ReleasedByUserId",
                        column: x => x.ReleasedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_orders_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_orders_output_warehouse",
                        columns: x => new { x.OrganizationId, x.OutputWarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_orders_sales_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_orders_tech_card",
                        columns: x => new { x.OrganizationId, x.TechCardId },
                        principalSchema: "kniterp",
                        principalTable: "tech_cards",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_orders_wip_warehouse",
                        columns: x => new { x.OrganizationId, x.WipWarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_order_materials",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductionOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    PlannedNet = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    PlannedGross = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    WastePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    PlannedPrice = table.Column<decimal>(type: "decimal(19,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_order_materials", x => x.Id);
                    table.CheckConstraint("ck_production_order_materials_gross", "[PlannedGross] >= [PlannedNet]");
                    table.CheckConstraint("ck_production_order_materials_net", "[PlannedNet] > 0");
                    table.CheckConstraint("ck_production_order_materials_price", "[PlannedPrice] IS NULL OR [PlannedPrice] >= 0");
                    table.CheckConstraint("ck_production_order_materials_waste", "[WastePercent] >= 0 AND [WastePercent] < 100");
                    table.ForeignKey(
                        name: "FK_production_order_materials_items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_order_materials_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "kniterp",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_order_materials_ItemId",
                schema: "kniterp",
                table: "production_order_materials",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "ux_production_order_materials_order_item",
                schema: "kniterp",
                table: "production_order_materials",
                columns: new[] { "ProductionOrderId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_CancelledByUserId",
                schema: "kniterp",
                table: "production_orders",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_CreatedByUserId",
                schema: "kniterp",
                table: "production_orders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_production_orders_org_sales_order",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "ix_production_orders_org_status",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_ItemId",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_OutputWarehouseId",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "OutputWarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_TechCardId",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "TechCardId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_WipWarehouseId",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "WipWarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_ReleasedByUserId",
                schema: "kniterp",
                table: "production_orders",
                column: "ReleasedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_production_orders_org_number",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            // Снимок норм запущенного заказа не меняется и не удаляется даже в обход приложения (D90):
            // по нему считаются план выдачи, материальный баланс и плановая себестоимость.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_production_order_materials_immutable]
                ON [kniterp].[production_order_materials]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51090, N''Нормы запущенного производственного заказа не меняются.'', 1;
                END');
                """);

            // После запуска изделие, количество и версия техкарты заказа зафиксированы (D90).
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

            // Допущение D91: права производства системным ролям существующих организаций (новые получают их из матрицы P0).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                JOIN (VALUES
                    ('owner', 'production.order.view'), ('admin', 'production.order.view'), ('department_head', 'production.order.view'),
                    ('senior_storekeeper', 'production.order.view'), ('storekeeper', 'production.order.view'),
                    ('accountant', 'production.order.view'), ('auditor', 'production.order.view'),
                    ('owner', 'production.order.edit'), ('admin', 'production.order.edit'), ('department_head', 'production.order.edit')
                ) AS p([Role], [Code]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('production.order.view', 'production.order.edit');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_production_orders_fixed_after_release]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_production_order_materials_immutable]');");

            migrationBuilder.DropTable(
                name: "production_order_materials",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "production_orders",
                schema: "kniterp");
        }
    }
}
