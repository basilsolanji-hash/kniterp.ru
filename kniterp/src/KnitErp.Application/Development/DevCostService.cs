using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Условия сценария расчёта (D745): цены вместо найденных (позиция → цена), замена материала (позиция → другая позиция), способ цены,
/// наценка или целевая цена, комментарий версии.
/// </summary>
public sealed record DevCostScenario(
    IReadOnlyDictionary<long, decimal>? Prices = null, IReadOnlyDictionary<long, long>? Replace = null, DevPriceMode Mode = DevPriceMode.Markup,
    decimal? MarkupPercent = null, decimal? TargetPrice = null, string? Note = null, long? ColorwayId = null);

/// <summary>Строка расчёта на экране: откуда цена («закупка», «каталог», «вручную», «нет цены») и можно ли заменить материал.</summary>
public sealed record DevCostLineDto(
    DevCostLineKind Kind, long? ItemId, long? OriginalItemId, string Name, string Unit, decimal Quantity, decimal WastePercent, decimal UnitPrice,
    decimal Amount, string PriceSource);

public sealed record DevCostRunDto(long Id, int Version, DateTime CreatedAtUtc, string CreatedBy, decimal Cost, decimal Price, decimal Margin, string? Note,
    DevPriceMode Mode, decimal? MarkupPercent);

public sealed record DevAccessoryDto(long Id, long ItemId, string Code, string Name, string Unit, decimal Quantity, byte[] RowVersion);

/// <summary>Расчёт себестоимости модели (D740–D745): строки, итоги, условия, версии, фурнитура, замечания.</summary>
public sealed record DevCostingDto(
    IReadOnlyList<DevCostLineDto> Lines, decimal Yarn, decimal Accessories, decimal Labor, decimal Overhead, decimal Cost, decimal Price, decimal Margin,
    decimal? MarginPercent, decimal? TargetCost, decimal? TargetPrice, decimal? VersusTargetCost, decimal OverheadPercent, decimal YarnWastePercent,
    decimal? LaborRatePerMinute, DevPriceMode Mode, decimal? MarkupPercent, IReadOnlyList<DevNote> Warnings, IReadOnlyList<DevCostRunDto> Runs,
    IReadOnlyList<DevAccessoryDto> AccessoryList, IReadOnlyList<DevOptionDto> Materials, IReadOnlyList<DevOptionDto> AccessoryOptions, bool CanEdit,
    IReadOnlyList<DevOptionDto>? Colorways = null, long? CurrentColorwayId = null);

