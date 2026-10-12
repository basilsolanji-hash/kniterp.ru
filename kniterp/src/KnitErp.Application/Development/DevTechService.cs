using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Техдокументация модели (D710–D712, D716, D717): справочник станков и их журнал, библиотека программ, палитра дизайнера и закреплённый
/// эскиз, детали и техзадание дессинатора (станок, переплетение, заправка нитеводителей, программа), техкарта вязания для цеха,
/// показатели заполнения. Права: смотреть — «Разработка: просмотр» (техкарту — ещё и «отметка этапов» цеха), станки — «справочники»,
/// палитра и детали — «изменение», заправка и программы — «рисунки» (дессинатор) или «изменение» (программист).
/// </summary>
public sealed class DevTechService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, DevModelService models)
{
    // ---------- Станки ----------

    public async Task<IReadOnlyList<DevMachineDto>> MachinesAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        var org = ctx.OrganizationId;
        var machines = await db.DevMachines.AsNoTracking().Where(m => m.OrganizationId == org && (includeArchived || !m.IsArchived))
            .OrderBy(m => m.IsArchived).ThenBy(m => m.Name).ToListAsync(ct);
        var ids = machines.Select(m => m.Id).ToList();
        var programs = await db.DevFiles.AsNoTracking().Where(f => f.OrganizationId == org && f.MachineId != null && ids.Contains(f.MachineId.Value))
            .GroupBy(f => f.MachineId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var notes = await db.DevMachineNotes.AsNoTracking().Where(n => n.OrganizationId == org && ids.Contains(n.MachineId))
            .GroupBy(n => n.MachineId).Select(g => g.OrderByDescending(n => n.Id).First()).ToListAsync(ct);
        return machines.Select(m =>
        {
            var last = notes.FirstOrDefault(n => n.MachineId == m.Id);
            return new DevMachineDto(m.Id, m.Name, m.Brand, m.Model, m.Gauge, m.FeederCount, m.WidthCm, m.Systems, m.InventoryNumber, m.Status, m.IsArchived,
                m.Parameters, programs.GetValueOrDefault(m.Id), last?.Text, last?.AtUtc, m.RowVersion);
        }).ToList();
    }

    public async Task<IReadOnlyList<DevMachineNoteDto>> MachineNotesAsync(long machineId, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        await FindMachineAsync(ctx, machineId, ct);
        return await (from n in db.DevMachineNotes.AsNoTracking()
                      join u in db.Users.AsNoTracking() on n.UserId equals u.Id
                      where n.OrganizationId == ctx.OrganizationId && n.MachineId == machineId
                      orderby n.Id descending
                      select new DevMachineNoteDto(n.Text, n.StatusAfter, n.AtUtc, u.DisplayName)).Take(100).ToListAsync(ct);
    }

    public async Task<long> CreateMachineAsync(DevMachineValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        var machine = DevMachine.Create(ctx.OrganizationId, values);
        await EnsureMachineNameAsync(ctx, machine.Name, null, ct);
        db.DevMachines.Add(machine);
        return await SaveNewAsync(ctx, machine, () => machine.Id, AuditActions.DevSettingsChanged, $"Станок: {machine.Name}", machine.Parameters, ct);
    }

