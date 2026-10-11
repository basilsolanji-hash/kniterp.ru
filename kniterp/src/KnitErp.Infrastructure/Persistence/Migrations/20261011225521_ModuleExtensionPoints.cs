using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModuleExtensionPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.AddColumn<long>(
                name: "CrmDealId",
                schema: "kniterp",
                table: "sales_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 99");

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_org_crm_deal",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "CrmDealId" },
                filter: "[CrmDealId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 99");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropIndex(
                name: "ix_sales_orders_org_crm_deal",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.DropColumn(
                name: "CrmDealId",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 29");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 29");
        }
    }
}
