using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchKey",
                schema: "kniterp",
                table: "items",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "UPPER(CONCAT([Code], N'|', [Name], N'|', [Article])) COLLATE Latin1_General_100_BIN2",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchKey",
                schema: "kniterp",
                table: "items");
        }
    }
}
