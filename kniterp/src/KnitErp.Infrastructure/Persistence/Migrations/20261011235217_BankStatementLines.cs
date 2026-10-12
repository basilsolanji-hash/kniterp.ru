using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankStatementLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_statement_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    MoneyAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Direction = table.Column<byte>(type: "tinyint", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CustomerPaymentId = table.Column<long>(type: "bigint", nullable: true),
                    SupplierPaymentId = table.Column<long>(type: "bigint", nullable: true),
                    ImportedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_lines", x => x.Id);
                    table.CheckConstraint("ck_bank_statement_lines_amount", "[Amount] > 0");
                    table.CheckConstraint("ck_bank_statement_lines_payment", "([Direction] = 1 AND [CustomerPaymentId] IS NOT NULL AND [SupplierPaymentId] IS NULL) OR ([Direction] = 2 AND [SupplierPaymentId] IS NOT NULL AND [CustomerPaymentId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_lines_account",
                        columns: x => new { x.OrganizationId, x.MoneyAccountId },
                        principalSchema: "kniterp",
                        principalTable: "legal_entity_accounts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_lines_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_lines_customer_payment",
                        column: x => x.CustomerPaymentId,
                        principalSchema: "kniterp",
                        principalTable: "customer_payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_lines_supplier_payment",
                        column: x => x.SupplierPaymentId,
                        principalSchema: "kniterp",
                        principalTable: "supplier_payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_statement_lines_user",
                        column: x => x.ImportedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_CustomerPaymentId",
                schema: "kniterp",
                table: "bank_statement_lines",
                column: "CustomerPaymentId");

            migrationBuilder.CreateIndex(
                name: "ix_bank_statement_lines_date",
                schema: "kniterp",
                table: "bank_statement_lines",
                columns: new[] { "OrganizationId", "DocumentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_ImportedByUserId",
                schema: "kniterp",
                table: "bank_statement_lines",
                column: "ImportedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_OrganizationId_CounterpartyId",
                schema: "kniterp",
                table: "bank_statement_lines",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_OrganizationId_MoneyAccountId",
                schema: "kniterp",
                table: "bank_statement_lines",
                columns: new[] { "OrganizationId", "MoneyAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_SupplierPaymentId",
                schema: "kniterp",
                table: "bank_statement_lines",
                column: "SupplierPaymentId");

            migrationBuilder.CreateIndex(
                name: "ux_bank_statement_lines_fingerprint",
                schema: "kniterp",
                table: "bank_statement_lines",
                columns: new[] { "OrganizationId", "Fingerprint" },
                unique: true);

            // D119: журнал загруженных строк выписки — только вставка: удаление строки открыло бы повторную загрузку и дубль оплаты.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_bank_statement_lines_immutable]
                ON [kniterp].[bank_statement_lines]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Строки загруженных выписок неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [kniterp].[tr_bank_statement_lines_immutable];");
            migrationBuilder.DropTable(
                name: "bank_statement_lines",
                schema: "kniterp");
        }
    }
}
