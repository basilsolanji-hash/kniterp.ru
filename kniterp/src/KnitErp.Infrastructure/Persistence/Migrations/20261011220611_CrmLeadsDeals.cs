using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmLeadsDeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.CreateTable(
                name: "crm_contacts",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Position = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: true),
                    Phone = table.Column<string>(type: "varchar(16)", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Messenger = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResponsibleUserId = table.Column<long>(type: "bigint", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_contacts", x => x.Id);
                    table.UniqueConstraint("ak_crm_contacts_org_id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_crm_contacts_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_contacts_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_contacts_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_contacts_responsible",
                        column: x => x.ResponsibleUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_pipelines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_pipelines", x => x.Id);
                    table.UniqueConstraint("ak_crm_pipelines_org_id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_crm_pipelines_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_references",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_references", x => x.Id);
                    table.UniqueConstraint("ak_crm_references_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_crm_references_kind", "[Kind] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_crm_references_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_stages",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    PipelineId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Color = table.Column<string>(type: "varchar(16)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Probability = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_stages", x => x.Id);
                    table.UniqueConstraint("ak_crm_stages_org_id", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("ak_crm_stages_org_pipeline_id", x => new { x.OrganizationId, x.PipelineId, x.Id });
                    table.CheckConstraint("ck_crm_stages_color", "[Color] IN ('gray', 'blue', 'teal', 'green', 'yellow', 'orange', 'red', 'magenta', 'violet')");
                    table.CheckConstraint("ck_crm_stages_kind", "[Kind] IN (0, 1, 2) AND ([Kind] = 0 OR [IsArchived] = 0)");
                    table.CheckConstraint("ck_crm_stages_probability", "[Probability] BETWEEN 0 AND 100 AND ([Kind] <> 1 OR [Probability] = 100) AND ([Kind] <> 2 OR [Probability] = 0)");
                    table.ForeignKey(
                        name: "FK_crm_stages_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_stages_pipeline",
                        columns: x => new { x.OrganizationId, x.PipelineId },
                        principalSchema: "kniterp",
                        principalTable: "crm_pipelines",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_deals",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: true),
                    PipelineId = table.Column<long>(type: "bigint", nullable: false),
                    StageId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "char(3)", nullable: false),
                    Probability = table.Column<int>(type: "int", nullable: false),
                    ExpectedCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ResponsibleUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true),
                    SourceId = table.Column<long>(type: "bigint", nullable: true),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: false),
                    LossReasonId = table.Column<long>(type: "bigint", nullable: true),
                    LossComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StageEnteredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deals", x => x.Id);
                    table.UniqueConstraint("ak_crm_deals_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_crm_deals_amount", "[Amount] >= 0");
                    table.CheckConstraint("ck_crm_deals_closed", "([Outcome] = 1 AND [ClosedAtUtc] IS NULL) OR ([Outcome] <> 1 AND [ClosedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("ck_crm_deals_lost", "([Outcome] = 3 AND [LossReasonId] IS NOT NULL) OR ([Outcome] <> 3 AND [LossReasonId] IS NULL)");
                    table.CheckConstraint("ck_crm_deals_outcome", "[Outcome] IN (1, 2, 3)");
                    table.CheckConstraint("ck_crm_deals_probability", "[Probability] BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_crm_deals_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_channel",
                        columns: x => new { x.OrganizationId, x.ChannelId },
                        principalSchema: "kniterp",
                        principalTable: "lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_contact",
                        columns: x => new { x.OrganizationId, x.ContactId },
                        principalSchema: "kniterp",
                        principalTable: "crm_contacts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_loss_reason",
                        columns: x => new { x.OrganizationId, x.LossReasonId },
                        principalSchema: "kniterp",
                        principalTable: "crm_references",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_pipeline",
                        columns: x => new { x.OrganizationId, x.PipelineId },
                        principalSchema: "kniterp",
                        principalTable: "crm_pipelines",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_project",
                        columns: x => new { x.OrganizationId, x.ProjectId },
                        principalSchema: "kniterp",
                        principalTable: "lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_responsible",
                        column: x => x.ResponsibleUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_source",
                        columns: x => new { x.OrganizationId, x.SourceId },
                        principalSchema: "kniterp",
                        principalTable: "crm_references",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deals_stage",
                        columns: x => new { x.OrganizationId, x.PipelineId, x.StageId },
                        principalSchema: "kniterp",
                        principalTable: "crm_stages",
                        principalColumns: new[] { "OrganizationId", "PipelineId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_deal_lines",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,4)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deal_lines", x => x.Id);
                    table.CheckConstraint("ck_crm_deal_lines_price", "[Price] >= 0 AND [Amount] >= 0");
                    table.CheckConstraint("ck_crm_deal_lines_quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_crm_deal_lines_crm_deals_DealId",
                        column: x => x.DealId,
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_deal_lines_item",
                        column: x => x.ItemId,
                        principalSchema: "kniterp",
                        principalTable: "items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_deal_orders",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    SalesOrderId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deal_orders", x => x.Id);
                    table.ForeignKey(
                        name: "fk_crm_deal_orders_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_orders_deal",
                        columns: x => new { x.OrganizationId, x.DealId },
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_orders_sales_order",
                        columns: x => new { x.OrganizationId, x.SalesOrderId },
                        principalSchema: "kniterp",
                        principalTable: "sales_orders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_deal_participants",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deal_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_crm_deal_participants_crm_deals_DealId",
                        column: x => x.DealId,
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crm_deal_participants_user",
                        column: x => x.UserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_deal_stage_history",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    FromStageId = table.Column<long>(type: "bigint", nullable: true),
                    ToStageId = table.Column<long>(type: "bigint", nullable: false),
                    ChangedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DaysInStage = table.Column<decimal>(type: "decimal(9,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_deal_stage_history", x => x.Id);
                    table.CheckConstraint("ck_crm_deal_stage_history_days", "[DaysInStage] >= 0");
                    table.ForeignKey(
                        name: "fk_crm_deal_stage_history_deal",
                        columns: x => new { x.OrganizationId, x.DealId },
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_stage_history_from",
                        columns: x => new { x.OrganizationId, x.FromStageId },
                        principalSchema: "kniterp",
                        principalTable: "crm_stages",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_stage_history_to",
                        columns: x => new { x.OrganizationId, x.ToStageId },
                        principalSchema: "kniterp",
                        principalTable: "crm_stages",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_deal_stage_history_user",
                        column: x => x.ChangedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crm_leads",
                schema: "kniterp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true),
                    ResponsibleUserId = table.Column<long>(type: "bigint", nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "varchar(16)", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Messenger = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Inn = table.Column<string>(type: "varchar(12)", nullable: true),
                    Need = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpectedAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "char(3)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    RejectReasonId = table.Column<long>(type: "bigint", nullable: true),
                    RejectComment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MergedIntoLeadId = table.Column<long>(type: "bigint", nullable: true),
                    ContactId = table.Column<long>(type: "bigint", nullable: true),
                    CounterpartyId = table.Column<long>(type: "bigint", nullable: true),
                    DealId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crm_leads", x => x.Id);
                    table.UniqueConstraint("ak_crm_leads_org_id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("ck_crm_leads_amount", "[ExpectedAmount] IS NULL OR [ExpectedAmount] >= 0");
                    table.CheckConstraint("ck_crm_leads_qualified", "([Status] = 3 AND [DealId] IS NOT NULL AND [ContactId] IS NOT NULL AND [CounterpartyId] IS NOT NULL) OR ([Status] <> 3 AND [DealId] IS NULL AND [ContactId] IS NULL AND [CounterpartyId] IS NULL)");
                    table.CheckConstraint("ck_crm_leads_rejected", "([Status] = 4 AND [RejectReasonId] IS NOT NULL) OR ([Status] <> 4 AND [RejectReasonId] IS NULL)");
                    table.CheckConstraint("ck_crm_leads_status", "[Status] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_crm_leads_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "kniterp",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_channel",
                        columns: x => new { x.OrganizationId, x.ChannelId },
                        principalSchema: "kniterp",
                        principalTable: "lookups",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_contact",
                        columns: x => new { x.OrganizationId, x.ContactId },
                        principalSchema: "kniterp",
                        principalTable: "crm_contacts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_counterparty",
                        columns: x => new { x.OrganizationId, x.CounterpartyId },
                        principalSchema: "kniterp",
                        principalTable: "counterparties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_created_by",
                        column: x => x.CreatedByUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_deal",
                        columns: x => new { x.OrganizationId, x.DealId },
                        principalSchema: "kniterp",
                        principalTable: "crm_deals",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_merged_into",
                        columns: x => new { x.OrganizationId, x.MergedIntoLeadId },
                        principalSchema: "kniterp",
                        principalTable: "crm_leads",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_reject_reason",
                        columns: x => new { x.OrganizationId, x.RejectReasonId },
                        principalSchema: "kniterp",
                        principalTable: "crm_references",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_responsible",
                        column: x => x.ResponsibleUserId,
                        principalSchema: "kniterp",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crm_leads_source",
                        columns: x => new { x.OrganizationId, x.SourceId },
                        principalSchema: "kniterp",
                        principalTable: "crm_references",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 39");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 39");

            migrationBuilder.CreateIndex(
                name: "IX_crm_contacts_CreatedByUserId",
                schema: "kniterp",
                table: "crm_contacts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_org_counterparty",
                schema: "kniterp",
                table: "crm_contacts",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_org_email",
                schema: "kniterp",
                table: "crm_contacts",
                columns: new[] { "OrganizationId", "Email" },
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_contacts_org_phone",
                schema: "kniterp",
                table: "crm_contacts",
                columns: new[] { "OrganizationId", "Phone" },
                filter: "[Phone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_crm_contacts_ResponsibleUserId",
                schema: "kniterp",
                table: "crm_contacts",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_lines_ItemId",
                schema: "kniterp",
                table: "crm_deal_lines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_deal_lines_deal_item",
                schema: "kniterp",
                table: "crm_deal_lines",
                columns: new[] { "DealId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_orders_CreatedByUserId",
                schema: "kniterp",
                table: "crm_deal_orders",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deal_orders_deal",
                schema: "kniterp",
                table: "crm_deal_orders",
                columns: new[] { "OrganizationId", "DealId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_orders_OrganizationId_SalesOrderId",
                schema: "kniterp",
                table: "crm_deal_orders",
                columns: new[] { "OrganizationId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "ux_crm_deal_orders_sales_order",
                schema: "kniterp",
                table: "crm_deal_orders",
                column: "SalesOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crm_deal_participants_user",
                schema: "kniterp",
                table: "crm_deal_participants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_deal_participants_deal_user",
                schema: "kniterp",
                table: "crm_deal_participants",
                columns: new[] { "DealId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_stage_history_ChangedByUserId",
                schema: "kniterp",
                table: "crm_deal_stage_history",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deal_stage_history_deal",
                schema: "kniterp",
                table: "crm_deal_stage_history",
                columns: new[] { "OrganizationId", "DealId", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_stage_history_OrganizationId_FromStageId",
                schema: "kniterp",
                table: "crm_deal_stage_history",
                columns: new[] { "OrganizationId", "FromStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_stage_history_OrganizationId_ToStageId",
                schema: "kniterp",
                table: "crm_deal_stage_history",
                columns: new[] { "OrganizationId", "ToStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_CreatedByUserId",
                schema: "kniterp",
                table: "crm_deals",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_org_counterparty",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_org_pipeline_stage",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "PipelineId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_deals_org_responsible_outcome",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "ResponsibleUserId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_OrganizationId_ChannelId",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_OrganizationId_ContactId",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_OrganizationId_LossReasonId",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "LossReasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_OrganizationId_ProjectId",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_OrganizationId_SourceId",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deals_ResponsibleUserId",
                schema: "kniterp",
                table: "crm_deals",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_deals_org_number",
                schema: "kniterp",
                table: "crm_deals",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_CreatedByUserId",
                schema: "kniterp",
                table: "crm_leads",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_crm_leads_org_email",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "Email" },
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_leads_org_inn",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "Inn" },
                filter: "[Inn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_leads_org_phone",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "Phone" },
                filter: "[Phone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_crm_leads_org_responsible_status",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "ResponsibleUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_ChannelId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_ContactId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_CounterpartyId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "CounterpartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_DealId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "DealId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_MergedIntoLeadId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "MergedIntoLeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_RejectReasonId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "RejectReasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_OrganizationId_SourceId",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_leads_ResponsibleUserId",
                schema: "kniterp",
                table: "crm_leads",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "ux_crm_leads_deal",
                schema: "kniterp",
                table: "crm_leads",
                column: "DealId",
                unique: true,
                filter: "[DealId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_crm_leads_org_number",
                schema: "kniterp",
                table: "crm_leads",
                columns: new[] { "OrganizationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_crm_pipelines_org_name_active",
                schema: "kniterp",
                table: "crm_pipelines",
                columns: new[] { "OrganizationId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "ux_crm_references_org_kind_name_active",
                schema: "kniterp",
                table: "crm_references",
                columns: new[] { "OrganizationId", "Kind", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "ux_crm_stages_pipeline_final",
                schema: "kniterp",
                table: "crm_stages",
                columns: new[] { "PipelineId", "Kind" },
                unique: true,
                filter: "[Kind] <> 0");

            migrationBuilder.CreateIndex(
                name: "ux_crm_stages_pipeline_name_active",
                schema: "kniterp",
                table: "crm_stages",
                columns: new[] { "PipelineId", "Name" },
                unique: true,
                filter: "[IsArchived] = 0");

            // D602: история этапов сделки — только вставка.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [kniterp].[tr_crm_deal_stage_history_immutable]
                ON [kniterp].[crm_deal_stage_history]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N''История этапов сделки неизменяема: изменение и удаление запрещены.'', 1;
                END');
                """);

            // D600: права CRM Владельцу и Администратору, роли «Менеджер продаж» и «Руководитель продаж» в существующих организациях
            // (новые получают их из RoleMatrixP0 и CrmRoleMatrix).
            migrationBuilder.Sql("""
                EXEC(N'
                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                JOIN (VALUES (''owner'', ''crm.view''), (''owner'', ''crm.edit''), (''owner'', ''crm.all''), (''owner'', ''crm.settings''), (''owner'', ''crm.export''),
                             (''admin'', ''crm.view''), (''admin'', ''crm.settings'')) AS p([Role], [Code]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);

                INSERT INTO [kniterp].[roles] ([OrganizationId], [Code], [Name], [IsSystem])
                SELECT o.[Id], n.[Code], n.[Name], 1
                FROM [kniterp].[organizations] o
                CROSS JOIN (VALUES (''sales_manager'', N''Менеджер продаж''), (''sales_head'', N''Руководитель продаж'')) AS n([Code], [Name])
                WHERE NOT EXISTS (SELECT 1 FROM [kniterp].[roles] r WHERE r.[OrganizationId] = o.[Id] AND r.[Code] = n.[Code]);

                INSERT INTO [kniterp].[role_permissions] ([RoleId], [PermissionCode], [Level])
                SELECT r.[Id], p.[Code], 3
                FROM [kniterp].[roles] r
                JOIN (VALUES (''sales_manager'', ''crm.view''), (''sales_manager'', ''crm.edit''), (''sales_manager'', ''sales.document.view''),
                             (''sales_manager'', ''sales.document.edit''), (''sales_manager'', ''finance.price.view''), (''sales_manager'', ''catalog.item.view''),
                             (''sales_head'', ''crm.view''), (''sales_head'', ''crm.edit''), (''sales_head'', ''crm.all''), (''sales_head'', ''crm.settings''),
                             (''sales_head'', ''crm.export''), (''sales_head'', ''sales.document.view''), (''sales_head'', ''sales.document.edit''),
                             (''sales_head'', ''finance.price.view''), (''sales_head'', ''catalog.item.view'')) AS p([Role], [Code]) ON p.[Role] = r.[Code]
                WHERE r.[IsSystem] = 1
                  AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_permissions] x WHERE x.[RoleId] = r.[Id] AND x.[PermissionCode] = p.[Code]);
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE p FROM [kniterp].[role_permissions] p JOIN [kniterp].[roles] r ON r.[Id] = p.[RoleId] WHERE r.[Code] IN ('sales_manager', 'sales_head');
                DELETE FROM [kniterp].[roles] WHERE [Code] IN ('sales_manager', 'sales_head')
                    AND NOT EXISTS (SELECT 1 FROM [kniterp].[role_assignments] a WHERE a.[RoleId] = [kniterp].[roles].[Id]);
                DELETE FROM [kniterp].[role_permissions] WHERE [PermissionCode] LIKE 'crm.%';
                DROP TRIGGER IF EXISTS [kniterp].[tr_crm_deal_stage_history_immutable];
                """);
            migrationBuilder.DropTable(
                name: "crm_deal_lines",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_deal_orders",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_deal_participants",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_deal_stage_history",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_leads",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_deals",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_contacts",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_references",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_stages",
                schema: "kniterp");

            migrationBuilder.DropTable(
                name: "crm_pipelines",
                schema: "kniterp");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tag_links_entity",
                schema: "kniterp",
                table: "tag_links",
                sql: "[Entity] BETWEEN 1 AND 29");

            migrationBuilder.AddCheckConstraint(
                name: "ck_discussion_comments_target",
                schema: "kniterp",
                table: "discussion_comments",
                sql: "[Target] BETWEEN 1 AND 29");
        }
    }
}
