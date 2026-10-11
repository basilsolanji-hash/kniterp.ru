using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmActivitiesOffersAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NextStepDays",
                schema: "kniterp",
                table: "crm_stages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextStepSubject",
                schema: "kniterp",
                table: "crm_stages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BasisDate",
                schema: "kniterp",
                table: "crm_contacts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BasisNote",
                schema: "kniterp",
                table: "crm_contacts",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                schema: "kniterp",
                table: "crm_contacts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ErasedAtUtc",
                schema: "kniterp",
                table: "crm_contacts",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MarketingConsent",
                schema: "kniterp",
                table: "crm_contacts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "MarketingConsentDate",
                schema: "kniterp",
                table: "crm_contacts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ProcessingBasis",
                schema: "kniterp",
                table: "crm_contacts",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<Guid>(
                name: "UnsubscribeToken",
                schema: "kniterp",
                table: "crm_contacts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "crm_contact_channels",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_contact_channels", x => x.Id);
                    table.CheckConstraint("ck_crm_contact_channels_kind", "[Kind] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_crm_contact_channels_crm_contacts_ContactId",
                        column: x => x.ContactId,
                        principalSchema: "kniterp",
                        principalTable: "crm_contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_external_links",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    System = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Entity = table.Column<byte>(type: "tinyint", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_external_links", x => x.Id);
                    table.CheckConstraint("ck_crm_external_links_entity", "[Entity] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_crm_external_links_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_mailings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    TagId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_mailings", x => x.Id);
                    table.CheckConstraint("ck_crm_mailings_status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_crm_mailings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_mailings_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_mailings_tag",
                        column: x => x.TagId,
                        principalSchema: "kniterp",
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_offers",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    OfferDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: false),
                    Terms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PriceTypeId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyCode = table.Column<string>(type: "char(3)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_offers", x => x.Id);
                    table.UniqueConstraint("ak_crm_offers_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_crm_offers_dates", "[ValidUntil] >= [OfferDate] AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_crm_offers_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_offers_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_offers_deal",
                        columns: x => new { x.OrganizationId, x.DealId },
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_offers_price_type",
                        column: x => x.PriceTypeId,
                        principalSchema: "kniterp",
                        principalTable: "price_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_plans",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    PipelineId = table.Column<long>(type: "bigint", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_plans", x => x.Id);
                    table.CheckConstraint("ck_crm_plans_values", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2100 AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_crm_plans_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_plans_pipeline",
                        columns: x => new { x.OrganizationId, x.PipelineId },
                        principalSchema: "kniterp",
                        principalTable: "crm_pipelines",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_plans_user",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_settings",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    AssignMode = table.Column<byte>(type: "tinyint", nullable: false),
                    DefaultResponsibleUserId = table.Column<long>(type: "bigint", nullable: true),
                    NextStepSuggestions = table.Column<bool>(type: "bit", nullable: false),
                    StageByEvents = table.Column<bool>(type: "bit", nullable: false),
                    RepeatSaleDays = table.Column<int>(type: "int", nullable: true),
                    StaleLeadDays = table.Column<int>(type: "int", nullable: false),
                    PlanFact = table.Column<byte>(type: "tinyint", nullable: false),
                    OfferTerms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OfferValidDays = table.Column<int>(type: "int", nullable: false),
                    LastAssignedUserId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_settings", x => x.Id);
                    table.CheckConstraint("ck_crm_settings_values", "[AssignMode] IN (1, 2) AND [PlanFact] IN (1, 2) AND [StaleLeadDays] BETWEEN 1 AND 90 AND [OfferValidDays] BETWEEN 1 AND 365 AND ([RepeatSaleDays] IS NULL OR [RepeatSaleDays] BETWEEN 1 AND 730)");
                    table.ForeignKey(
                        name: "FK_crm_settings_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_settings_default_user",
                        column: x => x.DefaultResponsibleUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_mailing_recipients",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MailingId = table.Column<long>(type: "bigint", nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Delivered = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_mailing_recipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_crm_mailing_recipients_crm_mailings_MailingId",
                        column: x => x.MailingId,
                        principalSchema: "kniterp",
                        principalTable: "crm_mailings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_mailing_recipients_contact",
                        column: x => x.ContactId,
                        principalSchema: "kniterp",
                        principalTable: "crm_contacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_activities",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Target = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    AssigneeUserId = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDone = table.Column<bool>(type: "bit", nullable: false),
                    DoneAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DoneByUserId = table.Column<long>(type: "bigint", nullable: true),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    ExternalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OfferId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_activities", x => x.Id);
                    table.CheckConstraint("ck_crm_activities_done", "([IsDone] = 1 AND [DoneAtUtc] IS NOT NULL AND [DoneByUserId] IS NOT NULL) OR ([IsDone] = 0 AND [DoneAtUtc] IS NULL AND [DoneByUserId] IS NULL)");
                    table.CheckConstraint("ck_crm_activities_kind", "[Kind] BETWEEN 1 AND 8 AND [Target] IN (1, 2, 3) AND [Source] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_crm_activities_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_activities_assignee",
                        column: x => x.AssigneeUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_activities_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_activities_done_by",
                        column: x => x.DoneByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_activities_offer",
                        column: x => x.OfferId,
                        principalSchema: "kniterp",
                        principalTable: "crm_offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_offer_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OfferId = table.Column<long>(type: "bigint", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    UnitSymbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_offer_lines", x => x.Id);
                    table.CheckConstraint("ck_crm_offer_lines_values", "[Quantity] > 0 AND [Price] >= 0 AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_crm_offer_lines_crm_offers_OfferId",
                        column: x => x.OfferId,
                        principalSchema: "kniterp",
                        principalTable: "crm_offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_offer_lines_item",
                        column: x => x.ItemId,
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_crm_contacts_unsubscribe",
                schema: "kniterp",
                table: "crm_contacts",
                column: "UnsubscribeToken",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_contacts_basis",
                schema: "kniterp",
                table: "crm_contacts",
                sql: "[ProcessingBasis] IN (1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_crm_contacts_marketing",
                schema: "kniterp",
                table: "crm_contacts",
                sql: "[MarketingConsent] = 0 OR [MarketingConsentDate] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_assignee_due",
                schema: "kniterp",
                table: "crm_activities",
                columns: new[] { "OrganizationId", "AssigneeUserId", "IsDone", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_AssigneeUserId",
                schema: "kniterp",
                table: "crm_activities",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_CreatedByUserId",
                schema: "kniterp",
                table: "crm_activities",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_DoneByUserId",
                schema: "kniterp",
                table: "crm_activities",
                column: "DoneByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_OfferId",
                schema: "kniterp",
                table: "crm_activities",
                column: "OfferId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_activities_target",
                schema: "kniterp",
                table: "crm_activities",
                columns: new[] { "OrganizationId", "Target", "TargetId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ux_crm_activities_external",
                schema: "kniterp",
                table: "crm_activities",
                columns: new[] { "OrganizationId", "Source", "ExternalRef" },
                unique: true,
                filter: "[ExternalRef] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contact_channels_kind_value",
                schema: "kniterp",
                table: "crm_contact_channels",
                columns: new[] { "Kind", "Value" });

            migrationBuilder.CreateIndex(
                name: "ux_crm_contact_channels_value",
                schema: "kniterp",
                table: "crm_contact_channels",
                columns: new[] { "ContactId", "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_crm_external_links",
                schema: "kniterp",
                table: "crm_external_links",
                columns: new[] { "OrganizationId", "System", "Entity", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailing_recipients_ContactId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_mailing_recipients",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                columns: new[] { "MailingId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailings_CreatedByUserId",
                schema: "kniterp",
                table: "crm_mailings",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_mailings_org",
                schema: "kniterp",
                table: "crm_mailings",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailings_TagId",
                schema: "kniterp",
                table: "crm_mailings",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_offer_lines_ItemId",
                schema: "kniterp",
                table: "crm_offer_lines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_offer_lines_no",
                schema: "kniterp",
                table: "crm_offer_lines",
                columns: new[] { "OfferId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_offers_CreatedByUserId",
                schema: "kniterp",
                table: "crm_offers",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_offers_OrganizationId_DealId",
                schema: "kniterp",
                table: "crm_offers",
                columns: new[] { "OrganizationId", "DealId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_offers_PriceTypeId",
                schema: "kniterp",
                table: "crm_offers",
                column: "PriceTypeId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_offers_org_number",
                schema: "kniterp",
                table: "crm_offers",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_plans_OrganizationId_PipelineId",
                schema: "kniterp",
                table: "crm_plans",
                columns: new[] { "OrganizationId", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_plans_UserId",
                schema: "kniterp",
                table: "crm_plans",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_plans_period",
                schema: "kniterp",
                table: "crm_plans",
                columns: new[] { "OrganizationId", "Year", "Month", "UserId", "PipelineId" },
                unique: true,
                filter: "[UserId] IS NOT NULL AND [PipelineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_crm_settings_DefaultResponsibleUserId",
                schema: "kniterp",
                table: "crm_settings",
                column: "DefaultResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_settings_org",
                schema: "kniterp",
                table: "crm_settings",
                column: "OrganizationId",
                unique: true);

            // D624: КП и его строки не меняются после создания.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_crm_offers_immutable]
                ON [kniterp].[crm_offers]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''КП неизменяемо: изменение и удаление запрещены.'', 1;
                END');
                """);
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_crm_offer_lines_immutable]
                ON [kniterp].[crm_offer_lines]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''Строки КП неизменяемы: изменение и удаление запрещены.'', 1;
                END');
                """);

            // D622: правила следующего шага по умолчанию для существующих воронок; D640, D615: новые права Владельцу и Руководителю продаж.
            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE [kniterp].[crm_stages] SET [NextStepDays] = 3, [NextStepSubject] = N''Позвонить: обсудить КП''
                WHERE [Name] = N''КП отправлено'' AND [NextStepDays] IS NULL;
                UPDATE [kniterp].[crm_stages] SET [NextStepDays] = 5, [NextStepSubject] = N''Уточнить получение образцов''
                WHERE [Name] = N''Образцы / расчёт'' AND [NextStepDays] IS NULL;
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                CROSS JOIN (VALUES (''crm.leads.create''), (''crm.mailing'')) AS p([Code])
                WHERE r.[IsSystem] = 1 AND r.[Code] IN (''owner'', ''sales_head'')
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] IN ('crm.leads.create', 'crm.mailing');
                DROP TRIGGER IF EXISTS [kniterp].[tr_crm_offers_immutable];
                DROP TRIGGER IF EXISTS [kniterp].[tr_crm_offer_lines_immutable];
                """);
            migrationBuilder.DropTable(
                name: "crm_activities",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_contact_channels",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_external_links",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_mailing_recipients",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_offer_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_plans",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_settings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_mailings",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_offers",
                schema: "kniterp");

            migrationBuilder.DropIndex(
                name: "ux_crm_contacts_unsubscribe",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_contacts_basis",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_crm_contacts_marketing",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "NextStepDays",
                schema: "kniterp",
                table: "crm_stages");

            migrationBuilder.DropColumn(
                name: "NextStepSubject",
                schema: "kniterp",
                table: "crm_stages");

            migrationBuilder.DropColumn(
                name: "BasisDate",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "BasisNote",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "ErasedAtUtc",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "MarketingConsent",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "MarketingConsentDate",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "ProcessingBasis",
                schema: "kniterp",
                table: "crm_contacts");

            migrationBuilder.DropColumn(
                name: "UnsubscribeToken",
                schema: "kniterp",
                table: "crm_contacts");
        }
    }
}
