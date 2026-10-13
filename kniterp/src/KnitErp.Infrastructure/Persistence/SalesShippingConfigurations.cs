using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Задания на сборку (D1150): склад и заказы своей организации, номер уникален.</summary>
internal sealed class SalesPickListConfiguration : IEntityTypeConfiguration<SalesPickList>
{
    public void Configure(EntityTypeBuilder<SalesPickList> b)
    {
        b.ToTable("sales_pick_lists", t =>
        {
            t.HasCheckConstraint("ck_sales_pick_lists_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_sales_pick_lists_picked",
                "([Status] = 2 AND [PickedByUserId] IS NOT NULL AND [PickedAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [PickedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(SalesPickList.CommentMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.PickedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.PartiallyPicked);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_pick_lists_org_id");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_pick_lists_warehouse");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_pick_lists_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PickedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_pick_lists_picked_by");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_sales_pick_lists_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.ListDate }).HasDatabaseName("ix_sales_pick_lists_org_status_date");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => new { x.OrganizationId, x.PickListId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_sales_pick_list_lines_list");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class SalesPickListLineConfiguration : IEntityTypeConfiguration<SalesPickListLine>
{
    public void Configure(EntityTypeBuilder<SalesPickListLine> b)
    {
        b.ToTable("sales_pick_list_lines", t =>
        {
            t.HasCheckConstraint("ck_sales_pick_list_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_sales_pick_list_lines_picked", "[Picked] >= 0 AND [Picked] <= [Quantity]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Picked).HasColumnType("decimal(18,6)");
        b.Property(x => x.Location).HasMaxLength(SalesPickListLine.LocationMaxLength);
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_pick_list_lines_order");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_pick_list_lines_item");
        b.HasIndex(x => new { x.PickListId, x.SalesOrderId, x.ItemId }).IsUnique().HasDatabaseName("ux_sales_pick_list_lines_order_item");
        b.HasIndex(x => new { x.OrganizationId, x.SalesOrderId }).HasDatabaseName("ix_sales_pick_list_lines_order");
    }
}

/// <summary>Способы доставки (D1152): название уникально среди действующих.</summary>
internal sealed class SalesDeliveryMethodConfiguration : IEntityTypeConfiguration<SalesDeliveryMethod>
{
    public void Configure(EntityTypeBuilder<SalesDeliveryMethod> b)
    {
        b.ToTable("sales_delivery_methods", t => t.HasCheckConstraint("ck_sales_delivery_methods_kind", "[Kind] IN (1, 2, 3)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(SalesDeliveryMethod.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_delivery_methods_org_id");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_sales_delivery_methods_org_name");
    }
}

/// <summary>Отгрузки по нескольким заказам (D1151): покупатель, склад и способ доставки — своей организации.</summary>
internal sealed class SalesShipmentGroupConfiguration : IEntityTypeConfiguration<SalesShipmentGroup>
{
    public void Configure(EntityTypeBuilder<SalesShipmentGroup> b)
    {
        b.ToTable("sales_shipment_groups", t =>
        {
            t.HasCheckConstraint("ck_sales_shipment_groups_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_sales_shipment_groups_packages", "[Packages] IS NULL OR [Packages] > 0");
            t.HasCheckConstraint("ck_sales_shipment_groups_weight", "[GrossWeightKg] IS NULL OR [GrossWeightKg] > 0");
            t.HasCheckConstraint("ck_sales_shipment_groups_posted",
                "([Status] = 2 AND [PostedByUserId] IS NOT NULL AND [PostedAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [PostedByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.TrackingNumber).HasMaxLength(SalesShipmentGroup.TrackingMaxLength);
        b.Property(x => x.Comment).HasMaxLength(SalesShipmentGroup.CommentMaxLength);
        b.Property(x => x.GrossWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.PostedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_shipment_groups_org_id");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_groups_customer");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_groups_warehouse");
        b.HasOne<SalesDeliveryMethod>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DeliveryMethodId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_groups_delivery");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_groups_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PostedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_groups_posted_by");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_sales_shipment_groups_org_number");
        b.HasIndex(x => new { x.OrganizationId, x.GroupDate }).HasDatabaseName("ix_sales_shipment_groups_org_date");
        b.HasMany(x => x.Documents).WithOne().HasForeignKey(x => new { x.OrganizationId, x.GroupId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_sales_shipment_group_documents_group");
        b.Navigation(x => x.Documents).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_documents");
    }
}

internal sealed class SalesShipmentGroupDocumentConfiguration : IEntityTypeConfiguration<SalesShipmentGroupDocument>
{
    public void Configure(EntityTypeBuilder<SalesShipmentGroupDocument> b)
    {
        b.ToTable("sales_shipment_group_documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StockDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_group_documents_document");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_shipment_group_documents_order");
        b.HasIndex(x => x.StockDocumentId).IsUnique().HasDatabaseName("ux_sales_shipment_group_documents_document");
        b.HasIndex(x => new { x.GroupId, x.SalesOrderId }).IsUnique().HasDatabaseName("ux_sales_shipment_group_documents_group_order");
    }
}

/// <summary>Причины возврата (D1153): название уникально среди действующих.</summary>
internal sealed class SalesReturnReasonConfiguration : IEntityTypeConfiguration<SalesReturnReason>
{
    public void Configure(EntityTypeBuilder<SalesReturnReason> b)
    {
        b.ToTable("sales_return_reasons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(SalesReturnReason.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_return_reasons_org_id");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_sales_return_reasons_org_name");
    }
}

/// <summary>Сведения возврата (D1153): один на документ возврата; склады, причины и документы брака — своей организации.</summary>
internal sealed class SalesReturnDetailsConfiguration : IEntityTypeConfiguration<SalesReturnDetails>
{
    public void Configure(EntityTypeBuilder<SalesReturnDetails> b)
    {
        b.ToTable("sales_return_details");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Comment).HasMaxLength(SalesReturnDetails.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_return_details_org_id");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StockDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_document");
        b.HasOne<SalesReturnReason>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReasonId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_reason");
        b.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.QuarantineWarehouseId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_quarantine");
        b.HasOne<OperationReason>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WriteOffReasonId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_writeoff_reason");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TransferDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_transfer");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.WriteOffDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_details_writeoff");
        b.HasIndex(x => x.StockDocumentId).IsUnique().HasDatabaseName("ux_sales_return_details_document");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => new { x.OrganizationId, x.DetailsId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_sales_return_lines_details");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
    }
}

internal sealed class SalesReturnLineConfiguration : IEntityTypeConfiguration<SalesReturnLine>
{
    public void Configure(EntityTypeBuilder<SalesReturnLine> b)
    {
        b.ToTable("sales_return_lines", t =>
        {
            t.HasCheckConstraint("ck_sales_return_lines_quality", "[Quality] IN (2, 3)");
            t.HasCheckConstraint("ck_sales_return_lines_quantity", "[Quantity] > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quality).HasConversion<byte>();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_return_lines_item");
        b.HasIndex(x => new { x.DetailsId, x.ItemId }).IsUnique().HasDatabaseName("ux_sales_return_lines_item");
    }
}

/// <summary>
/// Возвраты денег покупателю (D1154): оплата, заказ, возврат и денежная операция — своей организации (составные ключи; у оплат
/// и денежных операций ключ (OrganizationId, Id) создаётся этой связью).
/// </summary>
internal sealed class CustomerRefundConfiguration : IEntityTypeConfiguration<CustomerRefund>
{
    public void Configure(EntityTypeBuilder<CustomerRefund> b)
    {
        b.ToTable("customer_refunds", t =>
        {
            t.HasCheckConstraint("ck_customer_refunds_amount", "[Amount] > 0");
            t.HasCheckConstraint("ck_customer_refunds_status", "[Status] IN (2, 9)");
            t.HasCheckConstraint("ck_customer_refunds_cancelled",
                "([Status] = 9 AND [CancelledByUserId] IS NOT NULL AND [CancelReason] IS NOT NULL) OR ([Status] = 2 AND [CancelledByUserId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.CancelReason).HasMaxLength(CustomerRefund.ReasonMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CancelledAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CustomerId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_customer");
        b.HasOne<CustomerPayment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_payment");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_order");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReturnDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_return");
        b.HasOne<MoneyOperation>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.MoneyOperationId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_money_operation");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_refunds_cancelled_by");
        b.HasIndex(x => new { x.OrganizationId, x.MoneyOperationId }).HasDatabaseName("ix_customer_refunds_money_operation");
        b.HasIndex(x => new { x.OrganizationId, x.PaymentId }).HasDatabaseName("ix_customer_refunds_payment");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerId, x.RefundDate }).HasDatabaseName("ix_customer_refunds_customer_date");
    }
}

/// <summary>
/// Коды маркировки в документах (D1108): GTIN — 14 цифр, серийный — до 20 символов. Проданный код уникален в организации
/// (фильтрованный UNIQUE по проданным): один код не продаётся дважды даже при одновременном проведении.
/// </summary>
internal sealed class SalesMarkingCodeConfiguration : IEntityTypeConfiguration<SalesMarkingCode>
{
    public void Configure(EntityTypeBuilder<SalesMarkingCode> b)
    {
        b.ToTable("sales_marking_codes", t =>
        {
            t.HasCheckConstraint("ck_sales_marking_codes_gtin", "LEN([Gtin]) = 14 AND [Gtin] NOT LIKE '%[^0-9]%'");
            t.HasCheckConstraint("ck_sales_marking_codes_serial", "LEN([Serial]) BETWEEN 1 AND 20");
            t.HasCheckConstraint("ck_sales_marking_codes_sold", "[IsSold] = 0 OR [SoldAtUtc] IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Gtin).HasMaxLength(14).IsUnicode(false).IsRequired();
        b.Property(x => x.Serial).HasMaxLength(MarkingCodes.SerialMaxLength).IsUnicode(false).UseCollation("Latin1_General_BIN2").IsRequired();
        b.Property(x => x.SoldAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.ReleasedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.ScannedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Cis);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StockDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_marking_codes_document");
        b.HasOne<StockDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReleasedByDocumentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_marking_codes_released_by");
        b.HasOne<Item>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ItemId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_marking_codes_item");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ScannedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_marking_codes_user");
        b.HasIndex(x => new { x.StockDocumentId, x.Gtin, x.Serial }).IsUnique().HasDatabaseName("ux_sales_marking_codes_document_code");
        b.HasIndex(x => new { x.OrganizationId, x.Gtin, x.Serial }).IsUnique().HasFilter("[IsSold] = 1").HasDatabaseName("ux_sales_marking_codes_sold");
    }
}
