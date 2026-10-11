using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchesInOpeningAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_batch_movements_source",
                schema: "kniterp",
                table: "stock_batch_movements");

            migrationBuilder.AddColumn<int>(
                name: "BatchRevision",
                schema: "kniterp",
                table: "opening_balances",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BatchRevision",
                schema: "kniterp",
                table: "inventory_counts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "inventory_batch_counts",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_batch_counts", x => x.Id);
                    table.CheckConstraint("ck_inventory_batch_counts_quantity", "[CountedQuantity] >= 0");
                    table.ForeignKey(
                        name: "fk_inventory_batch_counts_batch",
                        columns: x => new { x.OrganizationId, x.ItemId, x.BatchId },
                        principalSchema: "kniterp",
                        principalTable: "stock_batches",
                        principalColumns: new[] { "OrganizationId", "ItemId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inventory_batch_counts_document",
                        columns: x => new { x.OrganizationId, x.DocumentId },
                        principalSchema: "kniterp",
                        principalTable: "inventory_counts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "opening_balance_batch_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opening_balance_batch_lines", x => x.Id);
                    table.CheckConstraint("ck_opening_balance_batch_lines_quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "fk_opening_balance_batch_lines_batch",
                        columns: x => new { x.OrganizationId, x.ItemId, x.BatchId },
                        principalSchema: "kniterp",
                        principalTable: "stock_batches",
                        principalColumns: new[] { "OrganizationId", "ItemId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_opening_balance_batch_lines_document",
                        columns: x => new { x.OrganizationId, x.DocumentId },
                        principalSchema: "kniterp",
                        principalTable: "opening_balances",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_batch_movements_source",
                schema: "kniterp",
                table: "stock_batch_movements",
                sql: "[Source] IN (1, 2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_batch_counts_OrganizationId_DocumentId",
                schema: "kniterp",
                table: "inventory_batch_counts",
                columns: new[] { "OrganizationId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_batch_counts_OrganizationId_ItemId_BatchId",
                schema: "kniterp",
                table: "inventory_batch_counts",
                columns: new[] { "OrganizationId", "ItemId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "ux_inventory_batch_counts_doc_item_batch",
                schema: "kniterp",
                table: "inventory_batch_counts",
                columns: new[] { "DocumentId", "ItemId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_batch_lines_OrganizationId_DocumentId",
                schema: "kniterp",
                table: "opening_balance_batch_lines",
                columns: new[] { "OrganizationId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_opening_balance_batch_lines_OrganizationId_ItemId_BatchId",
                schema: "kniterp",
                table: "opening_balance_batch_lines",
                columns: new[] { "OrganizationId", "ItemId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "ux_opening_balance_batch_lines_doc_item_batch",
                schema: "kniterp",
                table: "opening_balance_batch_lines",
                columns: new[] { "DocumentId", "ItemId", "BatchId" },
                unique: true);

            // D100: разбивка по партиям правится только у черновика — как строки самих документов.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_opening_balance_batch_lines_draft_only]
                ON [kniterp].[opening_balance_batch_lines]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM (SELECT [DocumentId] FROM inserted UNION ALL SELECT [DocumentId] FROM deleted) x
                               JOIN [kniterp].[opening_balances] d ON d.[Id] = x.[DocumentId] WHERE d.[Status] <> 1)
                        THROW 51002, N''Партии утверждённых начальных остатков не меняются.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_inventory_batch_counts_draft_only]
                ON [kniterp].[inventory_batch_counts]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM (SELECT [DocumentId] FROM inserted UNION ALL SELECT [DocumentId] FROM deleted) x
                               JOIN [kniterp].[inventory_counts] d ON d.[Id] = x.[DocumentId] WHERE d.[Status] <> 1)
                        THROW 51002, N''Факт по партиям проведённой инвентаризации не меняется.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_opening_balance_batch_lines_draft_only]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_inventory_batch_counts_draft_only]');");

            migrationBuilder.DropTable(
                name: "inventory_batch_counts",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "opening_balance_batch_lines",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_batch_movements_source",
                schema: "kniterp",
                table: "stock_batch_movements");

            migrationBuilder.DropColumn(
                name: "BatchRevision",
                schema: "kniterp",
                table: "opening_balances");

            migrationBuilder.DropColumn(
                name: "BatchRevision",
                schema: "kniterp",
                table: "inventory_counts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_batch_movements_source",
                schema: "kniterp",
                table: "stock_batch_movements",
                sql: "[Source] IN (2, 3)");
        }
    }
}
