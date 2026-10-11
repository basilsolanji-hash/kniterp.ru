using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Production;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

// Конфигурации модуля «Производство» (D90–D99) — отдельным файлом, чтобы не пересекаться с правками общих конфигураций.

internal sealed class ProductionOrderConfiguration : IEntityTypeConfiguration<ProductionOrder>
{
    public void Configure(EntityTypeBuilder<ProductionOrder> b)
    {
        b.ToTable("production_orders", t =>
        {
            t.HasCheckConstraint("ck_production_orders_status", "[Status] IN (1, 2, 3, 4, 5, 9)");
            t.HasCheckConstraint("ck_production_orders_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_production_orders_source",
                "([Source] = 1 AND [SalesOrderId] IS NULL) OR ([Source] = 2 AND [SalesOrderId] IS NOT NULL)");
            t.HasCheckConstraint("ck_production_orders_warehouses", "[WipWarehouseId] <> [OutputWarehouseId]");
            t.HasCheckConstraint("ck_production_orders_due", "[DueDate] IS NULL OR [DueDate] >= [OrderDate]");
            t.HasCheckConstraint("ck_production_orders_parent", "[ParentOrderId] IS NULL OR [ParentOrderId] <> [Id]");
            // Запущен и дальше — с отметкой о запуске; черновик — без неё; отменённый — с причиной.
            t.HasCheckConstraint("ck_production_orders_released",
                "([Status] = 1 AND [ReleasedByUserId] IS NULL) OR ([Status] IN (2, 3, 4, 5) AND [ReleasedByUserId] IS NOT NULL AND [ReleasedAtUtc] IS NOT NULL)"
                + " OR [Status] = 9");
            t.HasCheckConstraint("ck_production_orders_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelReason] IS NOT NULL)"
                + " OR ([Status] <> 9 AND [CancelledByUserId] IS NULL AND [CancelReason] IS NULL)");
            t.HasCheckConstraint("ck_production_orders_closed",
                "([Status] = 5 AND [ClosedByUserId] IS NOT NULL AND [ClosedAtUtc] IS NOT NULL) OR ([Status] <> 5 AND [ClosedByUserId] IS NULL)");
            // Изделие, количество и техкарта запущенного заказа не меняются — триггер в миграции AddProductionOrders.
            t.HasTrigger("tr_production_orders_fixed_after_release");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Source).HasConversion<byte>();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Comment).HasMaxLength(ProductionOrder.CommentMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(ProductionOrder.ReasonMaxLength);
        b.Property(x => x.CloseReason).HasMaxLength(ProductionOrder.ReasonMaxLength);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_production_orders_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_production_orders_org_number");
        // Изделие, техкарта, склады и заказ покупателя — только своей организации: составные ключи.
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_item");
        b.HasOne<TechCard>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TechCardId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_tech_card");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WipWarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_wip_warehouse");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OutputWarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_output_warehouse");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_sales_order");
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ParentOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_orders_parent");
        b.HasIndex(x => new { x.OrganizationId, x.ParentOrderId }).HasDatabaseName("ix_production_orders_org_parent");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReleasedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("ix_production_orders_org_status");
        b.HasIndex(x => new { x.OrganizationId, x.SalesOrderId }).HasDatabaseName("ix_production_orders_org_sales_order");
        b.HasMany(x => x.Materials).WithOne().HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Materials).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_materials");
    }
}

