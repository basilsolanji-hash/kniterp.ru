using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressPrefix = table.Column<string>(type: "varchar(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.Id);
                    table.CheckConstraint("ck_user_sessions_revoked", "[RevokedAtUtc] IS NULL OR [RevokedAtUtc] >= [CreatedAtUtc]");
                    table.CheckConstraint("ck_user_sessions_seen", "[LastSeenAtUtc] >= [CreatedAtUtc]");
                    table.ForeignKey(
                        name: "FK_user_sessions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user",
                schema: "kniterp",
                table: "user_sessions",
                columns: new[] { "UserId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ux_user_sessions_public_id",
                schema: "kniterp",
                table: "user_sessions",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "kniterp");
        }
    }
}
