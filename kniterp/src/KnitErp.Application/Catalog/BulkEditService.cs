using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>
/// Что менять массово (D318). У каждого поля флажок «менять»: без него поле не трогается. Пустое значение при флажке — очистить
/// (группа — «без группы», маркировка — «без маркировки»); НДС очистить нельзя, если он обязателен (услуга, настройки каталога).
/// </summary>
public sealed record BulkItemChanges(
    bool SetGroup = false, long? GroupId = null,
    bool SetVat = false, long? VatRateId = null,
    bool SetTnVed = false, string? TnVedCode = null,
    bool SetCountry = false, string? OriginCountryCode = null, string? OriginCountryName = null,
    bool SetTracking = false, TrackingType Tracking = TrackingType.None)
{
    public bool Any => SetGroup || SetVat || SetTnVed || SetCountry || SetTracking;
}

/// <summary>Позиция, которую не изменили, и почему.</summary>
public sealed record BulkSkip(string Code, string Name, string Reason);

public sealed record BulkEditResult(int Selected, int Changed, int Unchanged, IReadOnlyList<BulkSkip> Skipped);

/// <summary>
/// Массовое изменение позиций номенклатуры (D318): отмеченные в списке или все по отбору (до 50 000), одна транзакция — всё или ничего
/// при ошибке базы; позиции, к которым изменение неприменимо (архив, маркировка у услуги, обязательный НДС), пропускаются с причиной.
/// Каждое изменение поля — в журнал аудита. Право — «Справочники: изменение».
/// </summary>
public sealed class BulkEditService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int MaxItems = 50_000;
    private const int Chunk = 1000;

    public async Task<BulkEditResult> ApplyAsync(IReadOnlyList<long> itemIds, BulkItemChanges changes, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogEdit, ct);
        var org = ctx.OrganizationId;
        if (!changes.Any)
        {
            throw new BusinessRuleException("catalog.bulk.nothing", "Отметьте, что изменить.");
        }

        var ids = itemIds.Distinct().ToList();
        if (ids.Count is 0 or > MaxItems)
        {
            throw new BusinessRuleException("catalog.bulk.count", "Выберите от 1 до 50 000 позиций.");
        }

        // Проверки значений — до изменения позиций.
        string? groupPath = null;
        if (changes.SetGroup && changes.GroupId is { } gid)
        {
            var group = await db.ItemGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid && g.OrganizationId == org, ct)
                        ?? throw new NotFoundException("Группа номенклатуры");
            if (group.IsArchived)
            {
                throw new BusinessRuleException("catalog.archived", $"Группа «{group.Name}» в архиве.");
            }

            groupPath = group.Name;
        }

        if (changes.SetVat && changes.VatRateId is { } vid && !await db.VatRates.AnyAsync(r => r.Id == vid && r.OrganizationId == org && !r.IsArchived, ct))
        {
            throw new NotFoundException("Ставка НДС");
        }

        var tnVed = changes.SetTnVed ? Clean(changes.TnVedCode) : null;
        if (tnVed is not null && (tnVed.Length is not (4 or 6 or 8 or 10) || !tnVed.All(char.IsAsciiDigit)))
        {
            throw new BusinessRuleException("catalog.group_defaults.tnved", "Код ТН ВЭД — 4, 6, 8 или 10 цифр.");
        }

        var country = changes.SetCountry ? Clean(changes.OriginCountryCode) : null;
        if (country is not null && (country.Length != 3 || !country.All(char.IsAsciiDigit)))
        {
            throw new BusinessRuleException("catalog.group_defaults.country", "Код страны — 3 цифры по ОКСМ, например 643 — Россия.");
        }

        var settings = await db.CatalogSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct);
        if (changes.SetGroup && changes.GroupId is null && settings is { RequireGroup: true })
        {
            throw new BusinessRuleException("catalog.required.group", "Группа обязательна в настройках каталога: выберите группу позиции.");
        }

        var skipped = new List<BulkSkip>();
        int changed = 0, unchanged = 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            foreach (var chunk in ids.Chunk(Chunk))
            {
                var items = await db.Items.Where(i => i.OrganizationId == org && chunk.Contains(i.Id)).ToListAsync(ct);
                foreach (var item in items)
                {
                    if (item.IsArchived)
                    {
                        skipped.Add(new BulkSkip(item.Code, item.Name, "в архиве"));
                        continue;
                    }

                    if (changes.SetVat && changes.VatRateId is null && (item.Type == ItemType.Service || settings is { RequireVat: true }))
                    {
                        skipped.Add(new BulkSkip(item.Code, item.Name, "ставка НДС обязательна"));
                        continue;
                    }

                    if (changes.SetTracking && changes.Tracking != TrackingType.None && !ItemTypes.IsStocked(item.Type))
                    {
                        skipped.Add(new BulkSkip(item.Code, item.Name, "услугу и комплект не маркируют"));
                        continue;
                    }

                    if (changes.SetGroup && item.ParentItemId is not null)
                    {
                        skipped.Add(new BulkSkip(item.Code, item.Name, "модификация в группе основной позиции"));
                        continue;
                    }

                    var log = new List<KnitErp.Domain.Organizations.FieldChange>();
                    if (changes.SetVat && item.SetVatRate(changes.VatRateId) is { } vatChange)
                    {
                        log.Add(vatChange);
                    }

                    if (changes.SetGroup || changes.SetTnVed || changes.SetCountry)
                    {
                        var d = item.Details;
                        log.AddRange(item.SetDetails(d with
                        {
                            GroupId = changes.SetGroup ? changes.GroupId : d.GroupId,
                            TnVedCode = changes.SetTnVed ? tnVed : d.TnVedCode,
                            OriginCountryCode = changes.SetCountry ? country : d.OriginCountryCode,
                            OriginCountryName = changes.SetCountry ? (country is null ? null : Clean(changes.OriginCountryName)) : d.OriginCountryName,
                        }));
                    }

                    if (changes.SetTracking && ItemTypes.IsStocked(item.Type))
                    {
                        log.AddRange(item.SetTrade(item.Trade with { IsMarked = changes.Tracking != TrackingType.None, TrackingType = changes.Tracking }));
                    }

                    if (log.Count == 0)
                    {
                        unchanged++;
                        continue;
                    }

                    changed++;
                    foreach (var c in log)
                    {
                        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogChanged, nameof(Item),
                            item.Id.ToString(CultureInfo.InvariantCulture), c.Before, c.After, $"Массовое изменение: {c.Field}", currentUser.CorrelationId));
                    }
                }

                skipped.AddRange(chunk.Except(items.Select(i => i.Id)).Select(id => new BulkSkip(id.ToString(CultureInfo.InvariantCulture), "", "не найдена")));
                await db.SaveChangesAsync(ct);
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.CatalogChanged, "ItemBulkEdit", null, null,
                $"выбрано {ids.Count}: изменено {changed}, без изменений {unchanged}, пропущено {skipped.Count}" + (groupPath is null ? "" : $"; группа «{groupPath}»"),
                "Массовое изменение номенклатуры", currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        return new BulkEditResult(ids.Count, changed, unchanged, skipped.Take(500).ToList());
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
