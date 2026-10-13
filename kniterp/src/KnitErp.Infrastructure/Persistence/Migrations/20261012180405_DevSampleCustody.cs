using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevSampleCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Custody",
                schema: "kniterp",
                table: "dev_samples",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CustodyClosedOn",
                schema: "kniterp",
                table: "dev_samples",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CustodyCounterpartyId",
                schema: "kniterp",
                table: "dev_samples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustodyNote",
                schema: "kniterp",
                table: "dev_samples",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustodyRecipient",
                schema: "kniterp",
                table: "dev_samples",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "HandedOn",
                schema: "kniterp",
                table: "dev_samples",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReturnBy",
                schema: "kniterp",
                table: "dev_samples",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_dev_samples_at_customer",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "Custody" },
                filter: "[Custody] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_dev_samples_OrganizationId_CustodyCounterpartyId",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "CustodyCounterpartyId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_samples_custody",
                schema: "kniterp",
                table: "dev_samples",
                sql: "[Custody] BETWEEN 0 AND 4");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_samples_custody_closed",
                schema: "kniterp",
                table: "dev_samples",
                sql: "[Custody] IN (0, 1) OR [CustodyClosedOn] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dev_samples_custody_out",
                schema: "kniterp",
                table: "dev_samples",
                sql: "[Custody] = 0 OR ([HandedOn] IS NOT NULL AND [CustodyRecipient] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_dev_samples_custody_counterparty",
                schema: "kniterp",
                table: "dev_samples",
                columns: new[] { "OrganizationId", "CustodyCounterpartyId" },
                principalSchema: "kniterp",
                principalTable: "counterparties",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_dev_samples_custody_counterparty",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "ix_dev_samples_at_customer",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropIndex(
                name: "IX_dev_samples_OrganizationId_CustodyCounterpartyId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_samples_custody",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_samples_custody_closed",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dev_samples_custody_out",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "Custody",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "CustodyClosedOn",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "CustodyCounterpartyId",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "CustodyNote",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "CustodyRecipient",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "HandedOn",
                schema: "kniterp",
                table: "dev_samples");

            migrationBuilder.DropColumn(
                name: "ReturnBy",
                schema: "kniterp",
                table: "dev_samples");
        }
    }
}
