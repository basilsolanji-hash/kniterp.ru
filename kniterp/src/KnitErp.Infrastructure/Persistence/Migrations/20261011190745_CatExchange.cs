using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_connections",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    System = table.Column<byte>(type: "tinyint", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TokenHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ConnectedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_connections_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_connections_users_ConnectedByUserId",
                        column: x => x.ConnectedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_import_runs",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    System = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Overwrite = table.Column<bool>(type: "bit", nullable: false),
                    DefaultItemType = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Stage = table.Column<string>(type: "varchar(40)", nullable: true),
                    StageOffset = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Errors = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_import_runs", x => x.Id);
                    table.CheckConstraint("ck_external_import_runs_status", "[Status] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_external_import_runs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_import_runs_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_links",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    System = table.Column<byte>(type: "tinyint", nullable: false),
                    ObjectType = table.Column<byte>(type: "tinyint", nullable: false),
                    LocalId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(64)", nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ExternalVersion = table.Column<string>(type: "varchar(40)", nullable: true),
                    ContentHash = table.Column<string>(type: "char(64)", nullable: true),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_links", x => x.Id);
                    table.CheckConstraint("ck_external_links_kinds", "[System] BETWEEN 1 AND 8 AND [ObjectType] BETWEEN 1 AND 6");
                    table.ForeignKey(
                        name: "FK_external_links_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "external_import_issues",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    Entity = table.Column<string>(type: "varchar(40)", nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(64)", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsError = table.Column<bool>(type: "bit", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_import_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_import_issues_external_import_runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "kniterp",
                        principalTable: "external_import_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_connections_ConnectedByUserId",
                schema: "kniterp",
                table: "external_connections",
                column: "ConnectedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_external_connections_org_system",
                schema: "kniterp",
                table: "external_connections",
                columns: new[] { "OrganizationId", "System" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_import_issues_run",
                schema: "kniterp",
                table: "external_import_issues",
                columns: new[] { "RunId", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_external_import_runs_queue",
                schema: "kniterp",
                table: "external_import_runs",
                columns: new[] { "Status", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_external_import_runs_RequestedByUserId",
                schema: "kniterp",
                table: "external_import_runs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_external_import_runs_active",
                schema: "kniterp",
                table: "external_import_runs",
                columns: new[] { "OrganizationId", "System" },
                unique: true,
                filter: "[Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_external_links_code",
                schema: "kniterp",
                table: "external_links",
                columns: new[] { "OrganizationId", "System", "ObjectType", "ExternalCode" });

            migrationBuilder.CreateIndex(
                name: "ux_external_links_external",
                schema: "kniterp",
                table: "external_links",
                columns: new[] { "OrganizationId", "System", "ObjectType", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_external_links_local",
                schema: "kniterp",
                table: "external_links",
                columns: new[] { "OrganizationId", "System", "ObjectType", "LocalId" },
                unique: true);

            // D310: право «МойСклад: подключение и загрузка» — Владельцу и Администратору действующих организаций.
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], 'integration.moysklad.manage', 3
                FROM [kniterp].[roles] r
                WHERE r.[IsSystem] = 1 AND r.[Code] IN ('owner', 'admin')
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = 'integration.moysklad.manage');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = 'integration.moysklad.manage';");

            migrationBuilder.DropTable(
                name: "external_connections",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "external_import_issues",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "external_links",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "external_import_runs",
                schema: "kniterp");
        }
    }
}