    public async Task UpdateMachineAsync(long id, DevMachineValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        await RunAsync(async () =>
        {
            var m = await db.DevMachines.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Станок");
            m.EnsureVersion(m.RowVersion, rowVersion);
            var before = m.Parameters;
            m.Set(values);
            await EnsureMachineNameAsync(ctx, m.Name, m.Id, ct);
            Audit(ctx, AuditActions.DevSettingsChanged, "DevMachine", id, before, m.Parameters, $"Станок: {m.Name}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    public async Task SetMachineArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevSettings, ct);
        await RunAsync(async () =>
        {
            var m = await db.DevMachines.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Станок");
            m.EnsureVersion(m.RowVersion, rowVersion);
            m.SetArchived(archived);
            Audit(ctx, AuditActions.DevSettingsChanged, "DevMachine", id, archived ? "действует" : "в архиве", archived ? "в архиве" : "действует", $"Станок: {m.Name}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Комментарий к станку («перенёс, плохо работает») и, при необходимости, новое состояние. Пишут дессинатор, цех, технолог.</summary>
    public async Task AddMachineNoteAsync(long id, string? text, DevMachineStatus? status, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevEdit, Permissions.DevPattern, Permissions.DevSample, Permissions.DevSettings,
            Permissions.ProductionStageMark);
        await RunAsync(async () =>
        {
            var m = await db.DevMachines.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Станок");
            var before = m.Status;
            var note = m.AddNote(text, status, clock.UtcNow, ctx.UserId);
            db.DevMachineNotes.Add(note);
            Audit(ctx, AuditActions.DevMachineNoted, "DevMachine", id, DevMachine.StatusName(before), DevMachine.StatusName(m.Status), $"Станок {m.Name}: {note.Text}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Библиотека программ машин: по станку или все; с моделью, деталью и параметрами станка на момент загрузки.</summary>
    public async Task<IReadOnlyList<DevProgramDto>> ProgramsAsync(long? machineId = null, string? search = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var q = from f in db.DevFiles.AsNoTracking()
                join m in db.DevModels.AsNoTracking() on f.ModelId equals m.Id
                join u in db.Users.AsNoTracking() on f.UploadedByUserId equals u.Id
                join mc in db.DevMachines.AsNoTracking() on f.MachineId equals mc.Id into mj
                from mc in mj.DefaultIfEmpty()
                join d in db.DevDetails.AsNoTracking() on f.DetailId equals d.Id into dj
                from d in dj.DefaultIfEmpty()
                where f.OrganizationId == ctx.OrganizationId && f.Kind == DevFileKind.MachineProgram && (machineId == null || f.MachineId == machineId)
                select new { f, m.Number, ModelName = m.Name, User = u.DisplayName, Machine = mc == null ? null : mc.Name, Detail = d == null ? null : d.Name };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            q = q.Where(x => x.f.FileName.Contains(text) || x.Number.Contains(text) || x.ModelName.Contains(text));
        }

        return await q.OrderByDescending(x => x.f.Id).Take(500)
            .Select(x => new DevProgramDto(x.f.Id, x.f.FileName, x.f.Version, x.f.SizeBytes, x.Machine, x.f.MachineParams, x.f.ModelId, x.Number, x.ModelName,
                x.Detail, x.f.UploadedAtUtc, x.User, x.f.DetailId)).ToListAsync(ct);
    }

    // ---------- Дизайнер: палитра и эскиз ----------

    public async Task<IReadOnlyList<DevColorDto>> ColorsAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        await FindModelAsync(ctx, modelId, ct);
        return await Colors(ctx, modelId).ToListAsync(ct);
    }

    /// <summary>Пряжа для палитры: сырьё и материалы номенклатуры (с модификациями цвета), действующие.</summary>
    public async Task<IReadOnlyList<DevOptionDto>> YarnOptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        return await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == ctx.OrganizationId && !i.IsArchived && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material))
            .OrderBy(i => i.Name).Take(3000).Select(i => new DevOptionDto(i.Id, i.Code + " — " + i.Name)).ToListAsync(ct);
    }

    public async Task<long> AddColorAsync(long modelId, string? label, long? itemId, string? swatch, string? note, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var model = await FindEditableModelAsync(ctx, modelId, ct);
        await EnsureYarnAsync(ctx, itemId, ct);
        var count = await db.DevModelColors.CountAsync(c => c.OrganizationId == ctx.OrganizationId && c.ModelId == modelId, ct);
        if (count >= DevModelColor.MaxPerModel)
        {
            throw new BusinessRuleException("dev.color.limit", $"В палитре — не больше {DevModelColor.MaxPerModel} цветов.");
        }

        var color = DevModelColor.Create(ctx.OrganizationId, modelId, label, itemId, swatch, note, count + 1);
        await EnsureColorLabelAsync(ctx, modelId, color.Label, null, ct);
        db.DevModelColors.Add(color);
        return await SaveNewAsync(ctx, color, () => color.Id, AuditActions.DevTechChanged, $"{model.Number}: палитра", color.Label, ct, modelId);
    }

    public async Task UpdateColorAsync(long colorId, string? label, long? itemId, string? swatch, string? note, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var c = await db.DevModelColors.SingleOrDefaultAsync(x => x.Id == colorId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Цвет");
            var model = await FindEditableModelAsync(ctx, c.ModelId, ct);
            await EnsureYarnAsync(ctx, itemId, ct);
            var before = c.Label;
            c.Set(label, itemId, swatch, note);
            await EnsureColorLabelAsync(ctx, c.ModelId, c.Label, c.Id, ct);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), c.ModelId, before, c.Label, $"{model.Number}: палитра");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Убрать цвет из палитры можно, пока он не используется в заправке и фактах образцов.</summary>
    public async Task RemoveColorAsync(long colorId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var c = await db.DevModelColors.SingleOrDefaultAsync(x => x.Id == colorId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Цвет");
            var model = await FindEditableModelAsync(ctx, c.ModelId, ct);
            if (await db.DevThreading.AnyAsync(t => t.OrganizationId == ctx.OrganizationId && t.ColorId == colorId, ct)
                || await db.DevSampleColorShares.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.ColorId == colorId, ct))
            {
                throw new BusinessRuleException("dev.color.used", "Цвет используется в заправке или фактах образцов — сначала замените его там.");
            }

            db.DevModelColors.Remove(c);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), c.ModelId, c.Label, "убран", $"{model.Number}: палитра");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Закрепить эскиз модели (D712): файл этой модели вида «Эскиз» и картинка; null — снять.</summary>
    public async Task PinSketchAsync(long modelId, long? fileId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        await RunAsync(async () =>
        {
            var m = await db.DevModels.SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Модель");
            m.EnsureVersion(m.RowVersion, rowVersion);
            if (fileId is { } fid)
            {
                var file = await db.DevFiles.AsNoTracking().Where(f => f.Id == fid && f.OrganizationId == ctx.OrganizationId && f.ModelId == modelId)
                               .Select(f => new { f.Kind, f.ContentType }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Файл");
                if (file.Kind != DevFileKind.Sketch || !file.ContentType.StartsWith("image/") || file.ContentType == DevFileTypes.Dxf)
                {
                    throw new BusinessRuleException("dev.sketch.kind", "Закрепить можно эскиз-картинку (PNG, JPEG, BMP).");
                }
            }

            m.PinSketch(fileId);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), modelId, null, fileId is null ? "эскиз откреплён" : "эскиз закреплён", m.Number);
            await db.SaveOrConflictAsync(ct);
        });
    }

    // ---------- Детали и заправка ----------

    public async Task<IReadOnlyList<DevDetailRowDto>> DetailsAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        await FindModelAsync(ctx, modelId, ct);
        return await (from d in db.DevDetails.AsNoTracking()
                      join m in db.DevMachines.AsNoTracking() on d.MachineId equals m.Id into mj
                      from m in mj.DefaultIfEmpty()
                      where d.OrganizationId == ctx.OrganizationId && d.ModelId == modelId
                      orderby d.SortOrder, d.Id
                      select new DevDetailRowDto(d.Id, d.Name, d.Quantity, m == null ? null : m.Name, m == null ? null : m.FeederCount,
                          db.DevThreading.Count(t => t.DetailId == d.Id), d.ProgramFileId != null, d.PlanKnitSeconds, d.PlanWeightGrams)).ToListAsync(ct);
    }

    public async Task<long> AddDetailAsync(long modelId, string? name, int quantity, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevEdit, Permissions.DevPattern);
        var model = await FindEditableModelAsync(ctx, modelId, ct);
        var count = await db.DevDetails.CountAsync(d => d.OrganizationId == ctx.OrganizationId && d.ModelId == modelId, ct);
        if (count >= DevDetail.MaxPerModel)
        {
            throw new BusinessRuleException("dev.detail.limit", $"В модели — не больше {DevDetail.MaxPerModel} деталей.");
        }

        var detail = DevDetail.Create(ctx.OrganizationId, modelId, name, quantity, count + 1);
        db.DevDetails.Add(detail);
        return await SaveNewAsync(ctx, detail, () => detail.Id, AuditActions.DevTechChanged, $"{model.Number}: деталь", $"{detail.Name} × {detail.Quantity}", ct, modelId);
    }

    public async Task RenameDetailAsync(long detailId, string? name, int quantity, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevEdit, Permissions.DevPattern);
        await RunAsync(async () =>
        {
            var (d, model) = await LoadDetailAsync(ctx, detailId, rowVersion, ct);
            var before = $"{d.Name} × {d.Quantity}";
            d.Rename(name, quantity);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), d.ModelId, before, $"{d.Name} × {d.Quantity}", $"{model.Number}: деталь");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Убрать деталь можно, пока по ней нет фактов образцов; заправка и размеры детали удаляются вместе с ней.</summary>
    public async Task RemoveDetailAsync(long detailId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevEdit, Permissions.DevPattern);
        await RunAsync(async () =>
        {
            var (d, model) = await LoadDetailAsync(ctx, detailId, rowVersion, ct);
            if (await db.DevSampleDetails.AnyAsync(f => f.OrganizationId == ctx.OrganizationId && f.DetailId == detailId, ct)
                || await db.DevFiles.AnyAsync(f => f.OrganizationId == ctx.OrganizationId && f.DetailId == detailId, ct))
            {
                throw new BusinessRuleException("dev.detail.used", "По детали уже есть программы или факты образцов — её нельзя убрать.");
            }

            await using var tx = await db.BeginTransactionAsync(ct);
            db.DevThreading.RemoveRange(await db.DevThreading.Where(t => t.OrganizationId == ctx.OrganizationId && t.DetailId == detailId).ToListAsync(ct));
            db.DevDetailSizes.RemoveRange(await db.DevDetailSizes.Where(t => t.OrganizationId == ctx.OrganizationId && t.DetailId == detailId).ToListAsync(ct));
            db.DevDetails.Remove(d);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), d.ModelId, d.Name, "убрана", $"{model.Number}: деталь");
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    /// <summary>Техзадание дессинатора: станок из справочника, переплетение, плановые время и вес, важные комментарии.</summary>
    public async Task SetKnittingAsync(long detailId, long? machineId, long? weaveId, int? planKnitSeconds, decimal? planWeightGrams, string? notes,
        byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevPattern, Permissions.DevEdit);
        await RunAsync(async () =>
        {
            var (d, model) = await LoadDetailAsync(ctx, detailId, rowVersion, ct);
            if (machineId is { } mid && mid != d.MachineId)
            {
                var machine = await FindMachineAsync(ctx, mid, ct);
                if (machine.IsArchived)
                {
                    throw new BusinessRuleException("dev.machine.archived", "Станок в архиве — выберите действующий.");
                }

                var maxFeeder = await db.DevThreading.Where(t => t.OrganizationId == ctx.OrganizationId && t.DetailId == detailId)
                    .MaxAsync(t => (int?)t.Feeder, ct) ?? 0;
                if (maxFeeder > machine.FeederCount)
                {
                    throw new BusinessRuleException("dev.threading.feeder",
                        $"В заправке занят нитеводитель {maxFeeder}, а на станке «{machine.Name}» их {machine.FeederCount}.");
                }
            }

            if (weaveId is { } wid && wid != d.WeaveId
                && !await db.DevLookups.AnyAsync(l => l.Id == wid && l.OrganizationId == ctx.OrganizationId && l.Kind == DevLookupKind.Weave && !l.IsArchived, ct))
            {
                throw new BusinessRuleException("dev.detail.weave", "Переплетение не найдено или в архиве.");
            }

            d.SetKnitting(machineId, weaveId, planKnitSeconds, planWeightGrams, notes);
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), d.ModelId, null,
                $"станок {machineId?.ToString() ?? "—"}, время {planKnitSeconds?.ToString() ?? "—"} с, вес {planWeightGrams?.ToString("0.##") ?? "—"} г",
                $"{model.Number}: техзадание «{d.Name}»");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>
    /// Заправка детали целиком (D711): позиции нитеводителей слева и справа. Номер — в пределах нитеводителей станка, цвет — из палитры
    /// модели. Набор заменяется полностью; в журнал — число позиций до и после.
    /// </summary>
    public async Task SetThreadingAsync(long detailId, IReadOnlyList<DevThreadingRow> rows, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevPattern, Permissions.DevEdit);
        await RunAsync(async () =>
        {
            var (d, model) = await LoadDetailAsync(ctx, detailId, rowVersion, ct);
            if (d.MachineId is not { } machineId)
            {
                throw new BusinessRuleException("dev.threading.machine", "Сначала выберите станок детали — от него зависит число нитеводителей.");
            }

            var machine = await FindMachineAsync(ctx, machineId, ct);
            DevThreading.EnsureUnique(rows);
            var colorIds = rows.Select(r => r.ColorId).OfType<long>().Distinct().ToList();
            if (colorIds.Count > 0 && await db.DevModelColors.CountAsync(c => c.OrganizationId == ctx.OrganizationId && c.ModelId == d.ModelId
                    && colorIds.Contains(c.Id), ct) != colorIds.Count)
            {
                throw new BusinessRuleException("dev.threading.color", "Цвет заправки должен быть из палитры этой модели.");
            }

            var created = rows.Select(r => DevThreading.Create(ctx.OrganizationId, detailId, r, machine.FeederCount)).ToList();
            await using var tx = await db.BeginTransactionAsync(ct);
            var old = await db.DevThreading.Where(t => t.OrganizationId == ctx.OrganizationId && t.DetailId == detailId).ToListAsync(ct);
            db.DevThreading.RemoveRange(old);
            await db.SaveChangesAsync(ct);
            db.DevThreading.AddRange(created);
            // Версия детали меняется вместе с заправкой — вторая вкладка с устаревшей заправкой получит конфликт.
            d.TouchThreading();
            Audit(ctx, AuditActions.DevTechChanged, nameof(DevModel), d.ModelId, $"{old.Count} поз.", $"{created.Count} поз.", $"{model.Number}: заправка «{d.Name}»");
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    /// <summary>Скопировать заправку с другой детали той же модели (меньше ручного ввода: перед и спинка часто одинаковы).</summary>
    public async Task CopyThreadingAsync(long fromDetailId, long toDetailId, byte[] toRowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevPattern, Permissions.DevEdit);
        var from = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fromDetailId && x.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Деталь");
        var to = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == toDetailId && x.OrganizationId == ctx.OrganizationId, ct)
                 ?? throw new NotFoundException("Деталь");
        if (from.ModelId != to.ModelId)
        {
            throw new BusinessRuleException("dev.threading.copy", "Заправку копируют между деталями одной модели.");
        }

        var rows = await db.DevThreading.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && t.DetailId == fromDetailId)
            .Select(t => new DevThreadingRow(t.Feeder, t.Side, t.ColorId, t.Label, t.Ends, t.Paraffin, t.Mode, t.Elastic)).ToListAsync(ct);
        await SetThreadingAsync(toDetailId, rows, toRowVersion, ct);
    }

