using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Development;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Модели разработки (D701). Ограничения дублируют правила домена в SQL Server.</summary>
internal sealed class DevModelConfiguration : IEntityTypeConfiguration<DevModel>
{
    public void Configure(EntityTypeBuilder<DevModel> b)
    {
        b.ToTable("dev_models", t =>
        {
            t.HasCheckConstraint("ck_dev_models_stage", "[Stage] BETWEEN 1 AND 11 OR [Stage] = 99");
            t.HasCheckConstraint("ck_dev_models_version", "[VersionNo] >= 1 AND ([PreviousVersionId] IS NULL OR [VersionNo] >= 2)");
            t.HasCheckConstraint("ck_dev_models_audience", "[Audience] BETWEEN 1 AND 4");
            t.HasCheckConstraint("ck_dev_models_priority", "[Priority] BETWEEN 1 AND 4");
            t.HasCheckConstraint("ck_dev_models_child", "[ChildSafety] = 0 OR [Audience] = 3");
            t.HasCheckConstraint("ck_dev_models_cancel", "([Stage] = 99 AND [CancelReason] IS NOT NULL) OR ([Stage] <> 99 AND [CancelReason] IS NULL)");
            t.HasCheckConstraint("ck_dev_models_pattern", "[HasPattern] = 1 OR [Stage] <> 3");
            t.HasCheckConstraint("ck_dev_models_money", "([TargetCost] IS NULL OR [TargetCost] >= 0) AND ([TargetPrice] IS NULL OR [TargetPrice] >= 0)");
            t.HasCheckConstraint("ck_dev_models_run", "[RunQuantity] IS NULL OR [RunQuantity] > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(DevModel.NumberMaxLength).IsRequired();
        b.Property(x => x.Name).HasMaxLength(DevModel.NameMaxLength).IsRequired();
        b.Property(x => x.CancelReason).HasMaxLength(DevModel.ReasonMaxLength);
        b.Property(x => x.Description).HasMaxLength(DevModel.TextMaxLength);
        b.Property(x => x.SizeRange).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.ColorRange).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Composition).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Labeling).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Care).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.TargetCost).HasPrecision(19, 4);
        b.Property(x => x.TargetPrice).HasPrecision(19, 4);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CollectionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_collection");
        // D733: категория — запись справочника «Категории изделий» своей организации.
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CategoryId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_category");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_customer");
        b.HasOne<CrmDeal>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DealId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_deal");
        // D726, D728: рисунок модели — конкретная версия рисунка своей организации и колорит этого рисунка; пряжа и вязка.
        b.HasOne<DevPatternVersion>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId, x.PatternVersion })
            .HasPrincipalKey(v => new { v.OrganizationId, v.PatternId, v.Version }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_pattern_version");
        b.HasOne<DevPatternColorway>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId, x.ColorwayId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.PatternId, c.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_colorway");
        b.Property(x => x.DyeNumber).HasMaxLength(40);
        b.Property(x => x.DensityLoops).HasPrecision(6, 2);
        b.Property(x => x.DensityRows).HasPrecision(6, 2);
        b.Property(x => x.ShrinkagePercent).HasPrecision(5, 2);
        b.HasIndex(x => new { x.OrganizationId, x.PatternId }).HasDatabaseName("ix_dev_models_pattern");
        // D769: предыдущая версия — модель своей организации; у версии не больше одной следующей.
        b.Property(x => x.VersionNo).HasDefaultValue(1).HasSentinel(1);
        b.HasOne<DevModel>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PreviousVersionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_previous_version");
        b.HasIndex(x => new { x.OrganizationId, x.PreviousVersionId }).IsUnique().HasFilter("[PreviousVersionId] IS NOT NULL")
            .HasDatabaseName("ux_dev_models_previous_version");
        // D750: позиция и черновик техкарты, в которые передана модель, — только своей организации.
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_production_item");
        b.HasOne<KnitErp.Domain.Production.TechCard>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionTechCardId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_production_techcard");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DesignerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_designer");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DessinatorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_dessinator");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ConstructorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_constructor");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnologistUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_technologist");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ProgrammerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_programmer");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_author");
        b.Property(x => x.PackBox).HasMaxLength(60);
        b.HasOne<DevFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MainSketchFileId })
            .HasPrincipalKey(f => new { f.OrganizationId, f.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_models_sketch");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_models_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_dev_models_number");
        b.HasIndex(x => new { x.OrganizationId, x.Stage }).HasDatabaseName("ix_dev_models_stage");
    }
}

