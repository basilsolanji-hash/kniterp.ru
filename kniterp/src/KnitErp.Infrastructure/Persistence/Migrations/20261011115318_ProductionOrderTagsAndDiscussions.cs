using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionOrderTagsAndDiscussions : Migration
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 11");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 11");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 10");
        }
    }
}
