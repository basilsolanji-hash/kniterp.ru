using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Договоры контрагентов (D500). Ограничения дублируют правила домена в SQL Server.</summary>
internal sealed class CounterpartyContractConfiguration : IEntityTypeConfiguration<CounterpartyContract>
{
    public void Configure(EntityTypeBuilder<CounterpartyContract> b)
    {
        b.ToTable("counterparty_contracts", t =>
        {
            t.HasCheckConstraint("ck_counterparty_contracts_kind", "[Kind] IN (1, 2, 3, 9)");
            t.HasCheckConstraint("ck_counterparty_contracts_status", "[Status] IN (1, 2)");
            t.HasCheckConstraint("ck_counterparty_contracts_period", "[ValidFrom] IS NULL OR [ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
            t.HasCheckConstraint("ck_counterparty_contracts_days", $"[PaymentDays] IS NULL OR [PaymentDays] BETWEEN 0 AND {CounterpartyContract.MaxPaymentDays}");
            t.HasCheckConstraint("ck_counterparty_contracts_limit", "[AmountLimit] IS NULL OR [AmountLimit] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(CounterpartyContract.NumberMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(CounterpartyContract.SubjectMaxLength);
        b.Property(x => x.Comment).HasMaxLength(CounterpartyContract.CommentMaxLength);
        b.Property(x => x.AmountLimit).HasPrecision(19, 4);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_counterparty_contracts_counterparty");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_counterparty_contracts_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.CounterpartyId, x.Number }).IsUnique()
            .HasDatabaseName("ux_counterparty_contracts_number");
    }
}

/// <summary>Сканы договоров: только вставка (триггер в миграции), содержимое зашифровано мастер-ключом, как сканы согласий на ПДн.</summary>
internal sealed class CounterpartyContractFileConfiguration : IEntityTypeConfiguration<CounterpartyContractFile>
{
    public void Configure(EntityTypeBuilder<CounterpartyContractFile> b)
    {
        b.ToTable("counterparty_contract_files", t =>
        {
            t.HasCheckConstraint("ck_counterparty_contract_files_size", $"[SizeBytes] BETWEEN 1 AND {CounterpartyContractFile.MaxBytes}");
            t.HasTrigger("tr_counterparty_contract_files_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FileName).HasMaxLength(CounterpartyContractFile.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.Content).HasConversion(PersonalDataColumns.Bytes).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.UploadedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CounterpartyContract>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ContractId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_counterparty_contract_files_contract");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.ContractId }).HasDatabaseName("ix_counterparty_contract_files_contract");
    }
}
