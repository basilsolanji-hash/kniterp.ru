using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevReasonsAndVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReasonId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PreviousVersionId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VersionNo",
                schema: "kniterp",
                table: "dev_models",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_OrganizationId_ReasonId",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "ReasonId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_models_previous_version",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PreviousVersionId" },
                unique: true,
                filter: "[PreviousVersionId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_models_version",
                schema: "kniterp",
                table: "dev_models",
                sql: "[VersionNo] >= 1 AND ([PreviousVersionId] IS NULL OR [VersionNo] >= 2)");

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_previous_version",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PreviousVersionId" },
                principalSchema: "kniterp",
                principalTable: "dev_models",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_reason",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "ReasonId" },
                principalSchema: "kniterp",
                principalTable: "dev_lookups",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_previous_version",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_reason",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_OrganizationId_ReasonId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "ux_dev_models_previous_version",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_models_version",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "ReasonId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "PreviousVersionId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "VersionNo",
                schema: "kniterp",
                table: "dev_models");
        }
    }
}
