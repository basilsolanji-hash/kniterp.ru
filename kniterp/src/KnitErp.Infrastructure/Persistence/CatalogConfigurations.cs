using KnitErp.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

// Каталог: услуги, комплекты, упаковки, обмен с внешними системами (сессия № 4, D300–D399).
// Отдельный файл, чтобы не пересекаться с правками Configurations.cs других сессий.

internal sealed class BundleComponentConfiguration : IEntityTypeConfiguration<BundleComponent>
{
    public void Configure(EntityTypeBuilder<BundleComponent> b)
    {
        b.ToTable("bundle_components", t =>
        {
            t.HasCheckConstraint("ck_bundle_components_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_bundle_components_self", "[BundleItemId] <> [ComponentItemId]");
            t.HasTrigger("tr_bundle_components_types");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.BundleItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_bundle_components_bundle");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ComponentItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_bundle_components_component");
        b.HasIndex(x => new { x.BundleItemId, x.ComponentItemId }).IsUnique().HasDatabaseName("ux_bundle_components_pair");
        // Где позиция входит в комплекты — проверка перед сменой типа и для доступности.
        b.HasIndex(x => new { x.OrganizationId, x.ComponentItemId }).HasDatabaseName("ix_bundle_components_component");
    }
}

/// <summary>Поля торговли у позиции (D305) — дополнение к ItemConfiguration (Configurations.cs).</summary>
internal static class ItemTradeConfiguration
{
    public static void Apply(EntityTypeBuilder<Item> b)
    {
        b.Property(x => x.PaymentSubject).HasConversion<byte>().HasDefaultValue(PaymentSubject.Goods).HasSentinel((PaymentSubject)0);
        b.Property(x => x.TrackingType).HasConversion<byte>();
        b.Property(x => x.Gtin).HasColumnType("varchar(14)");
        b.Property(x => x.MinPrice).HasColumnType("decimal(19,4)");
        b.Ignore(x => x.Trade);

        // Сессия 4, поиск в списке на 50 000 позиций: код, наименование и артикул в верхнем регистре, двоичное сравнение.
        // LIKE '%…%' по одному такому столбцу в 15–20 раз быстрее, чем по трём столбцам в языковом сравнении; регистр не важен.
        b.Property<string>(KnitErp.Application.Catalog.ItemSearch.Column)
            .HasComputedColumnSql("UPPER(CONCAT([Code], N'|', [Name], N'|', [Article])) COLLATE Latin1_General_100_BIN2", stored: true);
        // GTIN — один товар: уникален в организации.
        b.HasIndex(x => new { x.OrganizationId, x.Gtin }).IsUnique().HasFilter("[Gtin] IS NOT NULL").HasDatabaseName("ux_items_org_gtin");
    }
}

internal sealed class ItemPriceHistoryConfiguration : IEntityTypeConfiguration<ItemPriceHistory>
{
    public void Configure(EntityTypeBuilder<ItemPriceHistory> b)
    {
        b.ToTable("item_price_history", t =>
        {
            t.HasCheckConstraint("ck_item_price_history_prices", "([OldPrice] IS NULL OR [OldPrice] >= 0) AND ([NewPrice] IS NULL OR [NewPrice] >= 0)");
            t.HasCheckConstraint("ck_item_price_history_source", "[Source] IN (1, 2, 3, 4, 5)");
            t.HasTrigger("tr_item_price_history_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.OldPrice).HasColumnType("decimal(19,4)");
        b.Property(x => x.NewPrice).HasColumnType("decimal(19,4)");
        b.Property(x => x.ChangedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.Source).HasConversion<byte>();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_price_history_item");
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_price_history_type");
        b.HasOne<PriceRevaluation>().WithMany().HasForeignKey(x => x.RevaluationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_price_history_revaluation");
        b.HasIndex(x => new { x.ItemId, x.ChangedAtUtc }).HasDatabaseName("ix_item_price_history_item_time");
    }
}

internal sealed class PriceRevaluationConfiguration : IEntityTypeConfiguration<PriceRevaluation>
{
    public void Configure(EntityTypeBuilder<PriceRevaluation> b)
    {
        b.ToTable("price_revaluations", t =>
        {
            t.HasCheckConstraint("ck_price_revaluations_mode", "[Mode] IN (1, 2) AND [Rounding] IN (0, 1, 2, 3) AND [Items] >= 0");
            t.HasTrigger("tr_price_revaluations_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Mode).HasConversion<byte>();
        b.Property(x => x.Rounding).HasConversion<byte>();
        b.Property(x => x.Value).HasColumnType("decimal(19,4)");
        b.Property(x => x.Rule).HasMaxLength(PriceRevaluation.RuleMaxLength).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<KnitErp.Domain.Organizations.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_price_revaluations_type");
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAtUtc }).HasDatabaseName("ix_price_revaluations_org_time");
    }
}

internal sealed class ItemPackConfiguration : IEntityTypeConfiguration<ItemPack>
{
    public void Configure(EntityTypeBuilder<ItemPack> b)
    {
        b.ToTable("item_packs", t => t.HasCheckConstraint("ck_item_packs_factor", "[Factor] > 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Factor).HasColumnType("decimal(18,6)");
        b.Property(x => x.Barcode).HasColumnType("varchar(64)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_packs_item");
        b.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.UnitId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_packs_unit");
        // У позиции одна упаковка каждой единицы; штрихкод упаковки уникален в организации (и не совпадает со штрихкодами позиций — сервис).
        b.HasIndex(x => new { x.ItemId, x.UnitId }).IsUnique().HasDatabaseName("ux_item_packs_item_unit");
        b.HasIndex(x => new { x.OrganizationId, x.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL").HasDatabaseName("ux_item_packs_org_barcode");
    }
}

internal sealed class ItemSupplierConfiguration : IEntityTypeConfiguration<ItemSupplier>
{
    public void Configure(EntityTypeBuilder<ItemSupplier> b)
    {
        b.ToTable("item_suppliers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.SupplierArticle).HasMaxLength(ItemSupplier.ArticleMaxLength);
        b.Property(x => x.SupplierItemName).HasMaxLength(ItemSupplier.NameMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_suppliers_item");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_suppliers_supplier");
        b.HasIndex(x => new { x.ItemId, x.SupplierId }).IsUnique().HasDatabaseName("ux_item_suppliers_item_supplier");
        // Основной поставщик у позиции — один.
        b.HasIndex(x => x.ItemId).IsUnique().HasFilter("[IsMain] = 1").HasDatabaseName("ux_item_suppliers_main");
        // Поиск позиции по артикулу поставщика (загрузка его счёта, подстановка в заказ).
        b.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.SupplierArticle }).HasDatabaseName("ix_item_suppliers_article");
    }
}

internal sealed class ExternalLinkConfiguration : IEntityTypeConfiguration<ExternalLink>
{
    public void Configure(EntityTypeBuilder<ExternalLink> b)
    {
        b.ToTable("external_links", t => t.HasCheckConstraint("ck_external_links_kinds", "[System] BETWEEN 1 AND 8 AND [ObjectType] BETWEEN 1 AND 6"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.System).HasConversion<byte>();
        b.Property(x => x.ObjectType).HasConversion<byte>();
        b.Property(x => x.ExternalId).HasColumnType("varchar(64)").IsRequired();
        b.Property(x => x.ExternalCode).HasMaxLength(ExternalLink.ExternalCodeMaxLength);
        b.Property(x => x.ExternalVersion).HasColumnType("varchar(40)");
        b.Property(x => x.ContentHash).HasColumnType("char(64)");
        b.Property(x => x.SyncedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<KnitErp.Domain.Organizations.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.System, x.ObjectType, x.ExternalId }).IsUnique().HasDatabaseName("ux_external_links_external");
        b.HasIndex(x => new { x.OrganizationId, x.System, x.ObjectType, x.LocalId }).IsUnique().HasDatabaseName("ux_external_links_local");
        b.HasIndex(x => new { x.OrganizationId, x.System, x.ObjectType, x.ExternalCode }).HasDatabaseName("ix_external_links_code");
    }
}

internal sealed class ExternalConnectionConfiguration : IEntityTypeConfiguration<ExternalConnection>
{
    public void Configure(EntityTypeBuilder<ExternalConnection> b)
    {
        b.ToTable("external_connections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.System).HasConversion<byte>();
        // Токен внешней системы — только зашифрованным мастер-ключом (AES-256-GCM), как секреты 2FA и вебхуков.
        b.Property(x => x.Token).HasMaxLength(1000).IsRequired()
            .HasConversion(v => KnitErp.Infrastructure.Security.SecretCipher.Encrypt(v), v => KnitErp.Infrastructure.Security.SecretCipher.Decrypt(v));
        b.Property(x => x.TokenHint).HasMaxLength(8).IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(ExternalConnection.AccountMaxLength);
        b.Property(x => x.ConnectedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<KnitErp.Domain.Organizations.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KnitErp.Domain.Access.UserAccount>().WithMany().HasForeignKey(x => x.ConnectedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.System }).IsUnique().HasDatabaseName("ux_external_connections_org_system");
    }
}

internal sealed class ExternalImportRunConfiguration : IEntityTypeConfiguration<ExternalImportRun>
{
    public void Configure(EntityTypeBuilder<ExternalImportRun> b)
    {
        b.ToTable("external_import_runs", t => t.HasCheckConstraint("ck_external_import_runs_status", "[Status] IN (1, 2, 3, 4)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.System).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.DefaultItemType).HasConversion<byte>();
        b.Property(x => x.Stage).HasColumnType("varchar(40)");
        b.Property(x => x.Message).HasMaxLength(ExternalImportRun.MessageMaxLength);
        b.Property(x => x.RequestedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.StartedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.FinishedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsActive);
        b.HasOne<KnitErp.Domain.Organizations.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KnitErp.Domain.Access.UserAccount>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        // Одна активная загрузка на организацию и систему.
        b.HasIndex(x => new { x.OrganizationId, x.System }).IsUnique().HasFilter("[Status] IN (1, 2)").HasDatabaseName("ux_external_import_runs_active");
        b.HasIndex(x => new { x.Status, x.RequestedAtUtc }).HasDatabaseName("ix_external_import_runs_queue");
    }
}

internal sealed class ExternalImportIssueConfiguration : IEntityTypeConfiguration<ExternalImportIssue>
{
    public void Configure(EntityTypeBuilder<ExternalImportIssue> b)
    {
        b.ToTable("external_import_issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Entity).HasColumnType("varchar(40)").IsRequired();
        b.Property(x => x.ExternalId).HasColumnType("varchar(64)");
        b.Property(x => x.Code).HasMaxLength(255);
        b.Property(x => x.Name).HasMaxLength(300);
        b.Property(x => x.Text).HasMaxLength(ExternalImportIssue.TextMaxLength).IsRequired();
        b.HasOne<ExternalImportRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RunId, x.Id }).HasDatabaseName("ix_external_import_issues_run");
    }
}

/// <summary>D312: общие настройки каталога организации — одна строка на организацию.</summary>
internal sealed class CatalogSettingsConfiguration : IEntityTypeConfiguration<CatalogSettings>
{
    public void Configure(EntityTypeBuilder<CatalogSettings> b)
    {
        b.ToTable("catalog_settings", t =>
        {
            t.HasCheckConstraint("ck_catalog_settings_digits", "[AutoCodeDigits] BETWEEN 3 AND 8");
            t.HasCheckConstraint("ck_catalog_settings_next", "[NextNumber] >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.AutoCodePrefix).HasMaxLength(CatalogSettings.PrefixMaxLength);
        b.Property(x => x.DefaultItemType).HasConversion<byte>();
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.AutoCode);
        b.HasOne<KnitErp.Domain.Organizations.Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("ux_catalog_settings_org");
    }
}
