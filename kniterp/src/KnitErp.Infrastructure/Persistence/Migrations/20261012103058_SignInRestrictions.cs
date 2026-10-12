using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignInRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "EndReason",
                schema: "kniterp",
                table: "user_sessions",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndsAtUtc",
                schema: "kniterp",
                table: "user_sessions",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_organization_members_org_id",
                schema: "kniterp",
                table: "organization_members",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "user_sign_in_restrictions",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    MemberId = table.Column<long>(type: "bigint", nullable: false),
                    SingleDevice = table.Column<bool>(type: "bit", nullable: false),
                    AllowedNetworks = table.Column<string>(type: "varchar(1000)", nullable: true),
                    AllowedDays = table.Column<byte>(type: "tinyint", nullable: true),
                    WindowStartMinute = table.Column<short>(type: "smallint", nullable: true),
                    WindowEndMinute = table.Column<short>(type: "smallint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sign_in_restrictions", x => x.Id);
                    table.CheckConstraint("ck_user_sign_in_restrictions_networks", "[AllowedNetworks] IS NULL OR LEN([AllowedNetworks]) > 0");
                    table.CheckConstraint("ck_user_sign_in_restrictions_window", "([AllowedDays] IS NULL AND [WindowStartMinute] IS NULL AND [WindowEndMinute] IS NULL) OR ([AllowedDays] BETWEEN 1 AND 127 AND [WindowStartMinute] BETWEEN 0 AND 1439 AND [WindowEndMinute] BETWEEN 0 AND 1439)");
                    table.ForeignKey(
                        name: "fk_user_sign_in_restrictions_member",
                        columns: x => new { x.OrganizationId, x.MemberId },
                        principalSchema: "kniterp",
                        principalTable: "organization_members",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sign_in_restrictions_updated_by",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_sessions_ends",
                schema: "kniterp",
                table: "user_sessions",
                sql: "([EndsAtUtc] IS NULL AND [EndReason] IS NULL) OR ([EndsAtUtc] IS NOT NULL AND [EndReason] IN (1, 2) AND [EndsAtUtc] >= [CreatedAtUtc])");

            migrationBuilder.CreateIndex(
                name: "IX_user_sign_in_restrictions_UpdatedByUserId",
                schema: "kniterp",
                table: "user_sign_in_restrictions",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_user_sign_in_restrictions_member",
                schema: "kniterp",
                table: "user_sign_in_restrictions",
                columns: new[] { "OrganizationId", "MemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_sign_in_restrictions",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_sessions_ends",
                schema: "kniterp",
                table: "user_sessions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_organization_members_org_id",
                schema: "kniterp",
                table: "organization_members");

            migrationBuilder.DropColumn(
                name: "EndReason",
                schema: "kniterp",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "EndsAtUtc",
                schema: "kniterp",
                table: "user_sessions");
        }
    }
}
