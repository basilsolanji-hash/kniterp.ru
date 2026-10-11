using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tags",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Color = table.Column<byte>(type: "tinyint", nullable: false),
                    IsFavorite = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                    table.UniqueConstraint("ak_tags_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_tags_color", "[Color] BETWEEN 0 AND 7");
                    table.ForeignKey(
                        name: "FK_tags_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tags_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tag_links",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    TagId = table.Column<long>(type: "bigint", nullable: false),
                    Entity = table.Column<byte>(type: "tinyint", nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_links", x => x.Id);
                    table.CheckConstraint("ck_tag_links_entity", "[Entity] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_tag_links_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tag_links_tag",
                        columns: x => new { x.OrganizationId, x.TagId },
                        principalSchema: "kniterp",
                        principalTable: "tags",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tag_links_CreatedByUserId",
                schema: "kniterp",
                table: "tag_links",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_tag_links_org_entity",
                schema: "kniterp",
                table: "tag_links",
                columns: new[] { "OrganizationId", "Entity", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_tag_links_OrganizationId_TagId",
                schema: "kniterp",
                table: "tag_links",
                columns: new[] { "OrganizationId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "ux_tag_links_tag_entity",
                schema: "kniterp",
                table: "tag_links",
                columns: new[] { "TagId", "Entity", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tags_CreatedByUserId",
                schema: "kniterp",
                table: "tags",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_tags_org_name",
                schema: "kniterp",
                table: "tags",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tag_links",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "kniterp");
        }
    }
}
