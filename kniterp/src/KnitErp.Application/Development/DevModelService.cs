using System.Linq.Expressions;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Нормативы и включённость стадий организации (D702): из настроек, недостающие — по умолчанию.</summary>
public sealed record DevStagePlan(IReadOnlyDictionary<DevStage, (int PlanDays, bool Enabled)> Stages)
{
    public bool IsEnabled(DevStage stage) => !Stages.TryGetValue(stage, out var s) || s.Enabled;

    public int PlanDays(DevStage stage) => Stages.TryGetValue(stage, out var s) ? s.PlanDays : 0;

    public static async Task<DevStagePlan> LoadAsync(IKnitErpDbContext db, long organizationId, CancellationToken ct)
    {
        var saved = await db.DevStageSettings.AsNoTracking().Where(s => s.OrganizationId == organizationId).ToListAsync(ct);
        var map = DevStages.Path.Where(s => s != DevStage.Handed)
            .ToDictionary(s => s, s => saved.FirstOrDefault(x => x.Stage == s) is { } x ? (x.PlanDays, x.IsEnabled) : (DevStages.DefaultPlanDays(s), true));
        return new DevStagePlan(map);
    }
}

/// <summary>
/// Модели разработки (D701–D708): список и доска по стадиям, карточка, задание, исполнители, стадии и их история, файлы, «Мои задачи».
/// Смотреть — «Разработка: просмотр», менять — «Разработка: изменение», утверждать — «Разработка: утверждение».
/// </summary>
public sealed class DevModelService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IImageShrinker? images = null)
{
    public const int DefaultPageSize = 50;

    /// <summary>Сколько карточек показывает колонка доски (D706): остальное — в таблице с отбором по стадии.</summary>
    public const int BoardColumnLimit = 30;

    private const string Counter = "dev.model";

    public async Task<DevModelPageDto> ListAsync(DevModelFilter filter, int page = 1, int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var today = await TodayAsync(ctx, ct);
        var q = Filtered(ctx, filter, plan, today);
        var total = await q.CountAsync(ct);
        pageSize = Math.Clamp(pageSize, 10, 200);
        page = Math.Max(1, page);
        var rows = await Rows(q.OrderBy(m => m.Stage == DevStage.Cancelled).ThenByDescending(m => m.Priority).ThenBy(m => m.PlannedLaunch == null)
                .ThenBy(m => m.PlannedLaunch).ThenByDescending(m => m.Id).Skip((page - 1) * pageSize).Take(pageSize), plan, today, ct);
        return new DevModelPageDto(rows, total, page, pageSize);
    }

    /// <summary>Доска: колонки включённых стадий, в каждой — до <see cref="BoardColumnLimit"/> моделей. Не больше 5 запросов к базе.</summary>
    public async Task<IReadOnlyList<DevBoardColumnDto>> BoardAsync(DevModelFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var today = await TodayAsync(ctx, ct);
        var q = Filtered(ctx, filter with { IncludeFinal = false }, plan, today);

        // Одним запросом — по N моделей на стадию (ROW_NUMBER по стадии), с названиями коллекции, заказчика и ответственного.
        var rows = await Rows(q.Where(m => q.Count(x => x.Stage == m.Stage
                                                   && (x.Priority > m.Priority || (x.Priority == m.Priority && x.Id > m.Id))) < BoardColumnLimit)
            .OrderByDescending(m => m.Priority).ThenByDescending(m => m.Id), plan, today, ct);
        return DevStages.Path.Where(s => s != DevStage.Handed && plan.IsEnabled(s))
            .Select(s => new DevBoardColumnDto(s, plan.PlanDays(s), rows.Where(r => r.Stage == s).ToList())).ToList();
    }

