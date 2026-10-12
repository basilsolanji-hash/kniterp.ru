using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmMaxChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddColumn<string>(
                name: "Peer",
                schema: "kniterp",
                table: "crm_activities",
                type: "nvarchar(100)",
                maxLength: 100,
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.DropColumn(
                name: "Peer",
                schema: "kniterp",
                table: "crm_activities");
        }
    }
}
