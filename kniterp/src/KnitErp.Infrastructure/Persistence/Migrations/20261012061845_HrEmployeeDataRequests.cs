using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrEmployeeDataRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonalEmail",
                schema: "kniterp",
                table: "hr_personal_data",
                type: "nvarchar(754)",
                maxLength: 754,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hr_employee_data_requests",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    IssuedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employee_data_requests", x => x.Id);
                    table.UniqueConstraint("ak_hr_employee_data_requests_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_employee_data_requests_closed", "([Status] = 1 AND [ClosedAtUtc] IS NULL) OR ([Status] <> 1 AND [ClosedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("ck_hr_employee_data_requests_expires", "[ExpiresAtUtc] > [IssuedAtUtc]");
                    table.CheckConstraint("ck_hr_employee_data_requests_hash", "DATALENGTH([TokenHash]) = 32");
                    table.CheckConstraint("ck_hr_employee_data_requests_status", "[Status] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_hr_employee_data_requests_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_employee_data_requests_users_IssuedByUserId",
                        column: x => x.IssuedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_data_requests_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_employee_data_submissions",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    BirthDate = table.Column<string>(type: "nvarchar(104)", maxLength: 104, nullable: true),
                    PassportNumber = table.Column<string>(type: "nvarchar(130)", maxLength: 130, nullable: true),
                    PassportIssuer = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    PassportIssuedOn = table.Column<string>(type: "nvarchar(104)", maxLength: 104, nullable: true),
                    PassportDivisionCode = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: true),
                    RegistrationAddress = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    ActualAddress = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(130)", maxLength: 130, nullable: true),
                    PersonalEmail = table.Column<string>(type: "nvarchar(754)", maxLength: 754, nullable: true),
                    Snils = table.Column<string>(type: "nvarchar(106)", maxLength: 106, nullable: true),
                    Inn = table.Column<string>(type: "nvarchar(109)", maxLength: 109, nullable: true),
                    ConsentVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConsentTextHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ClientAddress = table.Column<string>(type: "nvarchar(197)", maxLength: 197, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RejectReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employee_data_submissions", x => x.Id);
                    table.UniqueConstraint("ak_hr_employee_data_submissions_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_hr_employee_data_submissions_consent", "LEN([ConsentTextHash]) = 64 AND LEN([ConsentVersion]) > 0");
                    table.CheckConstraint("ck_hr_employee_data_submissions_review", "([Status] = 1 AND [ReviewedAtUtc] IS NULL AND [ReviewedByUserId] IS NULL AND [RejectReason] IS NULL) OR ([Status] = 2 AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedByUserId] IS NOT NULL AND [RejectReason] IS NULL) OR ([Status] = 3 AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedByUserId] IS NOT NULL AND LEN([RejectReason]) > 0)");
                    table.CheckConstraint("ck_hr_employee_data_submissions_status", "[Status] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_hr_employee_data_submissions_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_employee_data_submissions_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_data_submissions_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_data_submissions_request",
                        columns: x => new { x.OrganizationId, x.RequestId },
                        principalSchema: "kniterp",
                        principalTable: "hr_employee_data_requests",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_employee_data_submission_files",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SubmissionId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employee_data_submission_files", x => x.Id);
                    table.CheckConstraint("ck_hr_employee_data_submission_files_kind", "[Kind] IN (2, 3, 4, 7, 8)");
                    table.CheckConstraint("ck_hr_employee_data_submission_files_size", "[SizeBytes] BETWEEN 1 AND 3145728");
                    table.ForeignKey(
                        name: "FK_hr_employee_data_submission_files_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_data_submission_files_submission",
                        columns: x => new { x.OrganizationId, x.SubmissionId },
                        principalSchema: "kniterp",
                        principalTable: "hr_employee_data_submissions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hr_employee_data_requests_employee",
                schema: "kniterp",
                table: "hr_employee_data_requests",
                columns: new[] { "OrganizationId", "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employee_data_requests_IssuedByUserId",
                schema: "kniterp",
                table: "hr_employee_data_requests",
                column: "IssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_employee_data_requests_token",
                schema: "kniterp",
                table: "hr_employee_data_requests",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hr_employee_data_submission_files_submission",
                schema: "kniterp",
                table: "hr_employee_data_submission_files",
                columns: new[] { "OrganizationId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "ix_hr_employee_data_submissions_employee",
                schema: "kniterp",
                table: "hr_employee_data_submissions",
                columns: new[] { "OrganizationId", "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employee_data_submissions_OrganizationId_RequestId",
                schema: "kniterp",
                table: "hr_employee_data_submissions",
                columns: new[] { "OrganizationId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employee_data_submissions_ReviewedByUserId",
                schema: "kniterp",
                table: "hr_employee_data_submissions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_hr_employee_data_submissions_request",
                schema: "kniterp",
                table: "hr_employee_data_submissions",
                column: "RequestId",
                unique: true);

            // D125: ссылки анкеты не удаляются; закрытая (использованная или отменённая) не меняется; токен, срок и сотрудник — никогда.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_employee_data_requests_closed_immutable]
                ON [kniterp].[hr_employee_data_requests]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                        THROW 51000, N''Ссылки анкеты сотрудника не удаляются.'', 1;
                    IF UPDATE([TokenHash]) OR UPDATE([ExpiresAtUtc]) OR UPDATE([IssuedAtUtc]) OR UPDATE([EmployeeId]) OR UPDATE([OrganizationId]) OR UPDATE([IssuedByUserId])
                        THROW 51000, N''Токен, срок и сотрудник ссылки анкеты не меняются.'', 1;
                    IF EXISTS (SELECT 1 FROM deleted WHERE [Status] <> 1)
                        THROW 51000, N''Использованная или отменённая ссылка анкеты не меняется.'', 1;
                END');
                """);

            // D125: значения анкеты не меняются, анкета не удаляется, решение (принять или отклонить) — один раз.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_employee_data_submissions_immutable]
                ON [kniterp].[hr_employee_data_submissions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                        THROW 51000, N''Анкеты сотрудников не удаляются.'', 1;
                    IF UPDATE([BirthDate]) OR UPDATE([PassportNumber]) OR UPDATE([PassportIssuer]) OR UPDATE([PassportIssuedOn])
                       OR UPDATE([PassportDivisionCode]) OR UPDATE([RegistrationAddress]) OR UPDATE([ActualAddress]) OR UPDATE([Phone])
                       OR UPDATE([PersonalEmail]) OR UPDATE([Snils]) OR UPDATE([Inn]) OR UPDATE([ConsentVersion]) OR UPDATE([ConsentTextHash])
                       OR UPDATE([ClientAddress]) OR UPDATE([SubmittedAtUtc]) OR UPDATE([EmployeeId]) OR UPDATE([RequestId]) OR UPDATE([OrganizationId])
                        THROW 51000, N''Значения анкеты сотрудника не меняются.'', 1;
                    IF EXISTS (SELECT 1 FROM deleted WHERE [Status] <> 1)
                        THROW 51000, N''Анкета уже рассмотрена: решение не меняется.'', 1;
                END');
                """);

            // D125: сканы анкеты — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_employee_data_submission_files_insert_only]
                ON [kniterp].[hr_employee_data_submission_files]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Файлы анкеты сотрудника неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_hr_employee_data_submission_files_insert_only];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_hr_employee_data_submissions_immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_hr_employee_data_requests_closed_immutable];");

            migrationBuilder.DropTable(
                name: "hr_employee_data_submission_files",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_employee_data_submissions",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "hr_employee_data_requests",
                schema: "kniterp");

            migrationBuilder.DropColumn(
                name: "PersonalEmail",
                schema: "kniterp",
                table: "hr_personal_data");
        }
    }
}
