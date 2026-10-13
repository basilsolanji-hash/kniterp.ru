using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnitErp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CrmCompositeKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_crm_activities_offer",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.DropForeignKey(
                name: "FK_crm_deal_lines_crm_deals_DealId",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_deal_lines_item",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_crm_mailing_recipients_crm_mailings_MailingId",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailing_recipients_contact",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailings_tag",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropForeignKey(
                name: "FK_crm_offer_lines_crm_offers_OfferId",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_offer_lines_item",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_offers_price_type",
                schema: "kniterp",
                table: "crm_offers");

            migrationBuilder.DropIndex(
                name: "IX_crm_offers_PriceTypeId",
                schema: "kniterp",
                table: "crm_offers");

            migrationBuilder.DropIndex(
                name: "IX_crm_offer_lines_ItemId",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailings_TagId",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailing_recipients_ContactId",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropIndex(
                name: "IX_crm_deal_lines_ItemId",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropIndex(
                name: "IX_crm_activities_OfferId",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.AddColumn<long>(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_offer_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_deal_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // D682: организация строк — из родителя (сделки, КП, рассылки). Строки КП — только вставка: триггер выключается на время заполнения.
            migrationBuilder.Sql(@"EXEC(N'UPDATE l SET l.[OrganizationId] = d.[OrganizationId] FROM [kniterp].[crm_deal_lines] l JOIN [kniterp].[crm_deals] d ON d.[Id] = l.[DealId] WHERE l.[OrganizationId] = 0')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE r SET r.[OrganizationId] = m.[OrganizationId] FROM [kniterp].[crm_mailing_recipients] r JOIN [kniterp].[crm_mailings] m ON m.[Id] = r.[MailingId] WHERE r.[OrganizationId] = 0')");
            migrationBuilder.Sql(@"EXEC(N'DISABLE TRIGGER [kniterp].[tr_crm_offer_lines_immutable] ON [kniterp].[crm_offer_lines]')");
            migrationBuilder.Sql(@"EXEC(N'UPDATE l SET l.[OrganizationId] = o.[OrganizationId] FROM [kniterp].[crm_offer_lines] l JOIN [kniterp].[crm_offers] o ON o.[Id] = l.[OfferId] WHERE l.[OrganizationId] = 0')");
            migrationBuilder.Sql(@"EXEC(N'ENABLE TRIGGER [kniterp].[tr_crm_offer_lines_immutable] ON [kniterp].[crm_offer_lines]')");

            // D683: заказы, созданные из сделок до этой версии, получают связь и в заказе базы.
            migrationBuilder.Sql(@"EXEC(N'UPDATE o SET o.[CrmDealId] = l.[DealId] FROM [kniterp].[sales_orders] o JOIN [kniterp].[crm_deal_orders] l ON l.[SalesOrderId] = o.[Id] AND l.[OrganizationId] = o.[OrganizationId] WHERE o.[CrmDealId] IS NULL')");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_crm_mailings_org_id",
                schema: "kniterp",
                table: "crm_mailings",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_offers_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "crm_offers",
                columns: new[] { "OrganizationId", "PriceTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_offer_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "crm_offer_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_offer_lines_OrganizationId_OfferId",
                schema: "kniterp",
                table: "crm_offer_lines",
                columns: new[] { "OrganizationId", "OfferId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailings_OrganizationId_TagId",
                schema: "kniterp",
                table: "crm_mailings",
                columns: new[] { "OrganizationId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailing_recipients_OrganizationId_ContactId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                columns: new[] { "OrganizationId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailing_recipients_OrganizationId_MailingId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                columns: new[] { "OrganizationId", "MailingId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_lines_OrganizationId_DealId",
                schema: "kniterp",
                table: "crm_deal_lines",
                columns: new[] { "OrganizationId", "DealId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "crm_deal_lines",
                columns: new[] { "OrganizationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_OrganizationId_OfferId",
                schema: "kniterp",
                table: "crm_activities",
                columns: new[] { "OrganizationId", "OfferId" });

            migrationBuilder.AddForeignKey(
                name: "fk_crm_activities_offer",
                schema: "kniterp",
                table: "crm_activities",
                columns: new[] { "OrganizationId", "OfferId" },
                principalSchema: "kniterp",
                principalTable: "crm_offers",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_deal_lines_deal",
                schema: "kniterp",
                table: "crm_deal_lines",
                columns: new[] { "OrganizationId", "DealId" },
                principalSchema: "kniterp",
                principalTable: "crm_deals",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_deal_lines_item",
                schema: "kniterp",
                table: "crm_deal_lines",
                columns: new[] { "OrganizationId", "ItemId" },
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailing_recipients_contact",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                columns: new[] { "OrganizationId", "ContactId" },
                principalSchema: "kniterp",
                principalTable: "crm_contacts",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailing_recipients_mailing",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                columns: new[] { "OrganizationId", "MailingId" },
                principalSchema: "kniterp",
                principalTable: "crm_mailings",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailings_tag",
                schema: "kniterp",
                table: "crm_mailings",
                columns: new[] { "OrganizationId", "TagId" },
                principalSchema: "kniterp",
                principalTable: "tags",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_offer_lines_item",
                schema: "kniterp",
                table: "crm_offer_lines",
                columns: new[] { "OrganizationId", "ItemId" },
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_offer_lines_offer",
                schema: "kniterp",
                table: "crm_offer_lines",
                columns: new[] { "OrganizationId", "OfferId" },
                principalSchema: "kniterp",
                principalTable: "crm_offers",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_offers_price_type",
                schema: "kniterp",
                table: "crm_offers",
                columns: new[] { "OrganizationId", "PriceTypeId" },
                principalSchema: "kniterp",
                principalTable: "price_types",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sales_orders_crm_deal",
                schema: "kniterp",
                table: "sales_orders",
                columns: new[] { "OrganizationId", "CrmDealId" },
                principalSchema: "kniterp",
                principalTable: "crm_deals",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_crm_activities_offer",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_deal_lines_deal",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_deal_lines_item",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailing_recipients_contact",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailing_recipients_mailing",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_mailings_tag",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_offer_lines_item",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_offer_lines_offer",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_crm_offers_price_type",
                schema: "kniterp",
                table: "crm_offers");

            migrationBuilder.DropForeignKey(
                name: "fk_sales_orders_crm_deal",
                schema: "kniterp",
                table: "sales_orders");

            migrationBuilder.DropIndex(
                name: "IX_crm_offers_OrganizationId_PriceTypeId",
                schema: "kniterp",
                table: "crm_offers");

            migrationBuilder.DropIndex(
                name: "IX_crm_offer_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropIndex(
                name: "IX_crm_offer_lines_OrganizationId_OfferId",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_crm_mailings_org_id",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailings_OrganizationId_TagId",
                schema: "kniterp",
                table: "crm_mailings");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailing_recipients_OrganizationId_ContactId",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropIndex(
                name: "IX_crm_mailing_recipients_OrganizationId_MailingId",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropIndex(
                name: "IX_crm_deal_lines_OrganizationId_DealId",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropIndex(
                name: "IX_crm_deal_lines_OrganizationId_ItemId",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.DropIndex(
                name: "IX_crm_activities_OrganizationId_OfferId",
                schema: "kniterp",
                table: "crm_activities");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_offer_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_mailing_recipients");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "kniterp",
                table: "crm_deal_lines");

            migrationBuilder.CreateIndex(
                name: "IX_crm_offers_PriceTypeId",
                schema: "kniterp",
                table: "crm_offers",
                column: "PriceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_offer_lines_ItemId",
                schema: "kniterp",
                table: "crm_offer_lines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailings_TagId",
                schema: "kniterp",
                table: "crm_mailings",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_mailing_recipients_ContactId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_deal_lines_ItemId",
                schema: "kniterp",
                table: "crm_deal_lines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_crm_activities_OfferId",
                schema: "kniterp",
                table: "crm_activities",
                column: "OfferId");

            migrationBuilder.AddForeignKey(
                name: "fk_crm_activities_offer",
                schema: "kniterp",
                table: "crm_activities",
                column: "OfferId",
                principalSchema: "kniterp",
                principalTable: "crm_offers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_crm_deal_lines_crm_deals_DealId",
                schema: "kniterp",
                table: "crm_deal_lines",
                column: "DealId",
                principalSchema: "kniterp",
                principalTable: "crm_deals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_deal_lines_item",
                schema: "kniterp",
                table: "crm_deal_lines",
                column: "ItemId",
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_crm_mailing_recipients_crm_mailings_MailingId",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                column: "MailingId",
                principalSchema: "kniterp",
                principalTable: "crm_mailings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailing_recipients_contact",
                schema: "kniterp",
                table: "crm_mailing_recipients",
                column: "ContactId",
                principalSchema: "kniterp",
                principalTable: "crm_contacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_mailings_tag",
                schema: "kniterp",
                table: "crm_mailings",
                column: "TagId",
                principalSchema: "kniterp",
                principalTable: "tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_crm_offer_lines_crm_offers_OfferId",
                schema: "kniterp",
                table: "crm_offer_lines",
                column: "OfferId",
                principalSchema: "kniterp",
                principalTable: "crm_offers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_offer_lines_item",
                schema: "kniterp",
                table: "crm_offer_lines",
                column: "ItemId",
                principalSchema: "kniterp",
                principalTable: "items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_crm_offers_price_type",
                schema: "kniterp",
                table: "crm_offers",
                column: "PriceTypeId",
                principalSchema: "kniterp",
                principalTable: "price_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
