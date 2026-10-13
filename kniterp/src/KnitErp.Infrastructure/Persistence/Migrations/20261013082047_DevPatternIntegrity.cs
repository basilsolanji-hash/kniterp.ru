using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevPatternIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovedVersion",
                schema: "kniterp",
                table: "dev_patterns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAtUtc",
                schema: "kniterp",
                table: "dev_patterns",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dev_pattern_version_colors",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PatternId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ColorNo = table.Column<int>(type: "int", nullable: false),
                    ColorName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SharePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    Feeder = table.Column<int>(type: "int", nullable: true),
                    ColorwayId = table.Column<long>(type: "bigint", nullable: true),
                    Colorway = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Yarn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_version_colors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dev_pattern_version_colors_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_version_colors_version",
                        columns: x => new { x.OrganizationId, x.PatternId, x.Version },
                        principalSchema: "kniterp",
                        principalTable: "dev_pattern_versions",
                        principalColumns: new[] { "OrganizationId", "PatternId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dev_pattern_version_colors_version",
                schema: "kniterp",
                table: "dev_pattern_version_colors",
                columns: new[] { "OrganizationId", "PatternId", "Version" });

            // Ревью 13.10: снимки цветов версий — только вставка; уже утверждённые рисунки — их текущая версия считается утверждённой.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_pattern_version_colors_immutable]
                ON [kniterp].[dev_pattern_version_colors]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51729, N''Снимки версий рисунка неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("EXEC(N'UPDATE [kniterp].[dev_patterns] SET [ApprovedVersion] = [CurrentVersion] WHERE [Status] = 3 AND [ApprovedVersion] IS NULL');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_dev_pattern_version_colors_immutable];");
            migrationBuilder.DropTable(
                name: "dev_pattern_version_colors",
                schema: "kniterp");

            migrationBuilder.DropColumn(
                name: "ApprovedVersion",
                schema: "kniterp",
                table: "dev_patterns");

            migrationBuilder.DropColumn(
                name: "ChangedAtUtc",
                schema: "kniterp",
                table: "dev_patterns");
        }
    }
}
