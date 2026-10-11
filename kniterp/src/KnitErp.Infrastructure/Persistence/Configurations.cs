using KnitErp.Infrastructure.Security;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("organizations", t =>
        {
            // Налоговый номер страны (D60): ИНН 10, СТИР и УНП 9, БИН 12 цифр. КПП — только у российских.
            t.HasCheckConstraint("ck_organizations_inn", "LEN([Inn]) BETWEEN 9 AND 12 AND [Inn] NOT LIKE '%[^0-9]%'");
            t.HasCheckConstraint("ck_organizations_kpp", "[Kpp] IS NULL OR (LEN([Kpp]) = 9 AND [CountryCode] = 'RU')");
            t.HasCheckConstraint("ck_organizations_country", "[CountryCode] IN ('RU', 'UZ', 'KZ', 'BY')");
            t.HasCheckConstraint("ck_organizations_kpp_verified", "[KppVerified] = 0 OR [Kpp] IS NOT NULL");
            t.HasCheckConstraint("ck_organizations_currency", "LEN([CurrencyCode]) = 3");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FullName).HasMaxLength(Organization.NameMaxLength).IsRequired();
        b.Property(x => x.ShortName).HasMaxLength(Organization.ShortNameMaxLength).IsRequired();
        b.Property(x => x.Inn).HasColumnType("varchar(12)").IsRequired();
        b.Property(x => x.CountryCode).HasColumnType("char(2)").IsRequired().HasDefaultValue("RU");
        b.Property(x => x.Kpp).HasColumnType("char(9)");
        b.Property(x => x.ActualAddress).HasMaxLength(Organization.AddressMaxLength);
        b.Property(x => x.WebsiteUrl).HasMaxLength(Organization.WebsiteMaxLength);
        b.Property(x => x.TimeZoneId).HasMaxLength(Organization.TimeZoneMaxLength).IsRequired();
        b.Property(x => x.CurrencyCode).HasColumnType("char(3)").IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.PrintableKpp);
        b.HasIndex(x => new { x.CountryCode, x.Inn }).IsUnique().HasDatabaseName("ux_organizations_country_inn");
    }
}

