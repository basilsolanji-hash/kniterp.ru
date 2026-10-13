using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>
/// Вводные остатки взаиморасчётов с покупателями (D1146): ключи — в своей организации; сумма не ноль; аннулированный — с причиной;
/// один действующий остаток на покупателя и юрлицо (фильтрованный UNIQUE).
/// </summary>
internal sealed class CustomerOpeningBalanceConfiguration : IEntityTypeConfiguration<CustomerOpeningBalance>
{
    public void Configure(EntityTypeBuilder<CustomerOpeningBalance> b)
    {
        b.ToTable("customer_opening_balances", t =>
        {
            t.HasCheckConstraint("ck_customer_opening_balances_amount", "[Amount] <> 0");
            t.HasCheckConstraint("ck_customer_opening_balances_cancelled",
                "([IsCancelled] = 1 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([IsCancelled] = 0 AND [CancelledByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Comment).HasMaxLength(CustomerOpeningBalance.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(CustomerOpeningBalance.CommentMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CancelledAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_opening_balances_entity");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_opening_balances_customer");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_opening_balances_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_opening_balances_cancelled_by");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.LegalEntityId }).IsUnique().HasFilter("[IsCancelled] = 0")
            .HasDatabaseName("ux_customer_opening_balances_active");
    }
}
