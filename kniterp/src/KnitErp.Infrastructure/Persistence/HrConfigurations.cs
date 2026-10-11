using KnitErp.Domain.Hr;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Structure;
using KnitErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Шифрование персональных данных в колонках (D200): AES-256-GCM мастер-ключом, как секреты 2FA и интеграций.</summary>
internal static class PersonalDataColumns
{
    public static readonly ValueConverter<string?, string?> Text = new(
        v => v == null ? null : SecretCipher.Encrypt(v),
        v => v == null ? null : SecretCipher.Decrypt(v));

    public static readonly ValueConverter<DateOnly?, string?> Date = new(
        v => v == null ? null : SecretCipher.Encrypt(v.Value.ToString("yyyy-MM-dd")),
        v => v == null ? null : DateOnly.ParseExact(SecretCipher.Decrypt(v), "yyyy-MM-dd"));

    public static readonly ValueConverter<byte[], string> Bytes = new(
        v => SecretCipher.Encrypt(Convert.ToBase64String(v)),
        v => Convert.FromBase64String(SecretCipher.Decrypt(v)));

    /// <summary>Длина колонки под шифртекст: значение в UTF-8 (кириллица — 2 байта) + nonce и тег, в Base64, с префиксом.</summary>
    public static int Cipher(int plainMaxLength) => (plainMaxLength * 2 + 28) * 4 / 3 + 40;
}

