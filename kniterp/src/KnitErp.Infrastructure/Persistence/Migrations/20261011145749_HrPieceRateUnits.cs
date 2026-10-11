using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPieceRateUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_piece_rates_unit",
                schema: "kniterp",
                table: "hr_piece_rates");

            migrationBuilder.AlterColumn<string>(
                name: "Unit",
                schema: "kniterp",
                table: "hr_piece_rates",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(5)",
                oldMaxLength: 5);

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_piece_rates_unit",
                schema: "kniterp",
                table: "hr_piece_rates",
                sql: "LEN([Unit]) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_piece_rates_unit",
                schema: "kniterp",
                table: "hr_piece_rates");

            migrationBuilder.AlterColumn<string>(
                name: "Unit",
                schema: "kniterp",
                table: "hr_piece_rates",
                type: "nvarchar(5)",
                maxLength: 5,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_piece_rates_unit",
                schema: "kniterp",
                table: "hr_piece_rates",
                sql: "[Unit] IN (N'шт', N'кг')");
        }
    }
}
