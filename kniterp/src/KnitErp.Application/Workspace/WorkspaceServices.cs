using System.Collections.Concurrent;
using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;
using KnitErp.Domain.Workspace;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Workspace;

public sealed record SupportTicketDto(
    long Id, string Number, string Subject, string Text, string? Section, SupportTicketStatus Status, string? Answer,
    string Author, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool Unread, byte[] RowVersion)
{
    public string StatusName => SupportTicket.StatusName(Status);
}

public sealed record SupportListDto(IReadOnlyList<SupportTicketDto> Mine, IReadOnlyList<SupportTicketDto> Queue, bool CanHandle);

/// <summary>
/// Поддержка внутри организации (допущение D48): обращение создаёт любой пользователь, разбирает Администратор
/// (право управления пользователями). Свой DbContext на операцию — инструмент работает поверх страницы.
/// </summary>
public sealed class SupportService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    public async Task<SupportListDto> ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var canHandle = ctx.Permissions.Has(Permissions.UserManage);

        // Автор открыл список — ответы прочитаны, уведомление гаснет. До выборки: версии строк в списке должны быть свежими.
        var unread = await db.SupportTickets.Where(t => t.OrganizationId == ctx.OrganizationId && t.AuthorUserId == ctx.UserId && t.UnreadByAuthor).ToListAsync(ct);
        if (unread.Count > 0)
        {
            unread.ForEach(t => t.MarkReadByAuthor());
            await db.SaveChangesAsync(ct);
        }

        var q = db.SupportTickets.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId);
        var mine = await Project(db, q.Where(t => t.AuthorUserId == ctx.UserId).OrderByDescending(t => t.Id).Take(50)).ToListAsync(ct);
        var queue = canHandle
            ? await Project(db, q.Where(t => t.Status != SupportTicketStatus.Closed).OrderBy(t => t.Status).ThenBy(t => t.Id).Take(100)).ToListAsync(ct)
            : [];

        return new SupportListDto(mine, queue, canHandle);
    }

    public async Task<string> CreateAsync(string? subject, string? text, string? section, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, SupportTicket.NumberPrefix, ct);
        var ticket = SupportTicket.Create(ctx.OrganizationId, number, ctx.UserId, subject, text, section, clock.UtcNow);
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        Audit(db, ctx, AuditActions.SupportTicketCreated, ticket, null, ticket.Subject);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return number;
    }

    public async Task TakeAsync(long id, byte[] rowVersion, CancellationToken ct = default) =>
        await HandleAsync(id, rowVersion, (t, ctx) => t.TakeInWork(clock.UtcNow), AuditActions.SupportTicketChanged, ct);

    public async Task ReplyAsync(long id, string? answer, byte[] rowVersion, CancellationToken ct = default) =>
        await HandleAsync(id, rowVersion, (t, ctx) => t.Reply(answer, ctx.UserId, clock.UtcNow), AuditActions.SupportTicketChanged, ct);

    /// <summary>Закрыть может автор (вопрос решён) или Администратор.</summary>
    public async Task CloseAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var ticket = await LoadAsync(db, ctx, id, rowVersion, ct);
        if (ticket.AuthorUserId != ctx.UserId && !ctx.Permissions.Has(Permissions.UserManage))
        {
            await new AccessGuard(db, currentUser, clock).DenyAsync(ctx, Permissions.UserManage, ct);
        }

        var before = SupportTicket.StatusName(ticket.Status);
        ticket.Close(clock.UtcNow);
        Audit(db, ctx, AuditActions.SupportTicketChanged, ticket, before, SupportTicket.StatusName(ticket.Status));
        await db.SaveOrConflictAsync(ct);
    }

    private async Task HandleAsync(long id, byte[] rowVersion, Action<SupportTicket, AccessContext> action, string audit, CancellationToken ct)
    {
        await using var db = factory.Create();
        var guard = new AccessGuard(db, currentUser, clock);
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var ticket = await LoadAsync(db, ctx, id, rowVersion, ct);
        var before = SupportTicket.StatusName(ticket.Status);
        action(ticket, ctx);
        Audit(db, ctx, audit, ticket, before, SupportTicket.StatusName(ticket.Status));
        await db.SaveOrConflictAsync(ct);
    }

    private static async Task<SupportTicket> LoadAsync(IKnitErpDbContext db, AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var ticket = await db.SupportTickets.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Обращение");
        if (ticket.AuthorUserId != ctx.UserId && !ctx.Permissions.Has(Permissions.UserManage))
        {
            throw new NotFoundException("Обращение");
        }

        return ticket.EnsureVersion(ticket.RowVersion, rowVersion);
    }

    private static IQueryable<SupportTicketDto> Project(IKnitErpDbContext db, IQueryable<SupportTicket> q) =>
        q.Join(db.Users.AsNoTracking(), t => t.AuthorUserId, u => u.Id, (t, u) => new SupportTicketDto(
            t.Id, t.Number, t.Subject, t.Text, t.Section, t.Status, t.Answer, u.DisplayName, t.CreatedAtUtc, t.UpdatedAtUtc, t.UnreadByAuthor, t.RowVersion));

    private void Audit(IKnitErpDbContext db, AccessContext ctx, string action, SupportTicket t, string? before, string? after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(SupportTicket), t.Id.ToString(),
            before, after, t.Number, currentUser.CorrelationId));
}

