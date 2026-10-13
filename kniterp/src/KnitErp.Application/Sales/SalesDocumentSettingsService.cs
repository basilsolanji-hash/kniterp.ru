using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

public sealed record SalesDocumentImageDto(SalesDocumentImageKind Kind, string ContentType, int SizeBytes, DateTime UploadedAtUtc, string? DataUri)
{
    public string KindName => SalesDocumentImage.KindName(Kind);
}

/// <summary>
/// Оформление документов юрлица (D1120). Подписанты — из реквизитов юрлица (раздел «Юрлица и счета»): руководитель (должность, ФИО)
/// и главный бухгалтер; у ИП подписывает сам предприниматель. Images — без права «Цены и суммы» или изменения организации — без содержимого.
/// </summary>
public sealed record SalesDocumentEntityDto(
    long LegalEntityId, string Name, bool IsDefault, bool SoleProprietor, string? DirectorPosition, string? DirectorName, string? AccountantName,
    bool HasBankAccount, SalesDocumentFlags Flags, byte[]? RowVersion, IReadOnlyList<SalesDocumentImageDto> Images);

public sealed record SalesMailTemplateDto(SalesMailKind Kind, bool AutoSend, string Subject, string Body, byte[]? RowVersion)
{
    public string KindName => SalesMailMessage.KindName(Kind);
}

public sealed record SalesMailStageOptionDto(long Id, string Name, bool IsArchived, bool Selected);

public sealed record SalesDocumentSettingsDto(
    bool CanEdit, bool MailConfigured, IReadOnlyList<KnitErp.Application.Structure.LookupDto> Entities, IReadOnlyList<SalesMailTemplateDto> Templates,
    IReadOnlyList<SalesMailStageOptionDto> Stages);

/// <summary>
/// Настройки документов продаж (D1120, D1124): логотип, печать, факсимиле подписей и что выводить в счёте, УПД и заказе — по каждому
/// своему юрлицу; шаблоны писем клиенту и автоотправка. Смотреть — «Продажи: просмотр», менять — «Организация: изменение».
/// Загрузка, замена и удаление изображений пишутся в журнал (без содержимого).
/// </summary>
public sealed class SalesDocumentSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender email)
{
    public static readonly IReadOnlyList<SalesMailKind> MailKinds =
        [SalesMailKind.Order, SalesMailKind.Invoice, SalesMailKind.Upd, SalesMailKind.Stage, SalesMailKind.PaymentReminder, SalesMailKind.PaymentOverdue]; // D148

