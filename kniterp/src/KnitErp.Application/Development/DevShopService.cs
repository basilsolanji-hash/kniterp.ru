using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Organizations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Размеры, образцы и технология модели (D713–D715): размерная сетка, размеры деталей и упаковка (конструктор), табель мер с градацией,
/// образцы 1, 2, 3 с фактом цеха — время вязания, вес, доли цветов по деталям — и замерами, справочник технологических операций и
/// технология модели (технолог). Права: конструктор и технолог — «изменение», цех — «результат вязки образца», справочник операций —
/// «справочники», расценки — только с «себестоимостью».
/// </summary>
public sealed class DevShopService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, DevTechService tech)
{
    // ---------- Размеры (конструктор) ----------

    public async Task<DevSizesDto> SizesAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var model = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId && m.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var sizes = await Sizes(org, modelId).ToListAsync(ct);
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        var ids = details.Select(d => d.Id).ToList();
        var values = await db.DevDetailSizes.AsNoTracking().Where(s => s.OrganizationId == org && ids.Contains(s.DetailId)).ToListAsync(ct);
        var measures = await db.DevMeasures.AsNoTracking().Where(m => m.OrganizationId == org && m.ModelId == modelId).OrderBy(m => m.SortOrder).ThenBy(m => m.Id)
            .ToListAsync(ct);
        var mids = measures.Select(m => m.Id).ToList();
        var mv = await db.DevMeasureValues.AsNoTracking().Where(v => v.OrganizationId == org && mids.Contains(v.MeasureId)).ToListAsync(ct);
        var suggestions = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.MeasurePoint && !l.IsArchived)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => l.Name).ToListAsync(ct);
        return new DevSizesDto(sizes,
            details.Select(d => new DevDetailSizeRowDto(d.Id, d.Name, values.Where(v => v.DetailId == d.Id)
                .ToDictionary(v => v.SizeId, v => (v.LengthOffMachineCm, v.LengthAfterSteamCm, v.WidthCm)))).ToList(),
            measures.Select(m => new DevMeasureDto(m.Id, m.Name, m.Tolerance, m.Step, mv.Where(v => v.MeasureId == m.Id).ToDictionary(v => v.SizeId, v => v.Value)))
                .ToList(),
            model.PackBox, ctx.Permissions.Has(Permissions.DevEdit) && !DevStages.IsFinal(model.Stage), suggestions);
    }

    /// <summary>Размерная сетка одной строкой: «1, 2, 3, 4» или «42-52» (через 2). Новые размеры добавляются, существующие сохраняются.</summary>
    public async Task SetSizeGridAsync(long modelId, IReadOnlyList<string> names, string? baseSize, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var clean = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (clean.Count is 0 or > DevModelSize.MaxPerModel)
            {
                throw new BusinessRuleException("dev.size.grid", $"Размерная сетка — от 1 до {DevModelSize.MaxPerModel} размеров.");
            }

            var existing = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).ToListAsync(ct);
            var removed = existing.Where(e => !clean.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var removedIds = removed.Select(r => r.Id).ToList();
            if (removed.Count > 0 && (await db.DevDetailSizes.AnyAsync(v => v.OrganizationId == ctx.OrganizationId && removedIds.Contains(v.SizeId)
                                                                           && (v.LengthOffMachineCm != null || v.LengthAfterSteamCm != null || v.WidthCm != null), ct)
                                      || await db.DevMeasureValues.AnyAsync(v => v.OrganizationId == ctx.OrganizationId && removedIds.Contains(v.SizeId), ct)
                                      || await db.DevSamples.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.SizeId != null && removedIds.Contains(s.SizeId.Value), ct)))
            {
                throw new BusinessRuleException("dev.size.used", "Размер, по которому уже есть размеры деталей, табель мер или образец, убрать нельзя.");
            }

            await using var tx = await db.BeginTransactionAsync(ct);
            db.DevDetailSizes.RemoveRange(await db.DevDetailSizes.Where(v => v.OrganizationId == ctx.OrganizationId && removedIds.Contains(v.SizeId)).ToListAsync(ct));
            db.DevModelSizes.RemoveRange(removed);
            var baseName = string.IsNullOrWhiteSpace(baseSize) ? clean[clean.Count / 2] : baseSize.Trim();
            for (var i = 0; i < clean.Count; i++)
            {
                var size = existing.FirstOrDefault(e => string.Equals(e.Name, clean[i], StringComparison.OrdinalIgnoreCase));
                if (size is null)
                {
                    db.DevModelSizes.Add(DevModelSize.Create(ctx.OrganizationId, modelId, clean[i], i + 1, false, null));
                }
            }

            await db.SaveChangesAsync(ct);
            var all = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).ToListAsync(ct);
            foreach (var s in all)
            {
                var index = clean.FindIndex(n => string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase));
                s.Reorder(index + 1);
                s.SetBase(string.Equals(s.Name, baseName, StringComparison.OrdinalIgnoreCase));
            }

            if (!all.Any(s => s.IsBase))
            {
                throw new BusinessRuleException("dev.size.base", "Базовый размер должен быть в сетке.");
            }

            Audit(ctx, nameof(DevModel), modelId, string.Join(", ", existing.OrderBy(e => e.SortOrder).Select(e => e.Name)), string.Join(", ", clean),
                $"{model.Number}: размерная сетка, базовый {baseName}");
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Штук в коробке по размеру и размер короба (упаковка на техкарте). Ячейки — <see cref="DevCellEdit"/> (строка — размер); короб —
    /// со значением, которое видел пользователь. Изменённое другим пользователем не перезаписывается (D720).
    /// </summary>
    public async Task<DevSaveResult> SetPackingAsync(long modelId, IReadOnlyList<DevCellEdit> perBox, string? packBoxWas, string? packBox, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        DevSaveResult result = new(0, []);
        await RunAsync(async () =>
        {
            await tech.FindEditableModelAsync(ctx, modelId, ct);
            var m = await db.DevModels.SingleAsync(x => x.Id == modelId && x.OrganizationId == ctx.OrganizationId, ct);
            var sizes = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).ToListAsync(ct);
            if (perBox.Any(e => sizes.All(z => z.Id != e.RowId)))
            {
                throw new NotFoundException("Размер");
            }

            var saved = 0;
            var conflicts = new List<string>();
            if (!string.Equals(Norm(packBoxWas), Norm(packBox), StringComparison.Ordinal))
            {
                if (string.Equals(Norm(m.PackBox), Norm(packBoxWas), StringComparison.Ordinal))
                {
                    m.SetPackBox(packBox);
                    saved++;
                }
                else
                {
                    conflicts.Add($"размер короба (сейчас «{m.PackBox ?? "—"}»)");
                }
            }

            foreach (var e in perBox)
            {
                var size = sizes.Single(z => z.Id == e.RowId);
                if (size.PerBox != (int?)e.Was)
                {
                    conflicts.Add($"штук в коробке, размер {size.Name} (сейчас {size.PerBox?.ToString(CultureInfo.InvariantCulture) ?? "—"})");
                    continue;
                }

                size.Set(size.Name, e.Value is { } v ? (int)v : null);
                saved++;
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{saved} значений", $"{m.Number}: упаковка");
            await db.SaveOrConflictAsync(ct);
            result = new DevSaveResult(saved, conflicts);
        });
        return result;
    }

    private static string? Norm(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Размеры деталей по размерам сетки (длина с машины, после ВТО, ширина): только изменённые ячейки (D720). Ячейку, которую после
    /// загрузки экрана изменил другой пользователь, не перезаписываем — возвращаем в <see cref="DevSaveResult.Conflicts"/>.
    /// </summary>
    public async Task<DevSaveResult> SetDetailSizesAsync(long modelId, IReadOnlyList<DevCellEdit> edits, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        DevSaveResult result = new(0, []);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var details = await db.DevDetails.Where(d => d.OrganizationId == ctx.OrganizationId && d.ModelId == modelId).Select(d => new { d.Id, d.Name }).ToListAsync(ct);
            var sizes = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
            if (edits.Any(v => details.All(d => d.Id != v.RowId) || sizes.All(z => z.Id != v.ColumnId) || v.Field is < 0 or > 2))
            {
                throw new NotFoundException("Деталь");
            }

            var detailIds = details.Select(d => d.Id).ToList();
            var existing = await db.DevDetailSizes.Where(v => v.OrganizationId == ctx.OrganizationId && detailIds.Contains(v.DetailId)).ToListAsync(ct);
            var saved = 0;
            var conflicts = new List<string>();
            foreach (var e in edits)
            {
                var row = existing.FirstOrDefault(x => x.DetailId == e.RowId && x.SizeId == e.ColumnId);
                var current = row is null ? null : e.Field switch { 0 => row.LengthOffMachineCm, 1 => row.LengthAfterSteamCm, _ => row.WidthCm };
                if (current != e.Was)
                {
                    conflicts.Add($"{details.Single(d => d.Id == e.RowId).Name}, размер {sizes.Single(z => z.Id == e.ColumnId).Name}, "
                                  + $"{DetailFieldName(e.Field)} (сейчас {current?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—"})");
                    continue;
                }

                if (row is null)
                {
                    row = DevDetailSize.Create(ctx.OrganizationId, e.RowId, e.ColumnId);
                    db.DevDetailSizes.Add(row);
                    existing.Add(row);
                }

                row.Set(e.Field == 0 ? e.Value : row.LengthOffMachineCm, e.Field == 1 ? e.Value : row.LengthAfterSteamCm, e.Field == 2 ? e.Value : row.WidthCm);
                saved++;
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{saved} значений", $"{model.Number}: размеры деталей");
            await db.SaveOrConflictAsync(ct);
            result = new DevSaveResult(saved, conflicts);
        });
        return result;
    }

    private static string DetailFieldName(int field) => field switch { 0 => "длина с машины", 1 => "длина после ВТО", _ => "ширина" };

    public async Task<long> AddMeasureAsync(long modelId, string? name, decimal tolerance, decimal? step, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        long id = 0;
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var count = await db.DevMeasures.CountAsync(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId, ct);
            if (count >= DevMeasure.MaxPerModel)
            {
                throw new BusinessRuleException("dev.measure.limit", $"В табеле мер — не больше {DevMeasure.MaxPerModel} точек.");
            }

            var measure = DevMeasure.Create(ctx.OrganizationId, modelId, name, tolerance, step, count + 1);
            if (await db.DevMeasures.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId && m.Name == measure.Name, ct))
            {
                throw new BusinessRuleException("dev.measure.duplicate", $"Точка «{measure.Name}» уже есть в табеле мер.");
            }

            db.DevMeasures.Add(measure);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, nameof(DevModel), modelId, null, measure.Name, $"{model.Number}: табель мер");
            await db.SaveChangesAsync(ct);
            id = measure.Id;
        });
        return id;
    }

    /// <summary>Значения табеля мер: точка × размер, только изменённые ячейки; пустое значение убирает его. Чужие изменения не перезаписываем (D720).</summary>
    public async Task<DevSaveResult> SetMeasureValuesAsync(long modelId, IReadOnlyList<DevCellEdit> edits, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        DevSaveResult result = new(0, []);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var (mids, sizeIds) = await MeasureScopeAsync(ctx, modelId, ct);
            if (edits.Any(v => !mids.Contains(v.RowId) || !sizeIds.Contains(v.ColumnId)))
            {
                throw new NotFoundException("Точка измерения");
            }

            var names = await db.DevMeasures.Where(m => mids.Contains(m.Id)).Select(m => new { m.Id, m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
            var sizeNames = await db.DevModelSizes.Where(z => sizeIds.Contains(z.Id)).Select(z => new { z.Id, z.Name }).ToDictionaryAsync(z => z.Id, z => z.Name, ct);
            var existing = await db.DevMeasureValues.Where(v => v.OrganizationId == ctx.OrganizationId && mids.Contains(v.MeasureId)).ToListAsync(ct);
            var saved = 0;
            var conflicts = new List<string>();
            foreach (var e in edits)
            {
                var row = existing.FirstOrDefault(x => x.MeasureId == e.RowId && x.SizeId == e.ColumnId);
                if (row?.Value != e.Was)
                {
                    conflicts.Add($"{names[e.RowId]}, размер {sizeNames[e.ColumnId]} (сейчас {row?.Value.ToString("0.#", CultureInfo.InvariantCulture) ?? "—"})");
                    continue;
                }

                ApplyValue(row, e.Value, () => db.DevMeasureValues.Add(DevMeasureValue.Create(ctx.OrganizationId, e.RowId, e.ColumnId, e.Value!.Value)),
                    r => db.DevMeasureValues.Remove(r), (r, v) => r.Set(v));
                saved++;
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{saved} значений", $"{model.Number}: табель мер");
            await db.SaveOrConflictAsync(ct);
            result = new DevSaveResult(saved, conflicts);
        });
        return result;
    }

    /// <summary>Ячейка «значение или пусто»: нет строки и есть значение — добавить; пусто — убрать строку; иначе — изменить.</summary>
    private static void ApplyValue<T>(T? row, decimal? value, Action add, Action<T> remove, Action<T, decimal> set)
        where T : class
    {
        if (value is not { } v)
        {
            if (row is not null)
            {
                remove(row);
            }
        }
        else if (row is null)
        {
            add();
        }
        else
        {
            set(row, v);
        }
    }

    /// <summary>
    /// Градация (D713): для точек с шагом значения всех размеров считаются от базового (база + позиция × шаг). Заполненные вручную
    /// значения перезаписываются только с <paramref name="overwrite"/>. Возвращает, сколько значений заполнено.
    /// </summary>
    public async Task<int> GradeAsync(long modelId, bool overwrite, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var filled = 0;
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var sizes = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).OrderBy(s => s.SortOrder).ToListAsync(ct);
            var baseIndex = sizes.FindIndex(s => s.IsBase);
            if (baseIndex < 0)
            {
                throw new BusinessRuleException("dev.size.base", "Базовый размер должен быть в сетке.");
            }

            var measures = await db.DevMeasures.Where(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId && m.Step != null).ToListAsync(ct);
            var mids = measures.Select(m => m.Id).ToList();
            var values = await db.DevMeasureValues.Where(v => v.OrganizationId == ctx.OrganizationId && mids.Contains(v.MeasureId)).ToListAsync(ct);
            foreach (var m in measures)
            {
                var baseValue = values.FirstOrDefault(v => v.MeasureId == m.Id && v.SizeId == sizes[baseIndex].Id);
                if (baseValue is null)
                {
                    continue;
                }

                for (var i = 0; i < sizes.Count; i++)
                {
                    if (i == baseIndex)
                    {
                        continue;
                    }

                    var graded = DevMeasureRules.Grade(baseValue.Value, m.Step!.Value, i, baseIndex);
                    var row = values.FirstOrDefault(v => v.MeasureId == m.Id && v.SizeId == sizes[i].Id);
                    if (row is null)
                    {
                        db.DevMeasureValues.Add(DevMeasureValue.Create(ctx.OrganizationId, m.Id, sizes[i].Id, Math.Max(0, graded)));
                        filled++;
                    }
                    else if (overwrite)
                    {
                        row.Set(Math.Max(0, graded));
                        filled++;
                    }
                }
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{filled} значений", $"{model.Number}: градация от базового размера");
            await db.SaveOrConflictAsync(ct);
        });
        return filled;
    }

    public async Task RemoveMeasureAsync(long measureId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var m = await db.DevMeasures.SingleOrDefaultAsync(x => x.Id == measureId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Точка измерения");
            var model = await tech.FindEditableModelAsync(ctx, m.ModelId, ct);
            if (await db.DevSampleMeasures.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.MeasureId == measureId, ct))
            {
                throw new BusinessRuleException("dev.measure.used", "По точке уже есть замеры образцов — её нельзя убрать.");
            }

            await using var tx = await db.BeginTransactionAsync(ct);
            db.DevMeasureValues.RemoveRange(await db.DevMeasureValues.Where(v => v.OrganizationId == ctx.OrganizationId && v.MeasureId == measureId).ToListAsync(ct));
            db.DevMeasures.Remove(m);
            Audit(ctx, nameof(DevModel), m.ModelId, m.Name, "убрана", $"{model.Number}: табель мер");
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Заполнить табель мер по шаблону категории модели (ТЗ §9): добавляются точки шаблона, которых в табеле ещё нет (по названию),
    /// в порядке шаблона и в пределах <see cref="DevMeasure.MaxPerModel"/>. Возвращает, сколько точек добавлено.
    /// </summary>
    public async Task<int> ApplyMeasureTemplateAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var added = 0;
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var template = await db.DevCategoryMeasures.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.Category == model.Category)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
            var existing = await db.DevMeasures.Where(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId)
                .Select(m => new { m.Name, m.SortOrder }).ToListAsync(ct);
            var names = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var order = existing.Count == 0 ? 0 : existing.Max(e => e.SortOrder);
            var count = existing.Count;
            var addedNames = new List<string>();
            foreach (var t in template)
            {
                if (names.Contains(t.Name))
                {
                    continue;
                }

                if (count >= DevMeasure.MaxPerModel)
                {
                    if (addedNames.Count == 0)
                    {
                        throw new BusinessRuleException("dev.measure.limit", $"В табеле мер — не больше {DevMeasure.MaxPerModel} точек.");
                    }

                    break;
                }

                db.DevMeasures.Add(DevMeasure.Create(ctx.OrganizationId, modelId, t.Name, t.Tolerance, t.Step, ++order));
                names.Add(t.Name);
                addedNames.Add(t.Name);
                count++;
            }

            if (addedNames.Count > 0)
            {
                Audit(ctx, nameof(DevModel), modelId, null, string.Join(", ", addedNames), $"{model.Number}: табель мер по шаблону категории");
                await db.SaveOrConflictAsync(ct);
            }

            added = addedNames.Count;
        });
        return added;
    }

    /// <summary>Вес изделия по размерам, г (ТЗ §8): только размеры этой модели; пустое значение снимает вес.</summary>
    public async Task SetSizeWeightsAsync(long modelId, IReadOnlyList<(long SizeId, decimal? Grams)> weights, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var ids = weights.Select(w => w.SizeId).Distinct().ToList();
            var sizes = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId && ids.Contains(s.Id)).ToListAsync(ct);
            if (sizes.Count != ids.Count)
            {
                throw new NotFoundException("Размер");
            }

            var changed = new List<string>();
            foreach (var (sizeId, grams) in weights)
            {
                var size = sizes.Single(s => s.Id == sizeId);
                var value = grams is { } g ? decimal.Round(g, 2) : (decimal?)null;
                if (size.WeightGrams == value)
                {
                    continue;
                }

                size.SetWeight(value);
                changed.Add($"{size.Name} — {value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"} г");
            }

            if (changed.Count > 0)
            {
                Audit(ctx, nameof(DevModel), modelId, null, string.Join("; ", changed), $"{model.Number}: вес изделия по размерам");
                await db.SaveOrConflictAsync(ct);
            }
        });
    }

    // ---------- Образцы и факт цеха ----------

    public async Task<DevSamplesDto> SamplesAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var model = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId && m.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var sizes = await Sizes(org, modelId).ToListAsync(ct);
        var samples = await (from s in db.DevSamples.AsNoTracking()
                             join u in db.Users.AsNoTracking() on s.CreatedByUserId equals u.Id
                             where s.OrganizationId == org && s.ModelId == modelId
                             orderby s.No descending
                             select new { s, User = u.DisplayName }).ToListAsync(ct);
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .ToListAsync(ct);
        var colors = (await tech.ColorsAsync(modelId, ct)).ToDictionary(c => c.Id);
        var factDtos = await tech.FactsAsync(ctx, modelId, null, colors, ct);
        var measures = await db.DevMeasures.AsNoTracking().Where(m => m.OrganizationId == org && m.ModelId == modelId).OrderBy(m => m.SortOrder).ToListAsync(ct);
        var mids = measures.Select(m => m.Id).ToList();
        var plan = await db.DevMeasureValues.AsNoTracking().Where(v => v.OrganizationId == org && mids.Contains(v.MeasureId)).ToListAsync(ct);
        var sampleIds = samples.Select(s => s.s.Id).ToList();
        var actual = await db.DevSampleMeasures.AsNoTracking().Where(v => v.OrganizationId == org && sampleIds.Contains(v.SampleId)).ToListAsync(ct);
        var reasons = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.ReworkReason)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => new { l.Id, l.Name, l.IsArchived }).ToListAsync(ct);
        var reasonNames = reasons.ToDictionary(r => r.Id, r => r.Name);
        var today = await DevTechService.LocalTodayAsync(db, org, clock.UtcNow, ct);
        var customer = model.CustomerId is { } cid
            ? await db.Counterparties.AsNoTracking().Where(c => c.Id == cid && c.OrganizationId == org).Select(c => c.Name).SingleOrDefaultAsync(ct)
            : null;

        // ТЗ §8 (D728): колорит, люди паспорта и доработки, фото образца.
        var colorwayIds = samples.Select(x => x.s.ColorwayId).OfType<long>().Distinct().ToList();
        var colorways = colorwayIds.Count == 0
            ? []
            : await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && colorwayIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var users = await ActiveUsersAsync(org, ct);
        var people = users.ToDictionary(u => u.Id, u => u.Name);
        var otherPeople = samples.SelectMany(x => new[] { x.s.KnittedByUserId, x.s.AssembledByUserId, x.s.ReworkAssigneeUserId }).OfType<long>().Distinct()
            .Where(id => !people.ContainsKey(id)).ToList();
        foreach (var (uid, name) in await UserNamesAsync(otherPeople.Cast<long?>(), ct))
        {
            people[uid] = name;
        }

        var photos = await db.DevFiles.AsNoTracking()
            .Where(f => f.OrganizationId == org && f.ModelId == modelId && f.SampleId != null && sampleIds.Contains(f.SampleId.Value))
            .OrderBy(f => f.UploadedAtUtc).ThenBy(f => f.Id).Select(f => new { f.Id, SampleId = f.SampleId!.Value }).ToListAsync(ct);
        string? Person(long? id) => id is { } v ? people.GetValueOrDefault(v) : null;

        return new DevSamplesDto(samples.Select(x =>
            {
                var s = x.s;
                var facts = details.Select(d => (d.Quantity, Fact: factDtos.FirstOrDefault(f => f.SampleId == s.Id && f.DetailId == d.Id))).ToList();
                var timed = facts.Where(f => f.Fact?.KnitSeconds is not null).ToList();
                var weighed = facts.Where(f => f.Fact?.WeightGrams is not null).ToList();
                var making = new DevSampleMakingDto(s.ColorwayId is { } cw ? colorways.GetValueOrDefault(cw) : null, s.PatternVersion, s.YarnBatch,
                    s.KnittedByUserId, Person(s.KnittedByUserId), s.AssembledByUserId, Person(s.AssembledByUserId), s.AssemblyMinutes,
                    timed.Count == 0 ? null : decimal.Round(timed.Sum(f => (decimal)f.Fact!.KnitSeconds!.Value * f.Quantity) / 60m, 1),
                    weighed.Count == 0 ? null : weighed.Sum(f => f.Fact!.WeightGrams!.Value * f.Quantity));
                var rework = s.ReworkWhat is null && s.ReworkAssigneeUserId is null && s.ReworkDueDate is null
                    ? null
                    : new DevSampleReworkDto(s.ReworkWhat, s.ReworkAssigneeUserId, Person(s.ReworkAssigneeUserId), s.ReworkDueDate);
                return new DevSampleDto(s.Id, s.No, s.SizeId, sizes.FirstOrDefault(z => z.Id == s.SizeId)?.Name, s.Status, s.Comment, s.CreatedAtUtc, x.User,
                    details.Select(d => (d.Name,
                        factDtos.FirstOrDefault(f => f.SampleId == s.Id && f.DetailId == d.Id),
                        d.PlanKnitSeconds, d.PlanWeightGrams)).ToList(),
                    measures.Select(m =>
                    {
                        var p = s.SizeId is { } sid ? plan.FirstOrDefault(v => v.MeasureId == m.Id && v.SizeId == sid)?.Value : null;
                        var fact = actual.FirstOrDefault(a => a.SampleId == s.Id && a.MeasureId == m.Id)?.Value;
                        if (p is { } pv && fact is { } fv)
                        {
                            var (dev, outOf) = DevMeasureRules.Check(pv, fv, m.Tolerance);
                            return new DevSampleMeasureDto(m.Id, m.Name, p, m.Tolerance, fact, dev, outOf);
                        }

                        return new DevSampleMeasureDto(m.Id, m.Name, p, m.Tolerance, fact, null, false);
                    }).ToList(),
                    s.RowVersion, s.ReasonId is { } rid ? reasonNames.GetValueOrDefault(rid) : null, s.Custody, s.CustodyRecipient, s.HandedOn, s.ReturnBy,
                    s.CustodyClosedOn, s.CustodyNote, DevSample.IsReturnOverdue(s.Custody, s.ReturnBy, today), making, rework,
                    photos.Where(p => p.SampleId == s.Id).Select(p => p.Id).ToList());
            }).ToList(),
            sizes, ctx.Permissions.Has(Permissions.DevEdit) && !DevStages.IsFinal(model.Stage),
            ctx.Permissions.Has(Permissions.DevSample) && !DevStages.IsFinal(model.Stage), (await tech.SettingsAsync(org, ct)).FactTolerancePercent,
            reasons.Where(r => !r.IsArchived).Select(r => new DevOptionDto(r.Id, r.Name)).ToList(), customer, today,
            ctx.Permissions.Has(Permissions.DevEdit) && model.Stage != DevStage.Cancelled, ctx.Permissions.Has(Permissions.DevEdit), users);
    }

    /// <summary>Сколько образцов сравниваем рядом (ТЗ §8): меньше двух — нечего сравнивать, больше шести — не помещается на экран.</summary>
    public const int CompareMin = 2, CompareMax = 6;

    /// <summary>
    /// Сравнение образцов модели рядом (ТЗ §8 «замеры, вес, фото»): статус, размер, решение, факт вязания и веса по деталям против плана,
    /// общий вес, замеры с отклонением и выходом за допуск. Строится из тех же данных, что и вкладка «Образцы» (<see cref="SamplesAsync"/>).
    /// </summary>
    public async Task<DevSampleCompareDto> CompareSamplesAsync(long modelId, IReadOnlyList<long> sampleIds, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var ids = (sampleIds ?? []).Distinct().ToList();
        if (ids.Count is < CompareMin or > CompareMax)
        {
            throw new BusinessRuleException("dev.sample.compare", $"Для сравнения выберите от {CompareMin} до {CompareMax} образцов модели.");
        }

        var all = await SamplesAsync(modelId, ct); // чужая организация или нет модели → NotFoundException
        var chosen = all.Samples.Where(s => ids.Contains(s.Id)).OrderBy(s => s.No).ToList();
        if (chosen.Count != ids.Count)
        {
            throw new NotFoundException("Образец");
        }

        // Тот же порядок деталей, что в SamplesAsync, — чтобы сопоставить кортеж детали с количеством деталей в изделии.
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Name, d.Quantity }).ToListAsync(ct);

        var items = chosen.Select(s =>
        {
            var rows = s.Details.Select((d, i) =>
            {
                var meta = i < details.Count ? details[i] : null;
                return new DevSampleCompareDetailDto(meta?.Id ?? d.Fact?.DetailId ?? 0, d.Detail, meta?.Quantity ?? 1, d.PlanKnitSeconds, d.Fact?.KnitSeconds,
                    d.PlanWeightGrams, d.Fact?.WeightGrams);
            }).ToList();
            var weighed = rows.Where(r => r.FactWeightGrams is not null).ToList();
            decimal? total = weighed.Count == 0 ? null : weighed.Sum(r => r.FactWeightGrams!.Value * r.Quantity);
            // Фото — только свои фото образца (DevFile.SampleId, D728).
            return new DevSampleCompareItemDto(s.Id, s.No, s.Size, s.Status, s.Reason, s.Comment, s.CreatedAtUtc, rows, total, s.Measures, s.PhotoFileIds ?? []);
        }).ToList();

        var measureNames = chosen[0].Measures.Select(m => m.Name).ToList();
        return new DevSampleCompareDto(modelId, items, measureNames, details.Select(d => d.Name).ToList());
    }

    /// <summary>Новый образец (№ следующий в модели) в размере сетки.</summary>
    public async Task<long> CreateSampleAsync(long modelId, long? sizeId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        long id = 0;
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            if (sizeId is { } sid && !await db.DevModelSizes.AnyAsync(s => s.Id == sid && s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId, ct))
            {
                throw new NotFoundException("Размер");
            }

            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.dev.samples.{ctx.OrganizationId}.{modelId}", ct);
            var no = (await db.DevSamples.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).MaxAsync(s => (int?)s.No, ct) ?? 0) + 1;
            if (no > DevSample.MaxPerModel)
            {
                throw new BusinessRuleException("dev.sample.limit", $"У модели — не больше {DevSample.MaxPerModel} образцов.");
            }

            var sample = DevSample.Create(ctx.OrganizationId, modelId, no, sizeId, clock.UtcNow, ctx.UserId);
            sample.LinkPattern(model.ColorwayId, model.PatternVersion); // D728: колорит и версия рисунка — с модели на момент создания
            db.DevSamples.Add(sample);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, nameof(DevModel), modelId, null, $"образец № {no}", $"{model.Number}: образец");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            id = sample.Id;
        });
        return id;
    }

    public async Task DecideSampleAsync(long sampleId, DevSampleStatus status, string? comment, byte[] rowVersion, long? reasonId = null,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            var before = DevSample.StatusName(s.Status);
            string? reason = null;
            if (reasonId is { } rid)
            {
                // D768: причина — из справочника своей организации, вид «причина доработки», не в архиве.
                reason = await db.DevLookups.AsNoTracking()
                             .Where(l => l.Id == rid && l.OrganizationId == ctx.OrganizationId && l.Kind == DevLookupKind.ReworkReason && !l.IsArchived)
                             .Select(l => l.Name).SingleOrDefaultAsync(ct)
                         ?? throw new NotFoundException("Причина доработки");
            }

            s.Decide(status, comment, reasonId);
            Audit(ctx, nameof(DevModel), s.ModelId, before, DevSample.StatusName(s.Status), $"{model.Number}: образец № {s.No}{(reason is null || s.ReasonId is null ? "" : ", причина: " + reason)}{(s.Comment is null ? "" : ": " + s.Comment)}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>
    /// Паспорт образца (ТЗ §8, D728): партия пряжи, кто вязал, кто собирал, время сборки. Пишут конструктор и цех; люди — активные
    /// пользователи организации (уже записанного неактивного можно оставить).
    /// </summary>
    public async Task SetSampleMakingAsync(long sampleId, string? yarnBatch, long? knittedBy, long? assembledBy, int? assemblyMinutes, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await DemandEditOrSampleAsync(ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            await EnsureActiveMembersAsync(ctx, [knittedBy, assembledBy], [s.KnittedByUserId, s.AssembledByUserId], ct);
            s.SetMaking(yarnBatch, knittedBy, assembledBy, assemblyMinutes);
            var names = await UserNamesAsync([s.KnittedByUserId, s.AssembledByUserId], ct);
            Audit(ctx, nameof(DevModel), s.ModelId, null,
                $"партия: {s.YarnBatch ?? "—"}, вязал: {names.GetValueOrDefault(s.KnittedByUserId ?? 0) ?? "—"}, собирал: "
                + $"{names.GetValueOrDefault(s.AssembledByUserId ?? 0) ?? "—"}, сборка: {s.AssemblyMinutes?.ToString(CultureInfo.InvariantCulture) ?? "—"} мин",
                $"{model.Number}: паспорт образца № {s.No}", AuditActions.DevSampleRecorded);
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>План доработки образца «На доработку» (ТЗ §8): что меняем, кто (активный пользователь), срок.</summary>
    public async Task PlanReworkAsync(long sampleId, string? what, long? assigneeUserId, DateOnly? dueDate, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            await EnsureActiveMembersAsync(ctx, [assigneeUserId], [s.ReworkAssigneeUserId], ct);
            s.PlanRework(what, assigneeUserId, dueDate);
            var names = await UserNamesAsync([s.ReworkAssigneeUserId], ct);
            Audit(ctx, nameof(DevModel), s.ModelId, null,
                $"доработка: {s.ReworkWhat}; кто: {names.GetValueOrDefault(s.ReworkAssigneeUserId ?? 0) ?? "—"}; срок: "
                + (s.ReworkDueDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—"), $"{model.Number}: образец № {s.No}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Фото образца (ТЗ §8, D728): файл модели вида «Фото», привязанный к образцу; правила файлов модели — те же (предел, стадия).</summary>
    public async Task<long> AddSamplePhotoAsync(long sampleId, string? fileName, byte[] content, CancellationToken ct = default)
    {
        var ctx = await DemandEditOrSampleAsync(ct);
        var s = await db.DevSamples.AsNoTracking().Where(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId)
                    .Select(x => new { x.ModelId, x.No }).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Образец");
        return await tech.AddModelFileAsync(ctx, s.ModelId, DevFileKind.Photo, fileName, content, $"Образец № {s.No}",
            f => f.AttachToSample(sampleId), ct);
    }

    /// <summary>
    /// Передать образец заказчику (D723): кому, дата передачи, «вернуть до». Контрагент — заказчик модели. Отменённой модели — нельзя;
    /// у переданной в производство — можно (образец для продаж). Журнал — запись «Образец у заказчика».
    /// </summary>
    public async Task HandSampleAsync(long sampleId, string? recipient, DateOnly on, DateOnly? returnBy, string? note, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var model = await db.DevModels.AsNoTracking().Where(m => m.Id == s.ModelId && m.OrganizationId == ctx.OrganizationId)
                .Select(m => new { m.Number, m.Stage, m.CustomerId }).SingleAsync(ct);
            if (model.Stage == DevStage.Cancelled)
            {
                throw new BusinessRuleException("dev.model.cancelled", "Модель отменена — сначала верните её в работу.");
            }

            s.HandToCustomer(model.CustomerId, recipient, on, returnBy, note);
            Audit(ctx, nameof(DevModel), s.ModelId, null,
                $"передан: {s.CustodyRecipient}, {on:dd.MM.yyyy}" + (returnBy is { } r ? $", вернуть до {r:dd.MM.yyyy}" : ", без возврата"),
                $"{model.Number}: образец № {s.No}", AuditActions.DevSampleCustody);
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Образец вернули, оставили у заказчика или продали ему (D723). Возможно и после передачи модели в производство или отмены.</summary>
    public async Task CloseSampleCustodyAsync(long sampleId, DevSampleCustody result, DateOnly on, string? note, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var number = await db.DevModels.AsNoTracking().Where(m => m.Id == s.ModelId && m.OrganizationId == ctx.OrganizationId).Select(m => m.Number).SingleAsync(ct);
            var before = DevSample.CustodyName(s.Custody);
            s.CloseCustody(result, on, note);
            Audit(ctx, nameof(DevModel), s.ModelId, before, $"{DevSample.CustodyName(s.Custody)}, {on:dd.MM.yyyy}", $"{number}: образец № {s.No}",
                AuditActions.DevSampleCustody);
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Образцы у заказчиков сейчас (D723): по всем моделям, сначала просроченные; для аналитики и напоминаний.</summary>
    public async Task<IReadOnlyList<DevSampleOutDto>> SamplesOutAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        return await SamplesOut(db, ctx.OrganizationId, await DevTechService.LocalTodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct), ct);
    }

    internal static async Task<IReadOnlyList<DevSampleOutDto>> SamplesOut(IKnitErpDbContext db, long org, DateOnly today, CancellationToken ct)
    {
        var rows = await (from s in db.DevSamples.AsNoTracking()
                          join m in db.DevModels.AsNoTracking() on s.ModelId equals m.Id
                          where s.OrganizationId == org && m.OrganizationId == org && s.Custody == DevSampleCustody.AtCustomer
                          select new { s.Id, s.No, s.ModelId, m.Number, m.Name, s.CustodyRecipient, s.HandedOn, s.ReturnBy })
            .Take(500).ToListAsync(ct);
        return rows.Select(x => new DevSampleOutDto(x.Id, x.No, x.ModelId, x.Number, x.Name, x.CustodyRecipient ?? "", x.HandedOn!.Value, x.ReturnBy,
                DevSample.IsReturnOverdue(DevSampleCustody.AtCustomer, x.ReturnBy, today)))
            .OrderByDescending(x => x.Overdue).ThenBy(x => x.ReturnBy ?? DateOnly.MaxValue).ThenBy(x => x.HandedOn).ToList();
    }

    /// <summary>
    /// Факт цеха по детали образца (D714): время вязания (секунды), вес (г), доли цветов палитры (% или граммы — пересчитаются в %).
    /// Повторная запись заменяет прежнюю. Когда факт внесён по всем деталям, образец становится «Связан».
    /// </summary>
    public async Task RecordFactAsync(long sampleId, long detailId, int? knitSeconds, decimal? weightGrams, IReadOnlyList<(long ColorId, decimal Value)> shares,
        bool sharesInGrams = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSample, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            var detail = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(d => d.Id == detailId && d.OrganizationId == ctx.OrganizationId && d.ModelId == s.ModelId, ct)
                         ?? throw new NotFoundException("Деталь");
            var colorIds = shares.Select(x => x.ColorId).Distinct().ToList();
            if (colorIds.Count > 0 && await db.DevModelColors.CountAsync(c => c.OrganizationId == ctx.OrganizationId && c.ModelId == s.ModelId
                    && colorIds.Contains(c.Id), ct) != colorIds.Count)
            {
                throw new BusinessRuleException("dev.threading.color", "Цвет заправки должен быть из палитры этой модели.");
            }

            IReadOnlyList<(long ColorId, decimal Percent)> percents = sharesInGrams
                ? DevSampleColorShare.FromWeights(shares.Where(x => x.Value > 0).Select(x => (x.ColorId, x.Value)).ToList())
                : shares.Where(x => x.Value > 0).Select(x => (x.ColorId, x.Value)).ToList();
            DevSampleColorShare.EnsureValid(percents);

            await using var tx = await db.BeginTransactionAsync(ct);
            var fact = await db.DevSampleDetails.SingleOrDefaultAsync(f => f.OrganizationId == ctx.OrganizationId && f.SampleId == sampleId && f.DetailId == detailId, ct);
            var isNew = fact is null;
            fact ??= DevSampleDetail.Create(ctx.OrganizationId, sampleId, detailId);
            fact.Record(knitSeconds, weightGrams, clock.UtcNow, ctx.UserId);
            if (isNew)
            {
                db.DevSampleDetails.Add(fact);
            }
            else
            {
                db.DevSampleColorShares.RemoveRange(await db.DevSampleColorShares.Where(x => x.OrganizationId == ctx.OrganizationId && x.SampleDetailId == fact.Id).ToListAsync(ct));
            }

            await db.SaveOrConflictAsync(ct);
            db.DevSampleColorShares.AddRange(percents.Select(p => DevSampleColorShare.Create(ctx.OrganizationId, fact.Id, p.ColorId, p.Percent)));
            var detailCount = await db.DevDetails.CountAsync(d => d.OrganizationId == ctx.OrganizationId && d.ModelId == s.ModelId, ct);
            var factCount = await db.DevSampleDetails.CountAsync(f => f.OrganizationId == ctx.OrganizationId && f.SampleId == sampleId, ct);
            if (factCount >= detailCount)
            {
                s.MarkKnitted();
            }

            Audit(ctx, nameof(DevModel), s.ModelId, null,
                $"{detail.Name}: {knitSeconds?.ToString() ?? "—"} с, {weightGrams?.ToString("0.##") ?? "—"} г, цвета: "
                + string.Join(", ", percents.Select(p => $"{p.Percent:0.##}%")), $"{model.Number}: образец № {s.No}", AuditActions.DevSampleRecorded);
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Замеры образца по табелю мер (строка — точка, столбец не нужен): только изменённые; пустое значение убирает замер. Пишут конструктор
    /// и цех; замер, который успел изменить другой, не перезаписываем (D720).
    /// </summary>
    public async Task<DevSaveResult> SetSampleMeasuresAsync(long sampleId, IReadOnlyList<DevCellEdit> edits, CancellationToken ct = default)
    {
        var ctx = await DemandEditOrSampleAsync(ct);
        DevSaveResult result = new(0, []);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            var (mids, _) = await MeasureScopeAsync(ctx, s.ModelId, ct);
            if (edits.Any(v => !mids.Contains(v.RowId)))
            {
                throw new NotFoundException("Точка измерения");
            }

            var names = await db.DevMeasures.Where(m => mids.Contains(m.Id)).Select(m => new { m.Id, m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
            var existing = await db.DevSampleMeasures.Where(x => x.OrganizationId == ctx.OrganizationId && x.SampleId == sampleId).ToListAsync(ct);
            var saved = 0;
            var conflicts = new List<string>();
            foreach (var e in edits)
            {
                var row = existing.FirstOrDefault(x => x.MeasureId == e.RowId);
                if (row?.Value != e.Was)
                {
                    conflicts.Add($"{names[e.RowId]} (сейчас {row?.Value.ToString("0.#", CultureInfo.InvariantCulture) ?? "—"})");
                    continue;
                }

                ApplyValue(row, e.Value, () => db.DevSampleMeasures.Add(DevSampleMeasure.Create(ctx.OrganizationId, sampleId, e.RowId, e.Value!.Value)),
                    r => db.DevSampleMeasures.Remove(r), (r, v) => r.Set(v));
                saved++;
            }

            Audit(ctx, nameof(DevModel), s.ModelId, null, $"{saved} замеров", $"{model.Number}: замеры образца № {s.No}");
            await db.SaveOrConflictAsync(ct);
            result = new DevSaveResult(saved, conflicts);
        });
        return result;
    }

    // ---------- Операции технолога ----------

    public async Task<IReadOnlyList<DevOperationTypeDto>> OperationTypesAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var cost = ctx.Permissions.Has(Permissions.DevCostView);
        return await db.DevOperationTypes.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId && (includeArchived || !o.IsArchived))
            .OrderBy(o => o.IsArchived).ThenBy(o => o.ProductionStage ?? 9).ThenBy(o => o.Name)
            .Select(o => new DevOperationTypeDto(o.Id, o.Name, o.Unit, cost ? o.Price : null, o.NormMinutes, o.ProductionStage, o.IsArchived, o.RowVersion))
            .ToListAsync(ct);
    }

    public async Task<long> AddOperationTypeAsync(DevOperationValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        if (values.Price is not null && !ctx.Permissions.Has(Permissions.DevCostView))
        {
            values = values with { Price = null };
        }

        long id = 0;
        await RunAsync(async () =>
        {
            var op = DevOperationType.Create(ctx.OrganizationId, values);
            if (await db.DevOperationTypes.AnyAsync(o => o.OrganizationId == ctx.OrganizationId && o.Name == op.Name, ct))
            {
                throw new BusinessRuleException("dev.lookup.duplicate", $"«{op.Name}» уже есть в справочнике.");
            }

            db.DevOperationTypes.Add(op);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, "DevSettings", op.Id, null, op.Name, "Справочник операций", AuditActions.DevSettingsChanged);
            await db.SaveChangesAsync(ct);
            id = op.Id;
        });
        return id;
    }

    /// <summary>Изменение операции справочника; расценку меняет только тот, кто её видит.</summary>
    public async Task UpdateOperationTypeAsync(long id, DevOperationValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        await RunAsync(async () =>
        {
            var op = await db.DevOperationTypes.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Операция");
            op.EnsureVersion(op.RowVersion, rowVersion);
            if (!ctx.Permissions.Has(Permissions.DevCostView))
            {
                values = values with { Price = op.Price };
            }

            var before = op.Name;
            op.Set(values);
            Audit(ctx, "DevSettings", id, before, op.Name, "Справочник операций", AuditActions.DevSettingsChanged);
            await db.SaveOrConflictAsync(ct);
        });
    }

    public async Task SetOperationTypeArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        await RunAsync(async () =>
        {
            var op = await db.DevOperationTypes.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Операция");
            op.EnsureVersion(op.RowVersion, rowVersion);
            op.SetArchived(archived);
            Audit(ctx, "DevSettings", id, null, archived ? "в архиве" : "действует", $"Справочник операций: {op.Name}", AuditActions.DevSettingsChanged);
            await db.SaveOrConflictAsync(ct);
        });
    }

    public async Task<DevOperationsDto> OperationsAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var model = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId && m.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var cost = ctx.Permissions.Has(Permissions.DevCostView);
        var rows = await (from o in db.DevModelOperations.AsNoTracking()
                          join t in db.DevOperationTypes.AsNoTracking() on o.OperationTypeId equals t.Id
                          where o.OrganizationId == org && o.ModelId == modelId
                          orderby o.Seq
                          select new DevModelOperationDto(o.Id, o.Seq, o.OperationTypeId, t.Name, t.Unit, o.Note, o.NormMinutes, cost ? o.Price : null, t.NormMinutes,
                              cost ? t.Price : null)).ToListAsync(ct);
        var (minutes, sum) = DevModelOperation.Totals(rows.Select(r => (r.NormMinutes, r.Price, r.TypeNorm, r.TypePrice)));
        return new DevOperationsDto(rows, await OperationTypesAsync(false, ct), minutes, cost ? sum : null,
            ctx.Permissions.Has(Permissions.DevEdit) && !DevStages.IsFinal(model.Stage), cost);
    }

    public async Task<long> AddOperationAsync(long modelId, long operationTypeId, string? note, decimal? norm, decimal? price, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        long id = 0;
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var type = await db.DevOperationTypes.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationTypeId && o.OrganizationId == ctx.OrganizationId && !o.IsArchived, ct)
                       ?? throw new NotFoundException("Операция");
            var count = await db.DevModelOperations.CountAsync(o => o.OrganizationId == ctx.OrganizationId && o.ModelId == modelId, ct);
            if (count >= DevModelOperation.MaxPerModel)
            {
                throw new BusinessRuleException("dev.operation.limit", $"В технологии — не больше {DevModelOperation.MaxPerModel} операций.");
            }

            var op = DevModelOperation.Create(ctx.OrganizationId, modelId, count + 1, operationTypeId, note, norm,
                ctx.Permissions.Has(Permissions.DevCostView) ? price : null);
            db.DevModelOperations.Add(op);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, nameof(DevModel), modelId, null, $"{op.Seq}. {type.Name}", $"{model.Number}: технология");
            await db.SaveChangesAsync(ct);
            id = op.Id;
        });
        return id;
    }

    /// <summary>Операции из справочника, привязанные к этапам производства 1–5, одной кнопкой — в порядке этапов (меньше ручного ввода).</summary>
    public async Task<int> AddStandardOperationsAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var existing = await db.DevModelOperations.Where(o => o.OrganizationId == ctx.OrganizationId && o.ModelId == modelId).Select(o => o.OperationTypeId).ToListAsync(ct);
        var types = await db.DevOperationTypes.AsNoTracking()
            .Where(o => o.OrganizationId == ctx.OrganizationId && !o.IsArchived && o.ProductionStage != null && !existing.Contains(o.Id))
            .OrderBy(o => o.ProductionStage).ThenBy(o => o.Name).Select(o => o.Id).ToListAsync(ct);
        foreach (var t in types)
        {
            await AddOperationAsync(modelId, t, null, null, null, ct);
        }

        return types.Count;
    }

    public async Task UpdateOperationAsync(long id, string? note, decimal? norm, decimal? price, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var op = await db.DevModelOperations.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Операция");
            var model = await tech.FindEditableModelAsync(ctx, op.ModelId, ct);
            op.Set(note, norm, ctx.Permissions.Has(Permissions.DevCostView) ? price : op.Price);
            Audit(ctx, nameof(DevModel), op.ModelId, null, $"операция {op.Seq}", $"{model.Number}: технология");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Сдвинуть операцию вверх или вниз; порядок 1…N сохраняется без пропусков.</summary>
    public async Task MoveOperationAsync(long id, int delta, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var op = await db.DevModelOperations.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Операция");
            await tech.FindEditableModelAsync(ctx, op.ModelId, ct);
            var all = await db.DevModelOperations.Where(o => o.OrganizationId == ctx.OrganizationId && o.ModelId == op.ModelId).OrderBy(o => o.Seq).ToListAsync(ct);
            var i = all.FindIndex(o => o.Id == id);
            var j = Math.Clamp(i + Math.Sign(delta), 0, all.Count - 1);
            (all[i], all[j]) = (all[j], all[i]);
            for (var k = 0; k < all.Count; k++)
            {
                all[k].Move(k + 1);
            }

            await db.SaveOrConflictAsync(ct);
        });
    }

    public async Task RemoveOperationAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var op = await db.DevModelOperations.SingleOrDefaultAsync(o => o.Id == id && o.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Операция");
            var model = await tech.FindEditableModelAsync(ctx, op.ModelId, ct);
            db.DevModelOperations.Remove(op);
            var rest = await db.DevModelOperations.Where(o => o.OrganizationId == ctx.OrganizationId && o.ModelId == op.ModelId && o.Id != id).OrderBy(o => o.Seq).ToListAsync(ct);
            for (var k = 0; k < rest.Count; k++)
            {
                rest[k].Move(k + 1);
            }

            Audit(ctx, nameof(DevModel), op.ModelId, $"операция {op.Seq}", "убрана", $"{model.Number}: технология");
            await db.SaveOrConflictAsync(ct);
        });
    }

    // ---------- Помощники ----------

    private IQueryable<DevSizeDto> Sizes(long org, long modelId) =>
        db.DevModelSizes.AsNoTracking().Where(s => s.OrganizationId == org && s.ModelId == modelId).OrderBy(s => s.SortOrder)
            .Select(s => new DevSizeDto(s.Id, s.Name, s.IsBase, s.PerBox, s.WeightGrams));

    private async Task<(List<long> MeasureIds, List<long> SizeIds)> MeasureScopeAsync(AccessContext ctx, long modelId, CancellationToken ct) =>
        (await db.DevMeasures.Where(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId).Select(m => m.Id).ToListAsync(ct),
         await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).Select(s => s.Id).ToListAsync(ct));

    /// <summary>Конструктор («изменение») или цех («результат вязки образца»).</summary>
    private async Task<AccessContext> DemandEditOrSampleAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.DevEdit) && !ctx.Permissions.Has(Permissions.DevSample))
        {
            await guard.DemandAsync(Permissions.DevSample, ct);
        }

        return ctx;
    }

    private async Task<List<DevOptionDto>> ActiveUsersAsync(long org, CancellationToken ct) =>
        await (from m in db.OrganizationMembers.AsNoTracking()
               join u in db.Users.AsNoTracking() on m.UserId equals u.Id
               where m.OrganizationId == org && m.Status == MembershipStatus.Active
               orderby u.DisplayName
               select new DevOptionDto(u.Id, u.DisplayName)).ToListAsync(ct);

    /// <summary>Новые люди (не те, что уже записаны) — только активные пользователи организации.</summary>
    private async Task EnsureActiveMembersAsync(AccessContext ctx, IEnumerable<long?> users, IEnumerable<long?> unchanged, CancellationToken ct)
    {
        var check = users.OfType<long>().Distinct().Except(unchanged.OfType<long>()).ToList();
        if (check.Count == 0)
        {
            return;
        }

        var active = await db.OrganizationMembers.CountAsync(m => m.OrganizationId == ctx.OrganizationId && check.Contains(m.UserId) && m.Status == MembershipStatus.Active, ct);
        if (active != check.Count)
        {
            throw new BusinessRuleException("dev.model.assignee", "Исполнитель должен быть активным пользователем организации.");
        }
    }

    private async Task<Dictionary<long, string>> UserNamesAsync(IEnumerable<long?> users, CancellationToken ct)
    {
        var ids = users.OfType<long>().Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ux_dev_", StringComparison.Ordinal) == true)
        {
            // D720: ту же новую ячейку одновременно добавил другой пользователь — это конфликт правки, а не сбой.
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private void Audit(AccessContext ctx, string entity, long id, string? before, string? after, string? reason, string action = AuditActions.DevTechChanged) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
