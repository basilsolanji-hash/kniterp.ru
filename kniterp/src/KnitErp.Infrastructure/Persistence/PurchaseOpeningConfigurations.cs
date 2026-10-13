using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>
/// Вводные остатки взаиморасчётов с поставщиками (D133, по образцу D1146): ключи — в своей организации; сумма не ноль; аннулированный —
/// с причиной; один действующий остаток на поставщика и юрлицо (фильтрованный UNIQUE).
/// </summary>
internal sealed class SupplierOpeningBalanceConfiguration : IEntityTypeConfiguration<SupplierOpeningBalance>
{
    public void Configure(EntityTypeBuilder<SupplierOpeningBalance> b)
    {
        b.ToTable("supplier_opening_balances", t =>
        {
            t.HasCheckConstraint("ck_supplier_opening_balances_amount", "[Amount] <> 0");
            t.HasCheckConstraint("ck_supplier_opening_balances_cancelled",
                "([IsCancelled] = 1 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([IsCancelled] = 0 AND [CancelledByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Comment).HasMaxLength(SupplierOpeningBalance.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(SupplierOpeningBalance.CommentMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CancelledAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_opening_balances_entity");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_opening_balances_supplier");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_opening_balances_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_opening_balances_cancelled_by");
        b.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.LegalEntityId }).IsUnique().HasFilter("[IsCancelled] = 0")
            .HasDatabaseName("ux_supplier_opening_balances_active");
    }
}

/// <summary>Настройки закупок (D133): одна строка на организацию, допуск поступления сверх заказа 0–10 %.</summary>
internal sealed class PurchaseSettingsConfiguration : IEntityTypeConfiguration<PurchaseSettings>
{
    public void Configure(EntityTypeBuilder<PurchaseSettings> b)
    {
        b.ToTable("purchase_settings", t =>
            t.HasCheckConstraint("ck_purchase_settings_over_receipt", "[OverReceiptPercent] BETWEEN 0 AND 10"));
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.Property(x => x.OverReceiptPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithOne().HasForeignKey<PurchaseSettings>(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_purchase_settings_updated_by");
    }
}
