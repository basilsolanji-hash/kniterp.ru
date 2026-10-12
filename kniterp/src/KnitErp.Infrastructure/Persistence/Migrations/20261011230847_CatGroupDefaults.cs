using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatGroupDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_group_defaults",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: true),
                    UnitId = table.Column<long>(type: "bigint", nullable: true),
                    VatRateId = table.Column<long>(type: "bigint", nullable: true),
                    TnVedCode = table.Column<string>(type: "varchar(10)", nullable: true),
                    OriginCountryCode = table.Column<string>(type: "char(3)", nullable: true),
                    OriginCountryName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Tracking = table.Column<byte>(type: "tinyint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_group_defaults", x => x.Id);
                    table.ForeignKey(
                        name: "fk_item_group_defaults_group",
                        columns: x => new { x.OrganizationId, x.GroupId },
                        principalSchema: "kniterp",
                        principalTable: "item_groups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_group_defaults_unit",
                        columns: x => new { x.OrganizationId, x.UnitId },
                        principalSchema: "kniterp",
                        principalTable: "units",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_group_defaults_vat",
                        columns: x => new { x.OrganizationId, x.VatRateId },
                        principalSchema: "kniterp",
                        principalTable: "vat_rates",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_item_group_defaults_OrganizationId_GroupId",
                schema: "kniterp",
                table: "item_group_defaults",
                columns: new[] { "OrganizationId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_item_group_defaults_OrganizationId_UnitId",
                schema: "kniterp",
                table: "item_group_defaults",
                columns: new[] { "OrganizationId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_item_group_defaults_OrganizationId_VatRateId",
                schema: "kniterp",
                table: "item_group_defaults",
                columns: new[] { "OrganizationId", "VatRateId" });

            migrationBuilder.CreateIndex(
                name: "ux_item_group_defaults_group",
                schema: "kniterp",
                table: "item_group_defaults",
                column: "GroupId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_group_defaults",
                schema: "kniterp");
        }
    }
}
