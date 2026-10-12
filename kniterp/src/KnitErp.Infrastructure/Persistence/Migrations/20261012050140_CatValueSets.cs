using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatValueSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "characteristic_value_sets",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    CharacteristicId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValuesText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_characteristic_value_sets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_characteristic_value_sets_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_characteristic_value_sets_characteristic",
                        column: x => x.CharacteristicId,
                        principalSchema: "kniterp",
                        principalTable: "characteristics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_characteristic_value_sets_CharacteristicId",
                schema: "kniterp",
                table: "characteristic_value_sets",
                column: "CharacteristicId");

            migrationBuilder.CreateIndex(
                name: "ix_characteristic_value_sets_org_char",
                schema: "kniterp",
                table: "characteristic_value_sets",
                columns: new[] { "OrganizationId", "CharacteristicId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "characteristic_value_sets",
                schema: "kniterp");
        }
    }
}