public enum NotificationLevel
{
    Info,
    Action,
    Warning,
}

/// <summary>
/// Key — устойчивый ключ ситуации: «✕» скрывает именно её; изменилась (новое число, новый срок) — уведомление появится снова.
/// </summary>
public sealed record NotificationDto(string Text, string? Href, string? ToolId, NotificationLevel Level, string Key = "");

/// <summary>Видимые уведомления и сколько скрыто пользователем.</summary>
public sealed record NotificationListDto(IReadOnlyList<NotificationDto> Items, int Hidden);

/// <summary>
/// Уведомления собираются из существующих модулей при открытии, а не хранятся отдельно: напоминания из задач,
/// документы на утверждение и к проведению в области пользователя, ответы и новые обращения поддержки.
/// Пользователь может убрать уведомление или очистить все (D81): скрытые ключи хранятся в его личных данных.
/// </summary>
public sealed class NotificationService(IKnitErpDbContextFactory factory, PersonalToolsService personal, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Видимые уведомления (без скрытых пользователем).</summary>
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(CancellationToken ct = default) => (await ListWithHiddenAsync(ct)).Items;

    public async Task<NotificationListDto> ListWithHiddenAsync(CancellationToken ct = default)
    {
        var all = await CollectAsync(ct);
        var dismissed = await personal.GetDismissedNotificationsAsync(ct);
        var visible = all.Where(n => !dismissed.Contains(n.Key)).ToList();
        return new NotificationListDto(visible, all.Count - visible.Count);
    }

    public Task DismissAsync(string key, CancellationToken ct = default) => personal.DismissNotificationsAsync([key], ct);

    /// <summary>«Очистить все»: скрываются уведомления, видимые сейчас; новые появятся как обычно.</summary>
    public async Task DismissAllAsync(CancellationToken ct = default) =>
        await personal.DismissNotificationsAsync((await CollectAsync(ct)).Select(n => n.Key), ct);

    public Task RestoreAsync(CancellationToken ct = default) => personal.RestoreNotificationsAsync(ct);

    private async Task<List<NotificationDto>> CollectAsync(CancellationToken ct)
    {
        var result = new List<NotificationDto>();
        await using (var db = factory.Create())
        {
            var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
            var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
            var today = PersonalToolsService.Today(clock.UtcNow, tz);

            foreach (var task in (await personal.GetTasksAsync(ct)).Where(t => t.Remind && !t.Done && t.Date is { } d && d <= today)
                         .OrderBy(t => t.Date).ThenBy(t => t.Time))
            {
                var when = task.Date == today ? $"сегодня{(task.Time is { } time ? $" в {time:HH\\:mm}" : "")}" : $"просрочено с {PersonalToolsService.FormatDate(task.Date!.Value)}";
                result.Add(new(task.Title + " — " + when, null, ToolCatalog.Calendar, task.Date < today ? NotificationLevel.Warning : NotificationLevel.Info,
                    $"task:{task.Id}:{task.Date:yyyyMMdd}:{task.Time:HHmm}"));
            }

            // Дни рождения коллег сегодня (D83) — по личной настройке календаря и только с согласия сотрудника.
            foreach (var b in (await personal.GetCalendarEventsAsync(today, today, ct)).Where(e => e.Kind == CalendarEventKind.Birthday))
            {
                result.Add(new($"Сегодня день рождения: {b.Title}", null, ToolCatalog.Calendar, NotificationLevel.Info, $"bday:{b.Title}:{today:yyyyMMdd}"));
            }

            if (ctx.Permissions.Has(Permissions.OpeningBalanceApprove))
            {
                var visible = WarehouseScope.Visible(ctx, Permissions.OpeningBalanceApprove);
                var waiting = await db.OpeningBalances.AsNoTracking()
                    .CountAsync(d => d.OrganizationId == ctx.OrganizationId && d.Status == OpeningBalanceStatus.Submitted
                                     && d.CreatedByUserId != ctx.UserId && (visible == null || visible.Contains(d.WarehouseId)), ct);
                if (waiting > 0)
                {
                    result.Add(new($"Начальные остатки ждут утверждения: {waiting}", "opening-balances", null, NotificationLevel.Action,
                        $"opening:{waiting}"));
                }
            }

            if (ctx.Permissions.Has(Permissions.WarehouseDocumentPost))
            {
                var visible = WarehouseScope.Visible(ctx, Permissions.WarehouseDocumentPost);
                var drafts = await db.StockDocuments.AsNoTracking()
                    .CountAsync(d => d.OrganizationId == ctx.OrganizationId && d.Status == StockDocumentStatus.Draft
                                     && (visible == null || visible.Contains(d.WarehouseId)), ct);
                var counts = await db.InventoryCounts.AsNoTracking()
                    .CountAsync(d => d.OrganizationId == ctx.OrganizationId && d.Status == InventoryStatus.Draft
                                     && (visible == null || visible.Contains(d.WarehouseId)), ct);
                if (drafts > 0)
                {
                    result.Add(new($"Черновики складских документов к проведению: {drafts}", "stock-documents", null, NotificationLevel.Action,
                        $"drafts:{drafts}"));
                }

                if (counts > 0)
                {
                    result.Add(new($"Незавершённые инвентаризации: {counts}", "inventory", null, NotificationLevel.Action, $"counts:{counts}"));
                }
            }

            // D87: деньги — просроченные авансовые отчёты и кассы сверх лимита.
            if (ctx.Permissions.Has(Permissions.PriceView))
            {
                var alerts = await KnitErp.Application.Finance.MoneyAlerts.ComputeAsync(db, ctx.OrganizationId, today, ct);
                foreach (var a in alerts.OverdueAdvances)
                {
                    result.Add(new($"Просрочен авансовый отчёт: {a.Employee}, {a.Amount:N2} (срок {PersonalToolsService.FormatDate(a.Deadline)})", "accountables",
                        null, NotificationLevel.Warning, $"advance:{a.EmployeeId}:{a.Deadline:yyyyMMdd}"));
                }

                foreach (var c in alerts.CashOverLimit)
                {
                    result.Add(new($"Остаток кассы выше лимита: {c.Cash} — {c.Balance:N2} при лимите {c.Limit:N2}", "money", null, NotificationLevel.Warning,
                        $"cashlimit:{c.AccountId}:{today:yyyyMMdd}"));
                }
            }

            var answered = await db.SupportTickets.AsNoTracking()
                .Where(t => t.OrganizationId == ctx.OrganizationId && t.AuthorUserId == ctx.UserId && t.UnreadByAuthor)
                .Select(t => new { t.Number, t.Status }).ToListAsync(ct);
            result.AddRange(answered.Select(t => new NotificationDto(
                $"Обращение {t.Number}: {SupportTicket.StatusName(t.Status).ToLowerInvariant()}", null, ToolCatalog.Support, NotificationLevel.Info,
                $"ticket:{t.Number}:{t.Status}")));

            if (ctx.Permissions.Has(Permissions.UserManage))
            {
                var fresh = await db.SupportTickets.AsNoTracking()
                    .CountAsync(t => t.OrganizationId == ctx.OrganizationId && t.Status == SupportTicketStatus.Open, ct);
                if (fresh > 0)
                {
                    result.Add(new($"Новые обращения в поддержку: {fresh}", null, ToolCatalog.Support, NotificationLevel.Action, $"support:{fresh}"));
                }
            }

            // D796 (разработка): модели на моей стадии, мои просрочки, ждущие утверждения.
            foreach (var a in await KnitErp.Application.Development.DevNotifications.ComputeAsync(db, ctx, today, clock.UtcNow, ct))
            {
                result.Add(new(a.Text, a.Href, null, a.Warning ? NotificationLevel.Warning : NotificationLevel.Action, a.Key));
            }

            // D107: упоминания в обсуждениях и новые ответы там, где пользователь писал.
            foreach (var a in await KnitErp.Application.Collaboration.DiscussionService.AlertsAsync(db, ctx, clock.UtcNow, ct))
            {
                result.Add(new(a.Mention ? $"{a.Author} упомянул(а) вас: {a.ObjectTitle} — «{a.Excerpt}»" : $"Новый ответ в обсуждении: {a.ObjectTitle} — {a.Author}: «{a.Excerpt}»",
                    a.Href + "#discussion", null, a.Mention ? NotificationLevel.Action : NotificationLevel.Info, $"comment:{a.CommentId}"));
            }
        }

        return result;
    }
}

