using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmMessengerChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crm_messenger_channels",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(1410)", maxLength: 1410, nullable: true),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AppSecret = table.Column<string>(type: "nvarchar(610)", maxLength: 610, nullable: true),
                    SecretHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    AccountName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConnectedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DisconnectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_messenger_channels", x => x.Id);
                    table.CheckConstraint("ck_crm_messenger_channels_kind", "[Kind] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_crm_messenger_channels_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_crm_messenger_channels_users_ConnectedByUserId",
                        column: x => x.ConnectedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crm_messenger_channels_ConnectedByUserId",
                schema: "kniterp",
                table: "crm_messenger_channels",
                column: "ConnectedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_messenger_channels_org_kind",
                schema: "kniterp",
                table: "crm_messenger_channels",
                columns: new[] { "OrganizationId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_crm_messenger_channels_secret",
                schema: "kniterp",
                table: "crm_messenger_channels",
                column: "SecretHash",
                unique: true,
                filter: "[SecretHash] IS NOT NULL");

            // D670: подключение MAX переезжает из настроек CRM в каналы мессенджеров (токен уже зашифрован тем же ключом — копируется как есть).
            migrationBuilder.Sql(@"EXEC(N'INSERT INTO [kniterp].[crm_messenger_channels] ([OrganizationId], [Kind], [Token], [SecretHash], [AccountName], [ConnectedByUserId], [ConnectedAtUtc])
SELECT s.[OrganizationId], 1, s.[MaxToken], s.[MaxSecretHash], N''MAX'', s.[MaxConnectedByUserId], s.[MaxConnectedAtUtc]
FROM [kniterp].[crm_settings] s
WHERE s.[MaxToken] IS NOT NULL AND s.[MaxSecretHash] IS NOT NULL AND s.[MaxConnectedByUserId] IS NOT NULL AND s.[MaxConnectedAtUtc] IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM [kniterp].[crm_messenger_channels] c WHERE c.[OrganizationId] = s.[OrganizationId] AND c.[Kind] = 1)')");

            // Собеседник в чатах — с мессенджером («MAX:123»): мессенджер берётся из внешнего номера входящего, ответы до этой версии были только в MAX.
            migrationBuilder.Sql(@"EXEC(N'UPDATE [kniterp].[crm_activities] SET [Peer] = LEFT(
    CASE WHEN [ExternalRef] LIKE N''%:%'' THEN LEFT([ExternalRef], CHARINDEX(N'':'', [ExternalRef]) - 1) ELSE N''MAX'' END + N'':'' + [Peer], 100)
WHERE [Source] = 6 AND [Peer] IS NOT NULL AND [Peer] NOT LIKE N''MAX:%'' AND [Peer] NOT LIKE N''Telegram:%'' AND [Peer] NOT LIKE N''WhatsApp:%''')");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_settings_max_user",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropIndex(
                name: "IX_crm_settings_MaxConnectedByUserId",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropIndex(
                name: "ux_crm_settings_max_secret",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "MaxConnectedAtUtc",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "MaxConnectedByUserId",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "MaxSecretHash",
                schema: "kniterp",
                table: "crm_settings");

            migrationBuilder.DropColumn(
                name: "MaxToken",
                schema: "kniterp",
                table: "crm_settings");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_messenger_channels",
                schema: "kniterp");

            migrationBuilder.AddColumn<DateTime>(
                name: "MaxConnectedAtUtc",
                schema: "kniterp",
                table: "crm_settings",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MaxConnectedByUserId",
                schema: "kniterp",
                table: "crm_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "MaxSecretHash",
                schema: "kniterp",
                table: "crm_settings",
                type: "binary(32)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaxToken",
                schema: "kniterp",
                table: "crm_settings",
                type: "nvarchar(877)",
                maxLength: 877,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_settings_MaxConnectedByUserId",
                schema: "kniterp",
                table: "crm_settings",
                column: "MaxConnectedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_settings_max_secret",
                schema: "kniterp",
                table: "crm_settings",
                column: "MaxSecretHash",
                unique: true,
                filter: "[MaxSecretHash] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_crm_settings_max_user",
                schema: "kniterp",
                table: "crm_settings",
                column: "MaxConnectedByUserId",
                principalSchema: "kniterp",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
