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