/// <summary>История стадий: только вставка (триггер в миграции).</summary>
internal sealed class DevModelStageEntryConfiguration : IEntityTypeConfiguration<DevModelStageEntry>
{
    public void Configure(EntityTypeBuilder<DevModelStageEntry> b)
    {
        b.ToTable("dev_model_stages", t =>
        {
            t.HasCheckConstraint("ck_dev_model_stages_to", "[ToStage] BETWEEN 1 AND 11 OR [ToStage] = 99");
            t.HasCheckConstraint("ck_dev_model_stages_days", "[DaysInPrevious] IS NULL OR [DaysInPrevious] >= 0");
            t.HasTrigger("tr_dev_model_stages_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Comment).HasMaxLength(DevModel.ReasonMaxLength);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Model).WithMany().HasForeignKey(x => new { x.OrganizationId, x.ModelId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_model_stages_model");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Id }).HasDatabaseName("ix_dev_model_stages_model");
        // Аудит 2026-10-12: аналитика берёт переходы за период — без индекса просматривалась вся история организации.
        b.HasIndex(x => new { x.OrganizationId, x.AtUtc }).HasDatabaseName("ix_dev_model_stages_at");
    }
}

internal sealed class DevStageSettingConfiguration : IEntityTypeConfiguration<DevStageSetting>
{
    public void Configure(EntityTypeBuilder<DevStageSetting> b)
    {
        b.ToTable("dev_stage_settings", t =>
        {
            t.HasCheckConstraint("ck_dev_stage_settings_stage", "[Stage] BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_dev_stage_settings_days", $"[PlanDays] BETWEEN 0 AND {DevStageSetting.MaxPlanDays}");
            t.HasCheckConstraint("ck_dev_stage_settings_enabled", "[IsEnabled] = 1 OR [Stage] IN (5, 7, 8, 9)");
        });
        b.HasKey(x => new { x.OrganizationId, x.Stage });
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Файлы моделей: только вставка (триггер), версия — у каждого файла.</summary>
internal sealed class DevFileConfiguration : IEntityTypeConfiguration<DevFile>
{
    public void Configure(EntityTypeBuilder<DevFile> b)
    {
        b.ToTable("dev_files", t =>
        {
            t.HasCheckConstraint("ck_dev_files_kind", "[Kind] BETWEEN 1 AND 7");
            t.HasCheckConstraint("ck_dev_files_size", $"[SizeBytes] BETWEEN 1 AND {DevFile.MaxBytes}");
            t.HasCheckConstraint("ck_dev_files_version", "[Version] >= 1");
            t.HasTrigger("tr_dev_files_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FileName).HasMaxLength(DevFile.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.Comment).HasMaxLength(DevFile.CommentMaxLength);
        b.Property(x => x.Content).HasColumnType("varbinary(max)").IsRequired();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DevModel>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ModelId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_files_model");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Kind, x.FileName, x.Version }).IsUnique().HasDatabaseName("ux_dev_files_version");
        // D710, D711: программа машины хранится с деталью, станком и его параметрами на момент загрузки.
        b.Property(x => x.MachineParams).HasMaxLength(200);
        b.HasOne<DevDetail>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DetailId }).HasPrincipalKey(d => new { d.OrganizationId, d.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_files_detail");
        b.HasOne<DevMachine>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MachineId }).HasPrincipalKey(m => new { m.OrganizationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_files_machine");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_files_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.MachineId }).HasDatabaseName("ix_dev_files_machine");
        // D728: фото образца — образец своей организации.
        b.HasOne<DevSample>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SampleId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_files_sample");
    }
}

internal sealed class DevLookupConfiguration : IEntityTypeConfiguration<DevLookup>
{
    public void Configure(EntityTypeBuilder<DevLookup> b)
    {
        b.ToTable("dev_lookups", t =>
        {
            t.HasCheckConstraint("ck_dev_lookups_kind", "[Kind] BETWEEN 1 AND 7");
            t.HasCheckConstraint("ck_dev_lookups_number", $"[Number] IS NULL OR [Number] BETWEEN 0 AND {DevLookup.MaxNumber}");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevLookup.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_lookups_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.Name }).IsUnique().HasDatabaseName("ux_dev_lookups_name");
    }
}

/// <summary>Настройки автоматизации разработки (D791): одна запись на организацию.</summary>
internal sealed class DevSettingsConfiguration : IEntityTypeConfiguration<DevSettings>
{
    public void Configure(EntityTypeBuilder<DevSettings> b)
    {
        b.ToTable("dev_settings");
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.Property(x => x.AutoLaunchDate).HasDefaultValue(true).HasSentinel(true);
        b.Property(x => x.AutoAdvance).HasDefaultValue(true).HasSentinel(true);
        b.Property(x => x.FactTolerancePercent).HasDefaultValue(DevSettings.DefaultTolerance).HasSentinel(DevSettings.DefaultTolerance);
        b.Property(x => x.CompletenessSections).HasDefaultValue(DevCompleteness.AllSections).HasSentinel(DevCompleteness.AllSections);
        b.Property(x => x.LabelWidthMm).HasDefaultValue(DevSettings.DefaultLabelWidth).HasSentinel(DevSettings.DefaultLabelWidth);
        b.Property(x => x.LabelHeightMm).HasDefaultValue(DevSettings.DefaultLabelHeight).HasSentinel(DevSettings.DefaultLabelHeight);
        b.Property(x => x.YarnWastePercent).HasPrecision(9, 4);
        b.Property(x => x.OverheadPercent).HasPrecision(9, 4);
        b.Property(x => x.MarkupPercent).HasPrecision(9, 4);
        b.Property(x => x.LaborRatePerMinute).HasPrecision(19, 4);
        b.Property(x => x.MinMarginPercent).HasPrecision(9, 4);
        b.Property(x => x.RequireCostForApproval).HasDefaultValue(true).HasSentinel(true);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_dev_settings_tolerance", "[FactTolerancePercent] BETWEEN 1 AND 100");
            t.HasCheckConstraint("ck_dev_settings_costing",
                "[YarnWastePercent] BETWEEN 0 AND 100 AND [OverheadPercent] BETWEEN 0 AND 1000 AND [MarkupPercent] BETWEEN 0 AND 1000 AND ([LaborRatePerMinute] IS NULL OR [LaborRatePerMinute] >= 0) "
                + "AND ([MinMarginPercent] IS NULL OR [MinMarginPercent] BETWEEN -100 AND 100)");
            t.HasCheckConstraint("ck_dev_settings_sections", $"[CompletenessSections] BETWEEN 1 AND {DevCompleteness.AllSections}");
            t.HasCheckConstraint("ck_dev_settings_label", $"[LabelWidthMm] BETWEEN {DevSettings.MinLabelMm} AND {DevSettings.MaxLabelMm} AND [LabelHeightMm] BETWEEN {DevSettings.MinLabelMm} AND {DevSettings.MaxLabelMm}");
        });
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DefaultDesignerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_settings_designer");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DefaultDessinatorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_settings_dessinator");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DefaultConstructorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_settings_constructor");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DefaultTechnologistUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_settings_technologist");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DefaultProgrammerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_settings_programmer");
    }
}

/// <summary>Шаблоны задания по категориям (D790).</summary>
internal sealed class DevCategoryTemplateConfiguration : IEntityTypeConfiguration<DevCategoryTemplate>
{
    public void Configure(EntityTypeBuilder<DevCategoryTemplate> b)
    {
        b.ToTable("dev_category_templates");
        b.HasKey(x => new { x.OrganizationId, x.CategoryId });
        b.Property(x => x.SizeRange).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.ColorRange).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Composition).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Labeling).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.Care).HasMaxLength(DevModel.ShortTextMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CategoryId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_category_templates_category");
    }
}