    /// <summary>
    /// Программа машины детали (D710, D711): файл хранится с параметрами станка детали, становится программой детали;
    /// на стадии «Программа» модель сама переходит дальше (D795).
    /// </summary>
    public async Task<long> AddProgramAsync(long detailId, string? fileName, byte[] content, string? comment, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevPattern, Permissions.DevEdit);
        var detail = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == detailId && x.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Деталь");
        if (detail.MachineId is not { } machineId)
        {
            throw new BusinessRuleException("dev.threading.machine", "Сначала выберите станок детали — от него зависит число нитеводителей.");
        }

        var machine = await FindMachineAsync(ctx, machineId, ct);
        return await models.AddFileCoreAsync(ctx, detail.ModelId, DevFileKind.MachineProgram, fileName, content, comment,
            f => f.AttachProgram(detailId, machineId, $"{machine.Name}: {machine.Parameters}"), ct,
            async file =>
            {
                var d = await db.DevDetails.SingleAsync(x => x.Id == detailId, ct);
                d.SetProgram(file.Id);
                await db.SaveOrConflictAsync(ct);
            });
    }

    // ---------- Техкарта вязания ----------

    /// <summary>Техзадание детали для редактора: значения, заправка, станки, переплетения, палитра, программы детали.</summary>
    public async Task<DevDetailEditDto> DetailAsync(long detailId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var d = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == detailId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Деталь");
        var m = await FindModelAsync(ctx, d.ModelId, ct);
        var threading = await db.DevThreading.AsNoTracking().Where(t => t.OrganizationId == org && t.DetailId == detailId).OrderByDescending(t => t.Feeder)
            .ThenBy(t => t.Side).Select(t => new DevThreadingRow(t.Feeder, t.Side, t.ColorId, t.Label, t.Ends, t.Paraffin, t.Mode, t.Elastic)).ToListAsync(ct);
        var machines = (await MachinesAsync(false, ct)).Where(x => !x.IsArchived || x.Id == d.MachineId).ToList();
        var weaves = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.Weave && (!l.IsArchived || l.Id == d.WeaveId))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => new DevOptionDto(l.Id, l.Name)).ToListAsync(ct);
        var palette = await Colors(ctx, d.ModelId).ToListAsync(ct);
        var others = await db.DevDetails.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == d.ModelId && x.Id != detailId)
            .OrderBy(x => x.SortOrder).Select(x => new DevOptionDto(x.Id, x.Name)).ToListAsync(ct);
        var programs = (await ProgramsAsync(null, null, ct)).Where(p => p.DetailId == detailId).ToList();
        var editable = !DevStages.IsFinal(m.Stage);
        return new DevDetailEditDto(d.Id, m.Id, m.Number, m.Name, d.Name, d.Quantity, d.MachineId, d.WeaveId, d.PlanKnitSeconds, d.PlanWeightGrams, d.Notes,
            threading, machines, weaves, palette, others, programs, d.ProgramFileId,
            editable && (ctx.Permissions.Has(Permissions.DevPattern) || ctx.Permissions.Has(Permissions.DevEdit)),
            editable && (ctx.Permissions.Has(Permissions.DevPattern) || ctx.Permissions.Has(Permissions.DevEdit)), d.RowVersion);
    }

    /// <summary>Техкарты вязания для цеха: детали моделей в работе и переданных; поиск по номеру модели, названию, детали, станку.</summary>
    public async Task<IReadOnlyList<DevTechCardRowDto>> TechCardsAsync(string? search = null, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        var q = from d in db.DevDetails.AsNoTracking()
                join m in db.DevModels.AsNoTracking() on d.ModelId equals m.Id
                join mc in db.DevMachines.AsNoTracking() on d.MachineId equals mc.Id into mj
                from mc in mj.DefaultIfEmpty()
                where d.OrganizationId == ctx.OrganizationId && m.Stage != DevStage.Cancelled
                select new { d, m.Number, ModelName = m.Name, m.Stage, Machine = mc == null ? null : mc.Name };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            q = q.Where(x => x.Number.Contains(text) || x.ModelName.Contains(text) || x.d.Name.Contains(text) || (x.Machine != null && x.Machine.Contains(text)));
        }

        return await q.OrderByDescending(x => x.d.ModelId).ThenBy(x => x.d.SortOrder).Take(300)
            .Select(x => new DevTechCardRowDto(x.d.Id, x.d.ModelId, x.Number, x.ModelName, x.Stage, x.d.Name, x.Machine, x.d.ProgramFileId != null,
                db.DevThreading.Count(t => t.DetailId == x.d.Id))).ToListAsync(ct);
    }

    /// <summary>Техкарта вязания детали — для телефона и планшета у машины и для печати (D717).</summary>
    public async Task<DevTechCardDto> TechCardAsync(long detailId, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        var org = ctx.OrganizationId;
        var d = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == detailId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Деталь");
        var m = await db.DevModels.AsNoTracking().SingleAsync(x => x.Id == d.ModelId && x.OrganizationId == org, ct);
        var machine = d.MachineId is { } mid ? await db.DevMachines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mid, ct) : null;
        var lastNote = machine is null ? null : await db.DevMachineNotes.AsNoTracking().Where(n => n.MachineId == machine.Id)
            .OrderByDescending(n => n.Id).Select(n => n.Text).FirstOrDefaultAsync(ct);
        var weave = d.WeaveId is { } wid ? await db.DevLookups.AsNoTracking().Where(l => l.Id == wid).Select(l => l.Name).SingleOrDefaultAsync(ct) : null;
        var palette = await Colors(ctx, d.ModelId).ToListAsync(ct);
        var colors = palette.ToDictionary(c => c.Id);
        var threading = await db.DevThreading.AsNoTracking().Where(t => t.OrganizationId == org && t.DetailId == detailId).ToListAsync(ct);
        var feederCount = Math.Max(machine?.FeederCount ?? 0, threading.Count == 0 ? 0 : threading.Max(t => t.Feeder));
        DevThreadSlotDto? Slot(int feeder, DevFeederSide side)
        {
            var t = threading.FirstOrDefault(x => x.Feeder == feeder && x.Side == side);
            if (t is null)
            {
                return null;
            }

            var c = t.ColorId is { } cid ? colors.GetValueOrDefault(cid) : null;
            return new DevThreadSlotDto(t.ColorId, c?.Label ?? t.Label ?? "—", c?.ItemName, c?.Swatch, t.Ends, t.Paraffin, t.Mode, t.Elastic, t.Label);
        }

        var feeders = Enumerable.Range(1, feederCount).Reverse().Select(n => new DevFeederRowDto(n, Slot(n, DevFeederSide.Left), Slot(n, DevFeederSide.Right))).ToList();
        var canSeeCost = ctx.Permissions.Has(Permissions.DevCostView);
        var program = d.ProgramFileId is { } pid
            ? await db.DevFiles.AsNoTracking().Where(f => f.Id == pid).Select(f => new { f.FileName, f.Version, f.MachineParams }).SingleOrDefaultAsync(ct)
            : null;
        var samples = await (from s in db.DevSamples.AsNoTracking()
                             join z in db.DevModelSizes.AsNoTracking() on s.SizeId equals z.Id into zj
                             from z in zj.DefaultIfEmpty()
                             where s.OrganizationId == org && s.ModelId == d.ModelId
                             orderby s.No descending
                             select new DevSampleOptionDto(s.Id, s.No, z == null ? null : z.Name, s.Status)).Take(20).ToListAsync(ct);
        var facts = await FactsAsync(ctx, d.ModelId, detailId, colors, ct);
        var canEdit = !DevStages.IsFinal(m.Stage);
        return new DevTechCardDto(m.Id, m.Number, m.Name, d.Id, d.Name, d.Quantity, machine?.Id, machine?.Name, machine?.Parameters, feederCount, machine?.Status,
            lastNote, d.WeaveId, weave, d.PlanKnitSeconds, d.PlanWeightGrams, m.Composition, d.Notes, feeders, palette,
            d.ProgramFileId, program?.FileName, program?.Version, program?.MachineParams, m.MainSketchFileId,
            canEdit && (ctx.Permissions.Has(Permissions.DevPattern) || ctx.Permissions.Has(Permissions.DevEdit)),
            canEdit && ctx.Permissions.Has(Permissions.DevSample), canSeeCost, samples, facts, d.RowVersion, (await SettingsAsync(org, ct)).FactTolerancePercent);
    }

    /// <summary>Показатели заполнения модели (D716) — по данным разделов; 9 запросов-подсчётов без загрузки строк.</summary>
    public async Task<DevCompletenessDto> CompletenessAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var m = await FindModelAsync(ctx, modelId, ct);
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId)
            .Select(d => new { d.Id, d.ProgramFileId, Threads = db.DevThreading.Count(t => t.DetailId == d.Id) }).ToListAsync(ct);
        var sizeCount = await db.DevModelSizes.CountAsync(s => s.OrganizationId == org && s.ModelId == modelId, ct);
        var ids = details.Select(d => d.Id).ToList();
        var filledDetailSizes = await db.DevDetailSizes.CountAsync(s => s.OrganizationId == org && ids.Contains(s.DetailId)
            && (s.LengthOffMachineCm != null || s.LengthAfterSteamCm != null || s.WidthCm != null), ct);
        var measures = await db.DevMeasures.Where(x => x.OrganizationId == org && x.ModelId == modelId).Select(x => x.Id).ToListAsync(ct);
        var measureValues = await db.DevMeasureValues.CountAsync(v => v.OrganizationId == org && measures.Contains(v.MeasureId), ct);
        var hasPalette = await db.DevModelColors.AnyAsync(c => c.OrganizationId == org && c.ModelId == modelId && c.ItemId != null, ct);
        var hasOps = await db.DevModelOperations.AnyAsync(o => o.OrganizationId == org && o.ModelId == modelId, ct);
        var hasFact = await db.DevSampleDetails.AnyAsync(f => f.OrganizationId == org && db.DevSamples.Any(s => s.Id == f.SampleId && s.ModelId == modelId), ct);
        var c = DevCompleteness.From(
            brief: m.SizeRange is not null || m.RunQuantity is not null || m.Description is not null,
            sketch: m.MainSketchFileId is not null,
            palette: hasPalette,
            details: details.Count > 0,
            threading: details.Count > 0 && details.All(d => d.Threads > 0),
            programs: details.Count > 0 && details.All(d => d.ProgramFileId is not null),
            detailSizes: details.Count > 0 && sizeCount > 0 && filledDetailSizes >= details.Count * sizeCount,
            measures: measures.Count > 0 && sizeCount > 0 && measureValues >= measures.Count * sizeCount,
            operations: hasOps,
            sample: hasFact);
        c = c.Only((await SettingsAsync(org, ct)).CompletenessSections);
        return new DevCompletenessDto(c.Sections.Select(s => new DevSectionDto(s.Section, s.Done)).ToList(), c.Done, c.Total, c.Percent);
    }

    /// <summary>
    /// Новая версия переданной в производство модели (D769): новая модель с тем же названием, ссылкой на прежнюю и номером версии,
    /// задание и исполнители — как у прежней; копируются палитра, детали с заправкой и размерами, размерная сетка и упаковка, табель мер,
    /// операции и фурнитура. Файлы, программы, образцы и расчёты остаются у прежней версии. У версии не больше одной следующей.
    /// </summary>
    public async Task<long> NewVersionAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var org = ctx.OrganizationId;
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var src = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
            if (src.Stage != DevStage.Handed)
            {
                throw new BusinessRuleException("dev.version.stage", "Новая версия делается от модели, переданной в производство; для остальных — «Создать похожую».");
            }

            if (await db.DevModels.AnyAsync(x => x.OrganizationId == org && x.PreviousVersionId == modelId, ct))
            {
                throw new BusinessRuleException("dev.version.exists", "У этой версии уже есть следующая — изменения вносите в неё.");
            }

            var header = new DevModelHeader(src.Name, src.CollectionId, src.Category, src.Audience, src.ChildSafety, src.HasPattern, src.CustomerId, src.DealId, null,
                src.Priority, DevModel.Roles.ToDictionary(r => r, src.AssigneeOf));
            var brief = new DevBrief(src.Description, src.SizeRange, src.ColorRange, src.TargetCost, src.TargetPrice, src.RunQuantity, null, src.Composition,
                src.Labeling, src.Care);
            var version = src.VersionNo + 1;
            var newId = await models.CreateCoreAsync(ctx, header, brief, $"версия {version} модели {src.Number}", ct);
            var m = await db.DevModels.SingleAsync(x => x.Id == newId && x.OrganizationId == org, ct);
            m.MarkVersionOf(modelId, version);
            if (src.PackBox is not null)
            {
                m.SetPackBox(src.PackBox);
            }

            // Первый проход: записи без зависимостей — палитра, сетка, детали, точки табеля, операции, фурнитура.
            var colors = await db.DevModelColors.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct);
            var colorMap = colors.ToDictionary(c => c.Id, c => DevModelColor.Create(org, newId, c.Label, c.ItemId, c.Swatch, c.Note, c.SortOrder));
            var sizes = await db.DevModelSizes.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct);
            var sizeMap = sizes.ToDictionary(z => z.Id, z => DevModelSize.Create(org, newId, z.Name, z.SortOrder, z.IsBase, z.PerBox));
            var details = await db.DevDetails.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct);
            var detailMap = details.ToDictionary(d => d.Id, d =>
            {
                var nd = DevDetail.Create(org, newId, d.Name, d.Quantity, d.SortOrder);
                nd.SetKnitting(d.MachineId, d.WeaveId, d.PlanKnitSeconds, d.PlanWeightGrams, d.Notes);
                return nd;
            });
            var measures = await db.DevMeasures.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct);
            var measureMap = measures.ToDictionary(x => x.Id, x => DevMeasure.Create(org, newId, x.Name, x.Tolerance, x.Step, x.SortOrder));
            db.DevModelColors.AddRange(colorMap.Values);
            db.DevModelSizes.AddRange(sizeMap.Values);
            db.DevDetails.AddRange(detailMap.Values);
            db.DevMeasures.AddRange(measureMap.Values);
            db.DevModelOperations.AddRange((await db.DevModelOperations.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct))
                .Select(o => DevModelOperation.Create(org, newId, o.Seq, o.OperationTypeId, o.Note, o.NormMinutes, o.Price)));
            db.DevModelAccessories.AddRange((await db.DevModelAccessories.AsNoTracking().Where(x => x.OrganizationId == org && x.ModelId == modelId).ToListAsync(ct))
                .Select(a => DevModelAccessory.Create(org, newId, a.ItemId, a.Quantity)));
            await db.SaveChangesAsync(ct);

            // Второй проход: заправка, размеры деталей и значения табеля — с новыми номерами цветов, деталей, размеров и точек.
            var detailIds = details.Select(d => d.Id).ToList();
            foreach (var t in await db.DevThreading.AsNoTracking().Where(x => x.OrganizationId == org && detailIds.Contains(x.DetailId)).ToListAsync(ct))
            {
                db.DevThreading.Add(DevThreading.Create(org, detailMap[t.DetailId].Id, new DevThreadingRow(t.Feeder, t.Side,
                    t.ColorId is { } cid ? colorMap[cid].Id : null, t.Label, t.Ends, t.Paraffin, t.Mode, t.Elastic), DevMachine.MaxFeeders));
            }

            foreach (var v in await db.DevDetailSizes.AsNoTracking().Where(x => x.OrganizationId == org && detailIds.Contains(x.DetailId)).ToListAsync(ct))
            {
                var row = DevDetailSize.Create(org, detailMap[v.DetailId].Id, sizeMap[v.SizeId].Id);
                row.Set(v.LengthOffMachineCm, v.LengthAfterSteamCm, v.WidthCm);
                db.DevDetailSizes.Add(row);
            }

            var measureIds = measures.Select(x => x.Id).ToList();
            foreach (var v in await db.DevMeasureValues.AsNoTracking().Where(x => x.OrganizationId == org && measureIds.Contains(x.MeasureId)).ToListAsync(ct))
            {
                db.DevMeasureValues.Add(DevMeasureValue.Create(org, measureMap[v.MeasureId].Id, sizeMap[v.SizeId].Id, v.Value));
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevModelCreated, nameof(DevModel), newId.ToString(), null,
                $"{m.Number}: версия {version} модели {src.Number}", $"деталей {details.Count}, цветов {colors.Count}, размеров {sizes.Count}", currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return newId;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    // ---------- Общие помощники ----------

    /// <summary>Местное время организации (D722): даты документов и этикеток — по её часовому поясу, а не по UTC.</summary>
    internal static async Task<DateTime> LocalNowAsync(IKnitErpDbContext db, long org, DateTime utc, CancellationToken ct) =>
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc,
            await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct));

    internal async Task<DevSettings> SettingsAsync(long org, CancellationToken ct) =>
        await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct) ?? DevSettings.Defaults(org);

    internal async Task<IReadOnlyList<DevFactDto>> FactsAsync(AccessContext ctx, long modelId, long? detailId, IReadOnlyDictionary<long, DevColorDto> colors,
        CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var facts = await (from f in db.DevSampleDetails.AsNoTracking()
                           join s in db.DevSamples.AsNoTracking() on f.SampleId equals s.Id
                           join u in db.Users.AsNoTracking() on f.RecordedByUserId equals u.Id
                           where f.OrganizationId == org && s.ModelId == modelId && (detailId == null || f.DetailId == detailId)
                           orderby s.No descending
                           select new { f.Id, f.SampleId, f.DetailId, s.No, f.KnitSeconds, f.WeightGrams, f.RecordedAtUtc, User = u.DisplayName }).ToListAsync(ct);
        var factIds = facts.Select(f => f.Id).ToList();
        var shares = await db.DevSampleColorShares.AsNoTracking().Where(x => x.OrganizationId == org && factIds.Contains(x.SampleDetailId)).ToListAsync(ct);
        return facts.Select(f => new DevFactDto(f.SampleId, f.DetailId, f.No, f.KnitSeconds, f.WeightGrams,
            shares.Where(x => x.SampleDetailId == f.Id).Select(x => (colors.GetValueOrDefault(x.ColorId)?.Label ?? "—", x.Percent)).ToList(),
            f.RecordedAtUtc, f.User)).ToList();
    }

    private IQueryable<DevColorDto> Colors(AccessContext ctx, long modelId) =>
        from c in db.DevModelColors.AsNoTracking()
        join i in db.Items.AsNoTracking() on c.ItemId equals i.Id into ij
        from i in ij.DefaultIfEmpty()
        where c.OrganizationId == ctx.OrganizationId && c.ModelId == modelId
        orderby c.SortOrder, c.Id
        select new DevColorDto(c.Id, c.Label, c.ItemId, i == null ? null : i.Name, c.Swatch, c.Note);

    private async Task<AccessContext> DemandAnyAsync(CancellationToken ct, params string[] codes)
    {
        var ctx = await guard.CurrentAsync(ct);
        return codes.Any(ctx.Permissions.Has) ? ctx : await guard.DemandAsync(codes[0], ct);
    }

    private async Task<DevModel> FindModelAsync(AccessContext ctx, long modelId, CancellationToken ct) =>
        await db.DevModels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId && m.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Модель");

    internal async Task<DevModel> FindEditableModelAsync(AccessContext ctx, long modelId, CancellationToken ct)
    {
        var m = await FindModelAsync(ctx, modelId, ct);
        return m.Stage switch
        {
            DevStage.Handed => throw new BusinessRuleException("dev.model.handed", "Модель передана в производство — изменения вносятся новой версией модели."),
            DevStage.Cancelled => throw new BusinessRuleException("dev.model.cancelled", "Модель отменена — сначала верните её в работу."),
            _ => m,
        };
    }

    private async Task<DevMachine> FindMachineAsync(AccessContext ctx, long machineId, CancellationToken ct) =>
        await db.DevMachines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == machineId && m.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Станок");

    private async Task<(DevDetail Detail, DevModel Model)> LoadDetailAsync(AccessContext ctx, long detailId, byte[] rowVersion, CancellationToken ct)
    {
        var d = await db.DevDetails.SingleOrDefaultAsync(x => x.Id == detailId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Деталь");
        d.EnsureVersion(d.RowVersion, rowVersion);
        return (d, await FindEditableModelAsync(ctx, d.ModelId, ct));
    }

    private async Task EnsureYarnAsync(AccessContext ctx, long? itemId, CancellationToken ct)
    {
        if (itemId is { } id && !await db.Items.AnyAsync(i => i.Id == id && i.OrganizationId == ctx.OrganizationId && !i.IsArchived
                && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material), ct))
        {
            throw new BusinessRuleException("dev.color.yarn", "Пряжа — действующая позиция номенклатуры вида «Сырьё» или «Материалы».");
        }
    }

    private async Task EnsureColorLabelAsync(AccessContext ctx, long modelId, string label, long? exceptId, CancellationToken ct)
    {
        if (await db.DevModelColors.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.ModelId == modelId && c.Label == label && c.Id != exceptId, ct))
        {
            throw new BusinessRuleException("dev.color.duplicate", $"Цвет «{label}» уже есть в палитре.");
        }
    }

    private async Task EnsureMachineNameAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.DevMachines.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.Name == name && m.Id != exceptId, ct))
        {
            throw new BusinessRuleException("dev.machine.duplicate", $"Станок «{name}» уже есть в справочнике.");
        }
    }

    private async Task<long> SaveNewAsync(AccessContext ctx, object entity, Func<long> id, string action, string reason, string after, CancellationToken ct,
        long? modelId = null)
    {
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, action, modelId is null ? entity.GetType().Name : nameof(DevModel), modelId ?? id(), null, after, reason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return id();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

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

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));

    /// <summary>
    /// Этикетка модуля (D719). Видна тем же, кто видит техкарту (разработка или цех); деньги не печатаются. Код штрихкода — номер модели
    /// латиницей с суффиксом детали или образца: «M-00001-D12», «M-00001-S2» — его же читает сканер в поиске.
    /// </summary>
    public async Task<DevLabelDto> LabelAsync(DevLabelKind kind, long id, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevView, Permissions.ProductionStageMark);
        var org = ctx.OrganizationId;
        var label = await LabelCoreAsync(ctx, kind, id, ct);
        var settings = await SettingsAsync(org, ct);
        return label with { WidthMm = settings.LabelWidthMm, HeightMm = settings.LabelHeightMm, OnA4 = settings.LabelsOnA4 };
    }

    private async Task<DevLabelDto> LabelCoreAsync(AccessContext ctx, DevLabelKind kind, long id, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        switch (kind)
        {
            case DevLabelKind.Model:
            {
                if (!ctx.Permissions.Has(Permissions.DevView))
                {
                    throw new NotFoundException("Модель");
                }

                var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
                var collection = m.CollectionId is { } cid ? await db.DevLookups.AsNoTracking().Where(l => l.Id == cid).Select(l => l.Name).SingleOrDefaultAsync(ct) : null;
                var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == id).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
                    .Select(d => d.Name).ToListAsync(ct);
                return new DevLabelDto(LabelCode(m.Number, null), m.Number, m.Name,
                    Lines(("Стадия", DevStages.Name(m.Stage)), ("Коллекция", collection), ("Запуск", m.PlannedLaunch?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                        ("Детали", details.Count == 0 ? null : string.Join(", ", details))), $"dev/models/{m.Id}");
            }
            case DevLabelKind.Detail:
            {
                var d = await db.DevDetails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new NotFoundException("Деталь");
                var m = await db.DevModels.AsNoTracking().SingleAsync(x => x.Id == d.ModelId && x.OrganizationId == org, ct);
                var machine = d.MachineId is { } mid
                    ? await db.DevMachines.AsNoTracking().Where(x => x.Id == mid).Select(x => new { x.Name, x.Gauge }).SingleOrDefaultAsync(ct)
                    : null;
                var weave = d.WeaveId is { } wid ? await db.DevLookups.AsNoTracking().Where(l => l.Id == wid).Select(l => l.Name).SingleOrDefaultAsync(ct) : null;
                return new DevLabelDto(LabelCode(m.Number, "D" + d.Id.ToString(CultureInfo.InvariantCulture)), $"{m.Number} · {d.Name}", m.Name,
                    Lines(("Станок", machine is null ? null : $"{machine.Name} · E{machine.Gauge}"), ("Переплетение", weave),
                        ("Количество", d.Quantity > 1 ? d.Quantity.ToString(CultureInfo.InvariantCulture) : null),
                        ("Вес, г", d.PlanWeightGrams?.ToString("0.#", CultureInfo.InvariantCulture))), $"dev/tech/{d.Id}");
            }
            case DevLabelKind.Sample:
            {
                var s = await db.DevSamples.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new NotFoundException("Образец");
                var m = await db.DevModels.AsNoTracking().SingleAsync(x => x.Id == s.ModelId && x.OrganizationId == org, ct);
                var size = s.SizeId is { } zid ? await db.DevModelSizes.AsNoTracking().Where(z => z.Id == zid).Select(z => z.Name).SingleOrDefaultAsync(ct) : null;
                return new DevLabelDto(LabelCode(m.Number, "S" + s.No.ToString(CultureInfo.InvariantCulture)), $"{m.Number} · образец № {s.No}", m.Name,
                    Lines(("Размер", size), ("Статус", DevSample.StatusName(s.Status)), ("Создан", (await LocalNowAsync(db, org, s.CreatedAtUtc, ct)).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))),
                    $"dev/models/{m.Id}");
            }
            default:
                throw new NotFoundException("Этикетка");
        }
    }

    private static IReadOnlyList<DevLabelLineDto> Lines(params (string Key, string? Value)[] lines) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => new DevLabelLineDto(l.Key, l.Value!)).ToList();

    /// <summary>Номер модели латиницей (кириллица, похожая на латиницу, заменяется; прочее — отбрасывается), чтобы его кодировал Code 128.</summary>
    internal static string LabelCode(string number, string? suffix)
    {
        const string cyr = "АВЕКМНОРСТХаеорсх", lat = "ABEKMHOPCTXaeopcx";
        var chars = number.Select(c => cyr.IndexOf(c) is var i and >= 0 ? lat[i] : c).Where(c => c is > ' ' and <= '~').ToArray();
        var code = new string(chars);
        return suffix is null ? code : $"{code}-{suffix}";
    }
}