    public async Task<DevModelOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        await DevLookupDefaults.EnsureAsync(db, org, ct);
        var categories = await DevLookupDefaults.OptionsAsync(db, org, DevLookupKind.Category, ct);
        var collections = await db.DevLookups.AsNoTracking()
            .Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.Collection && !l.IsArchived)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).Select(l => new DevOptionDto(l.Id, l.Name)).ToListAsync(ct);
        // Допущение D708: заказчик — контрагент-покупатель организации.
        var customers = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.IsCustomer && !c.IsArchived)
            .OrderBy(c => c.Name).Take(1000).Select(c => new DevOptionDto(c.Id, c.Name)).ToListAsync(ct);
        var users = await Members(org).ToListAsync(ct);
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct) ?? DevSettings.Defaults(org);
        var activeIds = users.Select(u => u.Id).ToHashSet();
        return new DevModelOptionsDto(collections, customers, users, ctx.Permissions.Has(Permissions.DevEdit),
            DevModel.Roles.ToDictionary(r => r, r => settings.DefaultOf(r) is { } u && activeIds.Contains(u) ? u : (long?)null), settings.AutoLaunchDate,
            categories);
    }

    public async Task<DevModelDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Модель");
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var today = await TodayAsync(ctx, ct);
        var history = await db.DevModelStages.AsNoTracking().Where(h => h.OrganizationId == ctx.OrganizationId && h.ModelId == id)
            .OrderBy(h => h.Id).ToListAsync(ct);
        var files = await db.DevFiles.AsNoTracking().Where(f => f.OrganizationId == ctx.OrganizationId && f.ModelId == id)
            .OrderBy(f => f.Kind).ThenBy(f => f.FileName).ThenByDescending(f => f.Version)
            .Select(f => new { f.Id, f.Kind, f.FileName, f.Version, f.SizeBytes, f.ContentType, f.Comment, f.UploadedAtUtc, f.UploadedByUserId })
            .ToListAsync(ct);
        var userIds = DevModel.Roles.Select(m.AssigneeOf).OfType<long>().Concat(history.Select(h => h.UserId)).Concat(files.Select(f => f.UploadedByUserId))
            .Append(m.CreatedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var collection = m.CollectionId is { } cid
            ? await db.DevLookups.AsNoTracking().Where(l => l.Id == cid).Select(l => l.Name).SingleOrDefaultAsync(ct)
            : null;
        var customer = m.CustomerId is { } kid
            ? await db.Counterparties.AsNoTracking().Where(c => c.Id == kid).Select(c => c.Name).SingleOrDefaultAsync(ct)
            : null;
        var category = await db.DevLookups.AsNoTracking().Where(l => l.Id == m.CategoryId && l.OrganizationId == ctx.OrganizationId).Select(l => l.Name)
            .SingleAsync(ct);

        var canEdit = ctx.Permissions.Has(Permissions.DevEdit);
        var canApprove = ctx.Permissions.Has(Permissions.DevApprove);
        var canSeeCost = ctx.Permissions.Has(Permissions.DevCostView);
        var targets = canEdit
            ? DevStages.Path.Where(s => DevStages.CheckMove(m.Stage, s, m.HasPattern, canApprove, plan.IsEnabled) is null).ToList()
            : [];
        var next = DevStages.Next(m.Stage, m.HasPattern, plan.IsEnabled) is { } n && targets.Contains(n) ? n : (DevStage?)null;
        var template = await db.DevCategoryTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.OrganizationId == ctx.OrganizationId && t.CategoryId == m.CategoryId, ct);
        var hints = DevAutomationRules.Hints(m.Stage,
            hasBrief: m.SizeRange is not null || m.RunQuantity is not null || m.Description is not null,
            hasSketch: files.Any(f => f.Kind is DevFileKind.Sketch or DevFileKind.Inspiration),
            hasPatternFile: files.Any(f => f.Kind == DevFileKind.Pattern),
            hasProgram: files.Any(f => f.Kind == DevFileKind.MachineProgram),
            hasResponsible: DevStages.ResponsibleRole(m.Stage) is not { } role || m.AssigneeOf(role) is not null);
        // D769: соседние версии модели — ссылки «на основе» и «следующая версия».
        var previous = m.PreviousVersionId is { } pid
            ? await db.DevModels.AsNoTracking().Where(x => x.Id == pid && x.OrganizationId == ctx.OrganizationId).Select(x => new { x.Id, x.Number }).SingleOrDefaultAsync(ct)
            : null;
        var next2 = await db.DevModels.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.PreviousVersionId == m.Id)
            .Select(x => new { x.Id, x.Number }).SingleOrDefaultAsync(ct);
        return new DevModelDto(m.Id, m.Number, m.Name, m.CollectionId, collection, m.CategoryId, category, m.Audience, m.ChildSafety, m.HasPattern, m.CustomerId, customer,
            m.DealId, m.PlannedLaunch, m.Priority, m.Stage, m.StageSinceUtc, plan.PlanDays(m.Stage), m.CancelReason,
            DevModel.Roles.Select(r => new DevAssigneeDto(r, m.AssigneeOf(r), m.AssigneeOf(r) is { } u ? users.GetValueOrDefault(u) : null)).ToList(),
            new DevBrief(m.Description, m.SizeRange, m.ColorRange, canSeeCost ? m.TargetCost : null, canSeeCost ? m.TargetPrice : null, m.RunQuantity,
                m.BriefDueDate, m.Composition, m.Labeling, m.Care),
            canSeeCost,
            history.Select(h => new DevStageEntryDto(h.FromStage, h.ToStage, h.AtUtc, users.GetValueOrDefault(h.UserId) ?? "—", h.Comment, h.DaysInPrevious))
                .ToList(),
            files.Select(f => new DevFileDto(f.Id, f.Kind, f.FileName, f.Version, f.SizeBytes,
                f.ContentType.StartsWith("image/") && f.ContentType != DevFileTypes.Dxf, f.Comment, f.UploadedAtUtc,
                users.GetValueOrDefault(f.UploadedByUserId) ?? "—")).ToList(),
            targets, next, canEdit && !DevStages.IsFinal(m.Stage), canApprove,
            DevModel.IsLate(m.Stage, m.PlannedLaunch, today), DevModel.IsStageOverdue(m.Stage, m.StageSinceUtc, plan.PlanDays(m.Stage), clock.UtcNow),
            users.GetValueOrDefault(m.CreatedByUserId) ?? "—", m.CreatedAtUtc, m.RowVersion, hints, template is { IsEmpty: false }, m.MainSketchFileId,
            m.VersionNo, m.PreviousVersionId, previous?.Number, next2?.Id, next2?.Number, m.DyeNumber, m.DensityLoops, m.DensityRows, m.ShrinkagePercent);
    }

    /// <summary>
    /// Новая модель. Автоматизация (D790–D792): пустое название — «категория — коллекция — заказчик», незаполненные исполнители —
    /// исполнители по умолчанию, не указан запуск — по нормативам стадий, задание — из шаблона категории.
    /// </summary>
    public async Task<long> CreateAsync(DevModelHeader header, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        return await CreateCoreAsync(ctx, header, null, null, ct);
    }

    /// <summary>«Создать похожую» (D793): карточка, задание и исполнители копируются, файлы и история — нет; новая модель — на «Идее».</summary>
    public async Task<long> CopyAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        var m = await db.DevModels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Модель");
        var name = m.Name.Length + 8 > DevModel.NameMaxLength ? m.Name : m.Name + " (копия)";
        var header = new DevModelHeader(name, m.CollectionId, m.CategoryId, m.Audience, m.ChildSafety, m.HasPattern, m.CustomerId, m.DealId, null, m.Priority,
            DevModel.Roles.ToDictionary(r => r, m.AssigneeOf));
        var brief = new DevBrief(m.Description, m.SizeRange, m.ColorRange, m.TargetCost, m.TargetPrice, m.RunQuantity, null, m.Composition, m.Labeling, m.Care);
        return await CreateCoreAsync(ctx, header, brief, $"копия {m.Number}", ct);
    }

    /// <summary>Заполнить пустые поля задания из шаблона категории (D790).</summary>
    public Task FillBriefFromTemplateAsync(long id, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, async (ctx, m) =>
        {
            var template = await db.DevCategoryTemplates.AsNoTracking()
                               .SingleOrDefaultAsync(t => t.OrganizationId == ctx.OrganizationId && t.CategoryId == m.CategoryId, ct);
            if (template is null || template.IsEmpty)
            {
                throw new BusinessRuleException("dev.template.empty", "Для этой категории нет шаблона задания — заполните его в настройках разработки.");
            }

            var current = new DevBrief(m.Description, m.SizeRange, m.ColorRange, m.TargetCost, m.TargetPrice, m.RunQuantity, m.BriefDueDate, m.Composition,
                m.Labeling, m.Care);
            foreach (var c in m.UpdateBrief(template.FillBlanks(current)))
            {
                Audit(ctx, AuditActions.DevBriefChanged, id, c.Before, c.After, $"{m.Number}: {c.Field} (шаблон категории)");
            }
        }, ct);

    /// <summary>Создание с автоматизацией; участвует во внешней транзакции (загрузка из Excel — всё или ничего).</summary>
    internal async Task<long> CreateCoreAsync(AccessContext ctx, DevModelHeader header, DevBrief? brief, string? note, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org, ct) ?? DevSettings.Defaults(org);
        header = await PrepareAsync(ctx, header, settings, ct);
        await ValidateAsync(ctx, header, null, ct);
        var template = await db.DevCategoryTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.OrganizationId == org && t.CategoryId == header.CategoryId, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var number = FormatNumber(await DocumentNumbers.NextAsync(db, org, Counter, ct));
            var (model, first) = DevModel.Create(org, number, header, clock.UtcNow, ctx.UserId);
            var start = brief ?? new DevBrief(null, null, null, null, null, null, null, null, null, null);
            var filled = template is null ? start : template.FillBlanks(start);
            if (filled != new DevBrief(null, null, null, null, null, null, null, null, null, null))
            {
                model.UpdateBrief(filled);
            }

            db.DevModels.Add(model);
            db.DevModelStages.Add(first);
            await db.SaveOrConflictAsync(ct);
            Audit(ctx, AuditActions.DevModelCreated, model.Id, null, $"{model.Number} {model.Name}",
                note ?? (template is { IsEmpty: false } ? "задание из шаблона категории" : null));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return model.Id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Подстановки D791, D792: название, исполнители по умолчанию (только активные участники), плановый запуск.</summary>
    private async Task<DevModelHeader> PrepareAsync(AccessContext ctx, DevModelHeader h, DevSettings settings, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var assignees = DevModel.Roles.ToDictionary(r => r, r => h.Assignees.GetValueOrDefault(r));
        var defaults = DevModel.Roles.Where(r => assignees[r] is null && settings.DefaultOf(r) is not null).ToDictionary(r => r, r => settings.DefaultOf(r)!.Value);
        if (defaults.Count > 0)
        {
            var ids = defaults.Values.Distinct().ToList();
            var active = await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.OrganizationId == org && ids.Contains(m.UserId) && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct);
            foreach (var (role, user) in defaults.Where(d => active.Contains(d.Value)))
            {
                assignees[role] = user;
            }
        }

        var name = h.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            var collection = h.CollectionId is { } c
                ? await db.DevLookups.AsNoTracking().Where(l => l.Id == c && l.OrganizationId == org).Select(l => l.Name).SingleOrDefaultAsync(ct)
                : null;
            var customer = h.CustomerId is { } k
                ? await db.Counterparties.AsNoTracking().Where(x => x.Id == k && x.OrganizationId == org).Select(x => x.Name).SingleOrDefaultAsync(ct)
                : null;
            var category = await db.DevLookups.AsNoTracking().Where(l => l.Id == h.CategoryId && l.OrganizationId == org).Select(l => l.Name)
                .SingleOrDefaultAsync(ct);
            name = DevAutomationRules.DefaultName(category, collection, customer);
        }

        var launch = h.PlannedLaunch;
        if (launch is null && settings.AutoLaunchDate)
        {
            var plan = await DevStagePlan.LoadAsync(db, org, ct);
            launch = DevAutomationRules.PlannedLaunch(await TodayAsync(ctx, ct), h.HasPattern, plan.IsEnabled, plan.PlanDays);
        }

        return h with { Name = name, Assignees = assignees, PlannedLaunch = launch };
    }

    public Task UpdateAsync(long id, DevModelHeader header, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, async (ctx, m) =>
        {
            await ValidateAsync(ctx, header, m, ct);
            var changes = m.Update(header);
            foreach (var c in changes)
            {
                Audit(ctx, AuditActions.DevModelChanged, id, c.Before, c.After, $"{m.Number}: {c.Field}");
            }
        }, ct);

    /// <summary>Задание на разработку. Целевые суммы меняет только тот, кто их видит; без права они остаются прежними.</summary>
    public Task UpdateBriefAsync(long id, DevBrief brief, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, (ctx, m) =>
        {
            if (!ctx.Permissions.Has(Permissions.DevCostView))
            {
                brief = brief with { TargetCost = m.TargetCost, TargetPrice = m.TargetPrice };
            }

            foreach (var c in m.UpdateBrief(brief))
            {
                Audit(ctx, AuditActions.DevBriefChanged, id, c.Before, c.After, $"{m.Number}: {c.Field}");
            }

            return Task.CompletedTask;
        }, ct);

    /// <summary>Пряжа и вязка (ТЗ §8): номер краски, плотность петель и рядов на 10 см, усадка после ВТО, %. В журнал — изменённые поля.</summary>
    public Task SetKnittingAsync(long id, string? dyeNumber, decimal? densityLoops, decimal? densityRows, decimal? shrinkage, byte[] rowVersion,
        CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, (ctx, m) =>
        {
            (string Field, string? Value)[] Snapshot() =>
            [
                ("номер краски", m.DyeNumber), ("петель на 10 см", Num(m.DensityLoops)), ("рядов на 10 см", Num(m.DensityRows)),
                ("усадка после ВТО, %", Num(m.ShrinkagePercent)),
            ];

            var before = Snapshot();
            m.SetKnitting(dyeNumber, densityLoops, densityRows, shrinkage);
            var after = Snapshot();
            for (var i = 0; i < before.Length; i++)
            {
                if (before[i].Value != after[i].Value)
                {
                    Audit(ctx, AuditActions.DevModelChanged, id, before[i].Value, after[i].Value, $"{m.Number}: {before[i].Field}");
                }
            }

            return Task.CompletedTask;
        }, ct);

    private static string? Num(decimal? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public Task MoveAsync(long id, DevStage to, string? comment, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, async (ctx, m) =>
        {
            var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
            var before = m.Stage;
            // Сначала право утверждать (его проверяет MoveTo), затем расчёт: без права — понятный отказ «утверждает главный технолог».
            if (to == DevStage.Approved && ctx.Permissions.Has(Permissions.DevApprove))
            {
                await EnsureApprovableAsync(ctx, m, comment, ct);
            }

            db.DevModelStages.Add(m.MoveTo(to, ctx.Permissions.Has(Permissions.DevApprove), plan.IsEnabled, comment, clock.UtcNow, ctx.UserId));
            Audit(ctx, AuditActions.DevStageChanged, id, DevStages.Name(before), DevStages.Name(to), m.Number);
        }, ct);

    public Task CancelAsync(long id, string? reason, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, (ctx, m) =>
        {
            if (DevStages.NeedsApproval(m.Stage) && !ctx.Permissions.Has(Permissions.DevApprove))
            {
                throw new BusinessRuleException("dev.stage.move",
                    "Утверждать модель и снимать утверждение может только главный технолог (право «Разработка: утверждение»).");
            }

            var before = m.Stage;
            db.DevModelStages.Add(m.Cancel(reason, clock.UtcNow, ctx.UserId));
            Audit(ctx, AuditActions.DevModelCancelled, id, DevStages.Name(before), DevStages.Name(DevStage.Cancelled), $"{m.Number}: {m.CancelReason}");
            return Task.CompletedTask;
        }, ct);

    public Task RestoreAsync(long id, byte[] rowVersion, CancellationToken ct = default) =>
        EditAsync(id, rowVersion, Permissions.DevEdit, (ctx, m) =>
        {
            db.DevModelStages.Add(m.Restore(ctx.Permissions.Has(Permissions.DevApprove), clock.UtcNow, ctx.UserId));
            Audit(ctx, AuditActions.DevModelRestored, id, DevStages.Name(DevStage.Cancelled), DevStages.Name(m.Stage), m.Number);
            return Task.CompletedTask;
        }, ct);

    /// <summary>Файл модели: новая версия, если файл с тем же именем и видом уже есть. До 50 МБ, тип — по сигнатуре (D705).</summary>
    public async Task<long> AddFileAsync(long modelId, DevFileKind kind, string? fileName, byte[] content, string? comment, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        return await AddFileCoreAsync(ctx, modelId, kind, fileName, content, comment, null, ct);
    }

    /// <summary>
    /// Загрузка файла после проверки права вызывающим: версия под блокировкой модели, журнал, автопереход стадии (D795).
    /// <paramref name="configure"/> — привязка до вставки (программа детали к станку, D711); <paramref name="afterSave"/> — в той же транзакции.
    /// </summary>
    internal async Task<long> AddFileCoreAsync(AccessContext ctx, long modelId, DevFileKind kind, string? fileName, byte[] content, string? comment,
        Action<DevFile>? configure, CancellationToken ct, Func<DevFile, Task>? afterSave = null)
    {
        var model = await db.DevModels.AsNoTracking().Where(m => m.Id == modelId && m.OrganizationId == ctx.OrganizationId)
                        .Select(m => new { m.Id, m.Number, m.Stage }).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Модель");
        if (model.Stage == DevStage.Handed)
        {
            throw new BusinessRuleException("dev.model.handed", "Модель передана в производство — изменения вносятся новой версией модели.");
        }

        if (model.Stage == DevStage.Cancelled)
        {
            throw new BusinessRuleException("dev.model.cancelled", "Модель отменена — сначала верните её в работу.");
        }

        var name = DevFile.CleanName(fileName);
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            await db.LockAsync($"kniterp.dev.files.{ctx.OrganizationId}.{modelId}", ct);
            var used = await db.DevFiles.AsNoTracking().Where(f => f.OrganizationId == ctx.OrganizationId && f.ModelId == modelId)
                .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(f => (long)f.SizeBytes) }).SingleOrDefaultAsync(ct);
            DevFile.EnsureQuota(used?.Count ?? 0, used?.Bytes ?? 0, content.Length);

            var last = await db.DevFiles.AsNoTracking()
                .Where(f => f.OrganizationId == ctx.OrganizationId && f.ModelId == modelId && f.Kind == kind && f.FileName == name)
                .MaxAsync(f => (int?)f.Version, ct) ?? 0;
            // D729 (ТЗ §13): фото и образцы для вдохновения — до 2000 px по длинной стороне; эскизы, рисунки и документы не трогаем.
            if (kind is DevFileKind.Photo or DevFileKind.Inspiration && images is not null && DevFileTypes.Detect(kind, name, content) is { } type
                && images.Shrink(content, type, PhotoMaxSide) is { } smaller && smaller.Length > 0)
            {
                content = smaller;
            }

            var file = DevFile.Create(ctx.OrganizationId, modelId, kind, name, content, last + 1, comment, clock.UtcNow, ctx.UserId);
            configure?.Invoke(file);
            db.DevFiles.Add(file);
            await db.SaveOrConflictAsync(ct);
            if (afterSave is not null)
            {
                await afterSave(file);
            }

            Audit(ctx, AuditActions.DevFileAdded, modelId, null, $"{DevFile.KindName(kind)}: {file.FileName}, версия {file.Version}, {file.SizeBytes / 1024 + 1} КБ",
                model.Number);
            await AutoAdvanceAsync(ctx, modelId, kind, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return file.Id;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Автопереход по загруженному файлу (D795): программа машины на «Программе» → следующая стадия, рисунок на «Идее»/«Задании» → «Рисунок».
    /// Работает, если включён в настройках и переход разрешён обычными правилами; в истории — с пометкой «автоматически».
    /// </summary>
    private async Task AutoAdvanceAsync(AccessContext ctx, long modelId, DevFileKind kind, CancellationToken ct)
    {
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct)
                       ?? DevSettings.Defaults(ctx.OrganizationId);
        if (!settings.AutoAdvance)
        {
            return;
        }

        var model = await db.DevModels.SingleAsync(m => m.Id == modelId && m.OrganizationId == ctx.OrganizationId, ct);
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var canApprove = ctx.Permissions.Has(Permissions.DevApprove);
        if (DevAutomationRules.AutoAdvance(model.Stage, kind, model.HasPattern, plan.IsEnabled) is not { } to
            || DevStages.CheckMove(model.Stage, to, model.HasPattern, canApprove, plan.IsEnabled) is not null)
        {
            return;
        }

        var before = model.Stage;
        db.DevModelStages.Add(model.MoveTo(to, canApprove, plan.IsEnabled, $"Автоматически: загружен файл «{DevFile.KindName(kind)}»", clock.UtcNow, ctx.UserId));
        Audit(ctx, AuditActions.DevStageChanged, modelId, DevStages.Name(before), DevStages.Name(to), $"{model.Number}: автоматически");
    }

    /// <summary>
    /// Содержимое файла — только своей организации и с правом просмотра. Скачивание пишется в журнал; превью картинки на карточке
    /// (<paramref name="preview"/>) — нет, иначе каждое открытие карточки засоряло бы журнал (D799). Превью отдаётся только для картинок.
    /// </summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> GetFileAsync(long fileId, bool preview = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var file = await db.DevFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Файл");
        if (preview)
        {
            return file.IsImage ? (file.FileName, file.ContentType, file.Content) : throw new NotFoundException("Файл");
        }

        Audit(ctx, AuditActions.DevFileViewed, file.ModelId, null, $"{file.FileName}, версия {file.Version}", null);
        await db.SaveChangesAsync(ct);
        return (file.FileName, file.ContentType, file.Content);
    }

    /// <summary>«Мои задачи разработки» (D706): модели в работе, где я исполнитель; первыми — где моя стадия сейчас.</summary>
    public async Task<IReadOnlyList<DevMyTaskDto>> MyTasksAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var me = ctx.UserId;
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var today = await TodayAsync(ctx, ct);
        var models = await db.DevModels.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled
                        && (m.DesignerUserId == me || m.DessinatorUserId == me || m.ConstructorUserId == me || m.TechnologistUserId == me
                            || m.ProgrammerUserId == me))
            .Take(500).ToListAsync(ct);
        var tz = await TimeZoneAsync(ctx, ct);
        return models.Select(m =>
            {
                var roles = DevModel.Roles.Where(r => m.AssigneeOf(r) == me).ToList();
                var responsible = DevStages.ResponsibleRole(m.Stage) is { } r && roles.Contains(r);
                var days = plan.PlanDays(m.Stage);
                DateOnly? due = days > 0 ? DateOnly.FromDateTime(ToLocal(m.StageSinceUtc, tz)).AddDays(days) : null;
                var overdue = DevModel.IsLate(m.Stage, m.PlannedLaunch, today) || (due is { } d && d < today);
                return new DevMyTaskDto(m.Id, m.Number, m.Name, m.Stage, roles, responsible, due, m.PlannedLaunch, overdue, m.Priority);
            })
            .OrderByDescending(t => t.ResponsibleNow).ThenByDescending(t => t.IsOverdue).ThenBy(t => t.StageDue ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.Priority).ToList();
    }

    public const int PhotoMaxSide = 2000;

    /// <summary>Номер счётчика «dev.model-000001» → «М-00001».</summary>
    public static string FormatNumber(string counter) =>
        "М-" + long.Parse(counter[(counter.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture).ToString("D5");

    private IQueryable<DevModel> Filtered(AccessContext ctx, DevModelFilter f, DevStagePlan plan, DateOnly today)
    {
        var q = db.DevModels.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId);
        if (f.Stage is { } stage)
        {
            q = q.Where(m => m.Stage == stage);
        }
        else if (!f.IncludeFinal)
        {
            q = q.Where(m => m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled);
        }

        if (f.CollectionId is { } c)
        {
            q = q.Where(m => m.CollectionId == c);
        }

        if (f.CustomerId is { } k)
        {
            q = q.Where(m => m.CustomerId == k);
        }

        if (f.AssigneeUserId is { } u)
        {
            q = q.Where(m => m.DesignerUserId == u || m.DessinatorUserId == u || m.ConstructorUserId == u || m.TechnologistUserId == u
                             || m.ProgrammerUserId == u);
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var text = f.Search.Trim();
            q = q.Where(m => m.Number.Contains(text) || m.Name.Contains(text));
        }

        if (f.OverdueOnly)
        {
            q = q.Where(m => m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled).Where(Overdue(plan, today, clock.UtcNow));
        }

        return q;
    }

    /// <summary>Просрочка в SQL: плановый запуск прошёл или стадия идёт дольше своего норматива.</summary>
    private static Expression<Func<DevModel, bool>> Overdue(DevStagePlan plan, DateOnly today, DateTime nowUtc)
    {
        var m = Expression.Parameter(typeof(DevModel), "m");
        var launch = Expression.Property(m, nameof(DevModel.PlannedLaunch));
        Expression body = Expression.AndAlso(
            Expression.NotEqual(launch, Expression.Constant(null, typeof(DateOnly?))),
            Expression.LessThan(launch, Expression.Constant((DateOnly?)today, typeof(DateOnly?))));
        foreach (var (stage, (days, enabled)) in plan.Stages)
        {
            if (days <= 0)
            {
                continue;
            }

            body = Expression.OrElse(body, Expression.AndAlso(
                Expression.Equal(Expression.Property(m, nameof(DevModel.Stage)), Expression.Constant(stage)),
                Expression.LessThan(Expression.Property(m, nameof(DevModel.StageSinceUtc)), Expression.Constant(nowUtc.AddDays(-days)))));
        }

        return Expression.Lambda<Func<DevModel, bool>>(body, m);
    }

    private async Task<List<DevModelRowDto>> Rows(IQueryable<DevModel> q, DevStagePlan plan, DateOnly today, CancellationToken ct)
    {
        var raw = await (from m in q
                         join l in db.DevLookups.AsNoTracking() on m.CollectionId equals l.Id into lj
                         from l in lj.DefaultIfEmpty()
                         join c in db.Counterparties.AsNoTracking() on m.CustomerId equals c.Id into cj
                         from c in cj.DefaultIfEmpty()
                         join k in db.DevLookups.AsNoTracking() on m.CategoryId equals k.Id
                         select new { m, Collection = l == null ? null : l.Name, Customer = c == null ? null : c.Name, Category = k.Name }).ToListAsync(ct);
        var responsibleIds = raw.Select(x => DevStages.ResponsibleRole(x.m.Stage) is { } r ? x.m.AssigneeOf(r) : null).OfType<long>().Distinct().ToList();
        var names = responsibleIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.Users.AsNoTracking().Where(u => responsibleIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = clock.UtcNow;
        return raw.Select(x =>
        {
            var m = x.m;
            var responsible = DevStages.ResponsibleRole(m.Stage) is { } r && m.AssigneeOf(r) is { } uid ? names.GetValueOrDefault(uid) : null;
            return new DevModelRowDto(m.Id, m.Number, m.Name, m.Stage, x.Collection, x.Category, x.Customer, m.Priority, m.PlannedLaunch, m.StageSinceUtc,
                DevModel.IsLate(m.Stage, m.PlannedLaunch, today), DevModel.IsStageOverdue(m.Stage, m.StageSinceUtc, plan.PlanDays(m.Stage), now), responsible);
        }).ToList();
    }

    private IQueryable<DevOptionDto> Members(long org) =>
        from mem in db.OrganizationMembers.AsNoTracking()
        join u in db.Users.AsNoTracking() on mem.UserId equals u.Id
        where mem.OrganizationId == org && mem.Status == MembershipStatus.Active
        orderby u.DisplayName
        select new DevOptionDto(u.Id, u.DisplayName);

    private async Task ValidateAsync(AccessContext ctx, DevModelHeader h, DevModel? current, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        if (h.CollectionId is { } cid && cid != current?.CollectionId
            && !await db.DevLookups.AnyAsync(l => l.Id == cid && l.OrganizationId == org && l.Kind == DevLookupKind.Collection && !l.IsArchived, ct))
        {
            throw new BusinessRuleException("dev.model.collection", "Коллекция не найдена или в архиве.");
        }

        if (h.CustomerId is { } kid && kid != current?.CustomerId
            && !await db.Counterparties.AnyAsync(c => c.Id == kid && c.OrganizationId == org && !c.IsArchived, ct))
        {
            throw new BusinessRuleException("dev.model.customer", "Заказчик не найден или в архиве.");
        }

        // Аудит 2026-10-12: сделка — только своей организации (раньше номер принимался без проверки; в БД теперь составной FK).
        if (h.DealId is { } did and > 0 && did != current?.DealId
            && !await db.CrmDeals.AnyAsync(d => d.Id == did && d.OrganizationId == org, ct))
        {
            throw new BusinessRuleException("dev.model.deal", "Сделка не найдена.");
        }

        var users = h.Assignees.Values.OfType<long>().Distinct().ToList();
        var unchanged = current is null ? [] : DevModel.Roles.Select(current.AssigneeOf).OfType<long>().ToList();
        var check = users.Except(unchanged).ToList();
        if (check.Count > 0)
        {
            var active = await db.OrganizationMembers.CountAsync(m => m.OrganizationId == org && check.Contains(m.UserId) && m.Status == MembershipStatus.Active, ct);
            if (active != check.Count)
            {
                throw new BusinessRuleException("dev.model.assignee", "Исполнитель должен быть активным пользователем организации.");
            }
        }

        // D733: категория — запись справочника «Категории изделий» своей организации; новая ссылка — только на действующую.
        await DevLookupDefaults.RequireAsync(db, org, DevLookupKind.Category, h.CategoryId, current?.CategoryId, "dev.model.category",
            "Категория изделия не найдена или в архиве.", ct);
    }

    /// <summary>Изменение модели: право, своя организация, версия строки, журнал; при отказе изменения забываются (один DbContext вкладки).</summary>
    /// <summary>D746: утверждение — с сохранённым расчётом себестоимости; маржа ниже порога — с обоснованием.</summary>
    private async Task EnsureApprovableAsync(AccessContext ctx, DevModel m, string? comment, CancellationToken ct)
    {
        var settings = await db.DevSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct)
                       ?? DevSettings.Defaults(ctx.OrganizationId);
        var last = await db.DevCostRuns.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && r.ModelId == m.Id)
            .OrderByDescending(r => r.Version).Select(r => new { r.Price, r.Margin }).FirstOrDefaultAsync(ct);
        var marginPercent = last is null || last.Price == 0 ? (decimal?)null : last.Margin * 100m / last.Price;
        DevApprovalRules.Ensure(settings.RequireCostForApproval, settings.MinMarginPercent, last is not null, marginPercent, comment);
    }

    private async Task EditAsync(long id, byte[] rowVersion, string permission, Func<AccessContext, DevModel, Task> change, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(permission, ct);
        var m = await db.DevModels.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Модель");
        try
        {
            m.EnsureVersion(m.RowVersion, rowVersion);
            await change(ctx, m);
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<string> TimeZoneAsync(AccessContext ctx, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct) =>
        DateOnly.FromDateTime(ToLocal(clock.UtcNow, await TimeZoneAsync(ctx, ct)));

    private static DateTime ToLocal(DateTime utc, string timeZoneId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            : utc;

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(DevModel), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
