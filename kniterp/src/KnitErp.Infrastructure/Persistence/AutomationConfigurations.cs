using KnitErp.Domain.Access;
using KnitErp.Domain.Automation;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>D145: настройки автоматизации — одна строка на организацию, границы дней в CHECK.</summary>
internal sealed class AutomationSettingsConfiguration : IEntityTypeConfiguration<AutomationSettings>
{
    public void Configure(EntityTypeBuilder<AutomationSettings> b)
    {
        b.ToTable("automation_settings", t =>
        {
            t.HasCheckConstraint("ck_automation_settings_remind_days", $"[RemindDaysBefore] BETWEEN 0 AND {AutomationSettings.MaxRemindDaysBefore}");
            t.HasCheckConstraint("ck_automation_settings_repeat_days", $"[OverdueRepeatDays] BETWEEN 0 AND {AutomationSettings.MaxOverdueRepeatDays}");
        });
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithOne().HasForeignKey<AutomationSettings>(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_automation_settings_updated_by");
    }
}

/// <summary>
/// D146: курсы валют. Курс ЦБ — без организации (один на валюту и дату), ручной — своей организации (один на организацию, валюту и дату);
/// источник согласован с наличием организации (CHECK).
/// </summary>
internal sealed class CurrencyRateConfiguration : IEntityTypeConfiguration<CurrencyRate>
{
    public void Configure(EntityTypeBuilder<CurrencyRate> b)
    {
        b.ToTable("currency_rates", t =>
        {
            t.HasCheckConstraint("ck_currency_rates_source", "([Source] = 1 AND [OrganizationId] IS NULL) OR ([Source] = 2 AND [OrganizationId] IS NOT NULL)");
            t.HasCheckConstraint("ck_currency_rates_value", "[Value] > 0 AND [Nominal] >= 1");
            t.HasCheckConstraint("ck_currency_rates_code", "LEN([CurrencyCode]) = 3");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.Value).HasColumnType("decimal(19,4)");
        b.Property(x => x.Source).HasConversion<byte>();
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.PerUnit);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_currency_rates_updated_by");
        b.HasIndex(x => new { x.CurrencyCode, x.RateDate }).IsUnique().HasFilter("[OrganizationId] IS NULL").HasDatabaseName("ux_currency_rates_cbr");
        b.HasIndex(x => new { x.OrganizationId, x.CurrencyCode, x.RateDate }).IsUnique().HasFilter("[OrganizationId] IS NOT NULL")
            .HasDatabaseName("ux_currency_rates_manual");
    }
}

/// <summary>D147: шаблоны регулярных операций — счёт или касса и статья своей организации (составные FK).</summary>
internal sealed class RecurringOperationConfiguration : IEntityTypeConfiguration<RecurringOperation>
{
    public void Configure(EntityTypeBuilder<RecurringOperation> b)
    {
        b.ToTable("recurring_operations", t =>
        {
            t.HasCheckConstraint("ck_recurring_operations_kind", "[Kind] IN (1, 2)");
            t.HasCheckConstraint("ck_recurring_operations_period", "[Period] IN (1, 2, 3)");
            t.HasCheckConstraint("ck_recurring_operations_day", "([Period] = 2 AND [Day] BETWEEN 1 AND 7) OR ([Period] <> 2 AND [Day] BETWEEN 1 AND 31)");
            t.HasCheckConstraint("ck_recurring_operations_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_recurring_operations_end", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_recurring_operations_org_id");
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(RecurringOperation.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Period).HasConversion<byte>();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Party).HasMaxLength(MoneyOperation.PartyMaxLength);
        b.Property(x => x.Basis).HasMaxLength(MoneyOperation.BasisMaxLength).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntityAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.AccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recurring_operations_account");
        b.HasOne<CashFlowItem>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CashFlowItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recurring_operations_cash_flow_item");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_recurring_operations_created_by");
        b.HasIndex(x => new { x.OrganizationId, x.IsArchived, x.NextDate }).HasDatabaseName("ix_recurring_operations_next");
    }
}

/// <summary>D147: черновики регулярных операций — один на шаблон и дату; проведённый — с денежной операцией своей организации.</summary>
internal sealed class RecurringOperationRunConfiguration : IEntityTypeConfiguration<RecurringOperationRun>
{
    public void Configure(EntityTypeBuilder<RecurringOperationRun> b)
    {
        b.ToTable("recurring_operation_runs", t =>
        {
            t.HasCheckConstraint("ck_recurring_operation_runs_status",
                "([Status] = 1 AND [MoneyOperationId] IS NULL AND [DecidedAtUtc] IS NULL) " +
                "OR ([Status] = 2 AND [MoneyOperationId] IS NOT NULL AND [DecidedAtUtc] IS NOT NULL) " +
                "OR ([Status] = 3 AND [MoneyOperationId] IS NULL AND [DecidedAtUtc] IS NOT NULL AND [SkipReason] IS NOT NULL)");
            t.HasCheckConstraint("ck_recurring_operation_runs_amount", "[Amount] > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.SkipReason).HasMaxLength(RecurringOperationRun.ReasonMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.DecidedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RecurringOperation>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RecurringOperationId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recurring_operation_runs_template");
        b.HasOne<MoneyOperation>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MoneyOperationId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_recurring_operation_runs_money");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_recurring_operation_runs_decided_by");
        b.HasIndex(x => new { x.OrganizationId, x.RecurringOperationId, x.DueDate }).IsUnique().HasDatabaseName("ux_recurring_operation_runs_due");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("ix_recurring_operation_runs_status");
    }
}

/// <summary>D149: загрузки XML УПД поставщика — один файл (отпечаток) на организацию; заказ и поступление — своей организации.</summary>
internal sealed class SupplierUpdImportConfiguration : IEntityTypeConfiguration<SupplierUpdImport>
{
    public void Configure(EntityTypeBuilder<SupplierUpdImport> b)
    {
        b.ToTable("supplier_upd_imports", t => t.HasCheckConstraint("ck_supplier_upd_imports_hash", $"LEN([FileHash]) = {SupplierUpdImport.HashLength}"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FileHash).HasMaxLength(SupplierUpdImport.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(SupplierUpdImport.FileNameMaxLength).IsRequired();
        b.Property(x => x.SellerInn).HasMaxLength(12).IsUnicode(false).IsRequired();
        b.Property(x => x.InvoiceNumber).HasMaxLength(SupplierUpdImport.NumberMaxLength).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_upd_imports_order");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReceiptDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_supplier_upd_imports_receipt");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_supplier_upd_imports_created_by");
        b.HasIndex(x => new { x.OrganizationId, x.FileHash }).IsUnique().HasDatabaseName("ux_supplier_upd_imports_file");
    }
}
