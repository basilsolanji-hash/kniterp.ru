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
            t.HasCheckConstraint("ck_hr_consent_scans_size", $"[SizeBytes] BETWEEN 1 AND {ConsentScan.MaxBytes}"));
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
