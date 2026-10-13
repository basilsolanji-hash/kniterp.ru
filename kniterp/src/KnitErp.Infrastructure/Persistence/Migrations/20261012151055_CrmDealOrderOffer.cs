using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmDealOrderOffer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OfferId",
                schema: "kniterp",
                table: "crm_deal_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_crm_deal_orders_offer",
                schema: "kniterp",
                table: "crm_deal_orders",
                columns: new[] { "OrganizationId", "OfferId" },
                filter: "[OfferId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_crm_deal_orders_offer",
                schema: "kniterp",
                table: "crm_deal_orders",
                columns: new[] { "OrganizationId", "OfferId" },
                principalSchema: "kniterp",
                principalTable: "crm_offers",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_crm_deal_orders_offer",
                schema: "kniterp",
                table: "crm_deal_orders");

            migrationBuilder.DropIndex(
                name: "ix_crm_deal_orders_offer",
                schema: "kniterp",
                table: "crm_deal_orders");

            migrationBuilder.DropColumn(
                name: "OfferId",
                schema: "kniterp",
                table: "crm_deal_orders");
        }
    }
}
