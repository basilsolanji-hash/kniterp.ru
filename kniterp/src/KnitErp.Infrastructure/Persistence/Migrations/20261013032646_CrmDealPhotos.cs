using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmDealPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crm_deal_photos",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(20)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ArchivedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deal_photos", x => x.Id);
                    table.CheckConstraint("ck_crm_deal_photos_archive", "([IsArchived] = 0 AND [ArchivedAtUtc] IS NULL AND [ArchivedByUserId] IS NULL) OR ([IsArchived] = 1 AND [ArchivedAtUtc] IS NOT NULL AND [ArchivedByUserId] IS NOT NULL)");
                    table.CheckConstraint("ck_crm_deal_photos_size", "[SizeBytes] BETWEEN 1 AND 5242880 AND DATALENGTH([Content]) = [SizeBytes]");
                    table.CheckConstraint("ck_crm_deal_photos_type", "[ContentType] IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.ForeignKey(
                        name: "fk_crm_deal_photos_archived_by",
                        column: x => x.ArchivedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_photos_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_photos_deal",
                        columns: x => new { x.OrganizationId, x.DealId },
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_photos_organization",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_photos_ArchivedByUserId",
                schema: "kniterp",
                table: "crm_deal_photos",
                column: "ArchivedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_photos_CreatedByUserId",
                schema: "kniterp",
                table: "crm_deal_photos",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deal_photos_deal",
                schema: "kniterp",
                table: "crm_deal_photos",
                columns: new[] { "OrganizationId", "DealId", "IsArchived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_deal_photos",
                schema: "kniterp");
        }
    }
}
