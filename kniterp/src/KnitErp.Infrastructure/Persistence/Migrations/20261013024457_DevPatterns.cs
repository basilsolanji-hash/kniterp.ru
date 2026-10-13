using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevPatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssembledByUserId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssemblyMinutes",
                schema: "kniterp",
                table: "dev_samples",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ColorwayId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "KnittedByUserId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PatternVersion",
                schema: "kniterp",
                table: "dev_samples",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReworkAssigneeUserId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReworkDueDate",
                schema: "kniterp",
                table: "dev_samples",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReworkWhat",
                schema: "kniterp",
                table: "dev_samples",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YarnBatch",
                schema: "kniterp",
                table: "dev_samples",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ColorwayId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DensityLoops",
                schema: "kniterp",
                table: "dev_models",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DensityRows",
                schema: "kniterp",
                table: "dev_models",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DyeNumber",
                schema: "kniterp",
                table: "dev_models",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PatternId",
                schema: "kniterp",
                table: "dev_models",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PatternVersion",
                schema: "kniterp",
                table: "dev_models",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShrinkagePercent",
                schema: "kniterp",
                table: "dev_models",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightGrams",
                schema: "kniterp",
                table: "dev_model_sizes",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SampleId",
                schema: "kniterp",
                table: "dev_files",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dev_category_measures",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Tolerance = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: false),
                    Step = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_category_measures", x => x.Id);
                    table.CheckConstraint("ck_dev_category_measures_tolerance", "[Tolerance] >= 0");
                    table.ForeignKey(
                        name: "FK_dev_category_measures_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_patterns",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    RapportWidth = table.Column<int>(type: "int", nullable: true),
                    RapportHeight = table.Column<int>(type: "int", nullable: true),
                    MaxFloatMm = table.Column<decimal>(type: "decimal(6,1)", precision: 6, scale: 1, nullable: true),
                    MachineClassId = table.Column<long>(type: "bigint", nullable: true),
                    ProductWidthLoops = table.Column<int>(type: "int", nullable: true),
                    AlignmentRule = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    DessinatorUserId = table.Column<long>(type: "bigint", nullable: true),
                    ProgrammerUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReadyForProgrammingAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CopiedFromPatternId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_patterns", x => x.Id);
                    table.UniqueConstraint("ak_dev_patterns_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_patterns_kind", "[Kind] BETWEEN 1 AND 6");
                    table.CheckConstraint("ck_dev_patterns_rapport", "([RapportWidth] IS NULL OR [RapportWidth] BETWEEN 1 AND 2000) AND ([RapportHeight] IS NULL OR [RapportHeight] BETWEEN 1 AND 2000)");
                    table.CheckConstraint("ck_dev_patterns_status", "[Status] BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_dev_patterns_version", "[CurrentVersion] >= 1");
                    table.ForeignKey(
                        name: "FK_dev_patterns_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_patterns_copied_from",
                        columns: x => new { x.OrganizationId, x.CopiedFromPatternId },
                        principalSchema: "kniterp",
                        principalTable: "dev_patterns",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_patterns_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_patterns_dessinator",
                        column: x => x.DessinatorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_patterns_machine_class",
                        columns: x => new { x.OrganizationId, x.MachineClassId },
                        principalSchema: "kniterp",
                        principalTable: "dev_lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_patterns_programmer",
                        column: x => x.ProgrammerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_pattern_colors",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PatternId = table.Column<long>(type: "bigint", nullable: false),
                    No = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SharePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    Feeder = table.Column<int>(type: "int", nullable: true),
                    GridKey = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_colors", x => x.Id);
                    table.UniqueConstraint("ak_dev_pattern_colors_org_id", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("ak_dev_pattern_colors_pattern_id", x => new { x.OrganizationId, x.PatternId, x.Id });
                    table.CheckConstraint("ck_dev_pattern_colors_no", "[No] BETWEEN 1 AND 16");
                    table.CheckConstraint("ck_dev_pattern_colors_share", "[SharePercent] IS NULL OR [SharePercent] BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_dev_pattern_colors_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_colors_pattern",
                        columns: x => new { x.OrganizationId, x.PatternId },
                        principalSchema: "kniterp",
                        principalTable: "dev_patterns",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_pattern_colorways",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PatternId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_colorways", x => x.Id);
                    table.UniqueConstraint("ak_dev_pattern_colorways_org_id", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("ak_dev_pattern_colorways_pattern_id", x => new { x.OrganizationId, x.PatternId, x.Id });
                    table.ForeignKey(
                        name: "FK_dev_pattern_colorways_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_colorways_pattern",
                        columns: x => new { x.OrganizationId, x.PatternId },
                        principalSchema: "kniterp",
                        principalTable: "dev_patterns",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_pattern_versions",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PatternId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Change = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_versions", x => x.Id);
                    table.UniqueConstraint("ak_dev_pattern_versions_no", x => new { x.OrganizationId, x.PatternId, x.Version });
                    table.ForeignKey(
                        name: "FK_dev_pattern_versions_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_pattern_versions_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_versions_pattern",
                        columns: x => new { x.OrganizationId, x.PatternId },
                        principalSchema: "kniterp",
                        principalTable: "dev_patterns",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_pattern_colorway_yarns",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ColorwayId = table.Column<long>(type: "bigint", nullable: false),
                    ColorId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Swatch = table.Column<string>(type: "char(7)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_colorway_yarns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dev_pattern_colorway_yarns_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_colorway_yarns_color",
                        columns: x => new { x.OrganizationId, x.ColorId },
                        principalSchema: "kniterp",
                        principalTable: "dev_pattern_colors",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_colorway_yarns_colorway",
                        columns: x => new { x.OrganizationId, x.ColorwayId },
                        principalSchema: "kniterp",
                        principalTable: "dev_pattern_colorways",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_dev_pattern_colorway_yarns_item",
                        columns: x => new { x.OrganizationId, x.ItemId },
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_pattern_files",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PatternId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    KnitSeconds = table.Column<int>(type: "int", nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_pattern_files", x => x.Id);
                    table.CheckConstraint("ck_dev_pattern_files_kind", "[Kind] IN (1, 2)");
                    table.CheckConstraint("ck_dev_pattern_files_size", "[SizeBytes] BETWEEN 1 AND 52428800");
                    table.ForeignKey(
                        name: "FK_dev_pattern_files_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_pattern_files_users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_pattern_files_version",
                        columns: x => new { x.OrganizationId, x.PatternId, x.Version },
                        principalSchema: "kniterp",
                        principalTable: "dev_pattern_versions",
                        principalColumns: new[] { "OrganizationId", "PatternId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_AssembledByUserId",
                schema: "kniterp",
                table: "dev_samples",
                column: "AssembledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_KnittedByUserId",
                schema: "kniterp",
                table: "dev_samples",
                column: "KnittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_OrganizationId_ColorwayId",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "ColorwayId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_ReworkAssigneeUserId",
                schema: "kniterp",
                table: "dev_samples",
                column: "ReworkAssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_PatternId_ColorwayId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PatternId", "ColorwayId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_PatternId_PatternVersion",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PatternId", "PatternVersion" });

            migrationBuilder.CreateIndex(
                name: "ix_dev_models_pattern",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PatternId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_files_OrganizationId_SampleId",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "SampleId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_category_measures_name",
                schema: "kniterp",
                table: "dev_category_measures",
                columns: new[] { "OrganizationId", "Category", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_pattern_colors_no",
                schema: "kniterp",
                table: "dev_pattern_colors",
                columns: new[] { "OrganizationId", "PatternId", "No" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_pattern_colorway_yarns_OrganizationId_ColorId",
                schema: "kniterp",
                table: "dev_pattern_colorway_yarns",
                columns: new[] { "OrganizationId", "ColorId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_pattern_colorway_yarns_OrganizationId_ItemId",
                schema: "kniterp",
                table: "dev_pattern_colorway_yarns",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_pattern_colorway_yarns",
                schema: "kniterp",
                table: "dev_pattern_colorway_yarns",
                columns: new[] { "OrganizationId", "ColorwayId", "ColorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_pattern_colorways_name",
                schema: "kniterp",
                table: "dev_pattern_colorways",
                columns: new[] { "OrganizationId", "PatternId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dev_pattern_files_UploadedByUserId",
                schema: "kniterp",
                table: "dev_pattern_files",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_dev_pattern_files_version",
                schema: "kniterp",
                table: "dev_pattern_files",
                columns: new[] { "OrganizationId", "PatternId", "Version", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_pattern_versions_CreatedByUserId",
                schema: "kniterp",
                table: "dev_pattern_versions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_patterns_CreatedByUserId",
                schema: "kniterp",
                table: "dev_patterns",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_patterns_DessinatorUserId",
                schema: "kniterp",
                table: "dev_patterns",
                column: "DessinatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_patterns_OrganizationId_CopiedFromPatternId",
                schema: "kniterp",
                table: "dev_patterns",
                columns: new[] { "OrganizationId", "CopiedFromPatternId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_patterns_OrganizationId_MachineClassId",
                schema: "kniterp",
                table: "dev_patterns",
                columns: new[] { "OrganizationId", "MachineClassId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_patterns_ProgrammerUserId",
                schema: "kniterp",
                table: "dev_patterns",
                column: "ProgrammerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_dev_patterns_status",
                schema: "kniterp",
                table: "dev_patterns",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_dev_patterns_number",
                schema: "kniterp",
                table: "dev_patterns",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_files_sample",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "SampleId" },
                principalSchema: "kniterp",
                principalTable: "dev_samples",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_colorway",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PatternId", "ColorwayId" },
                principalSchema: "kniterp",
                principalTable: "dev_pattern_colorways",
                principalColumns: new[] { "OrganizationId", "PatternId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_models_pattern_version",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "PatternId", "PatternVersion" },
                principalSchema: "kniterp",
                principalTable: "dev_pattern_versions",
                principalColumns: new[] { "OrganizationId", "PatternId", "Version" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_assembled_by",
                schema: "kniterp",
                table: "dev_samples",
                column: "AssembledByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_colorway",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "ColorwayId" },
                principalSchema: "kniterp",
                principalTable: "dev_pattern_colorways",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_knitted_by",
                schema: "kniterp",
                table: "dev_samples",
                column: "KnittedByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_rework_assignee",
                schema: "kniterp",
                table: "dev_samples",
                column: "ReworkAssigneeUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // D727: версии рисунка и их файлы — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_pattern_versions_immutable]
                ON [kniterp].[dev_pattern_versions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51727, N''Версии рисунка неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_pattern_files_immutable]
                ON [kniterp].[dev_pattern_files]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51728, N''Файлы версий рисунка неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS [kniterp].[tr_dev_pattern_files_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_dev_pattern_versions_immutable];
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_dev_files_sample",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_colorway",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_models_pattern_version",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_assembled_by",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_colorway",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_knitted_by",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_rework_assignee",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropTable(
                name: "dev_category_measures",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_pattern_colorway_yarns",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_pattern_files",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_pattern_colors",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_pattern_colorways",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_pattern_versions",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_patterns",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_AssembledByUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_KnittedByUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_OrganizationId_ColorwayId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_ReworkAssigneeUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_PatternId_ColorwayId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "IX_dev_models_OrganizationId_PatternId_PatternVersion",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "ix_dev_models_pattern",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropIndex(
                name: "IX_dev_files_OrganizationId_SampleId",
                schema: "kniterp",
                table: "dev_files");

            migrationBuilder.DropColumn(
                name: "AssembledByUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "AssemblyMinutes",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ColorwayId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "KnittedByUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "PatternVersion",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ReworkAssigneeUserId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ReworkDueDate",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ReworkWhat",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "YarnBatch",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ColorwayId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "DensityLoops",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "DensityRows",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "DyeNumber",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "PatternId",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "PatternVersion",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "ShrinkagePercent",
                schema: "kniterp",
                table: "dev_models");

            migrationBuilder.DropColumn(
                name: "WeightGrams",
                schema: "kniterp",
                table: "dev_model_sizes");

            migrationBuilder.DropColumn(
                name: "SampleId",
                schema: "kniterp",
                table: "dev_files");
        }
    }
}