internal sealed class EmployeePersonalDataConfiguration : IEntityTypeConfiguration<EmployeePersonalData>
{
    public void Configure(EntityTypeBuilder<EmployeePersonalData> b)
    {
        b.ToTable("hr_personal_data", t =>
        {
            t.HasCheckConstraint("ck_hr_personal_data_gender", "[Gender] IN (0, 1, 2)");
            t.HasCheckConstraint("ck_hr_personal_data_consent",
                "([ConsentGivenOn] IS NOT NULL OR ([ConsentValidUntil] IS NULL AND [ConsentRevokedOn] IS NULL))"
                + " AND ([ConsentValidUntil] IS NULL OR [ConsentValidUntil] >= [ConsentGivenOn])"
                + " AND ([ConsentRevokedOn] IS NULL OR [ConsentRevokedOn] >= [ConsentGivenOn])");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Gender).HasConversion<byte>();
        Encrypted(b, x => x.BirthPlace, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Citizenship, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.PassportNumber, 20);
        Encrypted(b, x => x.PassportIssuer, EmployeePersonalData.LongMaxLength);
        b.Property(x => x.PassportIssuedOn).HasConversion(PersonalDataColumns.Date).HasMaxLength(PersonalDataColumns.Cipher(10));
        Encrypted(b, x => x.PassportDivisionCode, 7);
        Encrypted(b, x => x.Snils, 11);
        Encrypted(b, x => x.Inn, 12);
        Encrypted(b, x => x.RegistrationAddress, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.ActualAddress, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Phone, 20);
        Encrypted(b, x => x.Education, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Profession, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.MaritalStatus, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.FamilyMembers, EmployeePersonalData.LongMaxLength);
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_hr_personal_data_employee");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId }).IsUnique().HasDatabaseName("ux_hr_personal_data_employee");
    }

    private static void Encrypted(EntityTypeBuilder<EmployeePersonalData> b, System.Linq.Expressions.Expression<Func<EmployeePersonalData, string?>> property, int plainMax) =>
        b.Property(property).HasConversion(PersonalDataColumns.Text).HasMaxLength(PersonalDataColumns.Cipher(plainMax));
}

internal sealed class ConsentScanConfiguration : IEntityTypeConfiguration<ConsentScan>
{
    public void Configure(EntityTypeBuilder<ConsentScan> b)
    {
        b.ToTable("hr_consent_scans", t =>
        {
            t.HasCheckConstraint("ck_hr_consent_scans_size", $"[SizeBytes] BETWEEN 1 AND {ConsentScan.MaxBytes}");
            t.HasTrigger("tr_hr_consent_scans_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.Content).HasConversion(PersonalDataColumns.Bytes).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.UploadedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_hr_consent_scans_employee");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId }).HasDatabaseName("ix_hr_consent_scans_employee");
    }
}

internal sealed class StaffingPositionConfiguration : IEntityTypeConfiguration<StaffingPosition>
{
    public void Configure(EntityTypeBuilder<StaffingPosition> b)
    {
        b.ToTable("hr_staffing", t =>
        {
            t.HasCheckConstraint("ck_hr_staffing_rates", "[Rates] > 0");
            t.HasCheckConstraint("ck_hr_staffing_money", "[Salary] >= 0 AND [Allowance] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Rates).HasPrecision(6, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.Allowance).HasPrecision(19, 4);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_staffing_department");
        b.HasOne<Position>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PositionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_staffing_position");
        b.HasIndex(x => new { x.OrganizationId, x.DepartmentId, x.PositionId }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_hr_staffing_active");
    }
}

internal sealed class PersonnelOrderConfiguration : IEntityTypeConfiguration<PersonnelOrder>
{
    public void Configure(EntityTypeBuilder<PersonnelOrder> b)
    {
        b.ToTable("hr_orders", t =>
        {
            t.HasCheckConstraint("ck_hr_orders_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_hr_orders_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_hr_orders_posted", "([Status] = 2 AND [PostedAtUtc] IS NOT NULL AND [PostedByUserId] IS NOT NULL) OR ([Status] <> 2 AND [PostedAtUtc] IS NULL AND [PostedByUserId] IS NULL)");
            t.HasCheckConstraint("ck_hr_orders_fields",
                "([Kind] = 1 AND [PersonnelNumber] IS NOT NULL AND [LastName] IS NOT NULL AND [FirstName] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL"
                + " AND ([Status] <> 2 OR [EmployeeId] IS NOT NULL))"
                + " OR ([Kind] = 2 AND [EmployeeId] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL)"
                + " OR ([Kind] = 3 AND [EmployeeId] IS NOT NULL AND [LeaveKind] IS NOT NULL AND [EffectiveTo] >= [EffectiveFrom])"
                + " OR ([Kind] = 4 AND [EmployeeId] IS NOT NULL AND [DismissalGround] IS NOT NULL)");
            t.HasCheckConstraint("ck_hr_orders_rate", "[Rate] IS NULL OR ([Rate] > 0 AND [Rate] <= 1.5)");
            t.HasTrigger("tr_hr_orders_posted_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.LeaveKind).HasConversion<byte?>();
        b.Property(x => x.PersonnelNumber).HasMaxLength(20);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.MiddleName).HasMaxLength(100);
        b.Property(x => x.Rate).HasPrecision(4, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.DismissalGround).HasMaxLength(10);
        b.Property(x => x.Basis).HasMaxLength(PersonnelOrder.BasisMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.LeaveDays);
        b.Ignore(x => x.FullName);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_employee");
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_department");
        b.HasOne<Position>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PositionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_position");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_hr_orders_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_hr_orders_number");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Kind }).HasDatabaseName("ix_hr_orders_employee");
    }
}

internal sealed class EmploymentRecordConfiguration : IEntityTypeConfiguration<EmploymentRecord>
{
    public void Configure(EntityTypeBuilder<EmploymentRecord> b)
    {
        b.ToTable("hr_employment_history", t =>
        {
            t.HasCheckConstraint("ck_hr_employment_history_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_hr_employment_history_rate", "[Rate] > 0");
            t.HasTrigger("tr_hr_employment_history_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.LeaveKind).HasConversion<byte?>();
        b.Property(x => x.Rate).HasPrecision(4, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employment_history_employee");
        b.HasOne<PersonnelOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employment_history_order");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.EffectiveFrom }).HasDatabaseName("ix_hr_employment_history_employee");
    }
}
