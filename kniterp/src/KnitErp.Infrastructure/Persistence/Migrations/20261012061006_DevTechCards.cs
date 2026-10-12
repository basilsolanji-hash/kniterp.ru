using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevTechCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletenessSections",
                schema: "kniterp",
                table: "dev_settings",
                type: "int",
                nullable: false,
                defaultValue: 1023);

            migrationBuilder.AddColumn<int>(
                name: "FactTolerancePercent",
                schema: "kniterp",
                table: "dev_settings",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "LabelHeightMm",
                schema: "kniterp",
                table: "dev_settings",
                type: "int",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.AddColumn<int>(
                name: "LabelWidthMm",
                schema: "kniterp",
                table: "dev_settings",
                type: "int",
                nullable: false,
                defaultValue: 58);

            migrationBuilder.AddColumn<bool>(
                name: "LabelsOnA4",
                schema: "kniterp",
                table: "dev_settings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MainSketchFileId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackBox",
                schema: "kniterp",
                table: "dev_models",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DetailId",
                schema: "kniterp",
                table: "dev_files",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MachineId",
                schema: "kniterp",
                table: "dev_files",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineParams",
                schema: "kniterp",
                table: "dev_files",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_dev_files_org_id",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "dev_machines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Brand = table.Column<byte>(type: "tinyint", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Gauge = table.Column<int>(type: "int", nullable: false),
                    FeederCount = table.Column<int>(type: "int", nullable: false),
                    WidthCm = table.Column<int>(type: "int", nullable: true),
                    Systems = table.Column<int>(type: "int", nullable: true),
                    InventoryNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_machines", x => x.Id);
                    table.UniqueConstraint("ak_dev_machines_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_machines_brand", "[Brand] IN (1, 2, 9)");
                    table.CheckConstraint("ck_dev_machines_feeders", "[FeederCount] BETWEEN 1 AND 32");
                    table.CheckConstraint("ck_dev_machines_gauge", "[Gauge] BETWEEN 1 AND 30");
                    table.CheckConstraint("ck_dev_machines_status", "[Status] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_dev_machines_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_measures",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Tolerance = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Step = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_measures", x => x.Id);
                    table.UniqueConstraint("ak_dev_measures_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_measures_tolerance", "[Tolerance] >= 0");
                    table.ForeignKey(
                        name: "FK_dev_measures_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_measures_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_model_colors",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Swatch = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_model_colors", x => x.Id);
                    table.UniqueConstraint("ak_dev_model_colors_org_id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_dev_model_colors_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_colors_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_colors_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_model_sizes",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsBase = table.Column<bool>(type: "bit", nullable: false),
                    PerBox = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_model_sizes", x => x.Id);
                    table.UniqueConstraint("ak_dev_model_sizes_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_model_sizes_box", "[PerBox] IS NULL OR [PerBox] > 0");
                    table.ForeignKey(
                        name: "FK_dev_model_sizes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_sizes_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_operation_types",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NormMinutes = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ProductionStage = table.Column<byte>(type: "tinyint", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_operation_types", x => x.Id);
                    table.UniqueConstraint("ak_dev_operation_types_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_operation_types_stage", "[ProductionStage] IS NULL OR [ProductionStage] BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_dev_operation_types_values", "([Price] IS NULL OR [Price] >= 0) AND ([NormMinutes] IS NULL OR [NormMinutes] > 0)");
                    table.ForeignKey(
                        name: "FK_dev_operation_types_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_details",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    MachineId = table.Column<long>(type: "bigint", nullable: true),
                    WeaveId = table.Column<long>(type: "bigint", nullable: true),
                    ProgramFileId = table.Column<long>(type: "bigint", nullable: true),
                    PlanKnitSeconds = table.Column<int>(type: "int", nullable: true),
                    PlanWeightGrams = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ThreadingRevision = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_details", x => x.Id);
                    table.UniqueConstraint("ak_dev_details_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_details_plan", "([PlanKnitSeconds] IS NULL OR [PlanKnitSeconds] > 0) AND ([PlanWeightGrams] IS NULL OR [PlanWeightGrams] > 0)");
                    table.CheckConstraint("ck_dev_details_quantity", "[Quantity] BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_dev_details_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_details_machine",
                        columns: x => new { x.OrganizationId, x.MachineId },
                        principalSchema: "kniterp",
                        principalTable: "dev_machines",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_details_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_details_program",
                        columns: x => new { x.OrganizationId, x.ProgramFileId },
                        principalSchema: "kniterp",
                        principalTable: "dev_files",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_details_weave",
                        columns: x => new { x.OrganizationId, x.WeaveId },
                        principalSchema: "kniterp",
                        principalTable: "dev_lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_machine_notes",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    MachineId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StatusAfter = table.Column<byte>(type: "tinyint", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_machine_notes", x => x.Id);
                    table.CheckConstraint("ck_dev_machine_notes_status", "[StatusAfter] IS NULL OR [StatusAfter] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_dev_machine_notes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_machine_notes_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_machine_notes_machine",
                        columns: x => new { x.OrganizationId, x.MachineId },
                        principalSchema: "kniterp",
                        principalTable: "dev_machines",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_measure_values",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    MeasureId = table.Column<long>(type: "bigint", nullable: false),
                    SizeId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_measure_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dev_measure_values_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_measure_values_measure",
                        columns: x => new { x.OrganizationId, x.MeasureId },
                        principalSchema: "kniterp",
                        principalTable: "dev_measures",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_measure_values_size",
                        columns: x => new { x.OrganizationId, x.SizeId },
                        principalSchema: "kniterp",
                        principalTable: "dev_model_sizes",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_samples",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    No = table.Column<int>(type: "int", nullable: false),
                    SizeId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_samples", x => x.Id);
                    table.UniqueConstraint("ak_dev_samples_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_samples_status", "[Status] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_dev_samples_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_samples_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_samples_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_samples_size",
                        columns: x => new { x.OrganizationId, x.SizeId },
                        principalSchema: "kniterp",
                        principalTable: "dev_model_sizes",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_model_operations",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    OperationTypeId = table.Column<long>(type: "bigint", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    NormMinutes = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_model_operations", x => x.Id);
                    table.CheckConstraint("ck_dev_model_operations_values", "([Price] IS NULL OR [Price] >= 0) AND ([NormMinutes] IS NULL OR [NormMinutes] > 0)");
                    table.ForeignKey(
                        name: "FK_dev_model_operations_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_operations_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_operations_type",
                        columns: x => new { x.OrganizationId, x.OperationTypeId },
                        principalSchema: "kniterp",
                        principalTable: "dev_operation_types",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_detail_sizes",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DetailId = table.Column<long>(type: "bigint", nullable: false),
                    SizeId = table.Column<long>(type: "bigint", nullable: false),
                    LengthOffMachineCm = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    LengthAfterSteamCm = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    WidthCm = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_detail_sizes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dev_detail_sizes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_detail_sizes_detail",
                        columns: x => new { x.OrganizationId, x.DetailId },
                        principalSchema: "kniterp",
                        principalTable: "dev_details",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_detail_sizes_size",
                        columns: x => new { x.OrganizationId, x.SizeId },
                        principalSchema: "kniterp",
                        principalTable: "dev_model_sizes",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_threading",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DetailId = table.Column<long>(type: "bigint", nullable: false),
                    Feeder = table.Column<int>(type: "int", nullable: false),
                    Side = table.Column<byte>(type: "tinyint", nullable: false),
                    ColorId = table.Column<long>(type: "bigint", nullable: true),
                    Label = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Ends = table.Column<int>(type: "int", nullable: false),
                    Paraffin = table.Column<bool>(type: "bit", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Elastic = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_threading", x => x.Id);
                    table.CheckConstraint("ck_dev_threading_ends", "[Ends] BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_dev_threading_feeder", "[Feeder] BETWEEN 1 AND 32");
                    table.CheckConstraint("ck_dev_threading_side", "[Side] IN (1, 2)");
                    table.CheckConstraint("ck_dev_threading_yarn", "[ColorId] IS NOT NULL OR [Label] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_dev_threading_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_threading_color",
                        columns: x => new { x.OrganizationId, x.ColorId },
                        principalSchema: "kniterp",
                        principalTable: "dev_model_colors",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_threading_detail",
                        columns: x => new { x.OrganizationId, x.DetailId },
                        principalSchema: "kniterp",
                        principalTable: "dev_details",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_sample_details",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SampleId = table.Column<long>(type: "bigint", nullable: false),
                    DetailId = table.Column<long>(type: "bigint", nullable: false),
                    KnitSeconds = table.Column<int>(type: "int", nullable: true),
                    WeightGrams = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_sample_details", x => x.Id);
                    table.UniqueConstraint("ak_dev_sample_details_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_sample_details_fact", "([KnitSeconds] IS NULL OR [KnitSeconds] > 0) AND ([WeightGrams] IS NULL OR [WeightGrams] > 0)");
                    table.ForeignKey(
                        name: "FK_dev_sample_details_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_sample_details_users_RecordedByUserId",
                        column: x => x.RecordedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_details_detail",
                        columns: x => new { x.OrganizationId, x.DetailId },
                        principalSchema: "kniterp",
                        principalTable: "dev_details",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_details_sample",
                        columns: x => new { x.OrganizationId, x.SampleId },
                        principalSchema: "kniterp",
                        principalTable: "dev_samples",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_sample_measures",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SampleId = table.Column<long>(type: "bigint", nullable: false),
                    MeasureId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_sample_measures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dev_sample_measures_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_measures_measure",
                        columns: x => new { x.OrganizationId, x.MeasureId },
                        principalSchema: "kniterp",
                        principalTable: "dev_measures",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_measures_sample",
                        columns: x => new { x.OrganizationId, x.SampleId },
                        principalSchema: "kniterp",
                        principalTable: "dev_samples",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_sample_color_shares",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SampleDetailId = table.Column<long>(type: "bigint", nullable: false),
                    ColorId = table.Column<long>(type: "bigint", nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_sample_color_shares", x => x.Id);
                    table.CheckConstraint("ck_dev_sample_color_shares_percent", "[Percent] BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_dev_sample_color_shares_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_color_shares_color",
                        columns: x => new { x.OrganizationId, x.ColorId },
                        principalSchema: "kniterp",
                        principalTable: "dev_model_colors",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_sample_color_shares_fact",
                        columns: x => new { x.OrganizationId, x.SampleDetailId },
                        principalSchema: "kniterp",
                        principalTable: "dev_sample_details",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_label",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[LabelWidthMm] BETWEEN 20 AND 150 AND [LabelHeightMm] BETWEEN 20 AND 150");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_sections",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[CompletenessSections] BETWEEN 1 AND 1023");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_settings_tolerance",
                schema: "kniterp",
                table: "dev_settings",
                sql: "[FactTolerancePercent] BETWEEN 1 AND 100");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_MainSketchFileId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "MainSketchFileId" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_files_machine",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "MachineId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_files_OrganizationId_DetailId",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "DetailId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_detail_sizes_OrganizationId_SizeId",
                schema: "kniterp",
                table: "dev_detail_sizes",
                columns: new[] { "OrganizationId", "SizeId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_detail_sizes",
                schema: "kniterp",
                table: "dev_detail_sizes",
                columns: new[] { "OrganizationId", "DetailId", "SizeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_details_model",
                schema: "kniterp",
                table: "dev_details",
                columns: new[] { "OrganizationId", "ModelId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_details_OrganizationId_MachineId",
                schema: "kniterp",
                table: "dev_details",
                columns: new[] { "OrganizationId", "MachineId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_details_OrganizationId_ProgramFileId",
                schema: "kniterp",
                table: "dev_details",
                columns: new[] { "OrganizationId", "ProgramFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_details_OrganizationId_WeaveId",
                schema: "kniterp",
                table: "dev_details",
                columns: new[] { "OrganizationId", "WeaveId" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_machine_notes_machine",
                schema: "kniterp",
                table: "dev_machine_notes",
                columns: new[] { "OrganizationId", "MachineId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_machine_notes_UserId",
                schema: "kniterp",
                table: "dev_machine_notes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_dev_machines_name",
                schema: "kniterp",
                table: "dev_machines",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_measure_values_OrganizationId_SizeId",
                schema: "kniterp",
                table: "dev_measure_values",
                columns: new[] { "OrganizationId", "SizeId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_measure_values",
                schema: "kniterp",
                table: "dev_measure_values",
                columns: new[] { "OrganizationId", "MeasureId", "SizeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_measures_name",
                schema: "kniterp",
                table: "dev_measures",
                columns: new[] { "OrganizationId", "ModelId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_model_colors_OrganizationId_ItemId",
                schema: "kniterp",
                table: "dev_model_colors",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_model_colors_label",
                schema: "kniterp",
                table: "dev_model_colors",
                columns: new[] { "OrganizationId", "ModelId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_model_operations_model",
                schema: "kniterp",
                table: "dev_model_operations",
                columns: new[] { "OrganizationId", "ModelId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_model_operations_OrganizationId_OperationTypeId",
                schema: "kniterp",
                table: "dev_model_operations",
                columns: new[] { "OrganizationId", "OperationTypeId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_model_sizes_name",
                schema: "kniterp",
                table: "dev_model_sizes",
                columns: new[] { "OrganizationId", "ModelId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_operation_types_name",
                schema: "kniterp",
                table: "dev_operation_types",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_sample_color_shares_OrganizationId_ColorId",
                schema: "kniterp",
                table: "dev_sample_color_shares",
                columns: new[] { "OrganizationId", "ColorId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_sample_color_shares",
                schema: "kniterp",
                table: "dev_sample_color_shares",
                columns: new[] { "OrganizationId", "SampleDetailId", "ColorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_sample_details_OrganizationId_DetailId",
                schema: "kniterp",
                table: "dev_sample_details",
                columns: new[] { "OrganizationId", "DetailId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_sample_details_RecordedByUserId",
                schema: "kniterp",
                table: "dev_sample_details",
                column: "RecordedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_dev_sample_details",
                schema: "kniterp",
                table: "dev_sample_details",
                columns: new[] { "OrganizationId", "SampleId", "DetailId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_sample_measures_OrganizationId_MeasureId",
                schema: "kniterp",
                table: "dev_sample_measures",
                columns: new[] { "OrganizationId", "MeasureId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_sample_measures",
                schema: "kniterp",
                table: "dev_sample_measures",
                columns: new[] { "OrganizationId", "SampleId", "MeasureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_CreatedByUserId",
                schema: "kniterp",
                table: "dev_samples",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_OrganizationId_SizeId",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "SizeId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_samples_no",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "ModelId", "No" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_threading_OrganizationId_ColorId",
                schema: "kniterp",
                table: "dev_threading",
                columns: new[] { "OrganizationId", "ColorId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_threading_feeder",
                schema: "kniterp",
                table: "dev_threading",
                columns: new[] { "OrganizationId", "DetailId", "Feeder", "Side" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_files_detail",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "DetailId" },
                principalSchema: "kniterp",
                principalTable: "dev_details",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_files_machine",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "MachineId" },
                principalSchema: "kniterp",
                principalTable: "dev_machines",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_sketch",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "MainSketchFileId" },
                principalSchema: "kniterp",
                principalTable: "dev_files",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // D710: журнал станка — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_machine_notes_immutable]
                ON [kniterp].[dev_machine_notes]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51702, N''Журнал станка неизменяем: исправление — новой записью.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_dev_machine_notes_immutable];");
            migrationBuilder.DropForeignKey(
                name: "fk_dev_files_detail",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_files_machine",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_sketch",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropTable(
                name: "dev_detail_sizes",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_machine_notes",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_measure_values",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_model_operations",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_sample_color_shares",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_sample_measures",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_threading",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_operation_types",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_sample_details",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_measures",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_model_colors",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_details",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_samples",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_machines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_model_sizes",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_label",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_sections",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_settings_tolerance",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_MainSketchFileId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_dev_files_org_id",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropIndex(
                name: "ix_dev_files_machine",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropIndex(
                name: "IX_dev_files_OrganizationId_DetailId",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropColumn(
                name: "CompletenessSections",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "FactTolerancePercent",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "LabelHeightMm",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "LabelWidthMm",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "LabelsOnA4",
                schema: "kniterp",
                table: "dev_settings");

            migrationBuilder.DropColumn(
                name: "MainSketchFileId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "PackBox",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "DetailId",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropColumn(
                name: "MachineId",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropColumn(
                name: "MachineParams",
                schema: "kniterp",
                table: "dev_files");
        }
    }
}
