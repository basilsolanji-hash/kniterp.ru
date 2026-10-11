using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KnitErp.Infrastructure.Persistence;

// CRM (D600–D699): таблицы с префиксом crm_. Ссылки внутри организации — составными ключами (OrganizationId, Id),
// как в модулях базы: сделка не сошлётся на контрагента или этап чужой организации даже в обход приложения.

internal sealed class CrmPipelineConfiguration : IEntityTypeConfiguration<CrmPipeline>
{
    public void Configure(EntityTypeBuilder<CrmPipeline> b)
    {
        b.ToTable("crm_pipelines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(CrmPipeline.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_pipelines_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_crm_pipelines_org_name_active");
    }
}

internal sealed class CrmStageConfiguration : IEntityTypeConfiguration<CrmStage>
{
    public void Configure(EntityTypeBuilder<CrmStage> b)
    {
        b.ToTable("crm_stages", t =>
        {
            t.HasCheckConstraint("ck_crm_stages_color", "[Color] IN ('gray', 'blue', 'teal', 'green', 'yellow', 'orange', 'red', 'magenta', 'violet')");
            t.HasCheckConstraint("ck_crm_stages_probability",
                "[Probability] BETWEEN 0 AND 100 AND ([Kind] <> 1 OR [Probability] = 100) AND ([Kind] <> 2 OR [Probability] = 0)");
            t.HasCheckConstraint("ck_crm_stages_kind", "[Kind] IN (0, 1, 2) AND ([Kind] = 0 OR [IsArchived] = 0)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(CrmStage.NameMaxLength).IsRequired();
        b.Property(x => x.Color).HasColumnType("varchar(16)").IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsFinal);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_stages_org_id");
        b.HasAlternateKey(x => new { x.OrganizationId, x.PipelineId, x.Id }).HasName("ak_crm_stages_org_pipeline_id");
        b.HasOne<CrmPipeline>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PipelineId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_stages_pipeline");
        b.HasIndex(x => new { x.PipelineId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_crm_stages_pipeline_name_active");

        // У воронки ровно один «выигрыш» и один «проигрыш» (D602): второй такой этап база не примет.
        b.HasIndex(x => new { x.PipelineId, x.Kind }).IsUnique().HasFilter("[Kind] <> 0")
            .HasDatabaseName("ux_crm_stages_pipeline_final");
    }
}

internal sealed class CrmReferenceConfiguration : IEntityTypeConfiguration<CrmReference>
{
    public void Configure(EntityTypeBuilder<CrmReference> b)
    {
        b.ToTable("crm_references", t => t.HasCheckConstraint("ck_crm_references_kind", "[Kind] IN (1, 2)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Name).HasMaxLength(CrmReference.NameMaxLength).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_references_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.Name }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_crm_references_org_kind_name_active");
    }
}

internal sealed class CrmLeadConfiguration : IEntityTypeConfiguration<CrmLead>
{
    public void Configure(EntityTypeBuilder<CrmLead> b)
    {
        b.ToTable("crm_leads", t =>
        {
            t.HasCheckConstraint("ck_crm_leads_status", "[Status] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_crm_leads_amount", "[ExpectedAmount] IS NULL OR [ExpectedAmount] >= 0");
            t.HasCheckConstraint("ck_crm_leads_qualified",
                "([Status] = 3 AND [DealId] IS NOT NULL AND [ContactId] IS NOT NULL AND [CounterpartyId] IS NOT NULL)"
                + " OR ([Status] <> 3 AND [DealId] IS NULL AND [ContactId] IS NULL AND [CounterpartyId] IS NULL)");
            t.HasCheckConstraint("ck_crm_leads_rejected", "([Status] = 4 AND [RejectReasonId] IS NOT NULL) OR ([Status] <> 4 AND [RejectReasonId] IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Title).HasMaxLength(CrmLead.TitleMaxLength).IsRequired();
        b.Property(x => x.ContactName).HasMaxLength(CrmLead.NameMaxLength);
        b.Property(x => x.Phone).HasColumnType("varchar(16)");
        b.Property(x => x.Email).HasMaxLength(CrmText.EmailMaxLength);
        b.Property(x => x.Messenger).HasMaxLength(CrmText.MessengerMaxLength);
        b.Property(x => x.CompanyName).HasMaxLength(CrmLead.NameMaxLength);
        b.Property(x => x.Inn).HasColumnType("varchar(12)");
        b.Property(x => x.Need).HasMaxLength(CrmLead.NeedMaxLength);
        b.Property(x => x.ExpectedAmount).HasColumnType("decimal(19,4)");
        b.Property(x => x.CurrencyCode).HasColumnType("char(3)").IsRequired();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RejectComment).HasMaxLength(CrmLead.CommentMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.ClosedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.IsClosed);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_leads_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_crm_leads_org_number");
        Ref<CrmReference>(b, x => new { x.OrganizationId, x.SourceId }, "fk_crm_leads_source");
        Ref<CrmReference>(b, x => new { x.OrganizationId, x.RejectReasonId }, "fk_crm_leads_reject_reason");
        Ref<Lookup>(b, x => new { x.OrganizationId, x.ChannelId }, "fk_crm_leads_channel");
        Ref<CrmContact>(b, x => new { x.OrganizationId, x.ContactId }, "fk_crm_leads_contact");
        Ref<Counterparty>(b, x => new { x.OrganizationId, x.CounterpartyId }, "fk_crm_leads_counterparty");
        Ref<CrmDeal>(b, x => new { x.OrganizationId, x.DealId }, "fk_crm_leads_deal");
        Ref<CrmLead>(b, x => new { x.OrganizationId, x.MergedIntoLeadId }, "fk_crm_leads_merged_into");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_leads_responsible");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_leads_created_by");

        // Списки и поиск дубликатов (ТЗ CRM §8): по ответственному и статусу, по нормализованным телефону, почте и ИНН.
        b.HasIndex(x => new { x.OrganizationId, x.ResponsibleUserId, x.Status }).HasDatabaseName("ix_crm_leads_org_responsible_status");
        b.HasIndex(x => new { x.OrganizationId, x.Phone }).HasFilter("[Phone] IS NOT NULL").HasDatabaseName("ix_crm_leads_org_phone");
        b.HasIndex(x => new { x.OrganizationId, x.Email }).HasFilter("[Email] IS NOT NULL").HasDatabaseName("ix_crm_leads_org_email");
        b.HasIndex(x => new { x.OrganizationId, x.Inn }).HasFilter("[Inn] IS NOT NULL").HasDatabaseName("ix_crm_leads_org_inn");

        // Повторная квалификация невозможна и в обход приложения: одна сделка — один лид.
        b.HasIndex(x => x.DealId).IsUnique().HasFilter("[DealId] IS NOT NULL").HasDatabaseName("ux_crm_leads_deal");
    }

    internal static void Ref<TPrincipal>(EntityTypeBuilder<CrmLead> b, System.Linq.Expressions.Expression<Func<CrmLead, object?>> key, string name)
        where TPrincipal : class =>
        b.HasOne<TPrincipal>().WithMany().HasForeignKey(key).HasPrincipalKey(new[] { "OrganizationId", "Id" })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(name);
}

internal sealed class CrmContactConfiguration : IEntityTypeConfiguration<CrmContact>
{
    public void Configure(EntityTypeBuilder<CrmContact> b)
    {
        b.ToTable("crm_contacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FullName).HasMaxLength(CrmContact.NameMaxLength).IsRequired();
        b.Property(x => x.Position).HasMaxLength(CrmContact.PositionMaxLength);
        b.Property(x => x.Phone).HasColumnType("varchar(16)");
        b.Property(x => x.Email).HasMaxLength(CrmText.EmailMaxLength);
        b.Property(x => x.Messenger).HasMaxLength(CrmText.MessengerMaxLength);
        b.Property(x => x.Comment).HasMaxLength(CrmContact.CommentMaxLength);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_contacts_org_id");
        b.HasOne<Counterparty>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CounterpartyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_contacts_counterparty");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_contacts_responsible");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_contacts_created_by");
        b.HasIndex(x => new { x.OrganizationId, x.CounterpartyId }).HasDatabaseName("ix_crm_contacts_org_counterparty");
        b.HasIndex(x => new { x.OrganizationId, x.Phone }).HasFilter("[Phone] IS NOT NULL").HasDatabaseName("ix_crm_contacts_org_phone");
        b.HasIndex(x => new { x.OrganizationId, x.Email }).HasFilter("[Email] IS NOT NULL").HasDatabaseName("ix_crm_contacts_org_email");
    }
}

internal sealed class CrmDealConfiguration : IEntityTypeConfiguration<CrmDeal>
{
    public void Configure(EntityTypeBuilder<CrmDeal> b)
    {
        b.ToTable("crm_deals", t =>
        {
            t.HasCheckConstraint("ck_crm_deals_outcome", "[Outcome] IN (1, 2, 3)");
            t.HasCheckConstraint("ck_crm_deals_amount", "[Amount] >= 0");
            t.HasCheckConstraint("ck_crm_deals_probability", "[Probability] BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_crm_deals_lost", "([Outcome] = 3 AND [LossReasonId] IS NOT NULL) OR ([Outcome] <> 3 AND [LossReasonId] IS NULL)");
            t.HasCheckConstraint("ck_crm_deals_closed", "([Outcome] = 1 AND [ClosedAtUtc] IS NULL) OR ([Outcome] <> 1 AND [ClosedAtUtc] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(30).IsRequired();
        b.Property(x => x.Title).HasMaxLength(CrmDeal.TitleMaxLength).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.Property(x => x.CurrencyCode).HasColumnType("char(3)").IsRequired();
        b.Property(x => x.Outcome).HasConversion<byte>();
        b.Property(x => x.LossComment).HasMaxLength(CrmDeal.CommentMaxLength);
        b.Property(x => x.StageEnteredAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.ClosedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.WeightedAmount);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_crm_deals_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_crm_deals_org_number");

        // Этап — только своей воронки: ключ (организация, воронка, этап).
        b.HasOne<CrmStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PipelineId, x.StageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.PipelineId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deals_stage");
        b.HasOne<CrmPipeline>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PipelineId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deals_pipeline");
        Ref<Counterparty>(b, x => new { x.OrganizationId, x.CounterpartyId }, "fk_crm_deals_counterparty");
        Ref<CrmContact>(b, x => new { x.OrganizationId, x.ContactId }, "fk_crm_deals_contact");
        Ref<CrmReference>(b, x => new { x.OrganizationId, x.SourceId }, "fk_crm_deals_source");
        Ref<CrmReference>(b, x => new { x.OrganizationId, x.LossReasonId }, "fk_crm_deals_loss_reason");
        Ref<Lookup>(b, x => new { x.OrganizationId, x.ProjectId }, "fk_crm_deals_project");
        Ref<Lookup>(b, x => new { x.OrganizationId, x.ChannelId }, "fk_crm_deals_channel");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deals_responsible");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deals_created_by");

        // Канбан и списки (ТЗ CRM §8): по воронке и этапу, по ответственному и итогу.
        b.HasIndex(x => new { x.OrganizationId, x.PipelineId, x.StageId }).HasDatabaseName("ix_crm_deals_org_pipeline_stage");
        b.HasIndex(x => new { x.OrganizationId, x.ResponsibleUserId, x.Outcome }).HasDatabaseName("ix_crm_deals_org_responsible_outcome");
        b.HasIndex(x => new { x.OrganizationId, x.CounterpartyId }).HasDatabaseName("ix_crm_deals_org_counterparty");

        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");
        b.HasMany(x => x.Participants).WithOne().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Participants).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_participants");
    }

    private static void Ref<TPrincipal>(EntityTypeBuilder<CrmDeal> b, System.Linq.Expressions.Expression<Func<CrmDeal, object?>> key, string name)
        where TPrincipal : class =>
        b.HasOne<TPrincipal>().WithMany().HasForeignKey(key).HasPrincipalKey(new[] { "OrganizationId", "Id" })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(name);
}

internal sealed class CrmDealLineConfiguration : IEntityTypeConfiguration<CrmDealLine>
{
    public void Configure(EntityTypeBuilder<CrmDealLine> b)
    {
        b.ToTable("crm_deal_lines", t =>
        {
            t.HasCheckConstraint("ck_crm_deal_lines_quantity", "[Quantity] > 0");
            t.HasCheckConstraint("ck_crm_deal_lines_price", "[Price] >= 0 AND [Amount] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,6)");
        b.Property(x => x.Price).HasColumnType("decimal(19,4)");
        b.Property(x => x.Amount).HasColumnType("decimal(19,4)");
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_crm_deal_lines_item");
        b.HasIndex(x => new { x.DealId, x.ItemId }).IsUnique().HasDatabaseName("ux_crm_deal_lines_deal_item");
    }
}

internal sealed class CrmDealParticipantConfiguration : IEntityTypeConfiguration<CrmDealParticipant>
{
    public void Configure(EntityTypeBuilder<CrmDealParticipant> b)
    {
        b.ToTable("crm_deal_participants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_participants_user");
        b.HasIndex(x => new { x.DealId, x.UserId }).IsUnique().HasDatabaseName("ux_crm_deal_participants_deal_user");
        b.HasIndex(x => x.UserId).HasDatabaseName("ix_crm_deal_participants_user");
    }
}

internal sealed class CrmDealStageHistoryConfiguration : IEntityTypeConfiguration<CrmDealStageHistory>
{
    public void Configure(EntityTypeBuilder<CrmDealStageHistory> b)
    {
        // Только вставка (D602): изменение и удаление запрещает триггер.
        b.ToTable("crm_deal_stage_history", t =>
        {
            t.HasTrigger("tr_crm_deal_stage_history_immutable");
            t.HasCheckConstraint("ck_crm_deal_stage_history_days", "[DaysInStage] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.ChangedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.DaysInStage).HasColumnType("decimal(9,2)");
        b.HasOne<CrmDeal>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DealId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_stage_history_deal");
        b.HasOne<CrmStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.FromStageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_stage_history_from");
        b.HasOne<CrmStage>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ToStageId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_stage_history_to");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_stage_history_user");
        b.HasIndex(x => new { x.OrganizationId, x.DealId, x.ChangedAtUtc }).HasDatabaseName("ix_crm_deal_stage_history_deal");
    }
}

internal sealed class CrmDealOrderConfiguration : IEntityTypeConfiguration<CrmDealOrder>
{
    public void Configure(EntityTypeBuilder<CrmDealOrder> b)
    {
        b.ToTable("crm_deal_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<CrmDeal>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DealId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_orders_deal");
        b.HasOne<SalesOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SalesOrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_orders_sales_order");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_crm_deal_orders_created_by");
        b.HasIndex(x => x.SalesOrderId).IsUnique().HasDatabaseName("ux_crm_deal_orders_sales_order");
        b.HasIndex(x => new { x.OrganizationId, x.DealId }).HasDatabaseName("ix_crm_deal_orders_deal");
    }
}
