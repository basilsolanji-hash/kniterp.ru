using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmMailTemplatesQueueInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_mailings_status",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.AddColumn<long>(
                name: "QueuedByUserId",
                schema: "kniterp",
                table: "crm_mailings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledAtUtc",
                schema: "kniterp",
                table: "crm_mailings",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttemptedAtUtc",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                type: "datetime2(3)",
                nullable: true);

            // Получатели уже отправленных рассылок — с отметкой попытки (время отправки рассылки): в очереди их нет.
            migrationBuilder.Sql(@"EXEC(N'UPDATE r SET r.[AttemptedAtUtc] = m.[SentAtUtc] FROM [kniterp].[crm_mailing_recipients] r
JOIN [kniterp].[crm_mailings] m ON m.[Id] = r.[MailingId] WHERE m.[Status] = 2 AND r.[AttemptedAtUtc] IS NULL')");

            migrationBuilder.CreateTable(
                name: "crm_email_templates",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_email_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_crm_email_templates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_crm_email_templates_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_mailboxes",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Login = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Password = table.Column<string>(type: "nvarchar(877)", maxLength: 877, nullable: true),
                    CreateLeads = table.Column<bool>(type: "bit", nullable: false),
                    ConnectedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: true),
                    LastUid = table.Column<long>(type: "bigint", nullable: true),
                    LastCheckAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_mailboxes", x => x.Id);
                    table.CheckConstraint("ck_crm_mailboxes_port", "[Port] IN (0, 143, 993)");
                    table.ForeignKey(
                        name: "FK_crm_mailboxes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_crm_mailboxes_users_ConnectedByUserId",
                        column: x => x.ConnectedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_mailings_queue",
                schema: "kniterp",
                table: "crm_mailings",
                columns: new[] { "Status", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailings_QueuedByUserId",
                schema: "kniterp",
                table: "crm_mailings",
                column: "QueuedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_mailings_status",
                schema: "kniterp",
                table: "crm_mailings",
                sql: "[Status] IN (1, 2, 3, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_crm_email_templates_CreatedByUserId",
                schema: "kniterp",
                table: "crm_email_templates",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_email_templates_org_name",
                schema: "kniterp",
                table: "crm_email_templates",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailboxes_ConnectedByUserId",
                schema: "kniterp",
                table: "crm_mailboxes",
                column: "ConnectedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_mailboxes_org",
                schema: "kniterp",
                table: "crm_mailboxes",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailings_queued_by",
                schema: "kniterp",
                table: "crm_mailings",
                column: "QueuedByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailings_queued_by",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropTable(
                name: "crm_email_templates",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_mailboxes",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ix_crm_mailings_queue",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailings_QueuedByUserId",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_mailings_status",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropColumn(
                name: "QueuedByUserId",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropColumn(
                name: "ScheduledAtUtc",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropColumn(
                name: "AttemptedAtUtc",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_mailings_status",
                schema: "kniterp",
                table: "crm_mailings",
                sql: "[Status] IN (1, 2)");
        }
    }
}
