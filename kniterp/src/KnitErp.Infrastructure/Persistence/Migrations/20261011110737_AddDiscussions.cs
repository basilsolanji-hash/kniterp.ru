using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscussions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.CreateTable(
                name: "discussion_comments",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Target = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorUserId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EditedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discussion_comments", x => x.Id);
                    table.CheckConstraint("ck_discussion_comments_deleted", "([DeletedAtUtc] IS NULL AND [DeletedByUserId] IS NULL AND LEN([Text]) > 0) OR ([DeletedAtUtc] IS NOT NULL AND [DeletedByUserId] IS NOT NULL AND [Text] = N'')");
                    table.CheckConstraint("ck_discussion_comments_target", "[Target] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_discussion_comments_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_discussion_comments_users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_discussion_comments_users_DeletedByUserId",
                        column: x => x.DeletedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discussion_mentions",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CommentId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discussion_mentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discussion_mentions_discussion_comments_CommentId",
                        column: x => x.CommentId,
                        principalSchema: "kniterp",
                        principalTable: "discussion_comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_discussion_mentions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 10");

            migrationBuilder.CreateIndex(
                name: "IX_discussion_comments_AuthorUserId",
                schema: "kniterp",
                table: "discussion_comments",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_discussion_comments_DeletedByUserId",
                schema: "kniterp",
                table: "discussion_comments",
                column: "DeletedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_discussion_comments_org_recent",
                schema: "kniterp",
                table: "discussion_comments",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                columns: new[] { "OrganizationId", "Target", "TargetId", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_discussion_mentions_user",
                schema: "kniterp",
                table: "discussion_mentions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_discussion_mentions_comment_user",
                schema: "kniterp",
                table: "discussion_mentions",
                columns: new[] { "CommentId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discussion_mentions",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "discussion_comments",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 7");
        }
    }
}
