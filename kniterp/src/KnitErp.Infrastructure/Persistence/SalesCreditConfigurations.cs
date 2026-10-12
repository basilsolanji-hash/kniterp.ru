using KnitErp.Domain.Access;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Разрешения кредитного контроля (D1111): заказ — своей организации, причина обязательна, сумма больше нуля.</summary>
internal sealed class SalesCreditApprovalConfiguration : IEntityTypeConfiguration<SalesCreditApproval>
{
    public void Configure(EntityTypeBuilder<SalesCreditApproval> b)
    {
        b.ToTable("sales_credit_approvals", t =>
        {
            t.HasCheckConstraint("ck_sales_credit_approvals_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_sales_credit_approvals_reason", "LEN([Reason]) > 0");
            t.HasCheckConstraint("ck_sales_credit_approvals_limit", "[Limit] IS NULL OR [Limit] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.Property(x => x.Debt).HasPrecision(19, 4);
        b.Property(x => x.Limit).HasPrecision(19, 4);
        b.Property(x => x.Overdue).HasPrecision(19, 4);
        b.Property(x => x.Reason).HasMaxLength(SalesCreditApproval.ReasonMaxLength).IsRequired();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_credit_approvals_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_credit_approvals_user");
        // Последнее разрешение заказа — в карточке заказа и при каждой проверке.
        b.HasIndex(x => new { x.OrganizationId, x.OrderId, x.ApprovedAtUtc }).HasDatabaseName("ix_sales_credit_approvals_order");
    }
}
