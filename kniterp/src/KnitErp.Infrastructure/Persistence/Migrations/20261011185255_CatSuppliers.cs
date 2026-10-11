using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatSuppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_suppliers",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierArticle = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SupplierItemName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsMain = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_suppliers", x => x.Id);
                    table.ForeignKey(
                        name: "fk_item_suppliers_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_suppliers_supplier",
                        columns: x => new { x.OrganizationId, x.SupplierId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_suppliers_article",
                schema: "kniterp",
                table: "item_suppliers",
                columns: new[] { "OrganizationId", "SupplierId", "SupplierArticle" });

            migrationBuilder.CreateIndex(
                name: "IX_item_suppliers_OrganizationId_ItemId",
                schema: "kniterp",
                table: "item_suppliers",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_item_suppliers_item_supplier",
                schema: "kniterp",
                table: "item_suppliers",
                columns: new[] { "ItemId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_item_suppliers_main",
                schema: "kniterp",
                table: "item_suppliers",
                column: "ItemId",
                unique: true,
                filter: "[IsMain] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_suppliers",
                schema: "kniterp");
        }
    }
}
