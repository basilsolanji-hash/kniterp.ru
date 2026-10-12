using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sales_credit_approvals",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Debt = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Limit = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Overdue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ApprovedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_credit_approvals", x => x.Id);
                    table.CheckConstraint("ck_sales_credit_approvals_amount", "[Amount] > 0");
                    table.CheckConstraint("ck_sales_credit_approvals_limit", "[Limit] IS NULL OR [Limit] >= 0");
                    table.CheckConstraint("ck_sales_credit_approvals_reason", "LEN([Reason]) > 0");
                    table.ForeignKey(
                        name: "FK_sales_credit_approvals_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_credit_approvals_order",
                        columns: x => new { x.OrganizationId, x.OrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_credit_approvals_user",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_credit_approvals_ApprovedByUserId",
                schema: "kniterp",
                table: "sales_credit_approvals",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_sales_credit_approvals_order",
                schema: "kniterp",
                table: "sales_credit_approvals",
                columns: new[] { "OrganizationId", "OrderId", "ApprovedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_credit_approvals",
                schema: "kniterp");
        }
    }
}