internal sealed class LegalEntityConfiguration : IEntityTypeConfiguration<LegalEntity>
{
    public void Configure(EntityTypeBuilder<LegalEntity> b)
    {
        b.ToTable("legal_entities", t =>
        {
            t.HasCheckConstraint("ck_legal_entities_kind", "[Kind] IN (1, 2)");
            // ИНН: 9–12 цифр (страны D60); у ИП (вид 2) КПП нет; основное юрлицо не в архиве.
            t.HasCheckConstraint("ck_legal_entities_inn", "LEN([Inn]) BETWEEN 9 AND 14 AND [Inn] NOT LIKE '%[^0-9]%'");
            t.HasCheckConstraint("ck_legal_entities_kpp", "[Kpp] IS NULL OR (LEN([Kpp]) = 9 AND [Kind] = 1)");
            t.HasCheckConstraint("ck_legal_entities_default", "[IsDefault] = 0 OR [IsArchived] = 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(LegalEntity.NameMaxLength).IsRequired();
        b.Property(x => x.ShortName).HasMaxLength(LegalEntity.ShortNameMaxLength).IsRequired();
        b.Property(x => x.Inn).HasColumnType("varchar(14)").IsRequired();
        b.Property(x => x.Kpp).HasColumnType("char(9)");
        b.Property(x => x.Ogrn).HasColumnType("varchar(20)");
        b.Property(x => x.LegalAddress).HasMaxLength(LegalEntity.AddressMaxLength);
        b.Property(x => x.DirectorPosition).HasMaxLength(LegalEntity.PositionMaxLength);
        b.Property(x => x.DirectorName).HasMaxLength(LegalEntity.PersonNameMaxLength);
        b.Property(x => x.AccountantName).HasMaxLength(LegalEntity.PersonNameMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsSoleProprietor);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_legal_entities_org_id");
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("ux_legal_entities_org_default");
        b.HasIndex(x => new { x.OrganizationId, x.Inn, x.Kpp }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_legal_entities_org_inn_kpp_active");
    }
}

internal sealed class LegalEntityAccountConfiguration : IEntityTypeConfiguration<LegalEntityAccount>
{
    public void Configure(EntityTypeBuilder<LegalEntityAccount> b)
    {
        b.ToTable("legal_entity_accounts", t =>
        {
            t.HasCheckConstraint("ck_legal_entity_accounts_default", "[IsDefault] = 0 OR [IsArchived] = 0");
            // D80: вид — банк (1) или касса (2); у банка БИК и счёт обязательны, касса основной не бывает.
            t.HasCheckConstraint("ck_legal_entity_accounts_kind",
                "([Kind] = 1 AND LEN([Bic]) > 0 AND LEN([Account]) > 0) OR ([Kind] = 2 AND [IsDefault] = 0)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.BankName).HasMaxLength(LegalEntityAccount.BankNameMaxLength).IsRequired();
        b.Property(x => x.Bic).HasColumnType("varchar(11)").IsRequired();
        b.Property(x => x.Account).HasColumnType("varchar(34)").IsRequired();
        b.Property(x => x.CorrAccount).HasColumnType("varchar(34)");
        b.Property(x => x.Kind).HasConversion<byte>().HasDefaultValue(MoneyAccountKind.Bank);
        b.Property(x => x.OpeningBalance).HasColumnType("decimal(19,4)").HasDefaultValue(0m);
        b.Ignore(x => x.IsCash);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasAlternateKey(x => new { x.OrganizationId, x.LegalEntityId, x.Id }).HasName("ak_legal_entity_accounts_org_entity_id");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_legal_entity_accounts_org_id");
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_legal_entity_accounts_entity");
        b.HasIndex(x => x.LegalEntityId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("ux_legal_entity_accounts_entity_default");
        b.HasIndex(x => new { x.LegalEntityId, x.Account }).IsUnique().HasFilter("[IsArchived] = 0 AND [Kind] = 1")
            .HasDatabaseName("ux_legal_entity_accounts_entity_account_active");
    }
}

internal sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> b)
    {
        b.ToTable("users", t =>
        {
            t.HasCheckConstraint("ck_users_status", "[Status] IN (1, 2, 4)");
            t.HasCheckConstraint("ck_users_failed_sign_in", "[FailedSignInCount] >= 0");
            t.HasCheckConstraint("ck_users_2fa_key", "[TwoFactorEnabled] = 0 OR [AuthenticatorKey] IS NOT NULL");
            t.HasCheckConstraint("ck_users_setup_token",
                "([SetupTokenHash] IS NULL AND [SetupTokenExpiresAtUtc] IS NULL) OR ([SetupTokenHash] IS NOT NULL AND [SetupTokenExpiresAtUtc] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Email).HasMaxLength(UserAccount.EmailMaxLength).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(UserAccount.EmailMaxLength).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(UserAccount.DisplayNameMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.PasswordHash).HasColumnType("varchar(256)");
        b.Property(x => x.Language).HasColumnType("varchar(5)");
        // Секрет 2FA хранится зашифрованным (AES-256-GCM, мастер-ключ вне базы): копия базы не даёт выпускать коды входа.
        b.Property(x => x.AuthenticatorKey).HasColumnType("varchar(200)")
            .HasConversion(v => v == null ? null : SecretCipher.Encrypt(v), v => v == null ? null : SecretCipher.Decrypt(v));
        b.Property(x => x.SetupTokenHash).HasColumnType("binary(32)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.HasPassword);
        b.HasIndex(x => x.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
        b.HasIndex(x => x.SetupTokenHash).IsUnique().HasFilter("[SetupTokenHash] IS NOT NULL").HasDatabaseName("ux_users_setup_token");
    }
}

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> b)
    {
        b.ToTable("user_recovery_codes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.CodeHash).HasColumnType("binary(32)").IsRequired();
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique().HasDatabaseName("ux_user_recovery_codes_user_hash");
    }
}

internal sealed class TrustedDeviceConfiguration : IEntityTypeConfiguration<TrustedDevice>
{
    public void Configure(EntityTypeBuilder<TrustedDevice> b)
    {
        b.ToTable("user_trusted_devices", t => t.HasCheckConstraint("ck_user_trusted_devices_expiry", "[ExpiresAtUtc] > [CreatedAtUtc]"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.TokenHash).HasColumnType("binary(32)").IsRequired();
        b.Property(x => x.Label).HasMaxLength(TrustedDevice.LabelMaxLength).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.ExpiresAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.LastUsedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RevokedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_user_trusted_devices_token");
        b.HasIndex(x => new { x.UserId, x.RevokedAtUtc }).HasDatabaseName("ix_user_trusted_devices_user");
    }
}

internal sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> b)
    {
        b.ToTable("organization_members", t =>
        {
            t.HasCheckConstraint("ck_organization_members_status", "[Status] IN (1, 2)");
            t.HasCheckConstraint("ck_organization_members_blocked", "[Status] <> 2 OR [BlockedAtUtc] IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique().HasDatabaseName("ux_organization_members_org_user");
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(Role.NameMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(Role.DescriptionMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsAdministrative);
        // D112: своя роль — с кодом «custom.…»; системные роли не архивируются.
        b.ToTable(t => t.HasCheckConstraint("ck_roles_archive_custom", "[IsArchived] = 0 OR [IsSystem] = 0"));
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_roles_org_code");
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_permissions");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions", t => t.HasCheckConstraint("ck_role_permissions_level", "[Level] IN (1, 2, 3, 10, 11)"));
        b.HasKey(x => new { x.RoleId, x.PermissionCode });
        b.Property(x => x.PermissionCode).HasMaxLength(100).IsRequired();
        b.Property(x => x.Level).HasConversion<byte>();
    }
}

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> b)
    {
        b.ToTable("role_assignments", t =>
        {
            // Либо роль, либо индивидуальное право — ровно одно (ТЗ §4.11).
            t.HasCheckConstraint("ck_role_assignments_target",
                "([RoleId] IS NOT NULL AND [PermissionCode] IS NULL) OR ([RoleId] IS NULL AND [PermissionCode] IS NOT NULL)");
            t.HasCheckConstraint("ck_role_assignments_deny", "[IsDeny] = 0 OR [PermissionCode] IS NOT NULL");
            t.HasCheckConstraint("ck_role_assignments_deny_reason", "[IsDeny] = 0 OR [Reason] IS NOT NULL");
            t.HasCheckConstraint("ck_role_assignments_period", "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PermissionCode).HasMaxLength(100);
        b.Property(x => x.Reason).HasMaxLength(RoleAssignment.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_role_assignments_department");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_role_assignments_warehouse");
        b.HasIndex(x => new { x.OrganizationId, x.UserId }).HasDatabaseName("ix_role_assignments_org_user");
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("audit_log", t => t.HasTrigger("tr_audit_log_immutable"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Action).HasMaxLength(AuditEntry.ActionMaxLength).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(AuditEntry.EntityIdMaxLength);
        b.Property(x => x.Before).HasMaxLength(AuditEntry.ValueMaxLength);
        b.Property(x => x.After).HasMaxLength(AuditEntry.ValueMaxLength);
        b.Property(x => x.Reason).HasMaxLength(AuditEntry.ReasonMaxLength);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.OccurredAtUtc }).HasDatabaseName("ix_audit_log_org_time");
        b.Property(x => x.ChainHash).HasColumnType("binary(32)");
        b.HasIndex(x => new { x.OrganizationId, x.ChainSeq }).IsUnique().HasFilter("[ChainSeq] IS NOT NULL").HasDatabaseName("ux_audit_log_org_chain");
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments", t =>
        {
            t.HasCheckConstraint("ck_departments_archived", "[IsArchived] = 0 OR [ArchivedAtUtc] IS NOT NULL");
            t.HasCheckConstraint("ck_departments_not_self_parent", "[ParentId] IS NULL OR [ParentId] <> [Id]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Department.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        // Составные ключи (организация, id): ссылка на подразделение другой организации невозможна в самой базе.
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_departments_org_id");
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ParentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_departments_parent");
        // Два действующих подразделения с одним названием в организации путают учёт; архивные не мешают.
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_departments_org_name_active");
    }
}

internal sealed class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> b)
    {
        b.ToTable("positions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Position.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_positions_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_positions_org_name_active");
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employees", t =>
        {
            t.HasCheckConstraint("ck_employees_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_employees_dismissed",
                "([Status] = 9 AND [DismissedOn] IS NOT NULL AND [DismissedOn] >= [HiredOn]) OR ([Status] <> 9 AND [DismissedOn] IS NULL)");
            t.HasCheckConstraint("ck_employees_birthday_share", "[ShareBirthday] = 0 OR [BirthDate] IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.PersonnelNumber).HasMaxLength(Employee.PersonnelNumberMaxLength).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(Employee.NamePartMaxLength).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(Employee.NamePartMaxLength).IsRequired();
        b.Property(x => x.MiddleName).HasMaxLength(Employee.NamePartMaxLength);
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.FullName);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_employees_department");
        b.HasOne<Position>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PositionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_employees_position");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.PersonnelNumber }).IsUnique().HasDatabaseName("ux_employees_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL")
            .HasDatabaseName("ux_employees_org_user");
        b.HasIndex(x => new { x.OrganizationId, x.DepartmentId }).HasDatabaseName("ix_employees_org_department");
    }
}

internal sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> b)
    {
        b.ToTable("units", t => t.HasCheckConstraint("ck_units_precision", $"[Precision] BETWEEN 0 AND {UnitOfMeasure.MaxPrecision}"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Code).HasMaxLength(UnitOfMeasure.CodeMaxLength).IsRequired();
        b.Property(x => x.Name).HasMaxLength(UnitOfMeasure.NameMaxLength).IsRequired();
        b.Property(x => x.Symbol).HasMaxLength(UnitOfMeasure.SymbolMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_units_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_units_org_code");
    }
}

internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("items", t =>
        {
            t.HasCheckConstraint("ck_items_type", "[Type] IN (1, 2, 3, 4, 5, 6, 9, 10, 11)");
            // D302: комплект — без неснижаемого остатка и модификаций (остаток считается по компонентам).
            t.HasCheckConstraint("ck_items_bundle", "[Type] <> 11 OR ([MinStock] IS NULL AND [ParentItemId] IS NULL)");
            // D305: предмет расчёта (услуга — «услуга», остальное — товар или подакцизный), маркировка — с видом продукции.
            t.HasCheckConstraint("ck_items_payment_subject", "([Type] = 10 AND [PaymentSubject] = 4) OR ([Type] <> 10 AND [PaymentSubject] IN (1, 2))");
            t.HasCheckConstraint("ck_items_marking",
                "([IsMarked] = 0 AND [TrackingType] = 0) OR ([IsMarked] = 1 AND [TrackingType] IN (1, 2, 3, 9) AND [Type] <> 10)");
            t.HasCheckConstraint("ck_items_gtin", "[Gtin] IS NULL OR (LEN([Gtin]) = 14 AND [Gtin] NOT LIKE '%[^0-9]%')");
            t.HasCheckConstraint("ck_items_min_price", "[MinPrice] IS NULL OR [MinPrice] >= 0");
            // D300: услуга — со ставкой НДС, без веса, объёма, неснижаемого остатка и модификаций.
            t.HasTrigger("tr_items_service_without_stock");
            t.HasCheckConstraint("ck_items_service",
                "[Type] <> 10 OR ([VatRateId] IS NOT NULL AND [WeightKg] IS NULL AND [VolumeM3] IS NULL AND [MinStock] IS NULL AND [ParentItemId] IS NULL)");
            t.HasCheckConstraint("ck_items_archived", "[IsArchived] = 0 OR [ArchivedAtUtc] IS NOT NULL");
            t.HasCheckConstraint("ck_items_parent", "[ParentItemId] IS NULL OR [ParentItemId] <> [Id]");
            t.HasCheckConstraint("ck_items_variant",
                "([ParentItemId] IS NULL AND [VariantKey] IS NULL) OR ([ParentItemId] IS NOT NULL AND [VariantKey] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Code).HasMaxLength(Item.CodeMaxLength).IsRequired();
        b.Property(x => x.Name).HasMaxLength(Item.NameMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(Item.DescriptionMaxLength);
        b.Property(x => x.Type).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_items_org_id");
        b.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.UnitId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_items_unit");
        // Код уникален навсегда, включая архив: старые документы не должны указывать на другую вещь.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_items_org_code");
        b.HasIndex(x => new { x.OrganizationId, x.Type, x.Name }).HasDatabaseName("ix_items_org_type_name");
        b.HasOne<VatRate>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.VatRateId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_items_vat_rate");
        b.HasOne<ItemGroup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.GroupId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_items_group");
        b.HasIndex(x => new { x.OrganizationId, x.GroupId }).HasDatabaseName("ix_items_org_group");
        b.HasIndex(x => new { x.OrganizationId, x.Article }).HasDatabaseName("ix_items_org_article");
        b.Property(x => x.Article).HasMaxLength(ItemDetailRules.ArticleMaxLength);
        b.Property(x => x.OriginCountryCode).HasColumnType("char(3)");
        b.Property(x => x.OriginCountryName).HasMaxLength(ItemDetailRules.CountryNameMaxLength);
        b.Property(x => x.CustomsDeclaration).HasMaxLength(ItemDetailRules.DeclarationMaxLength);
        b.Property(x => x.TnVedCode).HasColumnType("varchar(10)");
        b.Property(x => x.WeightKg).HasColumnType("decimal(18,6)");
        b.Property(x => x.VolumeM3).HasColumnType("decimal(18,6)");
        b.Property(x => x.MinStock).HasColumnType("decimal(18,6)");
        b.Property(x => x.PurchasePrice).HasColumnType("decimal(19,4)");
        // Модификации (D82): основная позиция той же организации; набор характеристик уникален у одной основной позиции.
        b.Property(x => x.VariantKey).HasMaxLength(Variants.KeyMaxLength);
        b.Ignore(x => x.IsModification);
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ParentItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_items_parent");
        b.HasIndex(x => new { x.OrganizationId, x.ParentItemId, x.VariantKey }).IsUnique().HasFilter("[ParentItemId] IS NOT NULL")
            .HasDatabaseName("ux_items_parent_variant");
        b.Ignore(x => x.Details);
        ItemTradeConfiguration.Apply(b); // D305 (сессия 4): поля торговли — в CatalogConfigurations.cs
    }
}

internal sealed class ItemGroupConfiguration : IEntityTypeConfiguration<ItemGroup>
{
    public void Configure(EntityTypeBuilder<ItemGroup> b)
    {
        b.ToTable("item_groups", t => t.HasCheckConstraint("ck_item_groups_parent", "[ParentId] IS NULL OR [ParentId] <> [Id]"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(ItemGroup.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_item_groups_org_id");
        b.HasOne<ItemGroup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ParentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_groups_parent");
        b.HasIndex(x => new { x.OrganizationId, x.ParentId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_item_groups_org_parent_name_active");
    }
}

internal sealed class ItemBarcodeConfiguration : IEntityTypeConfiguration<ItemBarcode>
{
    public void Configure(EntityTypeBuilder<ItemBarcode> b)
    {
        b.ToTable("item_barcodes", t => t.HasCheckConstraint("ck_item_barcodes_type", "[Type] IN (1, 2, 3, 4)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Type).HasConversion<byte>();
        b.Property(x => x.Code).HasColumnType("varchar(64)").IsRequired();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_barcodes_item");
        // Сканер находит одну позицию: штрихкод уникален в организации.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_item_barcodes_org_code");
        b.HasIndex(x => x.ItemId).HasDatabaseName("ix_item_barcodes_item");
    }
}

internal sealed class PriceTypeConfiguration : IEntityTypeConfiguration<PriceType>
{
    public void Configure(EntityTypeBuilder<PriceType> b)
    {
        b.ToTable("price_types", t => t.HasCheckConstraint("ck_price_types_default", "[IsDefault] = 0 OR [IsArchived] = 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(PriceType.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_price_types_org_id");
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("ux_price_types_org_default");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_price_types_org_name_active");
    }
}

internal sealed class ItemPriceConfiguration : IEntityTypeConfiguration<ItemPrice>
{
    public void Configure(EntityTypeBuilder<ItemPrice> b)
    {
        b.ToTable("item_prices", t => t.HasCheckConstraint("ck_item_prices_price", "[Price] >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Price).HasColumnType("decimal(19,4)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_prices_item");
        b.HasOne<PriceType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PriceTypeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_prices_type");
        b.HasIndex(x => new { x.ItemId, x.PriceTypeId }).IsUnique().HasDatabaseName("ux_item_prices_item_type");
    }
}

internal sealed class CharacteristicConfiguration : IEntityTypeConfiguration<Characteristic>
{
    public void Configure(EntityTypeBuilder<Characteristic> b)
    {
        b.ToTable("characteristics");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Characteristic.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_characteristics_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_characteristics_org_name_active");
    }
}

internal sealed class ItemCharacteristicValueConfiguration : IEntityTypeConfiguration<ItemCharacteristicValue>
{
    public void Configure(EntityTypeBuilder<ItemCharacteristicValue> b)
    {
        b.ToTable("item_characteristic_values");
        b.HasKey(x => new { x.ItemId, x.CharacteristicId });
        b.Property(x => x.Value).HasMaxLength(ItemCharacteristicValue.ValueMaxLength).IsRequired();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_characteristic_values_item");
        b.HasOne<Characteristic>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CharacteristicId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_characteristic_values_characteristic");
        b.HasIndex(x => new { x.OrganizationId, x.CharacteristicId, x.Value }).HasDatabaseName("ix_item_characteristic_values_value");
    }
}

internal sealed class ItemPhotoConfiguration : IEntityTypeConfiguration<ItemPhoto>
{
    public void Configure(EntityTypeBuilder<ItemPhoto> b)
    {
        b.ToTable("item_photos", t =>
        {
            t.HasCheckConstraint("ck_item_photos_size", $"[SizeBytes] BETWEEN 1 AND {ItemPhoto.MaxBytes} AND DATALENGTH([Content]) = [SizeBytes]");
            t.HasCheckConstraint("ck_item_photos_type", "[ContentType] IN ('image/jpeg', 'image/png', 'image/webp')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.ContentType).HasColumnType("varchar(20)").IsRequired();
        b.Property(x => x.Content).HasColumnType("varbinary(max)").IsRequired();
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_item_photos_item");
        b.HasIndex(x => new { x.ItemId, x.SortOrder }).HasDatabaseName("ix_item_photos_item_order");
    }
}

internal sealed class UserCatalogConfiguration : IEntityTypeConfiguration<UserCatalog>
{
    public void Configure(EntityTypeBuilder<UserCatalog> b)
    {
        b.ToTable("user_catalogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(UserCatalog.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_user_catalogs_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_user_catalogs_org_name_active");
    }
}

internal sealed class UserCatalogEntryConfiguration : IEntityTypeConfiguration<UserCatalogEntry>
{
    public void Configure(EntityTypeBuilder<UserCatalogEntry> b)
    {
        b.ToTable("user_catalog_entries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(UserCatalogEntry.NameMaxLength).IsRequired();
        b.Property(x => x.Code).HasMaxLength(UserCatalogEntry.CodeMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Display);
        b.HasOne<UserCatalog>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CatalogId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_user_catalog_entries_catalog");
        b.HasIndex(x => new { x.CatalogId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_user_catalog_entries_catalog_name_active");
    }
}

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> b)
    {
        b.ToTable("sites");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Site.NameMaxLength).IsRequired();
        b.Property(x => x.Address).HasMaxLength(Site.AddressMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sites_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_sites_org_name_active");
    }
}

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("warehouses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Warehouse.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_warehouses_org_id");
        b.HasOne<Site>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SiteId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_warehouses_site");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_warehouses_org_name_active");
    }
}

internal sealed class CounterpartyConfiguration : IEntityTypeConfiguration<Counterparty>
{
    public void Configure(EntityTypeBuilder<Counterparty> b)
    {
        b.ToTable("counterparties", t =>
        {
            t.HasCheckConstraint("ck_counterparties_role", "[IsSupplier] = 1 OR [IsCustomer] = 1");
            t.HasCheckConstraint("ck_counterparties_inn", "[Inn] IS NULL OR (LEN([Inn]) BETWEEN 9 AND 14 AND [Inn] NOT LIKE '%[^0-9]%')");
            t.HasCheckConstraint("ck_counterparties_kpp", "[Kpp] IS NULL OR (LEN([Kpp]) = 9 AND LEN([Inn]) = 10 AND [CountryCode] = 'RU')");
            t.HasCheckConstraint("ck_counterparties_country", "[CountryCode] IN ('RU', 'UZ', 'KZ', 'BY')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(Counterparty.NameMaxLength).IsRequired();
        b.Property(x => x.Inn).HasColumnType("varchar(14)");
        b.Property(x => x.Kpp).HasColumnType("char(9)");
        b.Property(x => x.CountryCode).HasColumnType("char(2)").IsRequired().HasDefaultValue("RU");
        b.Property(x => x.Comment).HasMaxLength(Counterparty.CommentMaxLength);
        b.Property(x => x.Address).HasMaxLength(Counterparty.AddressMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_counterparties_org_id");
        // Филиалы одной организации отличаются КПП, поэтому уникальна пара ИНН + КПП среди действующих.
        b.HasIndex(x => new { x.OrganizationId, x.CountryCode, x.Inn, x.Kpp }).IsUnique()
            .HasFilter("[Inn] IS NOT NULL AND [IsArchived] = 0").HasDatabaseName("ux_counterparties_org_country_inn_kpp_active");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).HasDatabaseName("ix_counterparties_org_name");
    }
}

internal sealed class VatRateConfiguration : IEntityTypeConfiguration<VatRate>
{
    public void Configure(EntityTypeBuilder<VatRate> b)
    {
        b.ToTable("vat_rates", t => t.HasCheckConstraint("ck_vat_rates_kind", "[Kind] IN (1, 2, 3, 4)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(VatRate.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_vat_rates_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_vat_rates_org_name_active");
        b.HasMany(x => x.Periods).WithOne().HasForeignKey(x => x.VatRateId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Periods).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_periods");
    }
}

internal sealed class VatRatePeriodConfiguration : IEntityTypeConfiguration<VatRatePeriod>
{
    public void Configure(EntityTypeBuilder<VatRatePeriod> b)
    {
        b.ToTable("vat_rate_periods", t => t.HasCheckConstraint("ck_vat_rate_periods_percent", "[Percent] BETWEEN 0 AND 100"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Percent).HasColumnType("decimal(5,2)");
        b.HasIndex(x => new { x.VatRateId, x.ValidFrom }).IsUnique().HasDatabaseName("ux_vat_rate_periods_rate_from");
    }
}

internal sealed class TechCardConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Production.TechCard>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Production.TechCard> b)
    {
        b.ToTable("tech_cards", t =>
        {
            t.HasCheckConstraint("ck_tech_cards_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_tech_cards_output", "[OutputQuantity] > 0");
            t.HasCheckConstraint("ck_tech_cards_version", "[Version] >= 1");
            // Маршрут изделия (D401): вязание и контроль качества обязательны, только пять этапов.
            t.HasCheckConstraint("ck_tech_cards_route", "([Route] & 9) = 9 AND [Route] <= 31");
            t.HasCheckConstraint("ck_tech_cards_activated",
                "([Status] = 1 AND [ActivatedByUserId] IS NULL) OR ([Status] <> 1 AND [ActivatedByUserId] IS NOT NULL AND [ActivatedAtUtc] IS NOT NULL)"
                + " OR ([Status] = 9 AND [ActivatedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.OutputQuantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Revision).HasDefaultValue(0);
        b.Property(x => x.Comment).HasMaxLength(KnitErp.Domain.Production.TechCard.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_tech_cards_org_id");
        // Изделие — из той же организации: составной ключ, как у складских документов.
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_tech_cards_item");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActivatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.ItemId, x.Version }).IsUnique().HasDatabaseName("ux_tech_cards_item_version");
        // У изделия одна действующая карта — и при одновременном вводе в действие двух версий.
        b.HasIndex(x => new { x.OrganizationId, x.ItemId }).IsUnique().HasFilter("[Status] = 2").HasDatabaseName("ux_tech_cards_item_active");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TechCardId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class TechCardLineConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Production.TechCardLine>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Production.TechCardLine> b)
    {
        b.ToTable("tech_card_lines", t =>
        {
            t.HasCheckConstraint("ck_tech_card_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_tech_card_lines_waste", "[WastePercent] >= 0 AND [WastePercent] < 100");
            // Строки меняются только у черновика — триггер в миграции TechCardRevisionAndLineGuard.
            t.HasTrigger("tr_tech_card_lines_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.WastePercent).HasColumnType("decimal(5,2)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TechCardId, x.ItemId }).IsUnique().HasDatabaseName("ux_tech_card_lines_card_item");
    }
}

internal sealed class OperationReasonConfiguration : IEntityTypeConfiguration<OperationReason>
{
    public void Configure(EntityTypeBuilder<OperationReason> b)
    {
        b.ToTable("operation_reasons", t => t.HasCheckConstraint("ck_operation_reasons_kind", "[Kind] IN (1, 2, 3, 4, 5, 6, 7)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(OperationReason.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_operation_reasons_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_operation_reasons_org_kind_name_active");
    }
}

internal sealed class DocumentCounterConfiguration : IEntityTypeConfiguration<DocumentCounter>
{
    public void Configure(EntityTypeBuilder<DocumentCounter> b)
    {
        b.ToTable("document_counters", t => t.HasCheckConstraint("ck_document_counters_last", "[LastNumber] >= 0"));
        b.HasKey(x => new { x.OrganizationId, x.Kind });
        b.Property(x => x.Kind).HasMaxLength(DocumentCounter.KindMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpeningBalanceConfiguration : IEntityTypeConfiguration<OpeningBalance>
{
    public void Configure(EntityTypeBuilder<OpeningBalance> b)
    {
        b.ToTable("opening_balances", t =>
        {
            t.HasCheckConstraint("ck_opening_balances_status", "[Status] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_opening_balances_approved",
                "([Status] = 3 AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL AND [ApprovedByUserId] <> [CreatedByUserId])"
                + " OR ([Status] <> 3 AND [ApprovedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(OpeningBalance.CommentMaxLength);
        b.Property(x => x.ReturnReason).HasMaxLength(OpeningBalance.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_opening_balances_org_id");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_opening_balances_warehouse");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_opening_balances_org_number");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class OpeningBalanceLineConfiguration : IEntityTypeConfiguration<OpeningBalanceLine>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceLine> b)
    {
        b.ToTable("opening_balance_lines", t => t.HasCheckConstraint("ck_opening_balance_lines_quantity", "[Quantity] > 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DocumentId, x.ItemId }).IsUnique().HasDatabaseName("ux_opening_balance_lines_doc_item");
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movements", t =>
        {
            t.HasTrigger("tr_stock_movements_immutable");
            t.HasTrigger("tr_stock_movements_closed_period");
            t.HasTrigger("tr_stock_movements_stocked_items");
            t.HasCheckConstraint("ck_stock_movements_quantity", "[Quantity] <> 0");
            t.HasCheckConstraint("ck_stock_movements_source", "[Source] IN (1, 2, 3, 4)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Source).HasConversion<byte>();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_movements_warehouse");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_movements_item");
        b.HasIndex(x => new { x.OrganizationId, x.WarehouseId, x.ItemId }).HasDatabaseName("ix_stock_movements_org_wh_item");
        b.HasIndex(x => new { x.Source, x.SourceId }).HasDatabaseName("ix_stock_movements_source");
        b.Property(x => x.ChainHash).HasColumnType("binary(32)");
        b.HasIndex(x => new { x.OrganizationId, x.ChainSeq }).IsUnique().HasFilter("[ChainSeq] IS NOT NULL").HasDatabaseName("ux_stock_movements_org_chain");
    }
}

internal sealed class MoneyOperationConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.MoneyOperation>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.MoneyOperation> b)
    {
        b.ToTable("money_operations", t =>
        {
            t.HasCheckConstraint("ck_money_operations_kind", "[Kind] IN (1, 2, 3)");
            t.HasCheckConstraint("ck_money_operations_status", "[Status] IN (1, 9)");
            t.HasCheckConstraint("ck_money_operations_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_money_operations_target",
                "([Kind] = 3 AND [TargetAccountId] IS NOT NULL AND [TargetAccountId] <> [AccountId]) OR ([Kind] <> 3 AND [TargetAccountId] IS NULL)");
            t.HasCheckConstraint("ck_money_operations_transfer_item",
                "[Kind] <> 3 OR ([CashFlowItemId] IS NULL AND [EmployeeId] IS NULL)");
            t.HasCheckConstraint("ck_money_operations_item_required", "[Kind] = 3 OR [CashFlowItemId] IS NOT NULL");
            t.HasCheckConstraint("ck_money_operations_transfer_orders",
                "[Kind] = 3 OR ([CashOrderNumber] IS NULL AND [TargetCashOrderNumber] IS NULL)");
            t.HasCheckConstraint("ck_money_operations_cancel",
                "([Status] = 9 AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 1 AND [CancelledAtUtc] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Party).HasMaxLength(KnitErp.Domain.Finance.MoneyOperation.PartyMaxLength);
        b.Property(x => x.Basis).HasMaxLength(KnitErp.Domain.Finance.MoneyOperation.BasisMaxLength).IsRequired();
        b.Property(x => x.Comment).HasMaxLength(KnitErp.Domain.Finance.MoneyOperation.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(KnitErp.Domain.Finance.MoneyOperation.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.AccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_money_operations_account");
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TargetAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_money_operations_target");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_money_operations_org_number");
        b.Property(x => x.CashOrderNumber).HasMaxLength(30);
        b.Property(x => x.TargetCashOrderNumber).HasMaxLength(30);
        b.HasIndex(x => new { x.OrganizationId, x.CashOrderNumber }).IsUnique().HasFilter("[CashOrderNumber] IS NOT NULL")
            .HasDatabaseName("ux_money_operations_org_cash_order");
        b.HasIndex(x => new { x.OrganizationId, x.TargetCashOrderNumber }).IsUnique().HasFilter("[TargetCashOrderNumber] IS NOT NULL")
            .HasDatabaseName("ux_money_operations_org_target_cash_order");
        b.HasOne<KnitErp.Domain.Finance.CashFlowItem>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CashFlowItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_money_operations_cash_flow_item");
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_money_operations_employee");
        b.HasIndex(x => new { x.OrganizationId, x.OperationDate }).HasDatabaseName("ix_money_operations_org_date");
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable("purchase_orders", t =>
        {
            t.HasCheckConstraint("ck_purchase_orders_status", "[Status] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_purchase_orders_confirmed",
                "([Status] = 1 AND [ConfirmedByUserId] IS NULL) OR ([Status] IN (2, 3) AND [ConfirmedByUserId] IS NOT NULL AND [ConfirmedAtUtc] IS NOT NULL)"
                + " OR [Status] = 9");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        // Юрлицо-покупатель (D84): только своё юрлицо организации.
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_purchase_orders_legal_entity");
        b.HasIndex(x => new { x.OrganizationId, x.LegalEntityId }).HasDatabaseName("ix_purchase_orders_org_legal_entity");
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.SupplierInvoice).HasMaxLength(PurchaseOrder.InvoiceMaxLength);
        b.Property(x => x.Comment).HasMaxLength(PurchaseOrder.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Total);
        b.Ignore(x => x.VatTotal);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_purchase_orders_org_id");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_purchase_orders_supplier");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_purchase_orders_warehouse");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_purchase_orders_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.OrderDate }).HasDatabaseName("ix_purchase_orders_org_supplier_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_lines", t =>
        {
            t.HasCheckConstraint("ck_purchase_order_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_purchase_order_lines_price", "[Price] >= 0 AND [Amount] >= 0 AND [VatAmount] >= 0");
            t.HasCheckConstraint("ck_purchase_order_lines_vat", "[VatPercent] IS NULL OR [VatPercent] BETWEEN 0 AND 100");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Price).HasColumnType("decimal(19,4)");
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatAmount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatPercent).HasColumnType("decimal(5,2)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrderId, x.ItemId }).IsUnique().HasDatabaseName("ux_purchase_order_lines_order_item");
    }
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> b)
    {
        b.ToTable("supplier_payments", t =>
        {
            t.HasCheckConstraint("ck_supplier_payments_status", "[Status] IN (2, 9)");
            t.HasCheckConstraint("ck_supplier_payments_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_supplier_payments_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
        });
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MoneyAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_payments_money_account");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Comment).HasMaxLength(SupplierPayment.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(SupplierPayment.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_payments_supplier");
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_payments_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_supplier_payments_org_number");
        b.Property(x => x.CashOrderNumber).HasMaxLength(30);
        b.HasIndex(x => new { x.OrganizationId, x.CashOrderNumber }).IsUnique().HasFilter("[CashOrderNumber] IS NOT NULL")
            .HasDatabaseName("ux_supplier_payments_org_cash_order");
        b.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.PaymentDate }).HasDatabaseName("ix_supplier_payments_org_supplier_date");
    }
}

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> b)
    {
        b.ToTable("sales_orders", t =>
        {
            t.HasCheckConstraint("ck_sales_orders_status", "[Status] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_sales_orders_confirmed",
                "([Status] = 1 AND [ConfirmedByUserId] IS NULL) OR ([Status] IN (2, 3) AND [ConfirmedByUserId] IS NOT NULL AND [ConfirmedAtUtc] IS NOT NULL)"
                + " OR [Status] = 9");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.CustomerReference).HasMaxLength(SalesOrder.InvoiceMaxLength);
        b.Property(x => x.Comment).HasMaxLength(SalesOrder.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Total);
        b.Ignore(x => x.VatTotal);
        b.Ignore(x => x.DiscountTotal);
        b.Property(x => x.Reserve).HasDefaultValue(false);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_orders_org_id");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_customer");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_warehouse");
        b.HasOne<SalesOrderStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_stage");
        b.HasIndex(x => new { x.OrganizationId, x.StageId }).HasDatabaseName("ix_sales_orders_org_stage");
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_legal_entity");
        // Счёт — только своего юрлица заказа: ключ (организация, юрлицо, счёт).
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId, x.BankAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.LegalEntityId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_bank_account");
        b.HasIndex(x => new { x.OrganizationId, x.LegalEntityId }).HasDatabaseName("ix_sales_orders_org_legal_entity");
        b.Property(x => x.OrderTime).HasColumnType("time(0)");
        b.Property(x => x.DeliveryAddress).HasMaxLength(SalesOrder.AddressMaxLength);
        b.HasOne<Lookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProjectId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_project");
        b.HasOne<Lookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ChannelId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_channel");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_orders_responsible");
        b.HasIndex(x => new { x.OrganizationId, x.ProjectId }).HasDatabaseName("ix_sales_orders_org_project");
        b.HasIndex(x => new { x.OrganizationId, x.ChannelId }).HasDatabaseName("ix_sales_orders_org_channel");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ConfirmedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_sales_orders_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.OrderDate }).HasDatabaseName("ix_sales_orders_org_customer_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class LookupConfiguration : IEntityTypeConfiguration<Lookup>
{
    public void Configure(EntityTypeBuilder<Lookup> b)
    {
        b.ToTable("lookups", t => t.HasCheckConstraint("ck_lookups_kind", "[Kind] IN (1, 2)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(Lookup.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_lookups_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_lookups_org_kind_name_active");
    }
}

internal sealed class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldDefinition> b)
    {
        b.ToTable("custom_field_definitions", t =>
        {
            t.HasCheckConstraint("ck_custom_field_definitions_target", "[Target] IN (1, 2)");
            t.HasCheckConstraint("ck_custom_field_definitions_type", "[Type] IN (1, 2, 3, 4, 5)");
            t.HasCheckConstraint("ck_custom_field_definitions_catalog", "([Type] = 5 AND [CatalogId] IS NOT NULL) OR ([Type] <> 5 AND [CatalogId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Target).HasConversion<byte>();
        b.Property(x => x.Type).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(CustomFieldDefinition.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_custom_field_definitions_org_id");
        b.HasOne<UserCatalog>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CatalogId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_custom_field_definitions_catalog");
        b.HasIndex(x => new { x.OrganizationId, x.Target, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_custom_field_definitions_org_target_name_active");
    }
}

internal sealed class CustomFieldValueConfiguration : IEntityTypeConfiguration<CustomFieldValue>
{
    public void Configure(EntityTypeBuilder<CustomFieldValue> b)
    {
        b.ToTable("custom_field_values");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Value).HasMaxLength(CustomFieldDefinition.ValueMaxLength).IsRequired();
        b.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.FieldId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_custom_field_values_field");
        b.HasIndex(x => new { x.FieldId, x.TargetId }).IsUnique().HasDatabaseName("ux_custom_field_values_field_target");
        b.HasIndex(x => new { x.OrganizationId, x.TargetId }).HasDatabaseName("ix_custom_field_values_org_target");
    }
}

/// <summary>D110: правила автоматической смены этапа — одно на событие в организации, этап — своей организации.</summary>
internal sealed class SalesStageRuleConfiguration : IEntityTypeConfiguration<SalesStageRule>
{
    public void Configure(EntityTypeBuilder<SalesStageRule> b)
    {
        b.ToTable("sales_stage_rules", t => t.HasCheckConstraint("ck_sales_stage_rules_trigger", "[Trigger] BETWEEN 1 AND 8"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Trigger).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SalesOrderStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StageId })
            .HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Trigger }).IsUnique().HasDatabaseName("ux_sales_stage_rules_org_trigger");
    }
}

internal sealed class SalesOrderStageConfiguration : IEntityTypeConfiguration<SalesOrderStage>
{
    public void Configure(EntityTypeBuilder<SalesOrderStage> b)
    {
        b.ToTable("sales_order_stages", t =>
            t.HasCheckConstraint("ck_sales_order_stages_color",
                "[Color] IN ('gray', 'blue', 'teal', 'green', 'yellow', 'orange', 'red', 'magenta', 'violet')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(SalesOrderStage.NameMaxLength).IsRequired();
        b.Property(x => x.Color).HasColumnType("varchar(16)").IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_order_stages_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_sales_order_stages_org_name_active");
    }
}

internal sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> b)
    {
        b.ToTable("sales_order_lines", t =>
        {
            t.HasCheckConstraint("ck_sales_order_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_sales_order_lines_price", "[Price] >= 0 AND [Amount] >= 0 AND [VatAmount] >= 0");
            t.HasCheckConstraint("ck_sales_order_lines_vat", "[VatPercent] IS NULL OR [VatPercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_sales_order_lines_discount", "[DiscountPercent] BETWEEN 0 AND 100");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Price).HasColumnType("decimal(19,4)");
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatAmount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.DiscountPercent).HasColumnType("decimal(5,2)").HasDefaultValue(0m);
        b.Ignore(x => x.NetPrice);
        b.Ignore(x => x.DiscountAmount);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrderId, x.ItemId }).IsUnique().HasDatabaseName("ux_sales_order_lines_order_item");
    }
}

internal sealed class CustomerPaymentConfiguration : IEntityTypeConfiguration<CustomerPayment>
{
    public void Configure(EntityTypeBuilder<CustomerPayment> b)
    {
        b.ToTable("customer_payments", t =>
        {
            t.HasCheckConstraint("ck_customer_payments_status", "[Status] IN (2, 9)");
            t.HasCheckConstraint("ck_customer_payments_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_customer_payments_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
        });
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MoneyAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_payments_money_account");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Comment).HasMaxLength(CustomerPayment.CommentMaxLength);
        b.Property(x => x.DocumentNumber).HasMaxLength(CustomerPayment.DocumentNumberMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(CustomerPayment.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_payments_customer");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_payments_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_customer_payments_org_number");
        b.Property(x => x.CashOrderNumber).HasMaxLength(30);
        b.HasIndex(x => new { x.OrganizationId, x.CashOrderNumber }).IsUnique().HasFilter("[CashOrderNumber] IS NOT NULL")
            .HasDatabaseName("ux_customer_payments_org_cash_order");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.PaymentDate }).HasDatabaseName("ix_customer_payments_org_customer_date");
    }
}

internal sealed class CustomerInvoiceConfiguration : IEntityTypeConfiguration<CustomerInvoice>
{
    public void Configure(EntityTypeBuilder<CustomerInvoice> b)
    {
        b.ToTable("customer_invoices", t =>
        {
            t.HasCheckConstraint("ck_customer_invoices_status", "[Status] IN (2, 9)");
            t.HasCheckConstraint("ck_customer_invoices_due", "[DueDate] IS NULL OR [DueDate] >= [InvoiceDate]");
            t.HasCheckConstraint("ck_customer_invoices_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(CustomerInvoice.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(CustomerInvoice.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Total);
        b.Ignore(x => x.VatTotal);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_invoices_customer");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_invoices_order");
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_invoices_legal_entity");
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId, x.BankAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.LegalEntityId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_invoices_bank_account");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_customer_invoices_org_number");
        // У заказа один действующий счёт (D68): другой — после отмены прежнего.
        b.HasIndex(x => new { x.OrganizationId, x.SalesOrderId }).IsUnique().HasFilter("[Status] = 2")
            .HasDatabaseName("ux_customer_invoices_order_issued");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.InvoiceDate }).HasDatabaseName("ix_customer_invoices_org_customer_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class CustomerInvoiceLineConfiguration : IEntityTypeConfiguration<CustomerInvoiceLine>
{
    public void Configure(EntityTypeBuilder<CustomerInvoiceLine> b)
    {
        b.ToTable("customer_invoice_lines", t =>
        {
            t.HasCheckConstraint("ck_customer_invoice_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_customer_invoice_lines_price", "[Price] >= 0 AND [Amount] >= 0 AND [VatAmount] >= 0");
            t.HasCheckConstraint("ck_customer_invoice_lines_vat", "[VatPercent] IS NULL OR [VatPercent] BETWEEN 0 AND 100");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Price).HasColumnType("decimal(19,4)");
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatAmount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatPercent).HasColumnType("decimal(5,2)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.InvoiceId, x.ItemId }).IsUnique().HasDatabaseName("ux_customer_invoice_lines_invoice_item");
    }
}

internal sealed class ReceivedVatInvoiceConfiguration : IEntityTypeConfiguration<ReceivedVatInvoice>
{
    public void Configure(EntityTypeBuilder<ReceivedVatInvoice> b)
    {
        b.ToTable("received_vat_invoices", t =>
        {
            t.HasCheckConstraint("ck_received_vat_invoices_status", "[Status] IN (2, 9)");
            t.HasCheckConstraint("ck_received_vat_invoices_amount", "[Amount] > 0 AND [VatAmount] >= 0 AND [VatAmount] < [Amount]");
            t.HasCheckConstraint("ck_received_vat_invoices_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.SupplierNumber).HasMaxLength(ReceivedVatInvoice.NumberMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.VatAmount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Comment).HasMaxLength(ReceivedVatInvoice.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(ReceivedVatInvoice.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.AmountWithoutVat);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_received_vat_invoices_supplier");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_received_vat_invoices_receipt");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        // К приёмке — один действующий счёт-фактура; один и тот же документ поставщика не регистрируется дважды.
        b.HasIndex(x => new { x.OrganizationId, x.ReceiptDocumentId }).IsUnique().HasFilter("[Status] = 2")
            .HasDatabaseName("ux_received_vat_invoices_receipt_registered");
        b.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.SupplierNumber, x.InvoiceDate }).IsUnique().HasFilter("[Status] = 2")
            .HasDatabaseName("ux_received_vat_invoices_supplier_number");
        b.HasIndex(x => new { x.OrganizationId, x.InvoiceDate }).HasDatabaseName("ix_received_vat_invoices_org_date");
    }
}

internal sealed class StockDocumentConfiguration : IEntityTypeConfiguration<StockDocument>
{
    public void Configure(EntityTypeBuilder<StockDocument> b)
    {
        b.ToTable("stock_documents", t =>
        {
            t.HasCheckConstraint("ck_stock_documents_kind", "[Kind] IN (1, 2, 3, 5, 6, 7)");
            t.HasCheckConstraint("ck_stock_documents_status", "[Status] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_stock_documents_target",
                "([Kind] = 3 AND [TargetWarehouseId] IS NOT NULL AND [TargetWarehouseId] <> [WarehouseId])"
                + " OR ([Kind] <> 3 AND [TargetWarehouseId] IS NULL)");
            t.HasCheckConstraint("ck_stock_documents_counterparty",
                "([Kind] = 1) OR ([Kind] IN (5, 6, 7) AND [CounterpartyId] IS NOT NULL) OR ([Kind] NOT IN (1, 5, 6, 7) AND [CounterpartyId] IS NULL)");
            t.HasCheckConstraint("ck_stock_documents_sales_order", "[SalesOrderId] IS NULL OR [Kind] IN (6, 7)");
            t.HasCheckConstraint("ck_stock_documents_purchase_order", "[PurchaseOrderId] IS NULL OR [Kind] IN (1, 5)");
            t.HasCheckConstraint("ck_stock_documents_posted",
                "([Status] IN (2, 3) AND [PostedByUserId] IS NOT NULL AND [PostedAtUtc] IS NOT NULL AND [ReasonId] IS NOT NULL)"
                + " OR ([Status] NOT IN (2, 3) AND [PostedByUserId] IS NULL)");
            t.HasCheckConstraint("ck_stock_documents_reversed",
                "([Status] = 3 AND [ReversedByUserId] IS NOT NULL AND [ReversedAtUtc] IS NOT NULL AND [ReversalReason] IS NOT NULL)"
                + " OR ([Status] <> 3 AND [ReversedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(StockDocument.CommentMaxLength);
        b.Property(x => x.ReversalReason).HasMaxLength(StockDocument.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_stock_documents_org_id");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_warehouse");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TargetWarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_target_warehouse");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_counterparty");
        b.HasOne<OperationReason>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReasonId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_reason");
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_purchase_order");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_documents_sales_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReversedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_stock_documents_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.DocumentDate }).HasDatabaseName("ix_stock_documents_org_kind_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class StockDocumentLineConfiguration : IEntityTypeConfiguration<StockDocumentLine>
{
    public void Configure(EntityTypeBuilder<StockDocumentLine> b)
    {
        b.ToTable("stock_document_lines", t =>
        {
            t.HasCheckConstraint("ck_stock_document_lines_quantity", "[Quantity] > 0");
            t.HasTrigger("tr_stock_document_lines_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DocumentId, x.ItemId }).IsUnique().HasDatabaseName("ux_stock_document_lines_doc_item");
    }
}

internal sealed class StockBatchConfiguration : IEntityTypeConfiguration<StockBatch>
{
    public void Configure(EntityTypeBuilder<StockBatch> b)
    {
        b.ToTable("stock_batches", t =>
        {
            t.HasCheckConstraint("ck_stock_batches_package_weight", "[PackageWeightKg] IS NULL OR [PackageWeightKg] > 0");
            t.HasTrigger("tr_stock_batches_stocked_items");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(StockBatch.NumberMaxLength).IsRequired();
        b.Property(x => x.Color).HasMaxLength(StockBatch.ColorMaxLength);
        b.Property(x => x.DyeLot).HasMaxLength(StockBatch.DyeLotMaxLength);
        b.Property(x => x.Comment).HasMaxLength(StockBatch.CommentMaxLength);
        b.Property(x => x.PackageWeightKg).HasColumnType("decimal(18,6)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_stock_batches_org_id");
        b.HasAlternateKey(x => new { x.OrganizationId, x.ItemId, x.Id }).HasName("ak_stock_batches_org_item_id");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_batches_item");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_batches_supplier");
        b.HasIndex(x => new { x.OrganizationId, x.ItemId, x.Number }).IsUnique().HasDatabaseName("ux_stock_batches_org_item_number");
    }
}

internal sealed class StockDocumentBatchLineConfiguration : IEntityTypeConfiguration<StockDocumentBatchLine>
{
    public void Configure(EntityTypeBuilder<StockDocumentBatchLine> b)
    {
        b.ToTable("stock_document_batch_lines", t =>
        {
            t.HasCheckConstraint("ck_stock_document_batch_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_stock_document_batch_lines_packages", "[Packages] IS NULL OR [Packages] > 0");
            t.HasCheckConstraint("ck_stock_document_batch_lines_gross", "[GrossWeightKg] IS NULL OR [GrossWeightKg] > 0");
            t.HasTrigger("tr_stock_document_batch_lines_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.GrossWeightKg).HasColumnType("decimal(18,6)");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_document_batch_lines_document");
        // Партия той же позиции, что и строка: составной ключ (организация, позиция, партия).
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId, x.BatchId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.ItemId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_document_batch_lines_batch");
        b.HasIndex(x => new { x.DocumentId, x.ItemId, x.BatchId }).IsUnique().HasDatabaseName("ux_stock_document_batch_lines_doc_item_batch");
    }
}

internal sealed class OpeningBalanceBatchLineConfiguration : IEntityTypeConfiguration<OpeningBalanceBatchLine>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceBatchLine> b)
    {
        b.ToTable("opening_balance_batch_lines", t =>
        {
            t.HasCheckConstraint("ck_opening_balance_batch_lines_quantity", "[Quantity] > 0");
            t.HasTrigger("tr_opening_balance_batch_lines_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.HasOne<OpeningBalance>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_opening_balance_batch_lines_document");
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId, x.BatchId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.ItemId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_opening_balance_batch_lines_batch");
        b.HasIndex(x => new { x.DocumentId, x.ItemId, x.BatchId }).IsUnique().HasDatabaseName("ux_opening_balance_batch_lines_doc_item_batch");
    }
}

internal sealed class InventoryBatchCountConfiguration : IEntityTypeConfiguration<InventoryBatchCount>
{
    public void Configure(EntityTypeBuilder<InventoryBatchCount> b)
    {
        b.ToTable("inventory_batch_counts", t =>
        {
            t.HasCheckConstraint("ck_inventory_batch_counts_quantity", "[CountedQuantity] >= 0");
            t.HasTrigger("tr_inventory_batch_counts_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.CountedQuantity).HasColumnType("decimal(18,6)");
        b.HasOne<InventoryCount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_inventory_batch_counts_document");
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId, x.BatchId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.ItemId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_inventory_batch_counts_batch");
        b.HasIndex(x => new { x.DocumentId, x.ItemId, x.BatchId }).IsUnique().HasDatabaseName("ux_inventory_batch_counts_doc_item_batch");
    }
}

internal sealed class DiscussionCommentConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Collaboration.DiscussionComment>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Collaboration.DiscussionComment> b)
    {
        b.ToTable("discussion_comments", t =>
        {
            // Диапазоны (D202): 1–10 — общие объекты, 11–19 — производство, 20–29 — модуль «Сотрудники». Новое значение внутри своего
            // диапазона не требует миграции — модули не переписывают ограничение друг друга.
            t.HasCheckConstraint("ck_discussion_comments_target", "[Target] BETWEEN 1 AND 29 OR [Target] BETWEEN 40 AND 49");
            t.HasCheckConstraint("ck_discussion_comments_deleted",
                "([DeletedAtUtc] IS NULL AND [DeletedByUserId] IS NULL AND LEN([Text]) > 0) OR ([DeletedAtUtc] IS NOT NULL AND [DeletedByUserId] IS NOT NULL AND [Text] = N'')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Target).HasConversion<byte>();
        b.Property(x => x.Text).HasMaxLength(KnitErp.Domain.Collaboration.DiscussionComment.TextMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsDeleted);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DeletedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Target, x.TargetId, x.Id }).HasDatabaseName("ix_discussion_comments_target");
        b.HasIndex(x => new { x.OrganizationId, x.Id }).HasDatabaseName("ix_discussion_comments_org_recent");
    }
}

internal sealed class DiscussionMentionConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Collaboration.DiscussionMention>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Collaboration.DiscussionMention> b)
    {
        b.ToTable("discussion_mentions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasOne<KnitErp.Domain.Collaboration.DiscussionComment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CommentId, x.UserId }).IsUnique().HasDatabaseName("ux_discussion_mentions_comment_user");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_discussion_mentions_user");
    }
}

internal sealed class TagConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Tagging.Tag>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Tagging.Tag> b)
    {
        b.ToTable("tags", t => t.HasCheckConstraint("ck_tags_color", "[Color] BETWEEN 0 AND 7"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(KnitErp.Domain.Tagging.Tag.NameMaxLength).IsRequired();
        b.Property(x => x.Color).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_tags_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasDatabaseName("ux_tags_org_name");
    }
}

internal sealed class TagLinkConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Tagging.TagLink>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Tagging.TagLink> b)
    {
        b.ToTable("tag_links", t => t.HasCheckConstraint("ck_tag_links_entity", "[Entity] BETWEEN 1 AND 29 OR [Entity] BETWEEN 40 AND 49"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Entity).HasConversion<byte>();
        b.HasOne<KnitErp.Domain.Tagging.Tag>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TagId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_tag_links_tag");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TagId, x.Entity, x.EntityId }).IsUnique().HasDatabaseName("ux_tag_links_tag_entity");
        b.HasIndex(x => new { x.OrganizationId, x.Entity, x.EntityId }).HasDatabaseName("ix_tag_links_org_entity");
    }
}

internal sealed class StockBatchMovementConfiguration : IEntityTypeConfiguration<StockBatchMovement>
{
    public void Configure(EntityTypeBuilder<StockBatchMovement> b)
    {
        b.ToTable("stock_batch_movements", t =>
        {
            t.HasTrigger("tr_stock_batch_movements_immutable");
            t.HasCheckConstraint("ck_stock_batch_movements_quantity", "[Quantity] <> 0");
            t.HasCheckConstraint("ck_stock_batch_movements_source", "[Source] IN (1, 2, 3, 4)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Source).HasConversion<byte>();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_batch_movements_warehouse");
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId, x.BatchId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.ItemId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_batch_movements_batch");
        b.HasIndex(x => new { x.OrganizationId, x.WarehouseId, x.ItemId, x.BatchId }).HasDatabaseName("ix_stock_batch_movements_org_wh_item_batch");
        b.HasIndex(x => new { x.OrganizationId, x.BatchId }).HasDatabaseName("ix_stock_batch_movements_org_batch");
        b.HasIndex(x => new { x.Source, x.SourceId }).HasDatabaseName("ix_stock_batch_movements_source");
    }
}

internal sealed class InventoryCountConfiguration : IEntityTypeConfiguration<InventoryCount>
{
    public void Configure(EntityTypeBuilder<InventoryCount> b)
    {
        b.ToTable("inventory_counts", t =>
        {
            t.HasCheckConstraint("ck_inventory_counts_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_inventory_counts_posted",
                "([Status] = 2 AND [PostedByUserId] IS NOT NULL AND [PostedAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [PostedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(InventoryCount.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_inventory_counts_org_id");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_inventory_counts_warehouse");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_inventory_counts_org_number");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class InventoryLineConfiguration : IEntityTypeConfiguration<InventoryLine>
{
    public void Configure(EntityTypeBuilder<InventoryLine> b)
    {
        b.ToTable("inventory_lines", t =>
        {
            t.HasCheckConstraint("ck_inventory_lines_counted", "[CountedQuantity] IS NULL OR [CountedQuantity] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.BookQuantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.CountedQuantity).HasColumnType("decimal(18,6)");
        b.Ignore(x => x.Difference);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DocumentId, x.ItemId }).IsUnique().HasDatabaseName("ux_inventory_lines_doc_item");
    }
}

internal sealed class PeriodClosureConfiguration : IEntityTypeConfiguration<PeriodClosure>
{
    public void Configure(EntityTypeBuilder<PeriodClosure> b)
    {
        b.ToTable("period_closures");
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithOne().HasForeignKey<PeriodClosure>(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserToolDataConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Workspace.UserToolData>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Workspace.UserToolData> b)
    {
        b.ToTable("user_tool_data", t => t.HasCheckConstraint("ck_user_tool_data_size", "LEN([Json]) <= 200000"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasMaxLength(KnitErp.Domain.Workspace.UserToolData.KindMaxLength).IsRequired();
        b.Property(x => x.Json).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.UserId, x.Kind }).IsUnique().HasDatabaseName("ux_user_tool_data_org_user_kind");
    }
}

internal sealed class SupportTicketConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Workspace.SupportTicket>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Workspace.SupportTicket> b)
    {
        b.ToTable("support_tickets", t =>
        {
            t.HasCheckConstraint("ck_support_tickets_status", "[Status] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_support_tickets_answer", "[Status] <> 3 OR ([Answer] IS NOT NULL AND [AnsweredByUserId] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(KnitErp.Domain.Workspace.SupportTicket.SubjectMaxLength).IsRequired();
        b.Property(x => x.Text).HasMaxLength(KnitErp.Domain.Workspace.SupportTicket.TextMaxLength).IsRequired();
        b.Property(x => x.Section).HasMaxLength(KnitErp.Domain.Workspace.SupportTicket.SectionMaxLength);
        b.Property(x => x.Answer).HasMaxLength(KnitErp.Domain.Workspace.SupportTicket.TextMaxLength);
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AnsweredByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_support_tickets_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.AuthorUserId, x.Status }).HasDatabaseName("ix_support_tickets_org_author_status");
    }
}

internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> b) =>
        b.ToTable("data_protection_keys");
}

/// <summary>Разноски платежей по заказам (D85): заказ и платёж — той же организации; снятая разноска остаётся в истории.</summary>
internal static class PaymentAllocationMapping
{
    public static void Map<T>(EntityTypeBuilder<T> b, string table)
        where T : KnitErp.Domain.Finance.PaymentAllocation
    {
        b.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_amount", "[Amount] > 0");
            t.HasCheckConstraint($"ck_{table}_removed",
                "([RemovedAtUtc] IS NULL AND [RemovedByUserId] IS NULL) OR ([RemovedAtUtc] IS NOT NULL AND [RemovedByUserId] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsActive);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RemovedByUserId).OnDelete(DeleteBehavior.Restrict);

        // Действующая разноска одного платежа по одному заказу — одна: добавка суммы заменяет её новой.
        b.HasIndex(x => new { x.PaymentId, x.OrderId }).IsUnique().HasFilter("[RemovedAtUtc] IS NULL").HasDatabaseName($"ux_{table}_active");
        b.HasIndex(x => new { x.OrganizationId, x.OrderId }).HasDatabaseName($"ix_{table}_org_order");
    }
}

internal sealed class CustomerPaymentAllocationConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.CustomerPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.CustomerPaymentAllocation> b)
    {
        PaymentAllocationMapping.Map(b, "customer_payment_allocations");
        b.HasOne<CustomerPayment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_payment_allocations_payment");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_payment_allocations_order");
    }
}

internal sealed class SupplierPaymentAllocationConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.SupplierPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.SupplierPaymentAllocation> b)
    {
        PaymentAllocationMapping.Map(b, "supplier_payment_allocations");
        b.HasOne<SupplierPayment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_payment_allocations_payment");
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_payment_allocations_order");
    }
}

internal sealed class CashFlowItemConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.CashFlowItem>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.CashFlowItem> b)
    {
        b.ToTable("cash_flow_items", t =>
        {
            t.HasCheckConstraint("ck_cash_flow_items_direction", "[Direction] IN (1, 2)");
            t.HasCheckConstraint("ck_cash_flow_items_system_active", "[SystemCode] IS NULL OR [IsArchived] = 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Direction).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(KnitErp.Domain.Finance.CashFlowItem.NameMaxLength).IsRequired();
        b.Property(x => x.SystemCode).HasMaxLength(40);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsSystem);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Direction, x.Name }).IsUnique().HasDatabaseName("ux_cash_flow_items_org_direction_name");
        b.HasIndex(x => new { x.OrganizationId, x.SystemCode }).IsUnique().HasFilter("[SystemCode] IS NOT NULL")
            .HasDatabaseName("ux_cash_flow_items_org_system");
    }
}

internal sealed class ExpenseReportConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.ExpenseReport>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.ExpenseReport> b)
    {
        b.ToTable("expense_reports", t =>
        {
            t.HasCheckConstraint("ck_expense_reports_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_expense_reports_approved",
                "([Status] = 1 AND [ApprovedAtUtc] IS NULL) OR ([Status] = 2 AND [ApprovedAtUtc] IS NOT NULL) OR [Status] = 9");
            t.HasCheckConstraint("ck_expense_reports_cancel",
                "([Status] = 9 AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] <> 9 AND [CancelledAtUtc] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Purpose).HasMaxLength(KnitErp.Domain.Finance.ExpenseReport.PurposeMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(KnitErp.Domain.Finance.ExpenseReport.ReasonMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Total);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_expense_reports_employee");
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_expense_reports_legal_entity");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_expense_reports_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.ReportDate }).HasDatabaseName("ix_expense_reports_org_employee_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ExpenseReportId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class ExpenseReportLineConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Finance.ExpenseReportLine>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Finance.ExpenseReportLine> b)
    {
        b.ToTable("expense_report_lines", t =>
        {
            t.HasCheckConstraint("ck_expense_report_lines_amount", "[Amount] > 0");
            t.HasTrigger("tr_expense_report_lines_draft_only");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Document).HasMaxLength(KnitErp.Domain.Finance.ExpenseReportLine.DocumentMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(KnitErp.Domain.Finance.ExpenseReportLine.DescriptionMaxLength);
        b.HasOne<KnitErp.Domain.Finance.CashFlowItem>().WithMany().HasForeignKey(x => x.CashFlowItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApiTokenConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Integrations.ApiToken>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Integrations.ApiToken> b)
    {
        b.ToTable("api_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(KnitErp.Domain.Integrations.ApiToken.NameMaxLength).IsRequired();
        b.Property(x => x.Prefix).HasMaxLength(KnitErp.Domain.Integrations.ApiToken.PrefixLength).IsRequired();
        b.Property(x => x.TokenHash).HasColumnType("binary(32)").IsRequired();
        b.Property(x => x.Permissions).HasMaxLength(2000).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.PermissionList);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_api_tokens_hash");
        b.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_api_tokens_org");
    }
}

internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Integrations.WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Integrations.WebhookSubscription> b)
    {
        b.ToTable("webhook_subscriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Url).HasMaxLength(KnitErp.Domain.Integrations.WebhookSubscription.UrlMaxLength).IsRequired();
        b.Property(x => x.Events).HasMaxLength(1000).IsRequired();

        // Секрет подписи нужен для отправки — хранится зашифрованным мастер-ключом (AES-256-GCM), как секреты 2FA.
        b.Property(x => x.Secret).HasMaxLength(400).IsRequired()
            .HasConversion(v => KnitErp.Infrastructure.Security.SecretCipher.Encrypt(v), v => KnitErp.Infrastructure.Security.SecretCipher.Decrypt(v));
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.EventList);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_webhook_subscriptions_org_id");
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Integrations.WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Integrations.WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries", t => t.HasCheckConstraint("ck_webhook_deliveries_status", "[Status] IN (1, 2, 9)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.EventType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Payload).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.LastError).HasMaxLength(KnitErp.Domain.Integrations.WebhookDelivery.ErrorMaxLength);
        b.HasOne<KnitErp.Domain.Integrations.WebhookSubscription>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SubscriptionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_webhook_deliveries_subscription");
        b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc }).HasDatabaseName("ix_webhook_deliveries_due");
        b.HasIndex(x => new { x.SubscriptionId, x.Id }).HasDatabaseName("ix_webhook_deliveries_subscription");
        b.HasIndex(x => new { x.SubscriptionId, x.EventId }).IsUnique().HasDatabaseName("ux_webhook_deliveries_event");
    }
}

internal sealed class ApiIdempotencyRecordConfiguration : IEntityTypeConfiguration<KnitErp.Domain.Integrations.ApiIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<KnitErp.Domain.Integrations.ApiIdempotencyRecord> b)
    {
        b.ToTable("api_idempotency");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Key).HasMaxLength(KnitErp.Domain.Integrations.ApiIdempotencyRecord.KeyMaxLength).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResponseBody).IsRequired();
        b.HasOne<KnitErp.Domain.Integrations.ApiToken>().WithMany().HasForeignKey(x => x.TokenId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TokenId, x.Key }).IsUnique().HasDatabaseName("ux_api_idempotency_token_key");
        b.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("ix_api_idempotency_created");
    }
}
