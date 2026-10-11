using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrPersonalData : Migration
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
                name: "hr_consent_scans",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_consent_scans", x => x.Id);
                    table.CheckConstraint("ck_hr_consent_scans_size", "[SizeBytes] BETWEEN 1 AND 3145728");
                    table.ForeignKey(
                        name: "FK_hr_consent_scans_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_consent_scans_users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_consent_scans_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_personal_data",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    Gender = table.Column<byte>(type: "tinyint", nullable: false),
                    BirthPlace = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    Citizenship = table.Column<string>(type: "nvarchar(344)", maxLength: 344, nullable: true),
                    PassportNumber = table.Column<string>(type: "nvarchar(130)", maxLength: 130, nullable: true),
                    PassportIssuer = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    PassportIssuedOn = table.Column<string>(type: "nvarchar(104)", maxLength: 104, nullable: true),
                    PassportDivisionCode = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: true),
                    Snils = table.Column<string>(type: "nvarchar(106)", maxLength: 106, nullable: true),
                    Inn = table.Column<string>(type: "nvarchar(109)", maxLength: 109, nullable: true),
                    RegistrationAddress = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    ActualAddress = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(130)", maxLength: 130, nullable: true),
                    Education = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    Profession = table.Column<string>(type: "nvarchar(344)", maxLength: 344, nullable: true),
                    MaritalStatus = table.Column<string>(type: "nvarchar(344)", maxLength: 344, nullable: true),
                    FamilyMembers = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    ConsentGivenOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ConsentValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    ConsentRevokedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_personal_data", x => x.Id);
                    table.CheckConstraint("ck_hr_personal_data_consent", "([ConsentGivenOn] IS NOT NULL OR ([ConsentValidUntil] IS NULL AND [ConsentRevokedOn] IS NULL)) AND ([ConsentValidUntil] IS NULL OR [ConsentValidUntil] >= [ConsentGivenOn]) AND ([ConsentRevokedOn] IS NULL OR [ConsentRevokedOn] >= [ConsentGivenOn])");
                    table.CheckConstraint("ck_hr_personal_data_gender", "[Gender] IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_hr_personal_data_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_personal_data_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

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

            migrationBuilder.CreateIndex(
                name: "ix_hr_consent_scans_employee",
                schema: "kniterp",
                table: "hr_consent_scans",
                columns: new[] { "OrganizationId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_consent_scans_UploadedByUserId",
                schema: "kniterp",
                table: "hr_consent_scans",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_personal_data_employee",
                schema: "kniterp",
                table: "hr_personal_data",
                columns: new[] { "OrganizationId", "EmployeeId" },
                unique: true);

            // Сканы согласий — только вставка (D200): история не переписывается и не удаляется.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_consent_scans_immutable]
                ON [kniterp].[hr_consent_scans]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Сканы согласий неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);

            // Право на изменение ПДн системным ролям существующих организаций: Владелец — «Д» (3), Администратор — «П» (10).
            migrationBuilder.Sql("""
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], 'hr.personal_data.edit', p.[Level]
                FROM [kniterp].[roles] r
                JOIN (VALUES ('owner', 3), ('admin', 10)) AS p([Role], [Level]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = 'hr.personal_data.edit');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] = 'hr.personal_data.edit';");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_hr_consent_scans_immutable];");
            migrationBuilder.DropTable(
                name: "hr_consent_scans",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_personal_data",
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
                sql: "[Entity] BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 10");
        }
    }
}
