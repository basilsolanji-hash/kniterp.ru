using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>План продаж (D1162): одна запись на месяц и менеджера; месяц — первое число, сумма не меньше нуля.</summary>
internal sealed class SalesPlanConfiguration : IEntityTypeConfiguration<SalesPlan>
{
    public void Configure(EntityTypeBuilder<SalesPlan> b)
    {
        b.ToTable("sales_plans", t =>
        {
            t.HasCheckConstraint("ck_sales_plans_amount", "[Amount] >= 0");
            t.HasCheckConstraint("ck_sales_plans_month", "DAY([Month]) = 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_plans_manager");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_plans_updated_by");
        b.HasIndex(x => new { x.OrganizationId, x.Month, x.ManagerUserId }).IsUnique().HasDatabaseName("ux_sales_plans_month_manager");
    }
}

/// <summary>Свойства канала продаж (D1107): канал — своей организации, одна запись на канал.</summary>
internal sealed class SalesChannelSettingsConfiguration : IEntityTypeConfiguration<SalesChannelSettings>
{
    public void Configure(EntityTypeBuilder<SalesChannelSettings> b)
    {
        b.ToTable("sales_channel_settings", t =>
        {
            t.HasCheckConstraint("ck_sales_channel_settings_marketplace", "[Marketplace] IS NULL OR [Marketplace] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_sales_channel_settings_scheme",
                "([Scheme] IS NULL OR [Scheme] IN (1, 2)) AND ([Marketplace] IS NOT NULL OR ([Scheme] IS NULL AND [CounterpartyId] IS NULL))");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Marketplace).HasConversion<byte?>();
        b.Property(x => x.Scheme).HasConversion<byte?>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ChannelId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_channel_settings_channel");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_channel_settings_counterparty");
        b.HasIndex(x => new { x.OrganizationId, x.ChannelId }).IsUnique().HasDatabaseName("ux_sales_channel_settings_channel");
    }
}

/// <summary>
/// Отчёт маркетплейса (D1168–D1170): канал — своей организации; действующий отчёт с тем же отпечатком — только один
/// (повтор файла не создаёт дубль); аннулированный — с причиной.
/// </summary>
internal sealed class MarketplaceReportConfiguration : IEntityTypeConfiguration<MarketplaceReport>
{
    public void Configure(EntityTypeBuilder<MarketplaceReport> b)
    {
        b.ToTable("marketplace_reports", t =>
        {
            t.HasCheckConstraint("ck_marketplace_reports_marketplace", "[Marketplace] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_marketplace_reports_status", "[Status] IN (1, 9)");
            t.HasCheckConstraint("ck_marketplace_reports_period", "[PeriodTo] >= [PeriodFrom]");
            t.HasCheckConstraint("ck_marketplace_reports_fingerprint", "LEN([Fingerprint]) = 64");
            t.HasCheckConstraint("ck_marketplace_reports_counts", "[LineCount] > 0 AND [UnmatchedCount] BETWEEN 0 AND [LineCount]");
            t.HasCheckConstraint("ck_marketplace_reports_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL)"
                + " OR ([Status] <> 9 AND [CancelledByUserId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelReason] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_marketplace_reports_org_id");
        b.Property(x => x.Marketplace).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.FileName).HasMaxLength(MarketplaceReport.FileNameMaxLength).IsRequired();
        b.Property(x => x.Fingerprint).HasMaxLength(MarketplaceReport.FingerprintLength).IsUnicode(false).IsFixedLength().IsRequired();
        b.Property(x => x.CancelReason).HasMaxLength(MarketplaceReport.ReasonMaxLength);
        b.Property(x => x.SalesQuantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.ReturnsQuantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.SalesAmount).HasPrecision(19, 4);
        b.Property(x => x.ReturnsAmount).HasPrecision(19, 4);
        b.Property(x => x.Commission).HasPrecision(19, 4);
        b.Property(x => x.Logistics).HasPrecision(19, 4);
        b.Property(x => x.Payout).HasPrecision(19, 4);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CancelledAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ChannelId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_reports_channel");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_marketplace_reports_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_marketplace_reports_cancelled_by");
        b.HasIndex(x => new { x.OrganizationId, x.Fingerprint }).IsUnique().HasFilter("[Status] = 1").HasDatabaseName("ux_marketplace_reports_fingerprint");
        b.HasIndex(x => new { x.OrganizationId, x.PeriodTo }).HasDatabaseName("ix_marketplace_reports_period");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => new { x.OrganizationId, x.ReportId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_report_lines_report");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class MarketplaceReportLineConfiguration : IEntityTypeConfiguration<MarketplaceReportLine>
{
    public void Configure(EntityTypeBuilder<MarketplaceReportLine> b)
    {
        b.ToTable("marketplace_report_lines", t =>
        {
            t.HasCheckConstraint("ck_marketplace_report_lines_kind", "[Kind] IN (1, 2, 3)");
            t.HasCheckConstraint("ck_marketplace_report_lines_match", "([ItemId] IS NULL AND [MatchedBy] = 0) OR ([ItemId] IS NOT NULL AND [MatchedBy] IN (1, 2, 3))");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.MatchedBy).HasConversion<byte>();
        b.Property(x => x.Article).HasMaxLength(MarketplaceReportLine.ArticleMaxLength);
        b.Property(x => x.ExternalCode).HasMaxLength(MarketplaceReportLine.CodeMaxLength);
        b.Property(x => x.Barcode).HasMaxLength(MarketplaceReportLine.BarcodeMaxLength);
        b.Property(x => x.Name).HasMaxLength(MarketplaceReportLine.NameMaxLength);
        b.Property(x => x.Note).HasMaxLength(MarketplaceReportLine.NoteMaxLength);
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.Commission).HasPrecision(19, 4);
        b.Property(x => x.Logistics).HasPrecision(19, 4);
        b.Property(x => x.Payout).HasPrecision(19, 4);
        b.Ignore(x => x.NeedsItem);
        b.Ignore(x => x.Key);
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_report_lines_item");
        b.HasIndex(x => new { x.OrganizationId, x.ReportId, x.RowNumber }).HasDatabaseName("ix_marketplace_report_lines_report");
        b.HasIndex(x => new { x.OrganizationId, x.ItemId }).HasDatabaseName("ix_marketplace_report_lines_item");
    }
}

/// <summary>Своё сопоставление столбцов (D1168): одна запись на площадку и поле.</summary>
internal sealed class MarketplaceColumnMapConfiguration : IEntityTypeConfiguration<MarketplaceColumnMap>
{
    public void Configure(EntityTypeBuilder<MarketplaceColumnMap> b)
    {
        b.ToTable("marketplace_column_maps", t =>
        {
            t.HasCheckConstraint("ck_marketplace_column_maps_marketplace", "[Marketplace] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_marketplace_column_maps_field", "[Field] BETWEEN 1 AND 14");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Marketplace).HasConversion<byte>();
        b.Property(x => x.Field).HasConversion<byte>();
        b.Property(x => x.Header).HasMaxLength(MarketplaceColumnMap.HeaderMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Marketplace, x.Field }).IsUnique().HasDatabaseName("ux_marketplace_column_maps_field");
    }
}

/// <summary>
/// D1166 (сессия «Продажи»): индексы аналитики продаж — отгрузки и возвраты по заказам за период (в индекс входят заказ
/// и статус, чтобы не читать строки таблицы документов) и заказы по ответственному (видимость «Свои», план-факт).
/// </summary>
internal sealed class SalesAnalyticsIndexesConfiguration : IEntityTypeConfiguration<StockDocument>, IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<StockDocument> b) =>
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.Status, x.DocumentDate, x.SalesOrderId })
            .HasFilter("[SalesOrderId] IS NOT NULL").HasDatabaseName("ix_stock_documents_sales_analytics");

    public void Configure(EntityTypeBuilder<SalesOrder> b)
    {
        b.HasIndex(x => new { x.OrganizationId, x.ResponsibleUserId, x.OrderDate }).HasDatabaseName("ix_sales_orders_responsible_date");
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.ShipDate }).HasDatabaseName("ix_sales_orders_status_ship_date");
    }
}
