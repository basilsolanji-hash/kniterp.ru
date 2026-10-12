using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Настройки каталога для экрана. RowVersion пустой — строки ещё нет, действуют значения по умолчанию.</summary>
public sealed record CatalogSettingsDto(
    bool ServicesEnabled, bool BundlesEnabled, string? AutoCodePrefix, byte AutoCodeDigits, int NextNumber, ItemType DefaultItemType, string? NextCode,
    bool CanEdit, byte[] RowVersion, bool RequireGroup = false, bool RequireVat = false, CardBlocks HiddenBlocks = CardBlocks.None,
    CatalogCodesDto? Codes = null)
{
    public bool Shows(CardBlocks block) => (HiddenBlocks & block) == 0;

    public bool IsEnabled(ItemType type) => type switch
    {
        ItemType.Service => ServicesEnabled,
        ItemType.Bundle => BundlesEnabled,
        _ => true,
    };
}

/// <summary>D315: автоматические штрихкоды и артикулы; NextBarcode и ArticleExample — примеры следующих значений.</summary>
public sealed record CatalogCodesDto(
    bool AutoBarcode, byte BarcodePrefix, long NextBarcodeNumber, string? NextBarcode, bool AutoArticle, string? ArticleTemplate, byte ArticleDigits,
    int NextArticleNumber, string? ArticleExample);

public sealed record CatalogCodesCommand(
    bool AutoBarcode, byte BarcodePrefix, long NextBarcodeNumber, bool AutoArticle, string? ArticleTemplate, byte ArticleDigits, int NextArticleNumber);

public sealed record CatalogSettingsCommand(
    bool ServicesEnabled, bool BundlesEnabled, string? AutoCodePrefix, byte AutoCodeDigits, int NextNumber, ItemType DefaultItemType,
    bool RequireGroup = false, bool RequireVat = false, CardBlocks HiddenBlocks = CardBlocks.None);

/// <summary>
/// Общие настройки каталога организации (D312): смотреть — «Справочники: просмотр», менять — «Справочники: изменение».
/// Изменение пишется в журнал аудита (было → стало).
/// </summary>
public sealed class CatalogSettingsService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<CatalogSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var s = await db.CatalogSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct)
                ?? CatalogSettings.Default(ctx.OrganizationId);
        return new CatalogSettingsDto(s.ServicesEnabled, s.BundlesEnabled, s.AutoCodePrefix, s.AutoCodeDigits, s.NextNumber, s.DefaultItemType,
            s.PreviewCode(), ctx.Permissions.Has(Permissions.CatalogEdit), s.RowVersion, s.RequireGroup, s.RequireVat, s.HiddenBlocks,
            new CatalogCodesDto(s.AutoBarcode, s.BarcodePrefix, s.NextBarcodeNumber, s.PreviewBarcode(), s.AutoArticle, s.ArticleTemplateText, s.ArticleDigits,
                s.NextArticleNumber, s.ArticleTemplateText is null ? null : s.PreviewArticle(s.PreviewCode() ?? "ТВ-0001")));
    }

    public async Task SaveAsync(CatalogSettingsCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var s = await db.CatalogSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (s is null)
        {
            s = CatalogSettings.Default(ctx.OrganizationId);
            db.CatalogSettings.Add(s);
        }
        else
        {
            s.EnsureVersion(s.RowVersion, rowVersion);
        }

        var before = Describe(s);
        try
        {
            s.Set(cmd.ServicesEnabled, cmd.BundlesEnabled, cmd.AutoCodePrefix, cmd.AutoCodeDigits, cmd.NextNumber, cmd.DefaultItemType, clock.UtcNow);
            s.SetCard(cmd.RequireGroup, cmd.RequireVat, cmd.HiddenBlocks, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(CatalogSettings),
            ctx.OrganizationId.ToString(), before, Describe(s), "Настройки каталога", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>D315: автоматические штрихкоды и артикулы — отдельным сохранением (своя форма на вкладке «Общие»).</summary>
    public async Task SaveCodesAsync(CatalogCodesCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var s = await db.CatalogSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (s is null)
        {
            s = CatalogSettings.Default(ctx.OrganizationId);
            db.CatalogSettings.Add(s);
        }
        else
        {
            s.EnsureVersion(s.RowVersion, rowVersion);
        }

        var before = DescribeCodes(s);
        try
        {
            s.SetCodes(cmd.AutoBarcode, cmd.BarcodePrefix, cmd.NextBarcodeNumber, cmd.AutoArticle, cmd.ArticleTemplate, cmd.ArticleDigits, cmd.NextArticleNumber,
                clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(CatalogSettings),
            ctx.OrganizationId.ToString(), before, DescribeCodes(s), "Автоматические штрихкоды и артикулы", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    private static string DescribeCodes(CatalogSettings s) =>
        $"штрихкод: {(s.AutoBarcode ? "авто" : "вручную")}, следующий {s.PreviewBarcode() ?? "—"}; артикул: {(s.AutoArticle ? s.ArticleTemplateText : "вручную")}";

    private static string Describe(CatalogSettings s) =>
        $"услуги: {(s.ServicesEnabled ? "да" : "нет")}; комплекты: {(s.BundlesEnabled ? "да" : "нет")}; автокод: {s.PreviewCode() ?? "нет"}; " +
        $"тип по умолчанию: {ItemTypes.Name(s.DefaultItemType)}; обязательны: {(s.RequireGroup ? "группа " : "")}{(s.RequireVat ? "НДС" : "")}; " +
        $"скрыты блоки: {(s.HiddenBlocks == CardBlocks.None ? "нет" : s.HiddenBlocks.ToString())}";
}
