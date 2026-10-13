using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

public sealed record DevPatternFilter(string? Search = null, DevPatternKind? Kind = null, int? Colors = null, int? RapportWidth = null, long? MachineClassId = null,
    long? TagId = null, bool IncludeArchived = false);

public sealed record DevPatternRowDto(long Id, string Number, string Name, DevPatternKind Kind, int? RapportWidth, int? RapportHeight, int Colors,
    string? MachineClass, DevPatternStatus Status, int Version, int UsedInModels, bool ReadyForProgramming)
{
    public string KindName => DevPattern.KindName(Kind);
    public string StatusName => DevPattern.StatusName(Status);
}

public sealed record DevPatternColorDto(long Id, int No, string Name, decimal? SharePercent, int? Feeder, string? GridKey, byte[] RowVersion);

public sealed record DevPatternYarnDto(long ColorId, long? ItemId, string? ItemName, string? Swatch);

public sealed record DevPatternColorwayDto(long Id, string Name, IReadOnlyList<DevPatternYarnDto> Yarns, int UsedInModels, byte[] RowVersion);

public sealed record DevPatternFileDto(long Id, int Version, DevPatternFileKind Kind, string FileName, int SizeBytes, int? KnitSeconds, DateTime UploadedAtUtc,
    string UploadedBy, bool IsImage);

public sealed record DevPatternVersionDto(int Version, string Change, string? Reason, DateTime CreatedAtUtc, string CreatedBy, IReadOnlyList<DevPatternFileDto> Files);

public sealed record DevPatternUseDto(long ModelId, string Number, string Name, int? Version, string? Colorway, DevStage Stage)
{
    public string StageName => DevStages.Name(Stage);
}

/// <summary>Карточка рисунка (D726): свойства, цвета, колориты с пряжей, версии с файлами, где использован, что можно делать.</summary>
public sealed record DevPatternDto(
    long Id, string Number, string Name, DevPatternKind Kind, int? RapportWidth, int? RapportHeight, decimal? MaxFloatMm, long? MachineClassId, string? MachineClass,
    int? MachineFeeders, int? ProductWidthLoops, string? AlignmentRule, string? Note, DevPatternStatus Status, int CurrentVersion, long? DessinatorUserId,
    string? Dessinator, long? ProgrammerUserId, string? Programmer, DateTime? ReadyForProgrammingAtUtc, bool HasProgram, string? CopiedFrom,
    IReadOnlyList<DevPatternColorDto> Colors, IReadOnlyList<DevPatternColorwayDto> Colorways, IReadOnlyList<DevPatternVersionDto> Versions,
    IReadOnlyList<DevPatternUseDto> UsedIn, bool CanEdit, bool CanApprove, bool CanProgram, byte[] RowVersion)
{
    public string KindName => DevPattern.KindName(Kind);
    public string StatusName => DevPattern.StatusName(Status);
    public decimal ShareTotal => Colors.Sum(c => c.SharePercent ?? 0);
}

public sealed record DevPatternOptionsDto(IReadOnlyList<DevOptionDto> MachineClasses, IReadOnlyList<DevOptionDto> Yarns, IReadOnlyList<DevOptionDto> Users);

/// <summary>Рисунок модели в карточке модели (D726): что привязано и из чего выбирать.</summary>
public sealed record DevModelPatternDto(long? PatternId, string? Number, string? Name, int? Version, int? CurrentVersion, long? ColorwayId, string? Colorway,
    IReadOnlyList<DevOptionDto> Patterns, IReadOnlyList<DevOptionDto> Colorways, IReadOnlyList<int> Versions, bool CanLink);

