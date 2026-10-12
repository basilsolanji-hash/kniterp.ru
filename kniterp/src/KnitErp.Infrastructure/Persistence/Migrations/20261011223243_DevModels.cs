using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.CreateTable(
                name: "dev_lookups",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_lookups", x => x.Id);
                    table.UniqueConstraint("ak_dev_lookups_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_lookups_kind", "[Kind] BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_dev_lookups_number", "[Number] IS NULL OR [Number] BETWEEN 0 AND 1000");
                    table.ForeignKey(
                        name: "FK_dev_lookups_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_stage_settings",
                schema: "kniterp",
                columns: table => new
                {
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<byte>(type: "tinyint", nullable: false),
                    PlanDays = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_stage_settings", x => new { x.OrganizationId, x.Stage });
                    table.CheckConstraint("ck_dev_stage_settings_days", "[PlanDays] BETWEEN 0 AND 365");
                    table.CheckConstraint("ck_dev_stage_settings_enabled", "[IsEnabled] = 1 OR [Stage] IN (5, 7, 8, 9)");
                    table.CheckConstraint("ck_dev_stage_settings_stage", "[Stage] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_dev_stage_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_models",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CollectionId = table.Column<long>(type: "bigint", nullable: true),
                    Category = table.Column<byte>(type: "tinyint", nullable: false),
                    Audience = table.Column<byte>(type: "tinyint", nullable: false),
                    ChildSafety = table.Column<bool>(type: "bit", nullable: false),
                    HasPattern = table.Column<bool>(type: "bit", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    DealId = table.Column<long>(type: "bigint", nullable: true),
                    DesignerUserId = table.Column<long>(type: "bigint", nullable: true),
                    DessinatorUserId = table.Column<long>(type: "bigint", nullable: true),
                    ConstructorUserId = table.Column<long>(type: "bigint", nullable: true),
                    TechnologistUserId = table.Column<long>(type: "bigint", nullable: true),
                    ProgrammerUserId = table.Column<long>(type: "bigint", nullable: true),
                    PlannedLaunch = table.Column<DateOnly>(type: "date", nullable: true),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Stage = table.Column<byte>(type: "tinyint", nullable: false),
                    StageSinceUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StageBeforeCancel = table.Column<byte>(type: "tinyint", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SizeRange = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ColorRange = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TargetCost = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    TargetPrice = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RunQuantity = table.Column<int>(type: "int", nullable: true),
                    BriefDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Composition = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Labeling = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Care = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_models", x => x.Id);
                    table.UniqueConstraint("ak_dev_models_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_dev_models_audience", "[Audience] BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_dev_models_cancel", "([Stage] = 99 AND [CancelReason] IS NOT NULL) OR ([Stage] <> 99 AND [CancelReason] IS NULL)");
                    table.CheckConstraint("ck_dev_models_category", "[Category] BETWEEN 1 AND 10 OR [Category] = 99");
                    table.CheckConstraint("ck_dev_models_child", "[ChildSafety] = 0 OR [Audience] = 3");
                    table.CheckConstraint("ck_dev_models_money", "([TargetCost] IS NULL OR [TargetCost] >= 0) AND ([TargetPrice] IS NULL OR [TargetPrice] >= 0)");
                    table.CheckConstraint("ck_dev_models_pattern", "[HasPattern] = 1 OR [Stage] <> 3");
                    table.CheckConstraint("ck_dev_models_priority", "[Priority] BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_dev_models_run", "[RunQuantity] IS NULL OR [RunQuantity] > 0");
                    table.CheckConstraint("ck_dev_models_stage", "[Stage] BETWEEN 1 AND 11 OR [Stage] = 99");
                    table.ForeignKey(
                        name: "FK_dev_models_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_author",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_collection",
                        columns: x => new { x.OrganizationId, x.CollectionId },
                        principalSchema: "kniterp",
                        principalTable: "dev_lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_constructor",
                        column: x => x.ConstructorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_customer",
                        columns: x => new { x.OrganizationId, x.CustomerId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_designer",
                        column: x => x.DesignerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_dessinator",
                        column: x => x.DessinatorUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_programmer",
                        column: x => x.ProgrammerUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_models_technologist",
                        column: x => x.TechnologistUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_files",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_files", x => x.Id);
                    table.CheckConstraint("ck_dev_files_kind", "[Kind] BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_dev_files_size", "[SizeBytes] BETWEEN 1 AND 52428800");
                    table.CheckConstraint("ck_dev_files_version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_dev_files_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_files_users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_files_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dev_model_stages",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ModelId = table.Column<long>(type: "bigint", nullable: false),
                    FromStage = table.Column<byte>(type: "tinyint", nullable: true),
                    ToStage = table.Column<byte>(type: "tinyint", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DaysInPrevious = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dev_model_stages", x => x.Id);
                    table.CheckConstraint("ck_dev_model_stages_days", "[DaysInPrevious] IS NULL OR [DaysInPrevious] >= 0");
                    table.CheckConstraint("ck_dev_model_stages_to", "[ToStage] BETWEEN 1 AND 11 OR [ToStage] = 99");
                    table.ForeignKey(
                        name: "FK_dev_model_stages_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dev_model_stages_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dev_model_stages_model",
                        columns: x => new { x.OrganizationId, x.ModelId },
                        principalSchema: "kniterp",
                        principalTable: "dev_models",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 29 OR [Entity] BETWEEN 40 AND 49");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 29 OR [Target] BETWEEN 40 AND 49");

            migrationBuilder.CreateIndex(
                name: "IX_dev_files_UploadedByUserId",
                schema: "kniterp",
                table: "dev_files",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_dev_files_version",
                schema: "kniterp",
                table: "dev_files",
                columns: new[] { "OrganizationId", "ModelId", "Kind", "FileName", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dev_lookups_name",
                schema: "kniterp",
                table: "dev_lookups",
                columns: new[] { "OrganizationId", "Kind", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_model_stages_model",
                schema: "kniterp",
                table: "dev_model_stages",
                columns: new[] { "OrganizationId", "ModelId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_model_stages_UserId",
                schema: "kniterp",
                table: "dev_model_stages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_ConstructorUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "ConstructorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_CreatedByUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_DesignerUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "DesignerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_DessinatorUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "DessinatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_CollectionId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "CollectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_OrganizationId_CustomerId",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_ProgrammerUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "ProgrammerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_dev_models_stage",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_dev_models_TechnologistUserId",
                schema: "kniterp",
                table: "dev_models",
                column: "TechnologistUserId");

            migrationBuilder.CreateIndex(
                name: "ux_dev_models_number",
                schema: "kniterp",
                table: "dev_models",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            // D702, D705: история стадий и файлы моделей — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_model_stages_immutable]
                ON [kniterp].[dev_model_stages]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51700, N''История стадий модели неизменяема: изменение и удаление запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_dev_files_immutable]
                ON [kniterp].[dev_files]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51701, N''Файлы модели неизменяемы: новая версия загружается отдельным файлом.'', 1;
                END');
                """);

            // D700: права разработки ролям P0 и роли отдела разработки в существующих организациях (новые получают их из матриц).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES ('owner', 'dev.view', 3), ('owner', 'dev.edit', 3), ('owner', 'dev.pattern', 3), ('owner', 'dev.sample', 3),
                             ('owner', 'dev.approve', 3), ('owner', 'dev.cost.view', 3), ('owner', 'dev.settings', 3),
                             ('admin', 'dev.view', 3), ('admin', 'dev.settings', 3), ('admin', 'dev.cost.view', 3),
                             ('department_head', 'dev.view', 1),
                             ('accountant', 'dev.view', 1), ('accountant', 'dev.cost.view', 3),
                             ('auditor', 'dev.view', 1), ('auditor', 'dev.cost.view', 1)) AS p([Role], [Code], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);

                INSERT INTO [kniterp].[roles] ([OrganizationId], [Code], [Name], [IsSystem])
                SELECT o.[Id], n.[Code], n.[Name], 1
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES ('dev_chief', N'Главный технолог'), ('dev_technologist', N'Разработчик (технолог)'),
                                   ('dev_dessinator', N'Дессинатор'), ('dev_sampler', N'Образцовщица')) AS n([Code], [Name])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[roles] r WHERE r.[OrganizationId] = o.[Id] AND r.[Code] = n.[Code]);

                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                JOIN (VALUES ('dev_chief', 'dev.view'), ('dev_chief', 'dev.edit'), ('dev_chief', 'dev.approve'), ('dev_chief', 'dev.cost.view'),
                             ('dev_chief', 'dev.settings'), ('dev_chief', 'catalog.item.view'),
                             ('dev_technologist', 'dev.view'), ('dev_technologist', 'dev.edit'), ('dev_technologist', 'catalog.item.view'),
                             ('dev_dessinator', 'dev.view'), ('dev_dessinator', 'dev.edit'), ('dev_dessinator', 'dev.pattern'), ('dev_dessinator', 'catalog.item.view'),
                             ('dev_sampler', 'dev.view'), ('dev_sampler', 'dev.sample'), ('dev_sampler', 'catalog.item.view')) AS p([Role], [Code]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE p FROM [kniterp].[role_permissions] p JOIN [kniterp].[roles] r ON r.[Id] = p.[RoleId]
                WHERE r.[Code] IN ('dev_chief', 'dev_technologist', 'dev_dessinator', 'dev_sampler');
                DELETE FROM [kniterp].[roles] WHERE [Code] IN ('dev_chief', 'dev_technologist', 'dev_dessinator', 'dev_sampler')
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_assignments] a WHERE a.[RoleId] = [kniterp].[roles].[Id]);
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] LIKE 'dev.%';
                DROP TRIGGER IF EXISTS [kniterp].[tr_dev_files_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_dev_model_stages_immutable];
                """);
            migrationBuilder.DropTable(
                name: "dev_files",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_model_stages",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_stage_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_models",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "dev_lookups",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 29");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 29");
        }
    }
}
