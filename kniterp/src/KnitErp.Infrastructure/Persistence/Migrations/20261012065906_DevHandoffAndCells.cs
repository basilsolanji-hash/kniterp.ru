using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevHandoffAndCells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "YarnWriteOffDocumentId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_sample_measures",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<long>(
                name: "ProductionItemId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProductionTechCardId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_model_sizes",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_measure_values",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_detail_sizes",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_OrganizationId_YarnWriteOffDocumentId",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "YarnWriteOffDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_ProductionItemId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "ProductionItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_ProductionTechCardId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "ProductionTechCardId" });

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_production_item",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "ProductionItemId" },
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_production_techcard",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "ProductionTechCardId" },
                principalSchema: "kniterp",
                principalTable: "tech_cards",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_yarn_writeoff",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "YarnWriteOffDocumentId" },
                principalSchema: "kniterp",
                principalTable: "stock_documents",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_production_item",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_production_techcard",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_yarn_writeoff",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_OrganizationId_YarnWriteOffDocumentId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_ProductionItemId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_ProductionTechCardId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "YarnWriteOffDocumentId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_sample_measures");

            migrationBuilder.DropColumn(
                name: "ProductionItemId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "ProductionTechCardId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_model_sizes");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_measure_values");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "kniterp",
                table: "dev_detail_sizes");
        }
    }
}