/// <summary>
/// Себестоимость и цена модели (D740–D745, этап 5 ТЗ). Видит только право «Разработка: себестоимость» (`dev.cost.view`); сохранить версию
/// и менять фурнитуру — ещё и `dev.edit`. Итоги всегда считает сервер по своим данным — цифры с экрана не принимаются, только условия
/// сценария. Версии — только вставка; суммы в журнал аудита не пишутся (только факт расчёта и номер версии).
/// </summary>
public sealed class DevCostService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private static readonly ItemType[] AccessoryTypes = [ItemType.Accessory, ItemType.Packaging, ItemType.Material];

    public async Task<DevCostingDto> CostingAsync(long modelId, DevCostScenario? scenario = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevCostView, ct);
        var org = ctx.OrganizationId;
        var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var (lines, result, settings, warnings, s) = await ComputeAsync(org, m, scenario, ct);
        var runs = await (from r in db.DevCostRuns.AsNoTracking()
                          join u in db.Users.AsNoTracking() on r.CreatedByUserId equals u.Id
                          where r.OrganizationId == org && r.ModelId == modelId
                          orderby r.Version descending
                          select new DevCostRunDto(r.Id, r.Version, r.CreatedAtUtc, u.DisplayName, r.Cost, r.Price, r.Margin, r.Note, r.PriceMode, r.MarkupPercent))
            .Take(50).ToListAsync(ct);
        var accessories = await AccessoriesAsync(org, modelId, ct);
        var materials = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material))
            .OrderBy(i => i.Name).Take(3000).Select(i => new DevOptionDto(i.Id, i.Code + " — " + i.Name)).ToListAsync(ct);
        var accessoryOptions = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && AccessoryTypes.Contains(i.Type))
            .OrderBy(i => i.Name).Take(3000).Select(i => new DevOptionDto(i.Id, i.Code + " — " + i.Name)).ToListAsync(ct);
        return new DevCostingDto(lines, result.Yarn, result.Accessories, result.Labor, result.Overhead, result.Cost, result.Price, result.Margin,
            result.MarginPercent, m.TargetCost, m.TargetPrice, result.VersusTargetCost, settings.OverheadPercent, settings.YarnWastePercent,
            settings.LaborRatePerMinute, s.Mode, s.Mode == DevPriceMode.Markup ? s.MarkupPercent ?? settings.MarkupPercent : null, warnings, runs, accessories,
            materials, accessoryOptions, ctx.Permissions.Has(Permissions.DevEdit) && !DevStages.IsFinal(m.Stage),
            m.PatternId is { } pid
                ? await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == pid).OrderBy(c => c.SortOrder)
                    .Select(c => new DevOptionDto(c.Id, c.Name)).ToListAsync(ct)
                : [],
            m.ColorwayId);
    }

    /// <summary>Сохранить версию расчёта (D744): сервер пересчитывает по своим данным и условиям сценария; возвращает номер версии.</summary>
    public async Task<int> SaveAsync(long modelId, DevCostScenario scenario, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevCostView, ct);
        await guard.DemandAsync(Permissions.DevEdit, ct);
        var org = ctx.OrganizationId;
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
            if (DevStages.IsFinal(m.Stage))
            {
                throw new BusinessRuleException("dev.model.final", "Модель уже передана в производство или отменена.");
            }

            var (_, result, settings, _, s) = await ComputeAsync(org, m, scenario, ct);
            var version = (await db.DevCostRuns.Where(r => r.OrganizationId == org && r.ModelId == modelId).MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
            var run = DevCostRun.Create(org, modelId, version, s.Note, settings.OverheadPercent, s.Mode,
                s.Mode == DevPriceMode.Markup ? s.MarkupPercent ?? settings.MarkupPercent : null, result, clock.UtcNow, ctx.UserId);
            db.DevCostRuns.Add(run);
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevTechChanged, nameof(DevModel), modelId.ToString(), null,
                $"версия {version}" + (s.Replace is { Count: > 0 } ? $", замена материала: {s.Replace.Count}" : "") + (scenario.ColorwayId is null ? "" : ", другой колорит"), $"{m.Number}: расчёт себестоимости",
                currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return version;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ux_dev_cost_runs_version", StringComparison.Ordinal) == true)
        {
            // Две версии одновременно — вторая получает конфликт, а не дубль номера.
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Строки сохранённой версии (без права на суммы — отказ, чужая организация — «не найдено»).</summary>
    public async Task<IReadOnlyList<DevCostLineDto>> RunLinesAsync(long runId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevCostView, ct);
        if (!await db.DevCostRuns.AnyAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct))
        {
            throw new NotFoundException("Расчёт");
        }

        return await db.DevCostLines.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && l.RunId == runId).OrderBy(l => l.Kind).ThenBy(l => l.Id)
            .Select(l => new DevCostLineDto(l.Kind, l.ItemId, l.ItemId, l.Name, l.Unit, l.Quantity, l.WastePercent, l.UnitPrice, l.Amount, ""))
            .ToListAsync(ct);
    }

    // ---------- Фурнитура и упаковка (D741) ----------

    public async Task<long> AddAccessoryAsync(long modelId, long itemId, decimal quantity, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var org = ctx.OrganizationId;
        try
        {
            await EditableModelAsync(org, modelId, ct);
            var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == org && !i.IsArchived, ct)
                       ?? throw new NotFoundException("Позиция");
            if (!AccessoryTypes.Contains(item.Type))
            {
                throw new BusinessRuleException("dev.accessory.type", "Фурнитура — позиция вида «Фурнитура», «Упаковка» или «Материал».");
            }

            var existing = await db.DevModelAccessories.SingleOrDefaultAsync(a => a.OrganizationId == org && a.ModelId == modelId && a.ItemId == itemId, ct);
            if (existing is null)
            {
                existing = DevModelAccessory.Create(org, modelId, itemId, quantity);
                db.DevModelAccessories.Add(existing);
            }
            else
            {
                existing.Set(quantity);
            }

            Audit(ctx, modelId, null, $"{item.Code} {item.Name}: {quantity:0.######}", "фурнитура и упаковка");
            await db.SaveOrConflictAsync(ct);
            return existing.Id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public async Task RemoveAccessoryAsync(long accessoryId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        try
        {
            var a = await db.DevModelAccessories.SingleOrDefaultAsync(x => x.Id == accessoryId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Фурнитура");
            await EditableModelAsync(ctx.OrganizationId, a.ModelId, ct);
            db.DevModelAccessories.Remove(a);
            Audit(ctx, a.ModelId, $"позиция №{a.ItemId}", "убрана", "фурнитура и упаковка");
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    // ---------- Расчёт ----------

    /// <summary>Замены пряжи для колорита: колорит — того же рисунка, что выбран в модели; связь цвета палитры и цвета рисунка — по пряже.</summary>
    private async Task<IReadOnlyDictionary<long, long>> ColorwayReplaceAsync(long org, DevModel m, long colorwayId, IReadOnlyDictionary<long, long>? manual,
        CancellationToken ct)
    {
        if (m.PatternId is not { } pid || m.ColorwayId is not { } current)
        {
            throw new BusinessRuleException("dev.cost.colorway.none", "Выберите в модели рисунок и колорит — тогда можно посчитать другой колорит.");
        }

        if (!await db.DevPatternColorways.AnyAsync(c => c.Id == colorwayId && c.OrganizationId == org && c.PatternId == pid, ct))
        {
            throw new NotFoundException("Колорит");
        }

        var yarns = await db.DevPatternColorwayYarns.AsNoTracking()
            .Where(y => y.OrganizationId == org && (y.ColorwayId == current || y.ColorwayId == colorwayId) && y.ItemId != null)
            .Select(y => new { y.ColorwayId, y.ColorId, ItemId = y.ItemId!.Value }).ToListAsync(ct);
        var result = new Dictionary<long, long>();
        foreach (var from in yarns.Where(y => y.ColorwayId == current))
        {
            if (yarns.FirstOrDefault(y => y.ColorwayId == colorwayId && y.ColorId == from.ColorId) is { } to && to.ItemId != from.ItemId)
            {
                result.TryAdd(from.ItemId, to.ItemId);
            }
        }

        foreach (var (k, v) in manual ?? new Dictionary<long, long>())
        {
            result[k] = v;
        }

        return result;
    }

    private async Task<(List<DevCostLineDto> Lines, DevCostResult Result, DevSettings Settings, List<DevNote> Warnings, DevCostScenario Scenario)> ComputeAsync(
        long org, DevModel m, DevCostScenario? scenario, CancellationToken ct)
    {
        var s = scenario ?? new DevCostScenario();
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct) ?? DevSettings.Defaults(org);
        var (norms, warnings) = await DevYarnNorms.ForModelAsync(db, org, m.Id, ct);

        // D729: сценарий «другой колорит» — пряжа цветов рисунка в текущем колорите модели заменяется пряжей тех же цветов в выбранном
        // колорите; ручные замены важнее.
        if (s.ColorwayId is { } cwTo)
        {
            s = s with { Replace = await ColorwayReplaceAsync(org, m, cwTo, s.Replace, ct) };
        }

        // Замена материала (сценарий «другая пряжа»): позиция — своей организации, вида «Сырьё» или «Материал»; единица — кг или г.
        var replaceIds = (s.Replace ?? new Dictionary<long, long>()).Values.Distinct().ToList();
        var replacements = await (from i in db.Items.AsNoTracking()
                                  join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                                  where i.OrganizationId == org && replaceIds.Contains(i.Id) && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material)
                                  select new { i.Id, i.Code, i.Name, Unit = u.Code, u.Symbol, u.Precision }).ToDictionaryAsync(x => x.Id, ct);
        if (replaceIds.Any(id => !replacements.ContainsKey(id)))
        {
            throw new NotFoundException("Позиция");
        }

        var input = new List<(DevCostInputLine Line, long? Original)>();
        foreach (var n in norms)
        {
            var norm = n;
            if (s.Replace?.TryGetValue(n.ItemId, out var to) == true && to != n.ItemId)
            {
                var r = replacements[to];
                norm = n with { ItemId = r.Id, Code = r.Code, Name = r.Name, UnitCode = r.Unit, Symbol = r.Symbol, Precision = r.Precision };
            }

            if (DevYarnNorms.ExactQuantity(norm) is { } qty)
            {
                input.Add((new DevCostInputLine(DevCostLineKind.Yarn, norm.ItemId, $"{norm.Code} — {norm.Name}", norm.Symbol, qty, 0, settings.YarnWastePercent),
                    n.ItemId));
            }
            else
            {
                warnings.Add(DevNote.Of(DevNotes.UnitForCost, norm.Name, norm.Symbol));
            }
        }

        foreach (var a in await AccessoriesAsync(org, m.Id, ct))
        {
            input.Add((new DevCostInputLine(DevCostLineKind.Accessory, a.ItemId, $"{a.Code} — {a.Name}", a.Unit, a.Quantity, 0, 0), a.ItemId));
        }

        // Цены: вручную → последняя закупка (без НДС) → основной вид цены каталога → нет цены (0, замечание).
        var itemIds = input.Where(x => x.Line.ItemId is not null).Select(x => x.Line.ItemId!.Value).Distinct().ToList();
        var purchases = await LastPurchasePricesAsync(org, itemIds, ct);
        var catalog = await (from p in db.ItemPrices.AsNoTracking()
                             join t in db.PriceTypes.AsNoTracking() on p.PriceTypeId equals t.Id
                             where p.OrganizationId == org && t.IsDefault && itemIds.Contains(p.ItemId)
                             select new { p.ItemId, p.Price }).ToDictionaryAsync(x => x.ItemId, x => x.Price, ct);
        var priced = new List<(DevCostInputLine Line, long? Original, string Source)>();
        foreach (var (line, original) in input)
        {
            var id = line.ItemId!.Value;
            var (price, source) = s.Prices?.TryGetValue(id, out var manual) == true ? (manual, "вручную")
                : purchases.TryGetValue(id, out var bought) ? (bought, "закупка")
                : catalog.TryGetValue(id, out var listed) ? (listed, "каталог")
                : (0m, "нет цены");
            if (source == "нет цены")
            {
                warnings.Add(DevNote.Of(DevNotes.NoPrice, line.Name));
            }

            priced.Add((line with { UnitPrice = price }, original, source));
        }

        // Работа: расценка операции за штуку, иначе минуты × ставка из настроек (D740).
        var operations = await (from o in db.DevModelOperations.AsNoTracking()
                                join t in db.DevOperationTypes.AsNoTracking() on o.OperationTypeId equals t.Id
                                where o.OrganizationId == org && o.ModelId == m.Id
                                orderby o.Seq
                                select new { t.Name, Norm = o.NormMinutes ?? t.NormMinutes, Price = o.Price ?? t.Price }).ToListAsync(ct);
        foreach (var o in operations)
        {
            if (o.Price is { } rate)
            {
                priced.Add((new DevCostInputLine(DevCostLineKind.Labor, null, o.Name, "шт", 1, rate, 0), null, "расценка"));
            }
            else if (o.Norm is { } minutes && settings.LaborRatePerMinute is { } perMinute)
            {
                priced.Add((new DevCostInputLine(DevCostLineKind.Labor, null, o.Name, "мин", minutes, perMinute, 0), null, "ставка"));
            }
            else
            {
                warnings.Add(DevNote.Of(DevNotes.NoLabor, o.Name));
            }
        }

        if (operations.Count == 0)
        {
            warnings.Add(DevNote.Of(DevNotes.NoOperations));
        }

        var markup = s.Mode == DevPriceMode.Markup ? s.MarkupPercent ?? settings.MarkupPercent : (decimal?)null;
        var result = DevCosting.Compute(priced.Select(p => p.Line).ToList(), settings.OverheadPercent, s.Mode, markup, s.TargetPrice ?? m.TargetPrice, m.TargetCost);
        var lines = result.Lines.Select((l, i) => new DevCostLineDto(l.Kind, l.ItemId, priced[i].Original, l.Name, l.Unit, l.Quantity, l.WastePercent,
            l.UnitPrice, l.Amount, priced[i].Source)).ToList();
        return (lines, result, settings, warnings, s);
    }

    /// <summary>Цена последней закупки позиции без НДС: подтверждённый или закрытый заказ поставщику, самый поздний по дате.</summary>
    private async Task<Dictionary<long, decimal>> LastPurchasePricesAsync(long org, IReadOnlyList<long> itemIds, CancellationToken ct) =>
        (await (from o in db.PurchaseOrders.AsNoTracking()
                where o.OrganizationId == org && (o.Status == PurchaseOrderStatus.Confirmed || o.Status == PurchaseOrderStatus.Closed)
                from l in o.Lines
                where itemIds.Contains(l.ItemId)
                select new { l.ItemId, o.OrderDate, o.Id, l.Price, l.VatPercent, o.PricesIncludeVat }).ToListAsync(ct))
        .GroupBy(x => x.ItemId)
        .ToDictionary(g => g.Key, g =>
        {
            var x = g.OrderByDescending(p => p.OrderDate).ThenByDescending(p => p.Id).First();
            return x.PricesIncludeVat && x.VatPercent is { } vat and > 0 ? decimal.Round(x.Price * 100m / (100m + vat), 4) : x.Price;
        });

    private Task<List<DevAccessoryDto>> AccessoriesAsync(long org, long modelId, CancellationToken ct) =>
        (from a in db.DevModelAccessories.AsNoTracking()
         join i in db.Items.AsNoTracking() on a.ItemId equals i.Id
         join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
         where a.OrganizationId == org && a.ModelId == modelId
         orderby i.Name
         select new DevAccessoryDto(a.Id, i.Id, i.Code, i.Name, u.Symbol, a.Quantity, a.RowVersion)).ToListAsync(ct);

    private async Task EditableModelAsync(long org, long modelId, CancellationToken ct)
    {
        var stage = await db.DevModels.Where(x => x.Id == modelId && x.OrganizationId == org).Select(x => (DevStage?)x.Stage).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Модель");
        if (DevStages.IsFinal(stage))
        {
            throw new BusinessRuleException("dev.model.final", "Модель уже передана в производство или отменена.");
        }
    }

    private void Audit(AccessContext ctx, long modelId, string? before, string? after, string reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.DevTechChanged, nameof(DevModel), modelId.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
