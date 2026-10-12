using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_mail_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    NoAutoSend = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_mail_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_mail_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_mail_settings_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_document_images",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    LegalEntityId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_document_images", x => x.Id);
                    table.CheckConstraint("ck_sales_document_images_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_sales_document_images_size", "[SizeBytes] BETWEEN 1 AND 1048576");
                    table.CheckConstraint("ck_sales_document_images_type", "[ContentType] IN (N'image/png', N'image/jpeg')");
                    table.ForeignKey(
                        name: "FK_sales_document_images_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_images_legal_entity",
                        columns: x => new { x.OrganizationId, x.LegalEntityId },
                        principalSchema: "kniterp",
                        principalTable: "legal_entities",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_images_user",
                        column: x => x.UploadedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_document_links",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RemovedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_document_links", x => x.Id);
                    table.CheckConstraint("ck_sales_document_links_kind", "[Kind] IN (1, 2, 3, 4, 5, 6)");
                    table.CheckConstraint("ck_sales_document_links_removed", "([RemovedAtUtc] IS NULL AND [RemovedByUserId] IS NULL) OR ([RemovedAtUtc] IS NOT NULL AND [RemovedByUserId] IS NOT NULL)");
                    table.CheckConstraint("ck_sales_document_links_self", "NOT ([Kind] = 1 AND [DocumentId] = [SalesOrderId])");
                    table.ForeignKey(
                        name: "FK_sales_document_links_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_links_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_links_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_links_removed_by",
                        column: x => x.RemovedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_document_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    LegalEntityId = table.Column<long>(type: "bigint", nullable: false),
                    LogoOnInvoice = table.Column<bool>(type: "bit", nullable: false),
                    LogoOnOrder = table.Column<bool>(type: "bit", nullable: false),
                    StampOnInvoice = table.Column<bool>(type: "bit", nullable: false),
                    StampOnUpd = table.Column<bool>(type: "bit", nullable: false),
                    StampOnOrder = table.Column<bool>(type: "bit", nullable: false),
                    QrOnInvoice = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_document_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_document_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_document_settings_legal_entity",
                        columns: x => new { x.OrganizationId, x.LegalEntityId },
                        principalSchema: "kniterp",
                        principalTable: "legal_entities",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_mail_messages",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsAuto = table.Column<bool>(type: "bit", nullable: false),
                    DedupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_mail_messages", x => x.Id);
                    table.UniqueConstraint("ak_sales_mail_messages_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_sales_mail_messages_attempts", "[Attempts] >= 0");
                    table.CheckConstraint("ck_sales_mail_messages_dedup", "[DedupKey] IS NULL OR [IsAuto] = 1");
                    table.CheckConstraint("ck_sales_mail_messages_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_sales_mail_messages_sent", "([Status] = 2 AND [SentAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [SentAtUtc] IS NULL)");
                    table.CheckConstraint("ck_sales_mail_messages_status", "[Status] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_sales_mail_messages_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_mail_messages_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_mail_messages_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_mail_messages_user",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_mail_stages",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    StageId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_mail_stages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_mail_stages_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_mail_stages_stage",
                        columns: x => new { x.OrganizationId, x.StageId },
                        principalSchema: "kniterp",
                        principalTable: "sales_order_stages",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_mail_templates",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    AutoSend = table.Column<bool>(type: "bit", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_mail_templates", x => x.Id);
                    table.CheckConstraint("ck_sales_mail_templates_kind", "[Kind] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_sales_mail_templates_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_mail_attachments",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    Document = table.Column<byte>(type: "tinyint", nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_mail_attachments", x => x.Id);
                    table.CheckConstraint("ck_sales_mail_attachments_document", "[Document] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_sales_mail_attachments_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_mail_attachments_message",
                        columns: x => new { x.OrganizationId, x.MessageId },
                        principalSchema: "kniterp",
                        principalTable: "sales_mail_messages",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_customer_mail_settings_counterparty",
                schema: "kniterp",
                table: "customer_mail_settings",
                columns: new[] { "OrganizationId", "CounterpartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_document_images_UploadedByUserId",
                schema: "kniterp",
                table: "sales_document_images",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_document_images_entity_kind",
                schema: "kniterp",
                table: "sales_document_images",
                columns: new[] { "OrganizationId", "LegalEntityId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_document_links_CreatedByUserId",
                schema: "kniterp",
                table: "sales_document_links",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_sales_document_links_document",
                schema: "kniterp",
                table: "sales_document_links",
                columns: new[] { "OrganizationId", "Kind", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_document_links_RemovedByUserId",
                schema: "kniterp",
                table: "sales_document_links",
                column: "RemovedByUserId");

            migrationBuilder.CreateIndex(
                name: "ux_sales_document_links_active",
                schema: "kniterp",
                table: "sales_document_links",
                columns: new[] { "OrganizationId", "SalesOrderId", "Kind", "DocumentId" },
                unique: true,
                filter: "[RemovedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sales_document_settings_entity",
                schema: "kniterp",
                table: "sales_document_settings",
                columns: new[] { "OrganizationId", "LegalEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_mail_attachments_document",
                schema: "kniterp",
                table: "sales_mail_attachments",
                columns: new[] { "OrganizationId", "MessageId", "Document", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_mail_messages_CreatedByUserId",
                schema: "kniterp",
                table: "sales_mail_messages",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_sales_mail_messages_due",
                schema: "kniterp",
                table: "sales_mail_messages",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_mail_messages_order",
                schema: "kniterp",
                table: "sales_mail_messages",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_mail_messages_OrganizationId_CounterpartyId",
                schema: "kniterp",
                table: "sales_mail_messages",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_mail_messages_dedup",
                schema: "kniterp",
                table: "sales_mail_messages",
                columns: new[] { "OrganizationId", "DedupKey" },
                unique: true,
                filter: "[DedupKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_sales_mail_stages_stage",
                schema: "kniterp",
                table: "sales_mail_stages",
                columns: new[] { "OrganizationId", "StageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_mail_templates_kind",
                schema: "kniterp",
                table: "sales_mail_templates",
                columns: new[] { "OrganizationId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_mail_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_document_images",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_document_links",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_document_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_mail_attachments",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_mail_stages",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_mail_templates",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "sales_mail_messages",
                schema: "kniterp");
        }
    }
}