internal sealed class ProductionOrderMaterialConfiguration : IEntityTypeConfiguration<ProductionOrderMaterial>
{
    public void Configure(EntityTypeBuilder<ProductionOrderMaterial> b)
    {
        b.ToTable("production_order_materials", t =>
        {
            t.HasCheckConstraint("ck_production_order_materials_net", "[PlannedNet] > 0");
            t.HasCheckConstraint("ck_production_order_materials_gross", "[PlannedGross] >= [PlannedNet]");
            t.HasCheckConstraint("ck_production_order_materials_waste", "[WastePercent] >= 0 AND [WastePercent] < 100");
            t.HasCheckConstraint("ck_production_order_materials_price", "[PlannedPrice] IS NULL OR [PlannedPrice] >= 0");
            // Снимок норм пишется один раз при запуске — триггер в миграции AddProductionOrders.
            t.HasTrigger("tr_production_order_materials_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.PlannedNet).HasColumnType("decimal(18,6)");
        b.Property(x => x.PlannedGross).HasColumnType("decimal(18,6)");
        b.Property(x => x.WastePercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.PlannedPrice).HasColumnType("decimal(19,4)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProductionOrderId, x.ItemId }).IsUnique().HasDatabaseName("ux_production_order_materials_order_item");
    }
}

internal sealed class ProductionOperationConfiguration : IEntityTypeConfiguration<ProductionOperation>
{
    public void Configure(EntityTypeBuilder<ProductionOperation> b)
    {
        b.ToTable("production_operations", t =>
        {
            t.HasCheckConstraint("ck_production_operations_stage", "[Stage] IN (1, 2, 3, 4, 5)");
            t.HasCheckConstraint("ck_production_operations_kind", "[Kind] IN (1, 2, 3, 4)");
            // Исправление — отрицательное, остальное — положительное; переделка и брак — только на контроле качества.
            t.HasCheckConstraint("ck_production_operations_quantity", "([Kind] = 2 AND [Quantity] < 0) OR ([Kind] <> 2 AND [Quantity] > 0)");
            t.HasCheckConstraint("ck_production_operations_qc", "[Kind] IN (1, 2) OR [Stage] = 4");
            t.HasCheckConstraint("ck_production_operations_return",
                "([Kind] = 3 AND [ReturnToStage] IN (1, 2, 3)) OR ([Kind] <> 3 AND [ReturnToStage] IS NULL)");
            t.HasCheckConstraint("ck_production_operations_reason", "[Kind] NOT IN (2, 4) OR [Reason] IS NOT NULL");
            t.HasCheckConstraint("ck_production_operations_corrects", "[CorrectsOperationId] IS NULL OR ([Kind] = 2 AND [EmployeeId] IS NOT NULL)");
            // Только вставка — триггер в миграции AddProductionStages.
            t.HasTrigger("tr_production_operations_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Stage).HasConversion<byte>();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.ReturnToStage).HasConversion<byte?>();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Reason).HasMaxLength(ProductionOperation.ReasonMaxLength);
        b.Ignore(x => x.Step);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        // Заказ — той же организации.
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_operations_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.ProductionOrderId }).HasDatabaseName("ix_production_operations_org_order");
        // Исполнитель — сотрудник той же организации; выработка по сотруднику и дню (терминал цеха, IWorkOutputSource).
        b.HasOne<KnitErp.Domain.Structure.Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_operations_employee");
        // Исправление отметки рабочего ссылается на неё (та же организация); ссылку имеет только исправление.
        b.HasOne<ProductionOperation>().WithMany().HasForeignKey(x => x.CorrectsOperationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_operations_corrects");
        b.HasIndex(x => x.CorrectsOperationId).HasDatabaseName("ix_production_operations_corrects").HasFilter("[CorrectsOperationId] IS NOT NULL");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.OccurredOn }).HasDatabaseName("ix_production_operations_org_employee_date")
            .HasFilter("[EmployeeId] IS NOT NULL");
    }
}

internal sealed class ProductionOrderDocumentConfiguration : IEntityTypeConfiguration<ProductionOrderDocument>
{
    public void Configure(EntityTypeBuilder<ProductionOrderDocument> b)
    {
        b.ToTable("production_order_documents", t =>
        {
            t.HasCheckConstraint("ck_production_order_documents_purpose", "[Purpose] IN (1, 2, 3, 4, 5)");
            // Связь только добавляется — триггер в миграции AddProductionMaterials.
            t.HasTrigger("tr_production_order_documents_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Purpose).HasConversion<byte>();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_order_documents_order");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StockDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_production_order_documents_stock_document");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        // Документ принадлежит одному заказу и одной цели.
        b.HasIndex(x => x.StockDocumentId).IsUnique().HasDatabaseName("ux_production_order_documents_stock_document");
        b.HasIndex(x => new { x.OrganizationId, x.ProductionOrderId }).HasDatabaseName("ix_production_order_documents_org_order");
    }
}
