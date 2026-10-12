using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevApprovalRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.AddColumn<decimal>(
                name: "MinMarginPercent",
                schema: "kniterp",
                table: "dev_settings",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireCostForApproval",
                schema: "kniterp",
                table: "dev_settings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[YarnWastePercent] BETWEEN 0 AND 100 AND [OverheadPercent] BETWEEN 0 AND 1000 AND [MarkupPercent] BETWEEN 0 AND 1000 AND ([LaborRatePerMinute] IS NULL OR [LaborRatePerMinute] >= 0) AND ([MinMarginPercent] IS NULL OR [MinMarginPercent] BETWEEN -100 AND 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "MinMarginPercent",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "RequireCostForApproval",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[YarnWastePercent] BETWEEN 0 AND 100 AND [OverheadPercent] BETWEEN 0 AND 1000 AND [MarkupPercent] BETWEEN 0 AND 1000 AND ([LaborRatePerMinute] IS NULL OR [LaborRatePerMinute] >= 0)");
        }
    }
}
