using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Соглашения с покупателями (D1101). Ограничения дублируют правила домена в SQL Server; ссылки — в пределах организации.</summary>
internal sealed class SalesAgreementConfiguration : IEntityTypeConfiguration<SalesAgreement>
{
    public void Configure(EntityTypeBuilder<SalesAgreement> b)
    {
        b.ToTable("sales_agreements", t =>
        {
            t.HasCheckConstraint("ck_sales_agreements_status", "[Status] IN (1, 2)");
            t.HasCheckConstraint("ck_sales_agreements_scope", "[CounterpartyId] IS NULL OR [TagId] IS NULL");
            t.HasCheckConstraint("ck_sales_agreements_period", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
            t.HasCheckConstraint("ck_sales_agreements_discount", "[DiscountPercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_sales_agreements_prepayment", "[PrepaymentPercent] IS NULL OR [PrepaymentPercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_sales_agreements_days", $"[PaymentDays] IS NULL OR [PaymentDays] BETWEEN 0 AND {SalesAgreement.MaxPaymentDays}");
            t.HasCheckConstraint("ck_sales_agreements_limit", "[CreditLimit] IS NULL OR [CreditLimit] >= 0");
            t.HasCheckConstraint("ck_sales_agreements_contract", "[ContractId] IS NULL OR [CounterpartyId] IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(SalesAgreement.NameMaxLength).IsRequired();
        b.Property(x => x.Comment).HasMaxLength(SalesAgreement.CommentMaxLength);
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.DiscountPercent).HasPrecision(5, 2);
        b.Property(x => x.PrepaymentPercent).HasPrecision(5, 2);
        b.Property(x => x.CreditLimit).HasPrecision(19, 4);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Scope);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_agreements_org_id");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_agreements_counterparty");
        b.HasOne<Tag>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TagId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_agreements_tag");
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_agreements_price_type");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_agreements_warehouse");
        b.HasOne<CounterpartyContract>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ContractId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_agreements_contract");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_sales_agreements_org_number");
        // Подбор соглашения для заказа: по покупателю и датам действующих.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.CounterpartyId, x.ValidFrom }).HasDatabaseName("ix_sales_agreements_pick");
    }
}

/// <summary>Условия продаж покупателя (D1102): одна запись на контрагента.</summary>
internal sealed class CustomerSalesProfileConfiguration : IEntityTypeConfiguration<CustomerSalesProfile>
{
    public void Configure(EntityTypeBuilder<CustomerSalesProfile> b)
    {
        b.ToTable("customer_sales_profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_sales_profiles_counterparty");
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_sales_profiles_price_type");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_sales_profiles_manager");
        b.HasIndex(x => new { x.OrganizationId, x.CounterpartyId }).IsUnique().HasDatabaseName("ux_customer_sales_profiles_counterparty");
        b.HasIndex(x => new { x.OrganizationId, x.ManagerUserId }).HasDatabaseName("ix_customer_sales_profiles_manager");
    }
}

/// <summary>Настройки продаж роли (D1103): одна запись на роль.</summary>
internal sealed class SalesRoleSettingsConfiguration : IEntityTypeConfiguration<SalesRoleSettings>
{
    public void Configure(EntityTypeBuilder<SalesRoleSettings> b)
    {
        b.ToTable("sales_role_settings", t =>
        {
            t.HasCheckConstraint("ck_sales_role_settings_discount", "[MaxDiscountPercent] IS NULL OR [MaxDiscountPercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_sales_role_settings_visibility", "[Visibility] IN (1, 2, 3)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.MaxDiscountPercent).HasPrecision(5, 2);
        b.Property(x => x.Visibility).HasConversion<byte>().HasDefaultValue(SalesOrderVisibility.All);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Role>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RoleId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_role_settings_role");
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MinPriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_role_settings_price_type");
        b.HasIndex(x => new { x.OrganizationId, x.RoleId }).IsUnique().HasDatabaseName("ux_sales_role_settings_role");
    }
}

/// <summary>Правила скидок и акции (D1106).</summary>
internal sealed class SalesDiscountRuleConfiguration : IEntityTypeConfiguration<SalesDiscountRule>
{
    public void Configure(EntityTypeBuilder<SalesDiscountRule> b)
    {
        b.ToTable("sales_discount_rules", t =>
        {
            t.HasCheckConstraint("ck_sales_discount_rules_kind", "[Kind] IN (2, 3, 4)");
            t.HasCheckConstraint("ck_sales_discount_rules_percent", "[Percent] > 0 AND [Percent] <= 100");
            t.HasCheckConstraint("ck_sales_discount_rules_period", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
            t.HasCheckConstraint("ck_sales_discount_rules_terms",
                "([Kind] = 2 AND [MinOrderAmount] > 0) OR ([Kind] = 3 AND [MinQuantity] > 0) OR ([Kind] = 4 AND [ItemGroupId] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(SalesDiscountRule.NameMaxLength).IsRequired();
        b.Property(x => x.Percent).HasPrecision(5, 2);
        b.Property(x => x.MinOrderAmount).HasPrecision(19, 4);
        b.Property(x => x.MinQuantity).HasPrecision(18, 6);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ItemGroup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemGroupId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_discount_rules_group");
        b.HasOne<Tag>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TagId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_discount_rules_tag");
        b.HasIndex(x => new { x.OrganizationId, x.IsArchived, x.ValidFrom }).HasDatabaseName("ix_sales_discount_rules_active");
    }
}

/// <summary>Настройки скидок организации (D1106): одна запись.</summary>
internal sealed class SalesDiscountSettingsConfiguration : IEntityTypeConfiguration<SalesDiscountSettings>
{
    public void Configure(EntityTypeBuilder<SalesDiscountSettings> b)
    {
        b.ToTable("sales_discount_settings", t => t.HasCheckConstraint("ck_sales_discount_settings_mode", "[Mode] IN (1, 2)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Mode).HasConversion<byte>();
        b.Property(x => x.KindOrder).HasMaxLength(20).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Order);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("ux_sales_discount_settings_org");
    }
}

/// <summary>Причины отмены заказа (D1137).</summary>
internal sealed class SalesCancelReasonConfiguration : IEntityTypeConfiguration<SalesCancelReason>
{
    public void Configure(EntityTypeBuilder<SalesCancelReason> b)
    {
        b.ToTable("sales_cancel_reasons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(SalesCancelReason.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_cancel_reasons_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_sales_cancel_reasons_name");
    }
}
