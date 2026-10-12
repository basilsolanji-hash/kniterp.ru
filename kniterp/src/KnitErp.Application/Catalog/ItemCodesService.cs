using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>Итог массовой генерации: сколько выдано и диапазон кодов.</summary>
public sealed record BarcodeBatchResult(int Created, string? First, string? Last);

/// <summary>
/// Автоматические штрихкоды и артикулы (D315): внутренний EAN-13 (префикс 20–29) по счётчику настроек каталога — в карточке, массово
/// для позиций без штрихкода и при создании позиции; артикул по шаблону. Занятые коды и артикулы пропускаются; право —
/// «Справочники: изменение»; всё пишется в журнал аудита.
/// </summary>
public sealed class ItemCodesService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxBatch = 50_000;

    /// <summary>Выдать позиции следующий внутренний EAN-13.</summary>
    public async Task<string> GenerateBarcodeAsync(long itemId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        try
        {
            var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Номенклатура");
            if (item.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", "Позиция в архиве.");
            }

            if (await db.ItemBarcodes.CountAsync(b => b.ItemId == itemId, ct) >= ItemBarcode.MaxPerItem)
            {
                throw new BusinessRuleException("catalog.barcode.too_many", $"У позиции не больше {ItemBarcode.MaxPerItem} штрихкодов.");
            }

            var settings = await SettingsAsync(ctx.OrganizationId, ct);
            var code = await NextFreeBarcodeAsync(db, ctx.OrganizationId, settings, null, ct);
            db.ItemBarcodes.Add(ItemBarcode.Create(ctx.OrganizationId, itemId, BarcodeType.Ean13, code));
            Audit(ctx, itemId.ToString(CultureInfo.InvariantCulture), $"EAN-13 {code}", "Штрихкод создан автоматически");
            await db.SaveOrConflictAsync(ct);
            return code;
        }
        catch
        {
            db.DiscardChanges(); // сдвинутый счётчик не остаётся в контексте вкладки
            throw;
        }
    }

    /// <summary>Внутренние EAN-13 всем действующим позициям без штрихкода (кроме услуг) — одной транзакцией.</summary>
    public async Task<BarcodeBatchResult> GenerateMissingBarcodesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var org = ctx.OrganizationId;
        try
        {
            var items = await db.Items.AsNoTracking()
                .Where(i => i.OrganizationId == org && !i.IsArchived && i.Type != ItemType.Service && !db.ItemBarcodes.Any(b => b.ItemId == i.Id))
                .OrderBy(i => i.Id).Select(i => i.Id).Take(MaxBatch).ToListAsync(ct);
            if (items.Count == 0)
            {
                return new BarcodeBatchResult(0, null, null);
            }

            var settings = await SettingsAsync(org, ct);
            var taken = await TakenAsync(db, org, ct);
            var codes = new List<string>(items.Count);
            foreach (var itemId in items)
            {
                var code = await NextFreeBarcodeAsync(db, org, settings, taken, ct);
                taken.Add(code);
                codes.Add(code);
                db.ItemBarcodes.Add(ItemBarcode.Create(org, itemId, BarcodeType.Ean13, code));
            }

            Audit(ctx, null, $"{codes.Count} шт., {codes[0]} … {codes[^1]}", "Штрихкоды созданы автоматически для позиций без штрихкода");
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return new BarcodeBatchResult(codes.Count, codes[0], codes[^1]);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Следующий свободный внутренний EAN-13: занятые штрихкодами позиций и упаковок пропускаются.</summary>
    internal static async Task<string> NextFreeBarcodeAsync(IKnitErpDbContext db, long org, CatalogSettings settings, HashSet<string>? taken,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var code = settings.TakeBarcode();
            var busy = taken is not null
                ? taken.Contains(code)
                : await db.ItemBarcodes.AnyAsync(b => b.OrganizationId == org && b.Code == code, ct)
                  || await db.ItemPacks.AnyAsync(p => p.OrganizationId == org && p.Barcode == code, ct);
            if (!busy)
            {
                return code;
            }
        }

        throw new BusinessRuleException("catalog.barcode.range", "Номера внутренних штрихкодов закончились: смените префикс в настройках каталога.");
    }

    /// <summary>Артикул по шаблону; занятый другой позицией номер пропускается (при {CODE} артикул уникален сам).</summary>
    internal static async Task<string?> NextArticleAsync(IKnitErpDbContext db, long org, CatalogSettings settings, string itemCode, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var article = settings.TakeArticle(itemCode);
            if (article is null || !ArticleTemplate.UsesNumber(settings.ArticleTemplateText!)
                                || !await db.Items.AnyAsync(i => i.OrganizationId == org && i.Article == article, ct))
            {
                return article;
            }
        }

        throw new BusinessRuleException("catalog.article.exhausted", "Не удалось подобрать свободный артикул: увеличьте следующий номер в настройках каталога.");
    }

    private static async Task<HashSet<string>> TakenAsync(IKnitErpDbContext db, long org, CancellationToken ct)
    {
        var set = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org).Select(b => b.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        set.UnionWith(await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org && p.Barcode != null).Select(p => p.Barcode!).ToListAsync(ct));
        return set;
    }

    /// <summary>Строка настроек (создаётся со значениями по умолчанию, если её нет) — счётчик хранится в ней.</summary>
    private async Task<CatalogSettings> SettingsAsync(long org, CancellationToken ct)
    {
        var settings = await db.CatalogSettings.SingleOrDefaultAsync(s => s.OrganizationId == org, ct);
        if (settings is null)
        {
            settings = CatalogSettings.Default(org);
            db.CatalogSettings.Add(settings);
        }

        return settings;
    }

    private void Audit(AccessContext ctx, string? itemId, string after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogChanged, nameof(Item), itemId, null, after,
            reason, currentUser.CorrelationId));
}