    public async Task<SalesDocumentSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var org = ctx.OrganizationId;
        var entities = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == org && !e.IsArchived)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.ShortName)
            .Select(e => new KnitErp.Application.Structure.LookupDto(e.Id, e.ShortName)).ToListAsync(ct);
        var templates = await db.SalesMailTemplates.AsNoTracking().Where(t => t.OrganizationId == org).ToListAsync(ct);
        var selected = await db.SalesMailStages.AsNoTracking().Where(s => s.OrganizationId == org).Select(s => s.StageId).ToListAsync(ct);
        var stages = await db.SalesOrderStages.AsNoTracking().Where(s => s.OrganizationId == org).OrderBy(s => s.SortOrder)
            .Select(s => new { s.Id, s.Name, s.IsArchived }).ToListAsync(ct);
        return new SalesDocumentSettingsDto(ctx.Permissions.Has(Permissions.OrganizationEdit), email.IsConfigured, entities,
            MailKinds.Select(k => templates.SingleOrDefault(t => t.Kind == k) is { } t
                ? new SalesMailTemplateDto(k, t.AutoSend, t.Subject, t.Body, t.RowVersion)
                : new SalesMailTemplateDto(k, false, SalesMailText.Default(k).Subject, SalesMailText.Default(k).Body, null)).ToList(),
            stages.Where(s => !s.IsArchived || selected.Contains(s.Id)).Select(s => new SalesMailStageOptionDto(s.Id, s.Name, s.IsArchived, selected.Contains(s.Id)))
                .ToList());
    }

    public async Task<SalesDocumentEntityDto> EntityAsync(long legalEntityId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var entity = await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == legalEntityId && e.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Юрлицо");
        var settings = await db.SalesDocumentSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId && s.LegalEntityId == entity.Id, ct);
        var hasAccount = await db.LegalEntityAccounts.AsNoTracking().AnyAsync(a => a.OrganizationId == ctx.OrganizationId && a.LegalEntityId == entity.Id, ct);
        // Печать и подписи видит тот, кто и так печатает документы с ними (цены), или тот, кто их загружает.
        var showContent = ctx.Permissions.Has(Permissions.PriceView) || ctx.Permissions.Has(Permissions.OrganizationEdit);
        var images = await db.SalesDocumentImages.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && i.LegalEntityId == entity.Id).OrderBy(i => i.Kind).ToListAsync(ct);
        return new SalesDocumentEntityDto(entity.Id, entity.ShortName, entity.IsDefault, entity.IsSoleProprietor, entity.DirectorPosition, entity.DirectorName,
            entity.AccountantName, hasAccount, settings?.Flags ?? SalesDocumentFlags.Default, settings?.RowVersion,
            images.Select(i => new SalesDocumentImageDto(i.Kind, i.ContentType, i.SizeBytes, i.UploadedAtUtc,
                showContent ? new SalesPrintImage(i.ContentType, i.Content).DataUri : null)).ToList());
    }

    public async Task SaveFlagsAsync(long legalEntityId, SalesDocumentFlags flags, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await FindEntityAsync(ctx, legalEntityId, ct);
        var settings = await db.SalesDocumentSettings.SingleOrDefaultAsync(s => s.OrganizationId == ctx.OrganizationId && s.LegalEntityId == entity.Id, ct);
        var before = (settings?.Flags ?? SalesDocumentFlags.Default).Describe();
        if (settings is null)
        {
            settings = SalesDocumentSettings.Create(ctx.OrganizationId, entity.Id);
            settings.Set(flags);
            db.SalesDocumentSettings.Add(settings);
        }
        else
        {
            settings.EnsureVersion(settings.RowVersion, rowVersion ?? []);
            settings.Set(flags);
        }

        Audit(ctx, AuditActions.SalesDocumentSettingsChanged, "LegalEntity", entity.Id, before, flags.Describe(), entity.ShortName);
        await SaveAsync(ct);
    }

    /// <summary>Загрузить или заменить изображение вида (PNG/JPEG по сигнатуре, до 1 МБ).</summary>
    public async Task UploadImageAsync(long legalEntityId, SalesDocumentImageKind kind, string? contentType, byte[] content, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await FindEntityAsync(ctx, legalEntityId, ct);
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("sales.document_image.kind", "Неизвестный вид изображения.");
        }

        var image = await db.SalesDocumentImages.SingleOrDefaultAsync(i => i.OrganizationId == ctx.OrganizationId && i.LegalEntityId == entity.Id && i.Kind == kind, ct);
        var before = image is null ? null : $"{image.ContentType}, {image.SizeBytes / 1024 + 1} КБ";
        try
        {
            if (image is null)
            {
                image = SalesDocumentImage.Create(ctx.OrganizationId, entity.Id, kind, contentType, content, clock.UtcNow, ctx.UserId);
                db.SalesDocumentImages.Add(image);
            }
            else
            {
                image.Replace(contentType, content, clock.UtcNow, ctx.UserId);
            }
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.SalesDocumentImageChanged, "LegalEntity", entity.Id, before, $"{image.ContentType}, {image.SizeBytes / 1024 + 1} КБ",
            $"{entity.ShortName}: {SalesDocumentImage.KindName(kind)} — {(before is null ? "загружено" : "заменено")}");
        await SaveAsync(ct);
    }

    public async Task DeleteImageAsync(long legalEntityId, SalesDocumentImageKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var entity = await FindEntityAsync(ctx, legalEntityId, ct);
        var image = await db.SalesDocumentImages.SingleOrDefaultAsync(i => i.OrganizationId == ctx.OrganizationId && i.LegalEntityId == entity.Id && i.Kind == kind, ct)
                    ?? throw new NotFoundException(SalesDocumentImage.KindName(kind));
        db.SalesDocumentImages.Remove(image);
        Audit(ctx, AuditActions.SalesDocumentImageChanged, "LegalEntity", entity.Id, $"{image.ContentType}, {image.SizeBytes / 1024 + 1} КБ", null,
            $"{entity.ShortName}: {SalesDocumentImage.KindName(kind)} — удалено");
        await SaveAsync(ct);
    }

    public async Task SaveTemplateAsync(SalesMailKind kind, bool autoSend, string? subject, string? body, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var template = await db.SalesMailTemplates.SingleOrDefaultAsync(t => t.OrganizationId == ctx.OrganizationId && t.Kind == kind, ct);
        var before = template is null ? "по умолчанию, вручную" : $"{(template.AutoSend ? "автоматически" : "вручную")}: {template.Subject}";
        if (template is null)
        {
            template = SalesMailTemplate.Create(ctx.OrganizationId, kind);
            template.Set(autoSend, subject, body);
            db.SalesMailTemplates.Add(template);
        }
        else
        {
            template.EnsureVersion(template.RowVersion, rowVersion ?? []);
            try
            {
                template.Set(autoSend, subject, body);
            }
            catch
            {
                db.DiscardChanges();
                throw;
            }
        }

        Audit(ctx, AuditActions.SalesMailSettingsChanged, nameof(SalesMailTemplate), (long)kind, before,
            $"{(template.AutoSend ? "автоматически" : "вручную")}: {template.Subject}", $"Письмо «{SalesMailMessage.KindName(kind)}»");
        await SaveAsync(ct);
    }

    /// <summary>Этапы заказа, о переходе на которые клиенту уходит письмо (если включена автоотправка писем о статусе).</summary>
    public async Task SetMailStagesAsync(IReadOnlyCollection<long> stageIds, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var ids = stageIds.Distinct().ToList();
        var known = await db.SalesOrderStages.AsNoTracking().Where(s => s.OrganizationId == ctx.OrganizationId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        if (known.Count != ids.Count)
        {
            throw new NotFoundException("Этап заказа");
        }

        var current = await db.SalesMailStages.Where(s => s.OrganizationId == ctx.OrganizationId).ToListAsync(ct);
        db.SalesMailStages.RemoveRange(current.Where(c => !ids.Contains(c.StageId)));
        foreach (var id in ids.Where(id => current.All(c => c.StageId != id)))
        {
            db.SalesMailStages.Add(SalesMailStage.Create(ctx.OrganizationId, id));
        }

        Audit(ctx, AuditActions.SalesMailSettingsChanged, nameof(SalesMailStage), 0, null,
            known.Count == 0 ? "—" : string.Join(", ", known.Values.Order()), "Письма о статусе заказа: этапы");
        await SaveAsync(ct);
    }

    private async Task<KnitErp.Domain.Organizations.LegalEntity> FindEntityAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.LegalEntities.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Юрлицо");

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
