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
