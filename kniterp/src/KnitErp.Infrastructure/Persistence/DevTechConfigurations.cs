using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Development;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Общие правила таблиц техдокументации разработки: внешний ключ на организацию и составной — на модель или деталь.</summary>
internal static class DevKeys
{
    public static void Org<T>(EntityTypeBuilder<T> b)
        where T : class
    {
        b.HasOne<Organization>().WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict);
    }

    public static void ToModel<T>(EntityTypeBuilder<T> b, string name)
        where T : class =>
        b.HasOne<DevModel>().WithMany().HasForeignKey("OrganizationId", "ModelId").HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(name);
}

/// <summary>Станки (D710).</summary>
internal sealed class DevMachineConfiguration : IEntityTypeConfiguration<DevMachine>
{
    public void Configure(EntityTypeBuilder<DevMachine> b)
    {
        b.ToTable("dev_machines", t =>
        {
            t.HasCheckConstraint("ck_dev_machines_brand", "[Brand] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_dev_machines_status", "[Status] BETWEEN 1 AND 3");
            t.HasCheckConstraint("ck_dev_machines_gauge", $"[Gauge] BETWEEN 1 AND {DevMachine.MaxGauge}");
            t.HasCheckConstraint("ck_dev_machines_feeders", $"[FeederCount] BETWEEN 1 AND {DevMachine.MaxFeeders}");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevMachine.NameMaxLength).IsRequired();
        b.Property(x => x.Model).HasMaxLength(DevMachine.ModelMaxLength);
        b.Property(x => x.InventoryNumber).HasMaxLength(DevMachine.InventoryMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Parameters);
        DevKeys.Org(b);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_machines_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasDatabaseName("ux_dev_machines_name");
    }
}

/// <summary>Журнал станка: только вставка (триггер в миграции).</summary>
internal sealed class DevMachineNoteConfiguration : IEntityTypeConfiguration<DevMachineNote>
{
    public void Configure(EntityTypeBuilder<DevMachineNote> b)
    {
        b.ToTable("dev_machine_notes", t =>
        {
            t.HasCheckConstraint("ck_dev_machine_notes_status", "[StatusAfter] IS NULL OR [StatusAfter] BETWEEN 1 AND 3");
            t.HasTrigger("tr_dev_machine_notes_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Text).HasMaxLength(DevMachineNote.TextMaxLength).IsRequired();
        DevKeys.Org(b);
        b.HasOne<DevMachine>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MachineId }).HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_machine_notes_machine");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.MachineId, x.Id }).HasDatabaseName("ix_dev_machine_notes_machine");
    }
}

/// <summary>Палитра модели (D712).</summary>
internal sealed class DevModelColorConfiguration : IEntityTypeConfiguration<DevModelColor>
{
    public void Configure(EntityTypeBuilder<DevModelColor> b)
    {
        b.ToTable("dev_model_colors");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Label).HasMaxLength(DevModelColor.LabelMaxLength).IsRequired();
        b.Property(x => x.Swatch).HasMaxLength(7);
        b.Property(x => x.Note).HasMaxLength(DevModelColor.NoteMaxLength);
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_model_colors_model");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId }).HasPrincipalKey(i => new { i.OrganizationId, i.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_model_colors_item");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_model_colors_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Label }).IsUnique().HasDatabaseName("ux_dev_model_colors_label");
    }
}