public enum SearchHitKind
{
    Section,
    Item,
    Document,
    Help,
    Tag,
}

public sealed record SearchHitDto(SearchHitKind Kind, string Title, string? Subtitle, string? Href, string? HelpId);

/// <summary>
/// Быстрый поиск: номенклатура по коду и названию, документы по номеру (в области складов пользователя), справка.
/// Разделы меню ищет интерфейс — он знает их список. Показывается только доступное по правам.
/// </summary>
public sealed class QuickSearchService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock, IUiText ui)
{
    public const int MaxPerKind = 8;

    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length < 2)
        {
            return [];
        }

        var hits = new List<SearchHitDto>();
        await using (var db = factory.Create())
        {
            var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
            var org = ctx.OrganizationId;

            // D103: теги — «#сроч» ищет только теги, просто текст находит и теги с таким названием.
            var tagText = KnitErp.Domain.Tagging.Tag.Normalize(text);
            if (tagText.Length > 0)
            {
                var tags = await db.Tags.AsNoTracking()
                    .Where(t => t.OrganizationId == org && !t.IsArchived && t.Name.Contains(tagText))
                    .OrderBy(t => t.Name).Take(MaxPerKind)
                    .Select(t => new { t.Id, t.Name, Count = db.TagLinks.Count(l => l.TagId == t.Id) }).ToListAsync(ct);
                hits.AddRange(tags.Select(t => new SearchHitDto(SearchHitKind.Tag, "#" + t.Name, ui.Translate("Тег") + " · " + t.Count,
                    $"tags/{t.Id}", null)));
            }

            if (text.StartsWith('#'))
            {
                return hits;
            }

            if (ctx.Permissions.Has(Permissions.CatalogView))
            {
                var items = await db.Items.AsNoTracking()
                    .Where(i => i.OrganizationId == org && (i.Code.Contains(text) || i.Name.Contains(text)))
                    .OrderBy(i => i.IsArchived).ThenBy(i => i.Code).Take(MaxPerKind)
                    .Select(i => new { i.Code, i.Name, i.IsArchived }).ToListAsync(ct);
                var stock = ctx.Permissions.Has(Permissions.WarehouseReportView);
                hits.AddRange(items.Select(i => new SearchHitDto(SearchHitKind.Item, $"{i.Code} — {i.Name}",
                    ui.Translate(i.IsArchived ? "Номенклатура, в архиве" : stock ? "Номенклатура · открыть остатки" : "Номенклатура"),
                    stock ? $"stock?search={Uri.EscapeDataString(i.Code)}" : $"catalog?search={Uri.EscapeDataString(i.Code)}", null)));
            }

            string[] docPermissions = [Permissions.WarehouseDocumentCreate, Permissions.WarehouseDocumentPost, Permissions.WarehouseReportView];
            if (docPermissions.Any(ctx.Permissions.Has))
            {
                var visible = WarehouseScope.Visible(ctx, docPermissions);
                var docs = await db.StockDocuments.AsNoTracking()
                    .Where(d => d.OrganizationId == org && d.Number.Contains(text)
                                && (visible == null || visible.Contains(d.WarehouseId) || (d.TargetWarehouseId != null && visible.Contains(d.TargetWarehouseId.Value))))
                    .OrderByDescending(d => d.Id).Take(MaxPerKind).Select(d => new { d.Id, d.Number, d.Kind, d.Status, d.DocumentDate }).ToListAsync(ct);
                hits.AddRange(docs.Select(d => new SearchHitDto(SearchHitKind.Document, $"{ui.Translate(StockDocument.KindName(d.Kind))} {d.Number}",
                    $"{d.DocumentDate:dd.MM.yyyy} · {ui.Translate(StockDocument.StatusName(d.Status))}", $"stock-documents/{d.Id}", null)));

                var counts = await db.InventoryCounts.AsNoTracking()
                    .Where(d => d.OrganizationId == org && d.Number.Contains(text) && (visible == null || visible.Contains(d.WarehouseId)))
                    .OrderByDescending(d => d.Id).Take(MaxPerKind).Select(d => new { d.Id, d.Number, d.Status, d.CountDate }).ToListAsync(ct);
                hits.AddRange(counts.Select(d => new SearchHitDto(SearchHitKind.Document, $"{ui.Translate("Инвентаризация")} {d.Number}",
                    $"{d.CountDate:dd.MM.yyyy} · {ui.Translate(InventoryCount.StatusName(d.Status))}", $"inventory/{d.Id}", null)));
            }

            // D796 (разработка): модель по номеру «М-00012» или названию.
            if (ctx.Permissions.Has(Permissions.DevView))
            {
                var devModels = await db.DevModels.AsNoTracking()
                    .Where(m => m.OrganizationId == org && (m.Number.Contains(text) || m.Name.Contains(text)))
                    .OrderByDescending(m => m.Id).Take(MaxPerKind).Select(m => new { m.Id, m.Number, m.Name, m.Stage }).ToListAsync(ct);
                hits.AddRange(devModels.Select(m => new SearchHitDto(SearchHitKind.Document, $"{m.Number} — {m.Name}",
                    ui.Translate("Модель") + " · " + ui.Translate(KnitErp.Domain.Development.DevStages.Name(m.Stage)), $"dev/models/{m.Id}", null)));
            }

            string[] openingPermissions = [Permissions.OpeningBalanceCreate, Permissions.OpeningBalanceApprove, Permissions.WarehouseReportView];
            if (openingPermissions.Any(ctx.Permissions.Has))
            {
                var visible = WarehouseScope.Visible(ctx, openingPermissions);
                var opening = await db.OpeningBalances.AsNoTracking()
                    .Where(d => d.OrganizationId == org && d.Number.Contains(text) && (visible == null || visible.Contains(d.WarehouseId)))
                    .OrderByDescending(d => d.Id).Take(MaxPerKind).Select(d => new { d.Id, d.Number, d.Status, d.AsOfDate }).ToListAsync(ct);
                hits.AddRange(opening.Select(d => new SearchHitDto(SearchHitKind.Document, $"{ui.Translate("Начальные остатки")} {d.Number}",
                    $"{d.AsOfDate:dd.MM.yyyy} · {ui.Translate(OpeningBalance.StatusName(d.Status))}", $"opening-balances/{d.Id}", null)));
            }
        }

        hits.AddRange(HelpCenter.Search(text, 3, ui).Select(a =>
            new SearchHitDto(SearchHitKind.Help, ui.Translate(a.Title), ui.Translate("Справка") + " · " + ui.Translate(a.Category), null, a.Id)));
        return hits;
    }
}

