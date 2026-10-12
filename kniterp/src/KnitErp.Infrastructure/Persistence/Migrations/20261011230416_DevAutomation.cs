using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dev_category_templates",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<byte>(type: "tinyint", nullable: false),
                    SizeRange = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ColorRange = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Composition = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Labeling = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Care = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_category_templates", x => new { x.OrganizationId, x.Category });
                    table.CheckConstraint("ck_dev_category_templates_category", "[Category] BETWEEN 1 AND 10 OR [Category] = 99");
                    table.ForeignKey(
                        name: "FK_dev_category_templates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_settings",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DefaultDesignerUserId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultDessinatorUserId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultConstructorUserId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultTechnologistUserId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultProgrammerUserId = table.Column<long>(type: "bigint", nullable: true),
                    AutoLaunchDate = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    AutoAdvance = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_settings", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_dev_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_settings_constructor",
                        column: x => x.DefaultConstructorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_settings_designer",
                        column: x => x.DefaultDesignerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_settings_dessinator",
                        column: x => x.DefaultDessinatorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_settings_programmer",
                        column: x => x.DefaultProgrammerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_settings_technologist",
                        column: x => x.DefaultTechnologistUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_settings_DefaultConstructorUserId",
                schema: "kniterp",
                table: "dev_settings",
                column: "DefaultConstructorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_settings_DefaultDesignerUserId",
                schema: "kniterp",
                table: "dev_settings",
                column: "DefaultDesignerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_settings_DefaultDessinatorUserId",
                schema: "kniterp",
                table: "dev_settings",
                column: "DefaultDessinatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_settings_DefaultProgrammerUserId",
                schema: "kniterp",
                table: "dev_settings",
                column: "DefaultProgrammerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_settings_DefaultTechnologistUserId",
                schema: "kniterp",
                table: "dev_settings",
                column: "DefaultTechnologistUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dev_category_templates",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_settings",
                schema: "kniterp");
        }
    }
}