/// <summary>Детали модели (D711).</summary>
internal sealed class DevDetailConfiguration : IEntityTypeConfiguration<DevDetail>
{
    public void Configure(EntityTypeBuilder<DevDetail> b)
    {
        b.ToTable("dev_details", t =>
        {
            t.HasCheckConstraint("ck_dev_details_quantity", $"[Quantity] BETWEEN 1 AND {DevDetail.MaxQuantity}");
            t.HasCheckConstraint("ck_dev_details_plan", "([PlanKnitSeconds] IS NULL OR [PlanKnitSeconds] > 0) AND ([PlanWeightGrams] IS NULL OR [PlanWeightGrams] > 0)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevDetail.NameMaxLength).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(DevDetail.NotesMaxLength);
        b.Property(x => x.PlanWeightGrams).HasPrecision(18, 6);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_details_model");
        b.HasOne<DevMachine>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MachineId }).HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_details_machine");
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WeaveId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_details_weave");
        b.HasOne<DevFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProgramFileId }).HasPrincipalKey(f => new { f.OrganizationId, f.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_details_program");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_details_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.SortOrder }).HasDatabaseName("ix_dev_details_model");
    }
}

/// <summary>Заправка нитеводителей (D711): одна позиция на нитеводитель и сторону.</summary>
internal sealed class DevThreadingConfiguration : IEntityTypeConfiguration<DevThreading>
{
    public void Configure(EntityTypeBuilder<DevThreading> b)
    {
        b.ToTable("dev_threading", t =>
        {
            t.HasCheckConstraint("ck_dev_threading_feeder", $"[Feeder] BETWEEN 1 AND {DevMachine.MaxFeeders}");
            t.HasCheckConstraint("ck_dev_threading_side", "[Side] IN (1, 2)");
            t.HasCheckConstraint("ck_dev_threading_ends", $"[Ends] BETWEEN 1 AND {DevThreading.MaxEnds}");
            t.HasCheckConstraint("ck_dev_threading_yarn", "[ColorId] IS NOT NULL OR [Label] IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Label).HasMaxLength(DevModelColor.LabelMaxLength + 20);
        b.Property(x => x.Mode).HasMaxLength(DevThreading.ModeMaxLength);
        DevKeys.Org(b);
        b.HasOne<DevDetail>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DetailId }).HasPrincipalKey(d => new { d.OrganizationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_threading_detail");
        b.HasOne<DevModelColor>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ColorId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_threading_color");
        b.HasIndex(x => new { x.OrganizationId, x.DetailId, x.Feeder, x.Side }).IsUnique().HasDatabaseName("ux_dev_threading_feeder");
    }
}

/// <summary>Размерная сетка модели (D713).</summary>
internal sealed class DevModelSizeConfiguration : IEntityTypeConfiguration<DevModelSize>
{
    public void Configure(EntityTypeBuilder<DevModelSize> b)
    {
        b.ToTable("dev_model_sizes", t => t.HasCheckConstraint("ck_dev_model_sizes_box", "[PerBox] IS NULL OR [PerBox] > 0"));
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevModelSize.NameMaxLength).IsRequired();
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_model_sizes_model");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_model_sizes_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Name }).IsUnique().HasDatabaseName("ux_dev_model_sizes_name");
    }
}

internal sealed class DevDetailSizeConfiguration : IEntityTypeConfiguration<DevDetailSize>
{
    public void Configure(EntityTypeBuilder<DevDetailSize> b)
    {
        b.ToTable("dev_detail_sizes");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.LengthOffMachineCm).HasPrecision(18, 6);
        b.Property(x => x.LengthAfterSteamCm).HasPrecision(18, 6);
        b.Property(x => x.WidthCm).HasPrecision(18, 6);
        DevKeys.Org(b);
        b.HasOne<DevDetail>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DetailId }).HasPrincipalKey(d => new { d.OrganizationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_detail_sizes_detail");
        b.HasOne<DevModelSize>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SizeId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_detail_sizes_size");
        b.HasIndex(x => new { x.OrganizationId, x.DetailId, x.SizeId }).IsUnique().HasDatabaseName("ux_dev_detail_sizes");
    }
}

internal sealed class DevMeasureConfiguration : IEntityTypeConfiguration<DevMeasure>
{
    public void Configure(EntityTypeBuilder<DevMeasure> b)
    {
        b.ToTable("dev_measures", t => t.HasCheckConstraint("ck_dev_measures_tolerance", "[Tolerance] >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevMeasure.NameMaxLength).IsRequired();
        b.Property(x => x.Tolerance).HasPrecision(18, 6);
        b.Property(x => x.Step).HasPrecision(18, 6);
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_measures_model");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_measures_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Name }).IsUnique().HasDatabaseName("ux_dev_measures_name");
    }
}

internal sealed class DevMeasureValueConfiguration : IEntityTypeConfiguration<DevMeasureValue>
{
    public void Configure(EntityTypeBuilder<DevMeasureValue> b)
    {
        b.ToTable("dev_measure_values");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Value).HasPrecision(18, 6);
        DevKeys.Org(b);
        b.HasOne<DevMeasure>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MeasureId }).HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_measure_values_measure");
        b.HasOne<DevModelSize>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SizeId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_measure_values_size");
        b.HasIndex(x => new { x.OrganizationId, x.MeasureId, x.SizeId }).IsUnique().HasDatabaseName("ux_dev_measure_values");
    }
}

/// <summary>Образцы (D714).</summary>
internal sealed class DevSampleConfiguration : IEntityTypeConfiguration<DevSample>
{
    public void Configure(EntityTypeBuilder<DevSample> b)
    {
        b.ToTable("dev_samples", t => t.HasCheckConstraint("ck_dev_samples_status", "[Status] BETWEEN 1 AND 5"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Comment).HasMaxLength(DevSample.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_samples_model");
        b.HasOne<DevModelSize>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SizeId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_samples_size");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_samples_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.No }).IsUnique().HasDatabaseName("ux_dev_samples_no");
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAtUtc }).HasDatabaseName("ix_dev_samples_created");
        // D768: причина доработки или отказа — запись справочника разработки своей организации.
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReasonId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_samples_reason");
        // D721: черновик списания пряжи на образец — документ склада своей организации.
        b.HasOne<KnitErp.Domain.Warehousing.StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.YarnWriteOffDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_samples_yarn_writeoff");
    }
}

internal sealed class DevSampleDetailConfiguration : IEntityTypeConfiguration<DevSampleDetail>
{
    public void Configure(EntityTypeBuilder<DevSampleDetail> b)
    {
        b.ToTable("dev_sample_details", t =>
            t.HasCheckConstraint("ck_dev_sample_details_fact", "([KnitSeconds] IS NULL OR [KnitSeconds] > 0) AND ([WeightGrams] IS NULL OR [WeightGrams] > 0)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.WeightGrams).HasPrecision(18, 6);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        b.HasOne<DevSample>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SampleId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_details_sample");
        b.HasOne<DevDetail>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DetailId }).HasPrincipalKey(d => new { d.OrganizationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_details_detail");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_sample_details_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.SampleId, x.DetailId }).IsUnique().HasDatabaseName("ux_dev_sample_details");
    }
}

internal sealed class DevSampleColorShareConfiguration : IEntityTypeConfiguration<DevSampleColorShare>
{
    public void Configure(EntityTypeBuilder<DevSampleColorShare> b)
    {
        b.ToTable("dev_sample_color_shares", t => t.HasCheckConstraint("ck_dev_sample_color_shares_percent", "[Percent] BETWEEN 0 AND 100"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Percent).HasPrecision(18, 6);
        DevKeys.Org(b);
        b.HasOne<DevSampleDetail>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SampleDetailId })
            .HasPrincipalKey(d => new { d.OrganizationId, d.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_color_shares_fact");
        b.HasOne<DevModelColor>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ColorId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_color_shares_color");
        b.HasIndex(x => new { x.OrganizationId, x.SampleDetailId, x.ColorId }).IsUnique().HasDatabaseName("ux_dev_sample_color_shares");
    }
}

internal sealed class DevSampleMeasureConfiguration : IEntityTypeConfiguration<DevSampleMeasure>
{
    public void Configure(EntityTypeBuilder<DevSampleMeasure> b)
    {
        b.ToTable("dev_sample_measures");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Value).HasPrecision(18, 6);
        DevKeys.Org(b);
        b.HasOne<DevSample>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SampleId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_measures_sample");
        b.HasOne<DevMeasure>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MeasureId }).HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_sample_measures_measure");
        b.HasIndex(x => new { x.OrganizationId, x.SampleId, x.MeasureId }).IsUnique().HasDatabaseName("ux_dev_sample_measures");
    }
}

