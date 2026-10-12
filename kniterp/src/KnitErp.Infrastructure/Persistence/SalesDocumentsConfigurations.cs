using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Изображения для печатных форм юрлица (D1120): содержимое зашифровано мастер-ключом, одно изображение вида на юрлицо.</summary>
internal sealed class SalesDocumentImageConfiguration : IEntityTypeConfiguration<SalesDocumentImage>
{
    public void Configure(EntityTypeBuilder<SalesDocumentImage> b)
    {
        b.ToTable("sales_document_images", t =>
        {
            t.HasCheckConstraint("ck_sales_document_images_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_sales_document_images_size", $"[SizeBytes] BETWEEN 1 AND {SalesDocumentImage.MaxBytes}");
            t.HasCheckConstraint("ck_sales_document_images_type", "[ContentType] IN (N'image/png', N'image/jpeg')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.ContentType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Content).HasConversion(PersonalDataColumns.Bytes).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_document_images_legal_entity");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_document_images_user");
        b.HasIndex(x => new { x.OrganizationId, x.LegalEntityId, x.Kind }).IsUnique().HasDatabaseName("ux_sales_document_images_entity_kind");
    }
}

/// <summary>Что выводить в печатных формах юрлица (D1120): одна запись на юрлицо.</summary>
internal sealed class SalesDocumentSettingsConfiguration : IEntityTypeConfiguration<SalesDocumentSettings>
{
    public void Configure(EntityTypeBuilder<SalesDocumentSettings> b)
    {
        b.ToTable("sales_document_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.Flags);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LegalEntity>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LegalEntityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_document_settings_legal_entity");
        b.HasIndex(x => new { x.OrganizationId, x.LegalEntityId }).IsUnique().HasDatabaseName("ux_sales_document_settings_entity");
    }
}

/// <summary>Шаблоны писем клиенту (D1124): один на вид письма.</summary>
internal sealed class SalesMailTemplateConfiguration : IEntityTypeConfiguration<SalesMailTemplate>
{
    public void Configure(EntityTypeBuilder<SalesMailTemplate> b)
    {
        b.ToTable("sales_mail_templates", t => t.HasCheckConstraint("ck_sales_mail_templates_kind", "[Kind] IN (1, 2, 3, 4)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Subject).HasMaxLength(SalesMailTemplate.SubjectMaxLength).IsRequired();
        b.Property(x => x.Body).HasMaxLength(SalesMailTemplate.BodyMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.Kind }).IsUnique().HasDatabaseName("ux_sales_mail_templates_kind");
    }
}

/// <summary>Этапы заказа, о которых клиенту уходит письмо (D1124).</summary>
internal sealed class SalesMailStageConfiguration : IEntityTypeConfiguration<SalesMailStage>
{
    public void Configure(EntityTypeBuilder<SalesMailStage> b)
    {
        b.ToTable("sales_mail_stages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SalesOrderStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.StageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_mail_stages_stage");
        b.HasIndex(x => new { x.OrganizationId, x.StageId }).IsUnique().HasDatabaseName("ux_sales_mail_stages_stage");
    }
}

/// <summary>Почта покупателя для документов (D1124): одна запись на контрагента.</summary>
internal sealed class CustomerMailSettingsConfiguration : IEntityTypeConfiguration<CustomerMailSettings>
{
    public void Configure(EntityTypeBuilder<CustomerMailSettings> b)
    {
        b.ToTable("customer_mail_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Email).HasMaxLength(SalesMailMessage.RecipientsMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_customer_mail_settings_counterparty");
        b.HasIndex(x => new { x.OrganizationId, x.CounterpartyId }).IsUnique().HasDatabaseName("ux_customer_mail_settings_counterparty");
    }
}

/// <summary>Очередь писем клиенту (D1123): автоматическое письмо — одно на событие (уникальный DedupKey в организации).</summary>
internal sealed class SalesMailMessageConfiguration : IEntityTypeConfiguration<SalesMailMessage>
{
    public void Configure(EntityTypeBuilder<SalesMailMessage> b)
    {
        b.ToTable("sales_mail_messages", t =>
        {
            t.HasCheckConstraint("ck_sales_mail_messages_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_sales_mail_messages_status", "[Status] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_sales_mail_messages_sent", "([Status] = 2 AND [SentAtUtc] IS NOT NULL) OR ([Status] <> 2 AND [SentAtUtc] IS NULL)");
            t.HasCheckConstraint("ck_sales_mail_messages_attempts", "[Attempts] >= 0");
            t.HasCheckConstraint("ck_sales_mail_messages_dedup", "[DedupKey] IS NULL OR [IsAuto] = 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.Recipients).HasMaxLength(SalesMailMessage.RecipientsMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(SalesMailTemplate.SubjectMaxLength).IsRequired();
        b.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.DedupKey).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(SalesMailMessage.ErrorMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_sales_mail_messages_org_id");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_mail_messages_order");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_mail_messages_counterparty");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_mail_messages_user");
        b.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => new { x.OrganizationId, x.MessageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_mail_attachments_message");
        b.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => new { x.OrganizationId, x.DedupKey }).IsUnique().HasFilter("[DedupKey] IS NOT NULL").HasDatabaseName("ux_sales_mail_messages_dedup");
        b.HasIndex(x => new { x.OrganizationId, x.SalesOrderId }).HasDatabaseName("ix_sales_mail_messages_order");
        // Отправитель выбирает подошедшие письма из очереди.
        b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc }).HasDatabaseName("ix_sales_mail_messages_due");
    }
}

internal sealed class SalesMailAttachmentConfiguration : IEntityTypeConfiguration<SalesMailAttachment>
{
    public void Configure(EntityTypeBuilder<SalesMailAttachment> b)
    {
        b.ToTable("sales_mail_attachments", t => t.HasCheckConstraint("ck_sales_mail_attachments_document", "[Document] IN (1, 2, 3)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Document).HasConversion<byte>();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.MessageId, x.Document, x.DocumentId }).IsUnique().HasDatabaseName("ux_sales_mail_attachments_document");
    }
}

/// <summary>Ручные связи заказа покупателя с документами (D1125): одна действующая связь на документ.</summary>
internal sealed class SalesDocumentLinkConfiguration : IEntityTypeConfiguration<SalesDocumentLink>
{
    public void Configure(EntityTypeBuilder<SalesDocumentLink> b)
    {
        b.ToTable("sales_document_links", t =>
        {
            t.HasCheckConstraint("ck_sales_document_links_kind", "[Kind] IN (1, 2, 3, 4, 5, 6)");
            t.HasCheckConstraint("ck_sales_document_links_self", "NOT ([Kind] = 1 AND [DocumentId] = [SalesOrderId])");
            t.HasCheckConstraint("ck_sales_document_links_removed",
                "([RemovedAtUtc] IS NULL AND [RemovedByUserId] IS NULL) OR ([RemovedAtUtc] IS NOT NULL AND [RemovedByUserId] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Comment).HasMaxLength(SalesDocumentLink.CommentMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sales_document_links_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_document_links_created_by");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RemovedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sales_document_links_removed_by");
        b.HasIndex(x => new { x.OrganizationId, x.SalesOrderId, x.Kind, x.DocumentId }).IsUnique().HasFilter("[RemovedAtUtc] IS NULL")
            .HasDatabaseName("ux_sales_document_links_active");
        // Обратный поиск: с какими заказами связан документ.
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.DocumentId }).HasDatabaseName("ix_sales_document_links_document");
    }
}
