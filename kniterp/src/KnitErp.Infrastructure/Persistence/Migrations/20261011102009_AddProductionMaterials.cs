using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_order_documents",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ProductionOrderId = table.Column<long>(type: "bigint", nullable: false),
                    StockDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Purpose = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_order_documents", x => x.Id);
                    table.CheckConstraint("ck_production_order_documents_purpose", "[Purpose] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_production_order_documents_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_order_documents_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_order_documents_order",
                        columns: x => new { x.OrganizationId, x.ProductionOrderId },
                        principalSchema: "kniterp",
                        principalTable: "production_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_production_order_documents_stock_document",
                        columns: x => new { x.OrganizationId, x.StockDocumentId },
                        principalSchema: "kniterp",
                        principalTable: "stock_documents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_order_documents_CreatedByUserId",
                schema: "kniterp",
                table: "production_order_documents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_production_order_documents_org_order",
                schema: "kniterp",
                table: "production_order_documents",
                columns: new[] { "OrganizationId", "ProductionOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_order_documents_OrganizationId_StockDocumentId",
                schema: "kniterp",
                table: "production_order_documents",
                columns: new[] { "OrganizationId", "StockDocumentId" });

            migrationBuilder.CreateIndex(
                name: "ux_production_order_documents_stock_document",
                schema: "kniterp",
                table: "production_order_documents",
                column: "StockDocumentId",
                unique: true);

            // Связь заказа со складскими документами только добавляется (D93).
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_production_order_documents_immutable]
                ON [kniterp].[production_order_documents]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51093, N''Связь производственного заказа с документом не меняется и не удаляется.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER IF EXISTS [kniterp].[tr_production_order_documents_immutable]')");

            migrationBuilder.DropTable(
                name: "production_order_documents",
                schema: "kniterp");
        }
    }
}