public sealed record AssistantReply(string Text, bool FromModel, IReadOnlyList<HelpArticle> Articles);

/// <summary>
/// ИИ-помощник по работе в системе. Модели передаются вопрос, раздел, роли и права пользователя и справка —
/// данные фабрики (остатки, контрагенты, сотрудники) не передаются (допущение D49). Без настроенной модели или при
/// её недоступности помощник отвечает статьями справочного центра — работа не останавливается.
/// </summary>
public sealed class AssistantService(IAssistantModel model, IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock, IUiText ui)
{
    public const int MaxQuestionLength = 1000;
    public const int MaxRequestsPerHour = 30;
    private const int MaxHistoryTurns = 10;

    private static readonly ConcurrentDictionary<long, Queue<DateTime>> Requests = new();

    public bool ModelConfigured => model.IsConfigured;

    public async Task<AssistantReply> AskAsync(string? question, string? route, IReadOnlyList<AssistantTurn> history, CancellationToken ct = default) =>
        await AskAsync(question, route, history, null, ct);

    /// <summary>persona — выбранный персонаж (D116): Ника или Книт; меняет только обращение, не знания и не права.</summary>
    public async Task<AssistantReply> AskAsync(string? question, string? route, IReadOnlyList<AssistantTurn> history, string? persona,
        CancellationToken ct = default)
    {
        var text = question?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new BusinessRuleException("field.required", "Напишите вопрос.");
        }

