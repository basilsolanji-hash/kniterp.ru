using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionOrderParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ParentOrderId",
                schema: "kniterp",
                table: "production_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_orders_org_parent",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "ParentOrderId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_production_orders_parent",
                schema: "kniterp",
                table: "production_orders",
                sql: "[ParentOrderId] IS NULL OR [ParentOrderId] <> [Id]");

            migrationBuilder.AddForeignKey(
                name: "fk_production_orders_parent",
                schema: "kniterp",
                table: "production_orders",
                columns: new[] { "OrganizationId", "ParentOrderId" },
                principalSchema: "kniterp",
                principalTable: "production_orders",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_production_orders_parent",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropIndex(
                name: "ix_production_orders_org_parent",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_production_orders_parent",
                schema: "kniterp",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ParentOrderId",
                schema: "kniterp",
                table: "production_orders");
        }
    }
}
