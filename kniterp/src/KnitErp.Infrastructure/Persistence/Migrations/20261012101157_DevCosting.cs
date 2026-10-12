using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LaborRatePerMinute",
                schema: "kniterp",
                table: "dev_settings",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarkupPercent",
                schema: "kniterp",
                table: "dev_settings",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OverheadPercent",
                schema: "kniterp",
                table: "dev_settings",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "YarnWastePercent",
                schema: "kniterp",
                table: "dev_settings",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "dev_cost_runs",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OverheadPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    PriceMode = table.Column<byte>(type: "tinyint", nullable: false),
                    MarkupPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Yarn = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Accessories = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Labor = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Overhead = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Margin = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_cost_runs", x => x.Id);
                    table.UniqueConstraint("ak_dev_cost_runs_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_cost_runs_mode", "[PriceMode] BETWEEN 1 AND 2");
                    table.CheckConstraint("ck_dev_cost_runs_money", "[Yarn] >= 0 AND [Accessories] >= 0 AND [Labor] >= 0 AND [Overhead] >= 0 AND [Cost] >= 0 AND [Price] >= 0");
                    table.ForeignKey(
                        name: "FK_dev_cost_runs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_cost_runs_author",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_cost_runs_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_model_accessories",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_model_accessories", x => x.Id);
                    table.CheckConstraint("ck_dev_model_accessories_qty", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_dev_model_accessories_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_accessories_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_accessories_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_cost_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    WastePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_cost_lines", x => x.Id);
                    table.CheckConstraint("ck_dev_cost_lines_kind", "[Kind] BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_dev_cost_lines_values", "[Quantity] >= 0 AND [UnitPrice] >= 0 AND [Amount] >= 0 AND [WastePercent] BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_dev_cost_lines_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_cost_lines_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_cost_lines_run",
                        columns: x => new { x.OrganizationId, x.RunId },
                        principalSchema: "kniterp",
                        principalTable: "dev_cost_runs",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[YarnWastePercent] BETWEEN 0 AND 100 AND [OverheadPercent] BETWEEN 0 AND 1000 AND [MarkupPercent] BETWEEN 0 AND 1000 AND ([LaborRatePerMinute] IS NULL OR [LaborRatePerMinute] >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_dev_cost_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "dev_cost_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_cost_lines_OrganizationId_RunId",
                schema: "kniterp",
                table: "dev_cost_lines",
                columns: new[] { "OrganizationId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_cost_runs_CreatedByUserId",
                schema: "kniterp",
                table: "dev_cost_runs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_dev_cost_runs_version",
                schema: "kniterp",
                table: "dev_cost_runs",
                columns: new[] { "OrganizationId", "ModelId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_model_accessories_OrganizationId_ItemId",
                schema: "kniterp",
                table: "dev_model_accessories",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_model_accessories",
                schema: "kniterp",
                table: "dev_model_accessories",
                columns: new[] { "OrganizationId", "ModelId", "ItemId" },
                unique: true);

            // D744: версии расчёта себестоимости и их строки — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_cost_runs_immutable]
                ON [kniterp].[dev_cost_runs]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51744, N''Версия расчёта неизменяема: пересчёт — новой версией.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_cost_lines_immutable]
                ON [kniterp].[dev_cost_lines]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51745, N''Строки версии расчёта неизменяемы.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_dev_cost_lines_immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_dev_cost_runs_immutable];");
            migrationBuilder.DropTable(
                name: "dev_cost_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_model_accessories",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_cost_runs",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_costing",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "LaborRatePerMinute",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "MarkupPercent",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "OverheadPercent",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "YarnWastePercent",
                schema: "kniterp",
                table: "dev_settings");
        }
    }
}