/// <summary>Операции технолога (D715).</summary>
internal sealed class DevOperationTypeConfiguration : IEntityTypeConfiguration<DevOperationType>
{
    public void Configure(EntityTypeBuilder<DevOperationType> b)
    {
        b.ToTable("dev_operation_types", t =>
        {
            t.HasCheckConstraint("ck_dev_operation_types_stage", "[ProductionStage] IS NULL OR [ProductionStage] BETWEEN 1 AND 5");
            t.HasCheckConstraint("ck_dev_operation_types_values", "([Price] IS NULL OR [Price] >= 0) AND ([NormMinutes] IS NULL OR [NormMinutes] > 0)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevOperationType.NameMaxLength).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(DevOperationType.UnitMaxLength).IsRequired();
        b.Property(x => x.Price).HasPrecision(19, 4);
        b.Property(x => x.NormMinutes).HasPrecision(18, 6);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_operation_types_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasDatabaseName("ux_dev_operation_types_name");
    }
}

internal sealed class DevModelOperationConfiguration : IEntityTypeConfiguration<DevModelOperation>
{
    public void Configure(EntityTypeBuilder<DevModelOperation> b)
    {
        b.ToTable("dev_model_operations", t =>
            t.HasCheckConstraint("ck_dev_model_operations_values", "([Price] IS NULL OR [Price] >= 0) AND ([NormMinutes] IS NULL OR [NormMinutes] > 0)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Note).HasMaxLength(DevModelOperation.NoteMaxLength);
        b.Property(x => x.Price).HasPrecision(19, 4);
        b.Property(x => x.NormMinutes).HasPrecision(18, 6);
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_model_operations_model");
        b.HasOne<DevOperationType>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OperationTypeId })
            .HasPrincipalKey(o => new { o.OrganizationId, o.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_model_operations_type");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Seq }).HasDatabaseName("ix_dev_model_operations_model");
    }
}
