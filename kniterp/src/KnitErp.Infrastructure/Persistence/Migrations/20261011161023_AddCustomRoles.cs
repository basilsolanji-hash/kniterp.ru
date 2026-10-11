using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "kniterp",
                table: "roles",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                schema: "kniterp",
                table: "roles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "kniterp",
                table: "roles",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddCheckConstraint(
                name: "ck_roles_archive_custom",
                schema: "kniterp",
                table: "roles",
                sql: "[IsArchived] = 0 OR [IsSystem] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roles_archive_custom",
                schema: "kniterp",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "kniterp",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                schema: "kniterp",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "kniterp",
                table: "roles");
        }
    }
}
