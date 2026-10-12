using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>D741: фурнитура и упаковка модели — позиция каталога своей организации, одна строка на позицию.</summary>
internal sealed class DevModelAccessoryConfiguration : IEntityTypeConfiguration<DevModelAccessory>
{
    public void Configure(EntityTypeBuilder<DevModelAccessory> b)
    {
        b.ToTable("dev_model_accessories", t => t.HasCheckConstraint("ck_dev_model_accessories_qty", "[Quantity] > 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasPrecision(18, 6);
        b.Property(x => x.RowVersion).IsRowVersion();
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_model_accessories_model");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId }).HasPrincipalKey(i => new { i.OrganizationId, i.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_model_accessories_item");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.ItemId }).IsUnique().HasDatabaseName("ux_dev_model_accessories");
    }
}

/// <summary>D744: версия расчёта себестоимости — только вставка (триггер), номер версии уникален в модели.</summary>
internal sealed class DevCostRunConfiguration : IEntityTypeConfiguration<DevCostRun>
{
    public void Configure(EntityTypeBuilder<DevCostRun> b)
    {
        b.ToTable("dev_cost_runs", t =>
        {
            t.HasCheckConstraint("ck_dev_cost_runs_mode", "[PriceMode] BETWEEN 1 AND 2");
            t.HasCheckConstraint("ck_dev_cost_runs_money", "[Yarn] >= 0 AND [Accessories] >= 0 AND [Labor] >= 0 AND [Overhead] >= 0 AND [Cost] >= 0 AND [Price] >= 0");
            t.HasTrigger("tr_dev_cost_runs_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_dev_cost_runs_org_id");
        b.Property(x => x.Note).HasMaxLength(DevCostRun.NoteMaxLength);
        b.Property(x => x.OverheadPercent).HasPrecision(9, 4);
        b.Property(x => x.MarkupPercent).HasPrecision(9, 4);
        foreach (var money in new[] { nameof(DevCostRun.Yarn), nameof(DevCostRun.Accessories), nameof(DevCostRun.Labor), nameof(DevCostRun.Overhead),
                     nameof(DevCostRun.Cost), nameof(DevCostRun.Price), nameof(DevCostRun.Margin) })
        {
            b.Property(money).HasPrecision(19, 4);
        }

        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        DevKeys.Org(b);
        DevKeys.ToModel(b, "fk_dev_cost_runs_model");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_cost_runs_author");
        b.HasIndex(x => new { x.OrganizationId, x.ModelId, x.Version }).IsUnique().HasDatabaseName("ux_dev_cost_runs_version");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => new { l.OrganizationId, l.RunId }).HasPrincipalKey(r => new { r.OrganizationId, r.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_cost_lines_run");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>D744: строка версии расчёта — только вставка (триггер).</summary>
internal sealed class DevCostLineConfiguration : IEntityTypeConfiguration<DevCostLine>
{
    public void Configure(EntityTypeBuilder<DevCostLine> b)
    {
        b.ToTable("dev_cost_lines", t =>
        {
            t.HasCheckConstraint("ck_dev_cost_lines_kind", "[Kind] BETWEEN 1 AND 3");
            t.HasCheckConstraint("ck_dev_cost_lines_values", "[Quantity] >= 0 AND [UnitPrice] >= 0 AND [Amount] >= 0 AND [WastePercent] BETWEEN 0 AND 100");
            t.HasTrigger("tr_dev_cost_lines_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(DevCostLine.NameMaxLength).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(20).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 6);
        b.Property(x => x.WastePercent).HasPrecision(9, 4);
        b.Property(x => x.UnitPrice).HasPrecision(19, 4);
        b.Property(x => x.Amount).HasPrecision(19, 4);
        DevKeys.Org(b);
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId }).HasPrincipalKey(i => new { i.OrganizationId, i.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_dev_cost_lines_item");
    }
}
