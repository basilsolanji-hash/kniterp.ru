using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatFeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_feeds",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "varchar(9)", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: false),
                    WarehouseId = table.Column<long>(type: "bigint", nullable: true),
                    GroupId = table.Column<long>(type: "bigint", nullable: true),
                    OnlyAvailable = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastRequestedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_feeds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_feeds_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_feeds_group",
                        column: x => x.GroupId,
                        principalSchema: "kniterp",
                        principalTable: "item_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_feeds_price_type",
                        column: x => x.PriceTypeId,
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_feeds_warehouse",
                        column: x => x.WarehouseId,
                        principalSchema: "kniterp",
                        principalTable: "warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_feeds_GroupId",
                schema: "kniterp",
                table: "catalog_feeds",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_feeds_org",
                schema: "kniterp",
                table: "catalog_feeds",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_feeds_PriceTypeId",
                schema: "kniterp",
                table: "catalog_feeds",
                column: "PriceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_feeds_WarehouseId",
                schema: "kniterp",
                table: "catalog_feeds",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "ux_catalog_feeds_key",
                schema: "kniterp",
                table: "catalog_feeds",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_feeds",
                schema: "kniterp");
        }
    }
}
