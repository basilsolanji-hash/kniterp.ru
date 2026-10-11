using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CounterpartyContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "counterparty_contracts",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ContractDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PaymentDays = table.Column<int>(type: "int", nullable: true),
                    AmountLimit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counterparty_contracts", x => x.Id);
                    table.UniqueConstraint("ak_counterparty_contracts_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_counterparty_contracts_days", "[PaymentDays] IS NULL OR [PaymentDays] BETWEEN 0 AND 365");
                    table.CheckConstraint("ck_counterparty_contracts_kind", "[Kind] IN (1, 2, 3, 9)");
                    table.CheckConstraint("ck_counterparty_contracts_limit", "[AmountLimit] IS NULL OR [AmountLimit] >= 0");
                    table.CheckConstraint("ck_counterparty_contracts_period", "[ValidFrom] IS NULL OR [ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
                    table.CheckConstraint("ck_counterparty_contracts_status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_counterparty_contracts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_counterparty_contracts_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counterparty_contracts_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "counterparty_contract_files",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    ContractId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counterparty_contract_files", x => x.Id);
                    table.CheckConstraint("ck_counterparty_contract_files_size", "[SizeBytes] BETWEEN 1 AND 5242880");
                    table.ForeignKey(
                        name: "FK_counterparty_contract_files_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_counterparty_contract_files_users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counterparty_contract_files_contract",
                        columns: x => new { x.OrganizationId, x.ContractId },
                        principalSchema: "kniterp",
                        principalTable: "counterparty_contracts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_counterparty_contract_files_contract",
                schema: "kniterp",
                table: "counterparty_contract_files",
                columns: new[] { "OrganizationId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_counterparty_contract_files_UploadedByUserId",
                schema: "kniterp",
                table: "counterparty_contract_files",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_counterparty_contracts_CreatedByUserId",
                schema: "kniterp",
                table: "counterparty_contracts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_counterparty_contracts_number",
                schema: "kniterp",
                table: "counterparty_contracts",
                columns: new[] { "OrganizationId", "CounterpartyId", "Number" },
                unique: true);

            // Сканы договоров — только вставка (D500): новая версия не затирает старую.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_counterparty_contract_files_immutable]
                ON [kniterp].[counterparty_contract_files]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Файлы договоров неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_counterparty_contract_files_immutable];");
            migrationBuilder.DropTable(
                name: "counterparty_contract_files",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "counterparty_contracts",
                schema: "kniterp");
        }
    }
}