/// <summary>
/// Рисунки разработки (D726–D727, ТЗ §7): библиотека с поиском, карточка, цвета и доли (вручную или по файлу-сетке), колориты с пряжей,
/// версии (только вставка) с файлами рисунка и программами машины, статусы с проверками при утверждении, «готов к программированию»
/// — задача программисту, копирование, привязка версии и колорита к модели. Права: смотреть — «разработка: просмотр», менять —
/// «рисунки» (dev.pattern), утверждать — «утверждение», программу — «рисунки» или «изменение» (программист).
/// </summary>
public sealed class DevPatternService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    private const string Counter = "dev.pattern";

    public async Task<IReadOnlyList<DevPatternRowDto>> ListAsync(DevPatternFilter f, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var q = db.DevPatterns.AsNoTracking().Where(p => p.OrganizationId == org);
        if (!f.IncludeArchived)
        {
            q = q.Where(p => p.Status != DevPatternStatus.Archived);
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var text = f.Search.Trim();
            q = q.Where(p => p.Number.Contains(text) || p.Name.Contains(text));
        }

        if (f.Kind is { } kind)
        {
            q = q.Where(p => p.Kind == kind);
        }

        if (f.RapportWidth is { } rw)
        {
            q = q.Where(p => p.RapportWidth == rw);
        }

        if (f.MachineClassId is { } mc)
        {
            q = q.Where(p => p.MachineClassId == mc);
        }

        if (f.TagId is { } tag)
        {
            q = q.Where(p => db.TagLinks.Any(l => l.OrganizationId == org && l.TagId == tag && l.Entity == TaggedEntity.DevPattern && l.EntityId == p.Id));
        }

        var rows = await q.OrderByDescending(p => p.Id).Take(500)
            .Select(p => new
            {
                p.Id, p.Number, p.Name, p.Kind, p.RapportWidth, p.RapportHeight, p.Status, p.CurrentVersion, p.ReadyForProgrammingAtUtc,
                Colors = db.DevPatternColors.Count(c => c.OrganizationId == org && c.PatternId == p.Id),
                Class = db.DevLookups.Where(l => l.Id == p.MachineClassId).Select(l => l.Name).FirstOrDefault(),
                Used = db.DevModels.Count(m => m.OrganizationId == org && m.PatternId == p.Id),
            }).ToListAsync(ct);
        return rows.Where(r => f.Colors is not { } n || r.Colors == n)
            .Select(r => new DevPatternRowDto(r.Id, r.Number, r.Name, r.Kind, r.RapportWidth, r.RapportHeight, r.Colors, r.Class, r.Status, r.CurrentVersion, r.Used,
                r.ReadyForProgrammingAtUtc is not null)).ToList();
    }

    public async Task<DevPatternOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var classes = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.MachineClass && !l.IsArchived)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new DevOptionDto(l.Id, l.Number == null ? l.Name : l.Name + " · " + l.Number + " нитевод.")).ToListAsync(ct);
        var yarns = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material))
            .OrderBy(i => i.Name).Take(3000).Select(i => new DevOptionDto(i.Id, i.Code + " — " + i.Name)).ToListAsync(ct);
        var users = await (from m in db.OrganizationMembers.AsNoTracking()
                           join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                           where m.OrganizationId == org && m.Status == MembershipStatus.Active
                           orderby u.DisplayName
                           select new DevOptionDto(u.Id, u.DisplayName)).ToListAsync(ct);
        return new DevPatternOptionsDto(classes, yarns, users);
    }

    public async Task<DevPatternDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var p = await db.DevPatterns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new NotFoundException("Рисунок");
        var cls = p.MachineClassId is { } mc
            ? await db.DevLookups.AsNoTracking().Where(l => l.Id == mc && l.OrganizationId == org).Select(l => new { l.Name, l.Number }).SingleOrDefaultAsync(ct)
            : null;
        var colors = await db.DevPatternColors.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == id).OrderBy(c => c.No)
            .Select(c => new DevPatternColorDto(c.Id, c.No, c.Name, c.SharePercent, c.Feeder, c.GridKey, c.RowVersion)).ToListAsync(ct);
        var colorways = await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == id).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync(ct);
        var cwIds = colorways.Select(c => c.Id).ToList();
        var yarns = await (from y in db.DevPatternColorwayYarns.AsNoTracking()
                           join i in db.Items.AsNoTracking() on y.ItemId equals i.Id into ij
                           from i in ij.DefaultIfEmpty()
                           where y.OrganizationId == org && cwIds.Contains(y.ColorwayId)
                           select new { y.ColorwayId, y.ColorId, y.ItemId, Item = i == null ? null : i.Code + " — " + i.Name, y.Swatch }).ToListAsync(ct);
        var uses = await (from m in db.DevModels.AsNoTracking()
                          join cw in db.DevPatternColorways.AsNoTracking() on m.ColorwayId equals cw.Id into cj
                          from cw in cj.DefaultIfEmpty()
                          where m.OrganizationId == org && m.PatternId == id
                          orderby m.Id descending
                          select new DevPatternUseDto(m.Id, m.Number, m.Name, m.PatternVersion, cw == null ? null : cw.Name, m.Stage)).ToListAsync(ct);
        var cwUse = await db.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && m.ColorwayId != null && cwIds.Contains(m.ColorwayId.Value))
            .GroupBy(m => m.ColorwayId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var versions = await (from v in db.DevPatternVersions.AsNoTracking()
                              join u in db.Users.AsNoTracking() on v.CreatedByUserId equals u.Id
                              where v.OrganizationId == org && v.PatternId == id
                              orderby v.Version descending
                              select new { v.Version, v.Change, v.Reason, v.CreatedAtUtc, User = u.DisplayName }).ToListAsync(ct);
        var files = await (from f in db.DevPatternFiles.AsNoTracking()
                           join u in db.Users.AsNoTracking() on f.UploadedByUserId equals u.Id
                           where f.OrganizationId == org && f.PatternId == id
                           orderby f.Id descending
                           select new DevPatternFileDto(f.Id, f.Version, f.Kind, f.FileName, f.SizeBytes, f.KnitSeconds, f.UploadedAtUtc, u.DisplayName,
                               f.ContentType == "image/png" || f.ContentType == "image/bmp")).ToListAsync(ct);
        var names = await UserNamesAsync([p.DessinatorUserId, p.ProgrammerUserId], ct);
        var copied = p.CopiedFromPatternId is { } src
            ? await db.DevPatterns.AsNoTracking().Where(x => x.Id == src && x.OrganizationId == org).Select(x => x.Number).SingleOrDefaultAsync(ct)
            : null;
        var perms = ctx.Permissions;
        return new DevPatternDto(p.Id, p.Number, p.Name, p.Kind, p.RapportWidth, p.RapportHeight, p.MaxFloatMm, p.MachineClassId, cls?.Name, cls?.Number,
            p.ProductWidthLoops, p.AlignmentRule, p.Note, p.Status, p.CurrentVersion, p.DessinatorUserId, Name(names, p.DessinatorUserId), p.ProgrammerUserId,
            Name(names, p.ProgrammerUserId), p.ReadyForProgrammingAtUtc,
            files.Any(f => f.Version == p.CurrentVersion && f.Kind == DevPatternFileKind.Program), copied, colors,
            colorways.Select(c => new DevPatternColorwayDto(c.Id, c.Name,
                yarns.Where(y => y.ColorwayId == c.Id).Select(y => new DevPatternYarnDto(y.ColorId, y.ItemId, y.Item, y.Swatch)).ToList(),
                cwUse.GetValueOrDefault(c.Id), c.RowVersion)).ToList(),
            versions.Select(v => new DevPatternVersionDto(v.Version, v.Change, v.Reason, v.CreatedAtUtc, v.User, files.Where(f => f.Version == v.Version).ToList())).ToList(),
            uses, perms.Has(Permissions.DevPattern), perms.Has(Permissions.DevApprove), perms.Has(Permissions.DevPattern) || perms.Has(Permissions.DevEdit),
            p.RowVersion);
    }

    /// <summary>Новый рисунок: номер «Р-00001», версия 1 «Создан», колорит «Основной».</summary>
    public async Task<long> CreateAsync(DevPatternValues values, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await ValidateAsync(ctx, values, ct);
        return await InTransactionAsync(async () =>
        {
            var number = DevPattern.FormatNumber(await DocumentNumbers.NextAsync(db, ctx.OrganizationId, Counter, ct));
            var p = DevPattern.Create(ctx.OrganizationId, number, values, clock.UtcNow, ctx.UserId);
            db.DevPatterns.Add(p);
            await db.SaveOrConflictAsync(ct);
            db.DevPatternVersions.Add(DevPatternVersion.Create(ctx.OrganizationId, p.Id, 1, "Создан", null, clock.UtcNow, ctx.UserId));
            db.DevPatternColorways.Add(DevPatternColorway.Create(ctx.OrganizationId, p.Id, "Основной", 1));
            Audit(ctx, p.Id, null, $"{p.Number} «{p.Name}»", "рисунок создан");
            await db.SaveChangesAsync(ct);
            return p.Id;
        }, ct);
    }

    public async Task UpdateAsync(long id, DevPatternValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var p = await LoadAsync(ctx, id, ct);
            p.EnsureVersion(p.RowVersion, rowVersion);
            await ValidateAsync(ctx, values, ct);
            var before = Describe(p);
            p.Set(values);
            Audit(ctx, p.Id, before, Describe(p), $"{p.Number}: свойства");
            await db.SaveOrConflictAsync(ct);
        });
    }

    // ---------- Цвета и колориты ----------

    public async Task<long> AddColorAsync(long patternId, string? name, decimal? share, int? feeder, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        return await InTransactionAsync(async () =>
        {
            var p = await LoadEditableAsync(ctx, patternId, ct);
            await db.LockAsync($"kniterp.dev.pattern.{ctx.OrganizationId}.{patternId}", ct);
            var nos = await db.DevPatternColors.Where(c => c.OrganizationId == ctx.OrganizationId && c.PatternId == patternId).Select(c => c.No).ToListAsync(ct);
            if (nos.Count >= DevPattern.MaxColors)
            {
                throw new BusinessRuleException("dev.pattern.colors.limit", $"В рисунке — не больше {DevPattern.MaxColors} цветов.");
            }

            var color = DevPatternColor.Create(ctx.OrganizationId, patternId, (nos.Count == 0 ? 0 : nos.Max()) + 1, name, share, feeder, null);
            db.DevPatternColors.Add(color);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, p.Id, null, $"цвет {color.No}: {color.Name}", $"{p.Number}: цвета");
            await db.SaveChangesAsync(ct);
            return color.Id;
        }, ct);
    }

    public async Task UpdateColorAsync(long colorId, string? name, decimal? share, int? feeder, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var c = await db.DevPatternColors.SingleOrDefaultAsync(x => x.Id == colorId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Цвет");
            c.EnsureVersion(c.RowVersion, rowVersion);
            var p = await LoadEditableAsync(ctx, c.PatternId, ct);
            var before = $"{c.Name} {c.SharePercent}% н.{c.Feeder}";
            c.Set(name, share, feeder, c.GridKey);
            Audit(ctx, p.Id, before, $"{c.Name} {c.SharePercent}% н.{c.Feeder}", $"{p.Number}: цвет {c.No}");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Убрать цвет — вместе с его пряжей во всех колоритах (рисунок не утверждён).</summary>
    public async Task RemoveColorAsync(long colorId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await InTransactionAsync(async () =>
        {
            var c = await db.DevPatternColors.SingleOrDefaultAsync(x => x.Id == colorId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Цвет");
            var p = await LoadEditableAsync(ctx, c.PatternId, ct);
            db.DevPatternColorwayYarns.RemoveRange(await db.DevPatternColorwayYarns.Where(y => y.OrganizationId == ctx.OrganizationId && y.ColorId == colorId).ToListAsync(ct));
            db.DevPatternColors.Remove(c);
            Audit(ctx, p.Id, $"цвет {c.No}: {c.Name}", "убран", $"{p.Number}: цвета");
            await db.SaveOrConflictAsync(ct);
            return 0L;
        }, ct);
    }

    /// <summary>
    /// Доли цветов по файлу-сетке текущей версии (ТЗ §7): клетка — петля. Цвет сетки сопоставляется с цветом рисунка по «цвету сетки»;
    /// цветам без него назначаются новые цвета сетки по убыванию доли; лишние цвета сетки добавляются цветами «Цвет N».
    /// </summary>
    public async Task<int> ComputeSharesAsync(long patternId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        return await InTransactionAsync(async () =>
        {
            var p = await LoadEditableAsync(ctx, patternId, ct);
            var file = await db.DevPatternFiles.AsNoTracking()
                           .Where(f => f.OrganizationId == ctx.OrganizationId && f.PatternId == patternId && f.Version == p.CurrentVersion && f.Kind == DevPatternFileKind.Pattern)
                           .OrderByDescending(f => f.Id).Select(f => new { f.Content, f.ContentType, f.FileName }).FirstOrDefaultAsync(ct)
                       ?? throw new BusinessRuleException("dev.pattern.grid.none", "У текущей версии нет файла-сетки рисунка (PNG, BMP или CSV).");
            var counted = DevPatternGrid.Count(file.Content, file.ContentType);
            await db.LockAsync($"kniterp.dev.pattern.{ctx.OrganizationId}.{patternId}", ct);
            var colors = await db.DevPatternColors.Where(c => c.OrganizationId == ctx.OrganizationId && c.PatternId == patternId).OrderBy(c => c.No).ToListAsync(ct);
            var free = new Queue<DevPatternColor>(colors.Where(c => c.GridKey is null || counted.All(g => g.Key != c.GridKey)));
            var no = colors.Count == 0 ? 0 : colors.Max(c => c.No);
            foreach (var g in counted)
            {
                var match = colors.FirstOrDefault(c => c.GridKey == g.Key) ?? (free.Count > 0 ? free.Dequeue() : null);
                if (match is null)
                {
                    if (no >= DevPattern.MaxColors)
                    {
                        throw new BusinessRuleException("dev.pattern.colors.limit", $"В рисунке — не больше {DevPattern.MaxColors} цветов.");
                    }

                    db.DevPatternColors.Add(DevPatternColor.Create(ctx.OrganizationId, patternId, ++no, $"Цвет {no}", g.Percent, null, g.Key));
                }
                else
                {
                    match.SetShare(g.Percent, g.Key);
                }
            }

            foreach (var c in free)
            {
                c.SetShare(0, null);
            }

            Audit(ctx, p.Id, null, string.Join(", ", counted.Select(g => $"{g.Key} {g.Percent:0.##}%")), $"{p.Number}: доли по файлу {file.FileName}");
            await db.SaveOrConflictAsync(ct);
            return counted.Count;
        }, ct);
    }

    public async Task<long> AddColorwayAsync(long patternId, string? name, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        return await InTransactionAsync(async () =>
        {
            var p = await LoadEditableAsync(ctx, patternId, ct);
            var count = await db.DevPatternColorways.CountAsync(c => c.OrganizationId == ctx.OrganizationId && c.PatternId == patternId, ct);
            if (count >= DevPattern.MaxColorways)
            {
                throw new BusinessRuleException("dev.pattern.colorways.limit", $"У рисунка — не больше {DevPattern.MaxColorways} колоритов.");
            }

            var cw = DevPatternColorway.Create(ctx.OrganizationId, patternId, name, count + 1);
            await EnsureColorwayNameAsync(ctx, patternId, cw.Name, null, ct);
            db.DevPatternColorways.Add(cw);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, p.Id, null, cw.Name, $"{p.Number}: колорит");
            await db.SaveChangesAsync(ct);
            return cw.Id;
        }, ct);
    }

    public async Task RenameColorwayAsync(long colorwayId, string? name, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var cw = await db.DevPatternColorways.SingleOrDefaultAsync(x => x.Id == colorwayId && x.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Колорит");
            cw.EnsureVersion(cw.RowVersion, rowVersion);
            var p = await LoadEditableAsync(ctx, cw.PatternId, ct);
            var before = cw.Name;
            cw.Rename(name);
            await EnsureColorwayNameAsync(ctx, cw.PatternId, cw.Name, cw.Id, ct);
            Audit(ctx, p.Id, before, cw.Name, $"{p.Number}: колорит");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Убрать колорит можно, пока его не выбрала ни одна модель и ни один образец.</summary>
    public async Task RemoveColorwayAsync(long colorwayId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var cw = await db.DevPatternColorways.SingleOrDefaultAsync(x => x.Id == colorwayId && x.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Колорит");
            var p = await LoadEditableAsync(ctx, cw.PatternId, ct);
            if (await db.DevModels.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.ColorwayId == colorwayId, ct)
                || await db.DevSamples.AnyAsync(s => s.OrganizationId == ctx.OrganizationId && s.ColorwayId == colorwayId, ct))
            {
                throw new BusinessRuleException("dev.pattern.colorway.used", "Колорит выбран в моделях или образцах — его нельзя убрать.");
            }

            db.DevPatternColorways.Remove(cw);
            Audit(ctx, p.Id, cw.Name, "убран", $"{p.Number}: колорит");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Пряжа цвета в колорите: позиция номенклатуры (сырьё или материал) своей организации и цвет на экране.</summary>
    public async Task SetYarnAsync(long colorwayId, long colorId, long? itemId, string? swatch, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var org = ctx.OrganizationId;
            var cw = await db.DevPatternColorways.AsNoTracking().SingleOrDefaultAsync(x => x.Id == colorwayId && x.OrganizationId == org, ct)
                     ?? throw new NotFoundException("Колорит");
            var p = await LoadEditableAsync(ctx, cw.PatternId, ct);
            if (!await db.DevPatternColors.AnyAsync(c => c.Id == colorId && c.OrganizationId == org && c.PatternId == cw.PatternId, ct))
            {
                throw new NotFoundException("Цвет");
            }

            if (itemId is { } iid && !await db.Items.AnyAsync(i => i.Id == iid && i.OrganizationId == org && !i.IsArchived
                                                                  && (i.Type == ItemType.RawMaterial || i.Type == ItemType.Material), ct))
            {
                throw new BusinessRuleException("dev.color.yarn", "Пряжа — действующая позиция «Сырьё» или «Материалы» своей номенклатуры.");
            }

            var y = await db.DevPatternColorwayYarns.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ColorwayId == colorwayId && x.ColorId == colorId, ct);
            if (y is null)
            {
                y = DevPatternColorwayYarn.Create(org, colorwayId, colorId);
                db.DevPatternColorwayYarns.Add(y);
            }

            y.Set(itemId, string.IsNullOrWhiteSpace(swatch) ? null : swatch.Trim());
            Audit(ctx, p.Id, null, $"колорит «{cw.Name}»: пряжа {itemId?.ToString() ?? "—"}", $"{p.Number}: раскладка цветов");
            await db.SaveOrConflictAsync(ct);
        });
    }

    // ---------- Версии, файлы, статусы ----------

    /// <summary>
    /// Файл к текущей версии: сетка рисунка — пока рисунок не утверждён; программа машины — в любой момент (программист прикладывает её к версии
    /// рисунка и отмечает время вязки изделия).
    /// </summary>
    public async Task<long> AddFileAsync(long patternId, DevPatternFileKind kind, string? fileName, byte[] content, int? knitSeconds, CancellationToken ct = default)
    {
        var ctx = kind == DevPatternFileKind.Program ? await DemandAnyAsync(ct, Permissions.DevPattern, Permissions.DevEdit) : await guard.DemandAsync(Permissions.DevPattern, ct);
        return await InTransactionAsync(async () =>
        {
            var p = await LoadAsync(ctx, patternId, ct);
            if (kind == DevPatternFileKind.Pattern)
            {
                p.EnsureEditable();
            }
            else if (p.Status == DevPatternStatus.Archived)
            {
                throw new BusinessRuleException("dev.pattern.locked", "Рисунок в архиве — сначала верните его из архива.");
            }

            await db.LockAsync($"kniterp.dev.pattern.{ctx.OrganizationId}.{patternId}", ct);
            var used = await db.DevPatternFiles.AsNoTracking().Where(f => f.OrganizationId == ctx.OrganizationId && f.PatternId == patternId)
                .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(f => (long)f.SizeBytes) }).SingleOrDefaultAsync(ct);
            DevFile.EnsureQuota(used?.Count ?? 0, used?.Bytes ?? 0, content.Length);
            var file = DevPatternFile.Create(ctx.OrganizationId, patternId, p.CurrentVersion, kind, fileName, content, knitSeconds, clock.UtcNow, ctx.UserId);
            db.DevPatternFiles.Add(file);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, p.Id, null, $"{(kind == DevPatternFileKind.Program ? "программа" : "файл рисунка")} {file.FileName}, версия {file.Version}"
                                   + (file.KnitSeconds is { } s ? $", вязка {s / 60}:{s % 60:00}" : ""), p.Number, AuditActions.DevFileAdded);
            await db.SaveChangesAsync(ct);
            return file.Id;
        }, ct);
    }

    public async Task<(string FileName, string ContentType, byte[] Content)> GetFileAsync(long fileId, bool preview = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var f = await db.DevPatternFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Файл");
        if (preview)
        {
            return f.ContentType is "image/png" or "image/bmp" ? (f.FileName, f.ContentType, f.Content) : throw new NotFoundException("Файл");
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.DevFileViewed, nameof(DevPattern), f.PatternId.ToString(),
            null, $"{f.FileName}, версия {f.Version}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return (f.FileName, f.ContentType == DevPatternFile.Csv ? "text/csv" : f.ContentType, f.Content);
    }

    /// <summary>Новая версия рисунка (ТЗ §7): что изменено и причина — обязательно «что»; утверждённый рисунок возвращается в черновик.</summary>
    public async Task<int> NewVersionAsync(long patternId, string? change, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        return await InTransactionAsync(async () =>
        {
            var p = await LoadAsync(ctx, patternId, ct);
            p.EnsureVersion(p.RowVersion, rowVersion);
            var before = DevPattern.StatusName(p.Status);
            var version = p.NextVersion();
            db.DevPatternVersions.Add(DevPatternVersion.Create(ctx.OrganizationId, p.Id, version, change, reason, clock.UtcNow, ctx.UserId));
            Audit(ctx, p.Id, before, $"версия {version}: {change}", p.Number);
            await db.SaveOrConflictAsync(ct);
            return version;
        }, ct);
    }

    /// <summary>Статус рисунка. Утвердить — с правом «утверждение» и после проверок ТЗ §7; остальное — с правом «рисунки».</summary>
    public async Task MoveAsync(long patternId, DevPatternStatus target, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(target == DevPatternStatus.Approved ? Permissions.DevApprove : Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var p = await LoadAsync(ctx, patternId, ct);
            p.EnsureVersion(p.RowVersion, rowVersion);
            if (target == DevPatternStatus.Approved)
            {
                await EnsureApprovableAsync(ctx, p, ct);
            }

            var before = DevPattern.StatusName(p.Status);
            p.MoveTo(target);
            Audit(ctx, p.Id, before, DevPattern.StatusName(p.Status), $"{p.Number}: статус");
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>«Рисунок готов к программированию»: у текущей версии есть файл рисунка; программист рисунка видит задачу в «Моих задачах» и колокольчике.</summary>
    public async Task ReadyForProgrammingAsync(long patternId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        await RunAsync(async () =>
        {
            var p = await LoadAsync(ctx, patternId, ct);
            p.EnsureVersion(p.RowVersion, rowVersion);
            if (!await db.DevPatternFiles.AnyAsync(f => f.OrganizationId == ctx.OrganizationId && f.PatternId == p.Id && f.Version == p.CurrentVersion
                                                         && f.Kind == DevPatternFileKind.Pattern, ct))
            {
                throw new BusinessRuleException("dev.pattern.ready.file", "Сначала загрузите файл рисунка текущей версии.");
            }

            if (p.ProgrammerUserId is null)
            {
                throw new BusinessRuleException("dev.pattern.ready.programmer", "Назначьте программиста рисунка.");
            }

            p.MarkReadyForProgramming(clock.UtcNow);
            Audit(ctx, p.Id, null, $"готов к программированию, версия {p.CurrentVersion}", p.Number);
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>
    /// Копия рисунка (ТЗ §7 «копирование рисунка в новую модель с новой версией»): новый номер, черновик, версия 1 «Копия Р-…»; цвета, колориты
    /// с пряжей и последний файл рисунка текущей версии. Если указана модель — копия сразу привязывается к ней.
    /// </summary>
    public async Task<long> CopyAsync(long patternId, long? modelId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevPattern, ct);
        var org = ctx.OrganizationId;
        return await InTransactionAsync(async () =>
        {
            var src = await LoadAsync(ctx, patternId, ct);
            var number = DevPattern.FormatNumber(await DocumentNumbers.NextAsync(db, org, Counter, ct));
            var copy = DevPattern.Create(org, number, new DevPatternValues($"{src.Name} (копия)"[..Math.Min(DevPattern.NameMaxLength, src.Name.Length + 8)], src.Kind,
                src.RapportWidth, src.RapportHeight, src.MaxFloatMm, src.MachineClassId, src.ProductWidthLoops, src.AlignmentRule, src.Note, src.DessinatorUserId,
                src.ProgrammerUserId), clock.UtcNow, ctx.UserId, src.Id);
            db.DevPatterns.Add(copy);
            await db.SaveOrConflictAsync(ct);
            db.DevPatternVersions.Add(DevPatternVersion.Create(org, copy.Id, 1, $"Копия {src.Number}, версия {src.CurrentVersion}", null, clock.UtcNow, ctx.UserId));
            var colors = await db.DevPatternColors.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == src.Id).OrderBy(c => c.No).ToListAsync(ct);
            var map = new Dictionary<long, DevPatternColor>();
            foreach (var c in colors)
            {
                var n = DevPatternColor.Create(org, copy.Id, c.No, c.Name, c.SharePercent, c.Feeder, c.GridKey);
                db.DevPatternColors.Add(n);
                map[c.Id] = n;
            }

            var colorways = await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == src.Id).OrderBy(c => c.SortOrder).ToListAsync(ct);
            var cwIds = colorways.Select(c => c.Id).ToList();
            var yarns = await db.DevPatternColorwayYarns.AsNoTracking().Where(y => y.OrganizationId == org && cwIds.Contains(y.ColorwayId)).ToListAsync(ct);
            var cwMap = new Dictionary<long, DevPatternColorway>();
            foreach (var cw in colorways)
            {
                var n = DevPatternColorway.Create(org, copy.Id, cw.Name, cw.SortOrder);
                db.DevPatternColorways.Add(n);
                cwMap[cw.Id] = n;
            }

            await db.SaveOrConflictAsync(ct);
            foreach (var y in yarns.Where(y => map.ContainsKey(y.ColorId)))
            {
                var n = DevPatternColorwayYarn.Create(org, cwMap[y.ColorwayId].Id, map[y.ColorId].Id);
                n.Set(y.ItemId, y.Swatch);
                db.DevPatternColorwayYarns.Add(n);
            }

            var file = await db.DevPatternFiles.AsNoTracking()
                .Where(f => f.OrganizationId == org && f.PatternId == src.Id && f.Version == src.CurrentVersion && f.Kind == DevPatternFileKind.Pattern)
                .OrderByDescending(f => f.Id).FirstOrDefaultAsync(ct);
            if (file is not null)
            {
                db.DevPatternFiles.Add(DevPatternFile.Create(org, copy.Id, 1, DevPatternFileKind.Pattern, file.FileName, file.Content, null, clock.UtcNow, ctx.UserId));
            }

            Audit(ctx, copy.Id, null, $"{copy.Number} — копия {src.Number}", "рисунок скопирован");
            await db.SaveChangesAsync(ct);
            if (modelId is { } mid)
            {
                var m = await db.DevModels.SingleOrDefaultAsync(x => x.Id == mid && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
                m.LinkPattern(copy.Id, 1, cwMap.Values.OrderBy(c => c.SortOrder).FirstOrDefault()?.Id);
                db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevTechChanged, nameof(DevModel), mid.ToString(), null,
                    $"рисунок {copy.Number}, версия 1", $"{m.Number}: рисунок", currentUser.CorrelationId));
                await db.SaveOrConflictAsync(ct);
            }

            return copy.Id;
        }, ct);
    }

    // ---------- Рисунок модели ----------

    public async Task<DevModelPatternDto> ModelPatternAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
        var p = m.PatternId is { } pid
            ? await db.DevPatterns.AsNoTracking().Where(x => x.Id == pid && x.OrganizationId == org).Select(x => new { x.Number, x.Name, x.CurrentVersion }).SingleOrDefaultAsync(ct)
            : null;
        var colorways = m.PatternId is { } pid2
            ? await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == pid2).OrderBy(c => c.SortOrder)
                .Select(c => new DevOptionDto(c.Id, c.Name)).ToListAsync(ct)
            : [];
        var versions = m.PatternId is { } pid3
            ? await db.DevPatternVersions.AsNoTracking().Where(v => v.OrganizationId == org && v.PatternId == pid3).OrderByDescending(v => v.Version)
                .Select(v => v.Version).ToListAsync(ct)
            : [];
        var patterns = await db.DevPatterns.AsNoTracking().Where(x => x.OrganizationId == org && x.Status != DevPatternStatus.Archived).OrderByDescending(x => x.Id)
            .Take(500).Select(x => new DevOptionDto(x.Id, x.Number + " — " + x.Name)).ToListAsync(ct);
        var canLink = (ctx.Permissions.Has(Permissions.DevEdit) || ctx.Permissions.Has(Permissions.DevPattern)) && !DevStages.IsFinal(m.Stage);
        return new DevModelPatternDto(m.PatternId, p?.Number, p?.Name, m.PatternVersion, p?.CurrentVersion, m.ColorwayId,
            colorways.FirstOrDefault(c => c.Id == m.ColorwayId)?.Name, patterns, colorways, versions, canLink);
    }

    /// <summary>Привязать к модели версию рисунка и колорит (или снять): свой рисунок, существующая версия, колорит этого рисунка.</summary>
    public async Task LinkToModelAsync(long modelId, long? patternId, int? version, long? colorwayId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandAnyAsync(ct, Permissions.DevEdit, Permissions.DevPattern);
        await RunAsync(async () =>
        {
            var org = ctx.OrganizationId;
            var m = await db.DevModels.SingleOrDefaultAsync(x => x.Id == modelId && x.OrganizationId == org, ct) ?? throw new NotFoundException("Модель");
            m.EnsureVersion(m.RowVersion, rowVersion);
            string after = "рисунок снят";
            if (patternId is { } pid)
            {
                var p = await db.DevPatterns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pid && x.OrganizationId == org, ct) ?? throw new NotFoundException("Рисунок");
                var v = version ?? p.CurrentVersion;
                if (!await db.DevPatternVersions.AnyAsync(x => x.OrganizationId == org && x.PatternId == pid && x.Version == v, ct))
                {
                    throw new NotFoundException("Версия рисунка");
                }

                if (colorwayId is { } cw && !await db.DevPatternColorways.AnyAsync(c => c.Id == cw && c.OrganizationId == org && c.PatternId == pid, ct))
                {
                    throw new NotFoundException("Колорит");
                }

                m.LinkPattern(pid, v, colorwayId);
                after = $"рисунок {p.Number}, версия {v}";
            }
            else
            {
                m.LinkPattern(null, null, null);
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.DevTechChanged, nameof(DevModel), m.Id.ToString(), null, after,
                $"{m.Number}: рисунок", currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
        });
    }

    /// <summary>Задачи программиста (ТЗ §7): рисунки, готовые к программированию, где я программист, а программы к текущей версии ещё нет.</summary>
    internal static IQueryable<DevPattern> ProgrammerQueue(IKnitErpDbContext db, long org, long userId) =>
        db.DevPatterns.AsNoTracking().Where(p => p.OrganizationId == org && p.ProgrammerUserId == userId && p.ReadyForProgrammingAtUtc != null
                                                 && p.Status != DevPatternStatus.Archived
                                                 && !db.DevPatternFiles.Any(f => f.OrganizationId == org && f.PatternId == p.Id && f.Version == p.CurrentVersion
                                                                                 && f.Kind == DevPatternFileKind.Program));

    public async Task<IReadOnlyList<DevPatternRowDto>> MyProgrammingAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        return (await ProgrammerQueue(db, ctx.OrganizationId, ctx.UserId).OrderBy(p => p.ReadyForProgrammingAtUtc)
                .Select(p => new { p.Id, p.Number, p.Name, p.Kind, p.RapportWidth, p.RapportHeight, p.Status, p.CurrentVersion }).ToListAsync(ct))
            .Select(p => new DevPatternRowDto(p.Id, p.Number, p.Name, p.Kind, p.RapportWidth, p.RapportHeight, 0, null, p.Status, p.CurrentVersion, 0, true)).ToList();
    }

    // ---------- Помощники ----------

    private async Task EnsureApprovableAsync(AccessContext ctx, DevPattern p, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var colors = await db.DevPatternColors.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == p.Id).Select(c => c.Id).ToListAsync(ct);
        var colorways = await db.DevPatternColorways.AsNoTracking().Where(c => c.OrganizationId == org && c.PatternId == p.Id).Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        var cwIds = colorways.Select(c => c.Id).ToList();
        var yarns = await db.DevPatternColorwayYarns.AsNoTracking().Where(y => y.OrganizationId == org && cwIds.Contains(y.ColorwayId) && y.ItemId != null)
            .Select(y => new { y.ColorwayId, y.ColorId }).ToListAsync(ct);
        var feeders = p.MachineClassId is { } mc
            ? await db.DevLookups.AsNoTracking().Where(l => l.Id == mc && l.OrganizationId == org).Select(l => l.Number).SingleOrDefaultAsync(ct)
            : null;
        DevPatternRules.EnsureApprovable(p, colors,
            colorways.Select(c => (c.Name, (IReadOnlyCollection<long>)yarns.Where(y => y.ColorwayId == c.Id).Select(y => y.ColorId).ToHashSet())).ToList(),
            p.MachineClassId is null ? null : feeders ?? int.MaxValue);
    }

    private async Task ValidateAsync(AccessContext ctx, DevPatternValues v, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        if (v.MachineClassId is { } mc && !await db.DevLookups.AnyAsync(l => l.Id == mc && l.OrganizationId == org && l.Kind == DevLookupKind.MachineClass, ct))
        {
            throw new NotFoundException("Класс машины");
        }

        var users = new[] { v.DessinatorUserId, v.ProgrammerUserId }.OfType<long>().Distinct().ToList();
        if (users.Count > 0 && await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org && users.Contains(m.UserId) && m.Status == MembershipStatus.Active, ct) != users.Count)
        {
            throw new BusinessRuleException("dev.model.assignee", "Исполнитель должен быть активным пользователем организации.");
        }
    }

    private async Task EnsureColorwayNameAsync(AccessContext ctx, long patternId, string name, long? except, CancellationToken ct)
    {
        if (await db.DevPatternColorways.AnyAsync(c => c.OrganizationId == ctx.OrganizationId && c.PatternId == patternId && c.Name == name && c.Id != except, ct))
        {
            throw new BusinessRuleException("dev.pattern.colorway.duplicate", "Колорит с таким названием у рисунка уже есть.");
        }
    }

    private async Task<DevPattern> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.DevPatterns.SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Рисунок");

    private async Task<DevPattern> LoadEditableAsync(AccessContext ctx, long id, CancellationToken ct)
    {
        var p = await LoadAsync(ctx, id, ct);
        p.EnsureEditable();
        return p;
    }

    private async Task<Dictionary<long, string>> UserNamesAsync(IEnumerable<long?> ids, CancellationToken ct)
    {
        var list = ids.OfType<long>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static string? Name(Dictionary<long, string> names, long? id) => id is { } v ? names.GetValueOrDefault(v) : null;

    private static string Describe(DevPattern p) =>
        $"{p.Name}; {DevPattern.KindName(p.Kind)}; раппорт {p.RapportWidth}×{p.RapportHeight}; протяжка {p.MaxFloatMm}; ширина {p.ProductWidthLoops}";

    private async Task<AccessContext> DemandAnyAsync(CancellationToken ct, params string[] codes)
    {
        var ctx = await guard.CurrentAsync(ct);
        return codes.Any(ctx.Permissions.Has) ? ctx : await guard.DemandAsync(codes[0], ct);
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

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var result = await action();
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private void Audit(AccessContext ctx, long patternId, string? before, string? after, string? reason, string action = AuditActions.DevPatternChanged) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(DevPattern), patternId.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
