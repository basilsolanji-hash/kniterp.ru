using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_batches",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DyeLot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SupplierId = table.Column<long>(type: "bigint", nullable: true),
                    PackageWeightKg = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_batches", x => x.Id);
                    table.UniqueConstraint("ak_stock_batches_org_id", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("ak_stock_batches_org_item_id", x => new { x.OrganizationId, x.ItemId, x.Id });
                    table.CheckConstraint("ck_stock_batches_package_weight", "[PackageWeightKg] IS NULL OR [PackageWeightKg] > 0");
                    table.ForeignKey(
                        name: "FK_stock_batches_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_batches_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_batches_supplier",
                        columns: x => new { x.OrganizationId, x.SupplierId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_batch_movements",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_batch_movements", x => x.Id);
                    table.CheckConstraint("ck_stock_batch_movements_quantity", "[Quantity] <> 0");
                    table.CheckConstraint("ck_stock_batch_movements_source", "[Source] IN (2, 3)");
                    table.ForeignKey(
                        name: "FK_stock_batch_movements_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_batch_movements_batch",
                        columns: x => new { x.OrganizationId, x.ItemId, x.BatchId },
                        principalSchema: "kniterp",
                        principalTable: "stock_batches",
                        principalColumns: new[] { "OrganizationId", "ItemId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_batch_movements_warehouse",
                        columns: x => new { x.OrganizationId, x.WarehouseId },
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_document_batch_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Packages = table.Column<int>(type: "int", nullable: true),
                    GrossWeightKg = table.Column<decimal>(type: "decimal(18,6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_document_batch_lines", x => x.Id);
                    table.CheckConstraint("ck_stock_document_batch_lines_gross", "[GrossWeightKg] IS NULL OR [GrossWeightKg] > 0");
                    table.CheckConstraint("ck_stock_document_batch_lines_packages", "[Packages] IS NULL OR [Packages] > 0");
                    table.CheckConstraint("ck_stock_document_batch_lines_quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "fk_stock_document_batch_lines_batch",
                        columns: x => new { x.OrganizationId, x.ItemId, x.BatchId },
                        principalSchema: "kniterp",
                        principalTable: "stock_batches",
                        principalColumns: new[] { "OrganizationId", "ItemId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_document_batch_lines_document",
                        columns: x => new { x.OrganizationId, x.DocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_batch_movements_org_batch",
                schema: "kniterp",
                table: "stock_batch_movements",
                columns: new[] { "OrganizationId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_batch_movements_org_wh_item_batch",
                schema: "kniterp",
                table: "stock_batch_movements",
                columns: new[] { "OrganizationId", "WarehouseId", "ItemId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_batch_movements_OrganizationId_ItemId_BatchId",
                schema: "kniterp",
                table: "stock_batch_movements",
                columns: new[] { "OrganizationId", "ItemId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_batch_movements_source",
                schema: "kniterp",
                table: "stock_batch_movements",
                columns: new[] { "Source", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_batches_OrganizationId_SupplierId",
                schema: "kniterp",
                table: "stock_batches",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_batches_org_item_number",
                schema: "kniterp",
                table: "stock_batches",
                columns: new[] { "OrganizationId", "ItemId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_document_batch_lines_OrganizationId_DocumentId",
                schema: "kniterp",
                table: "stock_document_batch_lines",
                columns: new[] { "OrganizationId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_document_batch_lines_OrganizationId_ItemId_BatchId",
                schema: "kniterp",
                table: "stock_document_batch_lines",
                columns: new[] { "OrganizationId", "ItemId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_batch_lines_doc_item_batch",
                schema: "kniterp",
                table: "stock_document_batch_lines",
                columns: new[] { "DocumentId", "ItemId", "BatchId" },
                unique: true);

            // D89: партийный регистр только на вставку, разбивка строк — только у черновика (как строки документа).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_batch_movements_immutable]
                ON [kniterp].[stock_batch_movements]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51001, N''Движения партий неизменяемы: исправление — новым документом.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_stock_document_batch_lines_draft_only]
                ON [kniterp].[stock_document_batch_lines]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM (SELECT [DocumentId] FROM inserted UNION ALL SELECT [DocumentId] FROM deleted) x
                               JOIN [kniterp].[stock_documents] d ON d.[Id] = x.[DocumentId] WHERE d.[Status] <> 1)
                        THROW 51002, N''Разбивка по партиям проведённого документа не меняется — оформите сторно или новый документ.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_batch_movements_immutable]');");
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_stock_document_batch_lines_draft_only]');");

            migrationBuilder.DropTable(
                name: "stock_batch_movements",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "stock_document_batch_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "stock_batches",
                schema: "kniterp");
        }
    }
}
