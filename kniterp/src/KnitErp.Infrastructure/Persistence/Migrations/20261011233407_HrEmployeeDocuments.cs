using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrEmployeeDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireAgreements",
                schema: "kniterp",
                table: "hr_settings",
                type: "bit",
                nullable: false,
                defaultValue: true); // D227: в действующих организациях правило включено

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                schema: "kniterp",
                table: "hr_consent_scans",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.CreateTable(
                name: "hr_employee_agreements",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    EmployeeId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TextHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Method = table.Column<byte>(type: "tinyint", nullable: false),
                    SignedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SignedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_employee_agreements", x => x.Id);
                    table.CheckConstraint("ck_hr_employee_agreements_hash", "LEN([TextHash]) = 64");
                    table.CheckConstraint("ck_hr_employee_agreements_kind", "[Kind] BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_hr_employee_agreements_method", "[Method] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_hr_employee_agreements_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_employee_agreements_users_SignedByUserId",
                        column: x => x.SignedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_hr_employee_agreements_employee",
                        columns: x => new { x.OrganizationId, x.EmployeeId },
                        principalSchema: "kniterp",
                        principalTable: "employees",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_hr_consent_scans_kind",
                schema: "kniterp",
                table: "hr_consent_scans",
                sql: "[Kind] BETWEEN 1 AND 13");

            migrationBuilder.CreateIndex(
                name: "ix_hr_employee_agreements_employee",
                schema: "kniterp",
                table: "hr_employee_agreements",
                columns: new[] { "OrganizationId", "EmployeeId", "Kind", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_employee_agreements_SignedByUserId",
                schema: "kniterp",
                table: "hr_employee_agreements",
                column: "SignedByUserId");

            // Подписи документов перед доступом — только вставка (D227): подписанное не переписывается и не удаляется.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_hr_employee_agreements_insert_only]
                ON [kniterp].[hr_employee_agreements]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Подписи документов неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_hr_employee_agreements_insert_only];");

            migrationBuilder.DropTable(
                name: "hr_employee_agreements",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_hr_consent_scans_kind",
                schema: "kniterp",
                table: "hr_consent_scans");

            migrationBuilder.DropColumn(
                name: "RequireAgreements",
                schema: "kniterp",
                table: "hr_settings");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "kniterp",
                table: "hr_consent_scans");
        }
    }
}