        if (text.Length > MaxQuestionLength)
        {
            throw new BusinessRuleException("field.too_long", $"Вопрос длиннее {MaxQuestionLength} символов.");
        }

        AccessContext ctx;
        string roles;
        await using (var db = factory.Create())
        {
            ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
            var now = clock.UtcNow;
            var roleIds = await db.RoleAssignments.AsNoTracking()
                .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == ctx.UserId && a.RoleId != null && a.RevokedAtUtc == null
                            && a.ValidFromUtc <= now && (a.ValidToUtc == null || now < a.ValidToUtc))
                .Select(a => a.RoleId!.Value).ToListAsync(ct);
            roles = string.Join(", ", (await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => r.Name).ToListAsync(ct)).Select(ui.Translate));
        }

        var current = HelpCenter.ForRoute(route);
        var related = HelpCenter.Search(text, 3, ui).Where(a => a != current).ToList();
        var articles = (current is null ? related : [current, .. related]).Take(4).ToList();

        if (!model.IsConfigured || !TryTakeQuota(ctx.UserId))
        {
            return Fallback(articles, model.IsConfigured ? ui.Translate("Лимит вопросов помощнику на этот час исчерпан. Вот что есть в справке:") : null);
        }

        var system = BuildSystemPrompt(roles, ctx, route, current, articles, AssistantPersonas.Name(AssistantPersonas.Clean(persona)));
        var conversation = history.TakeLast(MaxHistoryTurns).Append(new AssistantTurn(true, text)).ToList();
        try
        {
            // Служебные ответы модели (отказ, пустой ответ) — ключи перевода; обычный ответ уже на языке пользователя.
            return new AssistantReply(ui.Translate(await model.AskAsync(system, conversation, ct)), true, articles);
        }
        catch (ExternalServiceUnavailableException ex)
        {
            return Fallback(articles, ui.Translate(ex.Message) + " " + ui.Translate("Пока — ответ из справки:"));
        }
    }

    private AssistantReply Fallback(IReadOnlyList<HelpArticle> articles, string? lead)
    {
        var sb = new StringBuilder();
        if (lead is not null)
        {
            sb.AppendLine(lead);
        }

        if (articles.Count == 0)
        {
            sb.Append(ui.Translate("В справке ничего не нашлось. Переформулируйте вопрос или создайте обращение в «Поддержке»."));
        }
        else
        {
            sb.Append(lead is null ? ui.Translate("Нашёл в справке:") + " " : "").Append(string.Join("; ", articles.Select(a => $"«{ui.Translate(a.Title)}»"))).Append('.');
        }

        return new AssistantReply(sb.ToString().Trim(), false, articles);
    }

    /// <summary>
    /// Инструкция модели — по-русски, а справка, роли и права — на языке пользователя (D60): так ответ называет
    /// разделы и кнопки так же, как их видит пользователь.
    /// </summary>
    private string BuildSystemPrompt(string roles, AccessContext ctx, string? route, HelpArticle? current, IReadOnlyList<HelpArticle> articles,
        string? personaName = null)
    {
        var granted = string.Join("; ", Permissions.All.Where(ctx.Permissions.Has).Select(p => ui.Translate(Permissions.Describe(p))));
        var sb = new StringBuilder();
        var who = personaName is null ? "помощник" : $"{personaName}, {(personaName == "Ника" ? "помощница" : "помощник")}";
        sb.AppendLine($"Ты — {who} по работе в knitERP, системе учёта трикотажной фабрики. Отвечай {AnswerLanguage(ui.LanguageCode)}, коротко и по шагам; тон спокойный, деловой и уважительный.");
        sb.AppendLine("Отвечай о работе в системе и о производстве одежды и трикотажа — по справке и отраслевой базе знаний ниже. Не выдумывай функции, данные, нормы и номера стандартов: если ответа нет, так и скажи и предложи создать обращение в «Поддержке».");
        sb.AppendLine("Учитывай права пользователя: если для действия нужно право, которого у него нет, объясни, к кому обратиться (Владелец или Администратор), а не как обойти запрет.");
        if (ui.LanguageCode != UiLanguages.Default)
        {
            sb.AppendLine("Названия разделов, кнопок, ролей и прав пиши так, как они даны ниже в справке и в списке прав.");
        }

        sb.AppendLine($"Роли пользователя: {(roles.Length > 0 ? roles : "не назначены")}.");
        sb.AppendLine($"Права пользователя: {(granted.Length > 0 ? granted : "нет")}.");
        sb.AppendLine($"Текущий раздел: {(string.IsNullOrWhiteSpace(route) ? "главная" : route.Split('?')[0])}{(current is null ? "" : $" ({ui.Translate(current.Title)})")}.");
        sb.AppendLine("Справка:");
        foreach (var a in articles.Count > 0 ? articles : HelpCenter.Articles.Take(2).ToList())
        {
            sb.AppendLine($"## {ui.Translate(a.Title)}");
            foreach (var p in a.Body)
            {
                sb.AppendLine(ui.Translate(p));
            }
        }

        return sb.ToString();
    }

    /// <summary>Язык ответа помощника — язык интерфейса пользователя.</summary>
    private static string AnswerLanguage(string code) => code switch
    {
        "uz" => "по-узбекски (латиницей)",
        "kk" => "по-казахски",
        "be" => "по-белорусски",
        _ => "по-русски",
    };

    /// <summary>Не больше <see cref="MaxRequestsPerHour"/> обращений к модели в час на пользователя.</summary>
    private bool TryTakeQuota(long userId)
    {
        var queue = Requests.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = clock.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxRequestsPerHour)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
