using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Рисунки (D726): номер уникален в организации, класс машины — запись справочника разработки своей организации.</summary>
internal sealed class DevPatternConfiguration : IEntityTypeConfiguration<DevPattern>
{
    public void Configure(EntityTypeBuilder<DevPattern> b)
    {
        b.ToTable("dev_patterns", t =>
        {
            t.HasCheckConstraint("ck_dev_patterns_kind", "[Kind] BETWEEN 1 AND 6");
            t.HasCheckConstraint("ck_dev_patterns_status", "[Status] BETWEEN 1 AND 4");
            t.HasCheckConstraint("ck_dev_patterns_rapport",
                $"([RapportWidth] IS NULL OR [RapportWidth] BETWEEN 1 AND {DevPattern.MaxRapport}) AND ([RapportHeight] IS NULL OR [RapportHeight] BETWEEN 1 AND {DevPattern.MaxRapport})");
            t.HasCheckConstraint("ck_dev_patterns_version", "[CurrentVersion] >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(DevPattern.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.MaxFloatMm).HasPrecision(6, 1);
        b.Property(x => x.AlignmentRule).HasMaxLength(DevPattern.AlignmentMaxLength);
        b.Property(x => x.Note).HasMaxLength(DevPattern.NoteMaxLength);
        b.Property(x => x.ReadyForProgrammingAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_patterns_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_dev_patterns_number");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("ix_dev_patterns_status");
        b.HasOne<DevLookup>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MachineClassId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_patterns_machine_class");
        b.HasOne<DevPattern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CopiedFromPatternId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_patterns_copied_from");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DessinatorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_patterns_dessinator");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ProgrammerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_patterns_programmer");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_patterns_created_by");
    }
}

/// <summary>Версии рисунка (D727) — только вставка (триггер); номер версии уникален в рисунке.</summary>
internal sealed class DevPatternVersionConfiguration : IEntityTypeConfiguration<DevPatternVersion>
{
    public void Configure(EntityTypeBuilder<DevPatternVersion> b)
    {
        b.ToTable("dev_pattern_versions", t => t.HasTrigger("tr_dev_pattern_versions_immutable"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Change).HasMaxLength(DevPatternVersion.TextMaxLength).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(DevPatternVersion.TextMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        DevKeys.Org(b);
        b.HasOne<DevPattern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_versions_pattern");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.PatternId, x.Version }).HasName("ak_dev_pattern_versions_no");
    }
}

/// <summary>Файлы версий рисунка (D727) — только вставка (триггер); файл — версии этого же рисунка.</summary>
internal sealed class DevPatternFileConfiguration : IEntityTypeConfiguration<DevPatternFile>
{
    public void Configure(EntityTypeBuilder<DevPatternFile> b)
    {
        b.ToTable("dev_pattern_files", t =>
        {
            t.HasCheckConstraint("ck_dev_pattern_files_kind", "[Kind] IN (1, 2)");
            t.HasCheckConstraint("ck_dev_pattern_files_size", $"[SizeBytes] BETWEEN 1 AND {DevFile.MaxBytes}");
            t.HasTrigger("tr_dev_pattern_files_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.FileName).HasMaxLength(DevFile.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.UploadedAtUtc).HasColumnType("datetime2(3)");
        DevKeys.Org(b);
        b.HasOne<DevPatternVersion>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId, x.Version })
            .HasPrincipalKey(v => new { v.OrganizationId, v.PatternId, v.Version }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_files_version");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.PatternId, x.Version, x.Kind }).HasDatabaseName("ix_dev_pattern_files_version");
    }
}

/// <summary>Цвета рисунка (D726): номер уникален в рисунке, доля 0–100 %.</summary>
internal sealed class DevPatternColorConfiguration : IEntityTypeConfiguration<DevPatternColor>
{
    public void Configure(EntityTypeBuilder<DevPatternColor> b)
    {
        b.ToTable("dev_pattern_colors", t =>
        {
            t.HasCheckConstraint("ck_dev_pattern_colors_share", "[SharePercent] IS NULL OR [SharePercent] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_dev_pattern_colors_no", $"[No] BETWEEN 1 AND {DevPattern.MaxColors}");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevPatternColor.NameMaxLength).IsRequired();
        b.Property(x => x.SharePercent).HasPrecision(5, 2);
        b.Property(x => x.GridKey).HasMaxLength(DevPatternColor.GridKeyMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        b.HasOne<DevPattern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_colors_pattern");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_pattern_colors_org_id");
        b.HasAlternateKey(x => new { x.OrganizationId, x.PatternId, x.Id }).HasName("ak_dev_pattern_colors_pattern_id");
        b.HasIndex(x => new { x.OrganizationId, x.PatternId, x.No }).IsUnique().HasDatabaseName("ux_dev_pattern_colors_no");
    }
}

/// <summary>Колориты рисунка (D726): название уникально в рисунке.</summary>
internal sealed class DevPatternColorwayConfiguration : IEntityTypeConfiguration<DevPatternColorway>
{
    public void Configure(EntityTypeBuilder<DevPatternColorway> b)
    {
        b.ToTable("dev_pattern_colorways");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevPatternColorway.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        b.HasOne<DevPattern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PatternId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_colorways_pattern");
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_pattern_colorways_org_id");
        b.HasAlternateKey(x => new { x.OrganizationId, x.PatternId, x.Id }).HasName("ak_dev_pattern_colorways_pattern_id");
        b.HasIndex(x => new { x.OrganizationId, x.PatternId, x.Name }).IsUnique().HasDatabaseName("ux_dev_pattern_colorways_name");
    }
}

/// <summary>Пряжа цвета в колорите (D726): одна строка на колорит и цвет; пряжа — позиция номенклатуры своей организации.</summary>
internal sealed class DevPatternColorwayYarnConfiguration : IEntityTypeConfiguration<DevPatternColorwayYarn>
{
    public void Configure(EntityTypeBuilder<DevPatternColorwayYarn> b)
    {
        b.ToTable("dev_pattern_colorway_yarns");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Swatch).HasColumnType("char(7)");
        DevKeys.Org(b);
        b.HasOne<DevPatternColorway>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ColorwayId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_dev_pattern_colorway_yarns_colorway");
        b.HasOne<DevPatternColor>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ColorId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_colorway_yarns_color");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId }).HasPrincipalKey(i => new { i.OrganizationId, i.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_pattern_colorway_yarns_item");
        b.HasIndex(x => new { x.OrganizationId, x.ColorwayId, x.ColorId }).IsUnique().HasDatabaseName("ux_dev_pattern_colorway_yarns");
    }
}

/// <summary>Шаблоны табеля мер по категории (D729): точка уникальна в категории.</summary>
internal sealed class DevCategoryMeasureConfiguration : IEntityTypeConfiguration<DevCategoryMeasure>
{
    public void Configure(EntityTypeBuilder<DevCategoryMeasure> b)
    {
        b.ToTable("dev_category_measures", t => t.HasCheckConstraint("ck_dev_category_measures_tolerance", "[Tolerance] >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Category).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(DevMeasure.NameMaxLength).IsRequired();
        b.Property(x => x.Tolerance).HasPrecision(6, 2);
        b.Property(x => x.Step).HasPrecision(6, 2);
        DevKeys.Org(b);
        b.HasIndex(x => new { x.OrganizationId, x.Category, x.Name }).IsUnique().HasDatabaseName("ux_dev_category_measures_name");
    }
}
