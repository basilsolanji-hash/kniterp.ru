using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
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

    /// <summary>Штук в коробке по размеру и размер короба (упаковка на техкарте).</summary>
    public async Task SetPackingAsync(long modelId, IReadOnlyDictionary<long, int?> perBox, string? packBox, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            await tech.FindEditableModelAsync(ctx, modelId, ct);
            var m = await db.DevModels.SingleAsync(x => x.Id == modelId && x.OrganizationId == ctx.OrganizationId, ct);
            m.SetPackBox(packBox);
            foreach (var s in await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).ToListAsync(ct))
            {
                if (perBox.TryGetValue(s.Id, out var n))
                {
                    s.Set(s.Name, n);
                }
            }

            Audit(ctx, nameof(DevModel), modelId, null, m.PackBox ?? "—", $"{m.Number}: упаковка");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Размеры деталей по размерам сетки (длина с машины, после ВТО, ширина) — таблица целиком.</summary>
    public async Task SetDetailSizesAsync(long modelId, IReadOnlyList<(long DetailId, long SizeId, decimal? OffMachine, decimal? AfterSteam, decimal? Width)> values,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var detailIds = await db.DevDetails.Where(d => d.OrganizationId == ctx.OrganizationId && d.ModelId == modelId).Select(d => d.Id).ToListAsync(ct);
            var sizeIds = await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).Select(s => s.Id).ToListAsync(ct);
            if (values.Any(v => !detailIds.Contains(v.DetailId) || !sizeIds.Contains(v.SizeId)))
            {
                throw new NotFoundException("Деталь");
            }

            var existing = await db.DevDetailSizes.Where(v => v.OrganizationId == ctx.OrganizationId && detailIds.Contains(v.DetailId)).ToListAsync(ct);
            foreach (var v in values)
            {
                var row = existing.FirstOrDefault(e => e.DetailId == v.DetailId && e.SizeId == v.SizeId);
                if (row is null)
                {
                    row = DevDetailSize.Create(ctx.OrganizationId, v.DetailId, v.SizeId);
                    row.Set(v.OffMachine, v.AfterSteam, v.Width);
                    db.DevDetailSizes.Add(row);
                }
                else
                {
                    row.Set(v.OffMachine, v.AfterSteam, v.Width);
                }
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{values.Count} значений", $"{model.Number}: размеры деталей");
            await db.SaveOrConflictAsync(ct);
        });
    }

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

    /// <summary>Значения табеля мер: точка × размер. Пустое значение убирает его.</summary>
    public async Task SetMeasureValuesAsync(long modelId, IReadOnlyList<(long MeasureId, long SizeId, decimal? Value)> values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var model = await tech.FindEditableModelAsync(ctx, modelId, ct);
            var (mids, sizeIds) = await MeasureScopeAsync(ctx, modelId, ct);
            if (values.Any(v => !mids.Contains(v.MeasureId) || !sizeIds.Contains(v.SizeId)))
            {
                throw new NotFoundException("Точка измерения");
            }

            var existing = await db.DevMeasureValues.Where(v => v.OrganizationId == ctx.OrganizationId && mids.Contains(v.MeasureId)).ToListAsync(ct);
            foreach (var v in values)
            {
                var row = existing.FirstOrDefault(e => e.MeasureId == v.MeasureId && e.SizeId == v.SizeId);
                if (v.Value is not { } value)
                {
                    if (row is not null)
                    {
                        db.DevMeasureValues.Remove(row);
                    }
                }
                else if (row is null)
                {
                    db.DevMeasureValues.Add(DevMeasureValue.Create(ctx.OrganizationId, v.MeasureId, v.SizeId, value));
                }
                else
                {
                    row.Set(value);
                }
            }

            Audit(ctx, nameof(DevModel), modelId, null, $"{values.Count} значений", $"{model.Number}: табель мер");
            await db.SaveOrConflictAsync(ct);
        });
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
        return new DevSamplesDto(samples.Select(x =>
            {
                var s = x.s;
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
                    s.RowVersion);
            }).ToList(),
            sizes, ctx.Permissions.Has(Permissions.DevEdit) && !DevStages.IsFinal(model.Stage),
            ctx.Permissions.Has(Permissions.DevSample) && !DevStages.IsFinal(model.Stage), (await tech.SettingsAsync(org, ct)).FactTolerancePercent);
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
            db.DevSamples.Add(sample);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, nameof(DevModel), modelId, null, $"образец № {no}", $"{model.Number}: образец");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            id = sample.Id;
        });
        return id;
    }

    public async Task DecideSampleAsync(long sampleId, DevSampleStatus status, string? comment, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            s.EnsureVersion(s.RowVersion, rowVersion);
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            var before = DevSample.StatusName(s.Status);
            s.Decide(status, comment);
            Audit(ctx, nameof(DevModel), s.ModelId, before, DevSample.StatusName(s.Status), $"{model.Number}: образец № {s.No}{(s.Comment is null ? "" : ": " + s.Comment)}");
            await db.SaveOrConflictAsync(ct);
        });
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

    /// <summary>Замеры образца по табелю мер; пустое значение убирает замер. Пишут конструктор и цех.</summary>
    public async Task SetSampleMeasuresAsync(long sampleId, IReadOnlyList<(long MeasureId, decimal? Value)> values, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.DevEdit) && !ctx.Permissions.Has(Permissions.DevSample))
        {
            await guard.DemandAsync(Permissions.DevSample, ct);
        }

        await RunAsync(async () =>
        {
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            var model = await tech.FindEditableModelAsync(ctx, s.ModelId, ct);
            var (mids, _) = await MeasureScopeAsync(ctx, s.ModelId, ct);
            if (values.Any(v => !mids.Contains(v.MeasureId)))
            {
                throw new NotFoundException("Точка измерения");
            }

            var existing = await db.DevSampleMeasures.Where(x => x.OrganizationId == ctx.OrganizationId && x.SampleId == sampleId).ToListAsync(ct);
            foreach (var v in values)
            {
                var row = existing.FirstOrDefault(e => e.MeasureId == v.MeasureId);
                if (v.Value is not { } value)
                {
                    if (row is not null)
                    {
                        db.DevSampleMeasures.Remove(row);
                    }
                }
                else if (row is null)
                {
                    db.DevSampleMeasures.Add(DevSampleMeasure.Create(ctx.OrganizationId, sampleId, v.MeasureId, value));
                }
                else
                {
                    row.Set(value);
                }
            }

            Audit(ctx, nameof(DevModel), s.ModelId, null, $"{values.Count(v => v.Value is not null)} замеров", $"{model.Number}: замеры образца № {s.No}");
            await db.SaveOrConflictAsync(ct);
        });
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
            .Select(s => new DevSizeDto(s.Id, s.Name, s.IsBase, s.PerBox));

    private async Task<(List<long> MeasureIds, List<long> SizeIds)> MeasureScopeAsync(AccessContext ctx, long modelId, CancellationToken ct) =>
        (await db.DevMeasures.Where(m => m.OrganizationId == ctx.OrganizationId && m.ModelId == modelId).Select(m => m.Id).ToListAsync(ct),
         await db.DevModelSizes.Where(s => s.OrganizationId == ctx.OrganizationId && s.ModelId == modelId).Select(s => s.Id).ToListAsync(ct));

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
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
