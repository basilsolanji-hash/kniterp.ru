using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Tagging;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Collaboration;
using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Collaboration;

public sealed record CommentDto(
    long Id, long AuthorUserId, string Author, string Text, DateTime CreatedAtUtc, DateTime? EditedAtUtc, bool IsDeleted, bool IsMine,
    bool CanEdit, bool CanDelete, IReadOnlyList<string> Mentions, byte[] RowVersion)
{
    /// <summary>Две буквы для кружка-аватара: «Владелец (разработка)» → «ВР»; скобки и знаки пропускаются.</summary>
    public string Initials => string.Concat(Author.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.FirstOrDefault(char.IsLetterOrDigit)).Where(c => c != default).Take(2).Select(char.ToUpperInvariant));
}

public sealed record ColleagueDto(long UserId, string Name);

public sealed record DiscussionThreadDto(IReadOnlyList<CommentDto> Comments, IReadOnlyList<ColleagueDto> Colleagues);

/// <summary>Строка общей ленты обсуждений: комментарий и объект, к которому он относится.</summary>
public sealed record DiscussionFeedItemDto(TaggedObjectDto Object, CommentDto Comment, bool MentionsMe);

/// <summary>Событие для уведомлений: упоминание или новый ответ в обсуждении, где пользователь участвовал.</summary>
public sealed record DiscussionAlertDto(long CommentId, bool Mention, string Author, string ObjectTitle, string Href, string Excerpt, DateTime CreatedAtUtc);

/// <summary>
/// Обсуждения документов и объектов (D107). Комментировать и читать может тот, кто видит сам объект (права и область складов —
/// через <see cref="ObjectDirectory"/>); чужая организация и невидимый объект — «не найдено». Упомянутые коллеги получают
/// уведомление, участники обсуждения — уведомление о новых ответах. Каждый вызов — со своим контекстом базы: лента стоит на любой
/// карточке и не мешает операциям страницы.
/// </summary>
public sealed class DiscussionService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    public const int MaxThread = 500;
    public const int MaxMentions = 10;
    public static readonly TimeSpan AlertWindow = TimeSpan.FromDays(14);

    public async Task<DiscussionThreadDto> ThreadAsync(TaggedEntity target, long targetId, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        await RequireObjectAsync(db, ctx, target, targetId, ct);
        var comments = await LoadAsync(db, ctx, db.DiscussionComments.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Target == target && c.TargetId == targetId)
            .OrderBy(c => c.Id).Take(MaxThread), ct);
        var colleagues = await (from m in db.OrganizationMembers.AsNoTracking()
                                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                                where m.OrganizationId == ctx.OrganizationId && m.Status == MembershipStatus.Active && u.Status == UserStatus.Active
                                      && u.Id != ctx.UserId
                                orderby u.DisplayName
                                select new ColleagueDto(u.Id, u.DisplayName)).ToListAsync(ct);
        return new DiscussionThreadDto(comments, colleagues);
    }

    public async Task<long> AddAsync(TaggedEntity target, long targetId, string? text, IReadOnlyCollection<long>? mentions, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var obj = await RequireObjectAsync(db, ctx, target, targetId, ct);
        var mentionIds = (mentions ?? []).Where(id => id != ctx.UserId).Distinct().ToList();
        if (mentionIds.Count > MaxMentions)
        {
            throw new BusinessRuleException("discussion.mentions", $"Упомянуть можно не больше {MaxMentions} коллег.");
        }

        if (mentionIds.Count > 0)
        {
            var members = await db.OrganizationMembers.AsNoTracking()
                .Where(m => m.OrganizationId == ctx.OrganizationId && m.Status == MembershipStatus.Active && mentionIds.Contains(m.UserId))
                .Select(m => m.UserId).ToListAsync(ct);
            if (members.Count != mentionIds.Count)
            {
                throw new NotFoundException("Коллега");
            }
        }

        var comment = DiscussionComment.Create(ctx.OrganizationId, target, targetId, ctx.UserId, text, clock.UtcNow);
        // Комментарий, упоминания и запись аудита — одной транзакцией: без «немого» комментария без следа в журнале.
        await using var tx = await db.BeginTransactionAsync(ct);
        db.DiscussionComments.Add(comment);
        await db.SaveChangesAsync(ct);
        foreach (var userId in mentionIds)
        {
            db.DiscussionMentions.Add(DiscussionMention.Create(comment.Id, userId));
        }

        Audit(db, ctx, AuditActions.CommentAdded, comment.Id, null, Excerpt(comment.Text), obj.Title);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return comment.Id;
    }

    public async Task EditAsync(long commentId, string? text, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var comment = await LoadCommentAsync(db, ctx, commentId, rowVersion, ct);
        var before = comment.Text;
        comment.Edit(ctx.UserId, text, clock.UtcNow);
        Audit(db, ctx, AuditActions.CommentEdited, commentId, Excerpt(before), Excerpt(comment.Text), null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Удалить свой комментарий; чужой — только с правом изменения реквизитов организации (модерация).</summary>
    public async Task DeleteAsync(long commentId, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var comment = await LoadCommentAsync(db, ctx, commentId, rowVersion, ct);
        comment.Delete(ctx.UserId, ctx.Permissions.Has(Permissions.OrganizationEdit), clock.UtcNow);
        Audit(db, ctx, AuditActions.CommentDeleted, commentId, null, null, comment.AuthorUserId == ctx.UserId ? "удалён автором" : "удалён модератором");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Количество комментариев у объектов списка — для значка «💬 N».</summary>
    public async Task<IReadOnlyDictionary<long, int>> CountsAsync(TaggedEntity target, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var list = ids.Distinct().Take(5000).ToList();
        return await db.DiscussionComments.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.Target == target && list.Contains(c.TargetId) && c.DeletedAtUtc == null)
            .GroupBy(c => c.TargetId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    /// <summary>Общая лента: последние комментарии по объектам, которые пользователь видит; можно только упоминания меня.</summary>
    public async Task<IReadOnlyList<DiscussionFeedItemDto>> FeedAsync(bool onlyMentions = false, int take = 100, CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await Guard(db).CurrentAsync(ct);
        var mine = db.DiscussionMentions.AsNoTracking().Where(m => m.UserId == ctx.UserId).Select(m => m.CommentId);
        var q = db.DiscussionComments.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId);
        if (onlyMentions)
        {
            q = q.Where(c => mine.Contains(c.Id));
        }

        var raw = await q.OrderByDescending(c => c.Id).Take(Math.Clamp(take, 1, 200) * 3).ToListAsync(ct);
        var rawIds = raw.Select(c => c.Id).ToList();
        var mentionedMe = (await mine.Where(id => rawIds.Contains(id)).ToListAsync(ct)).ToHashSet();
        var objects = new Dictionary<(TaggedEntity, long), TaggedObjectDto>();
        foreach (var group in raw.GroupBy(c => c.Target))
        {
            foreach (var o in await ObjectDirectory.DescribeAsync(db, ctx, group.Key, group.Select(c => c.TargetId).Distinct().ToList(), ct))
            {
                objects[(o.Entity, o.EntityId)] = o;
            }
        }

        var visible = raw.Where(c => objects.ContainsKey((c.Target, c.TargetId))).Take(Math.Clamp(take, 1, 200)).ToList();
        var dtos = (await ToDtosAsync(db, ctx, visible, ct)).ToDictionary(d => d.Id);
        return visible.Select(c => new DiscussionFeedItemDto(objects[(c.Target, c.TargetId)], dtos[c.Id], mentionedMe.Contains(c.Id))).ToList();
    }

    /// <summary>
    /// Для уведомлений (D107): упоминания меня и новые ответы в обсуждениях, где я писал, — за <see cref="AlertWindow"/>,
    /// только по объектам, которые я вижу. Работает с переданным контекстом (вызывается из сборщика уведомлений).
    /// </summary>
    public static async Task<IReadOnlyList<DiscussionAlertDto>> AlertsAsync(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc - AlertWindow;
        var mentioned = db.DiscussionMentions.AsNoTracking().Where(m => m.UserId == ctx.UserId).Select(m => m.CommentId);
        var mentions = await db.DiscussionComments.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.CreatedAtUtc >= since && c.DeletedAtUtc == null && c.AuthorUserId != ctx.UserId
                        && mentioned.Contains(c.Id))
            .OrderByDescending(c => c.Id).Take(20).ToListAsync(ct);

        // Ответы: в тех обсуждениях, где я писал, — чужие комментарии после моего последнего; одним запросом, по последнему на обсуждение.
        var myLast = db.DiscussionComments
            .Where(c => c.OrganizationId == ctx.OrganizationId && c.AuthorUserId == ctx.UserId && c.CreatedAtUtc >= since)
            .GroupBy(c => new { c.Target, c.TargetId }).Select(g => new { g.Key.Target, g.Key.TargetId, Last = g.Max(c => c.Id) });
        var candidates = await (from c in db.DiscussionComments.AsNoTracking()
                                join t in myLast on new { c.Target, c.TargetId } equals new { t.Target, t.TargetId }
                                where c.OrganizationId == ctx.OrganizationId && c.Id > t.Last && c.AuthorUserId != ctx.UserId && c.DeletedAtUtc == null
                                orderby c.Id descending
                                select c).Take(200).ToListAsync(ct);
        var replies = candidates.GroupBy(c => (c.Target, c.TargetId)).Select(g => g.First())
            .Where(r => mentions.All(m => m.Id != r.Id)).Take(20).ToList();

        var all = mentions.Select(c => (c, true)).Concat(replies.Select(c => (c, false))).ToList();
        if (all.Count == 0)
        {
            return [];
        }

        var objects = new Dictionary<(TaggedEntity, long), TaggedObjectDto>();
        foreach (var group in all.GroupBy(x => x.c.Target))
        {
            foreach (var o in await ObjectDirectory.DescribeAsync(db, ctx, group.Key, group.Select(x => x.c.TargetId).Distinct().ToList(), ct))
            {
                objects[(o.Entity, o.EntityId)] = o;
            }
        }

        var authorIds = all.Select(x => x.c.AuthorUserId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return all.Where(x => objects.ContainsKey((x.c.Target, x.c.TargetId)))
            .Select(x =>
            {
                var o = objects[(x.c.Target, x.c.TargetId)];
                return new DiscussionAlertDto(x.c.Id, x.Item2, authors.GetValueOrDefault(x.c.AuthorUserId, "?"), o.Title, o.Href, Excerpt(x.c.Text), x.c.CreatedAtUtc);
            })
            .OrderByDescending(a => a.CreatedAtUtc).ToList();
    }

    /// <summary>Выдержка для журнала и уведомлений; длинные номера (СНИЛС, паспорт, телефон) скрыты — D210.</summary>
    public static string Excerpt(string text)
    {
        var masked = KnitErp.Domain.Hr.PersonalDataMask.Mask(text);
        return masked.Length <= 80 ? masked : masked[..80].TrimEnd() + "…";
    }

    private async Task<IReadOnlyList<CommentDto>> LoadAsync(IKnitErpDbContext db, AccessContext ctx, IQueryable<DiscussionComment> query, CancellationToken ct) =>
        await ToDtosAsync(db, ctx, await query.ToListAsync(ct), ct);

    private async Task<IReadOnlyList<CommentDto>> ToDtosAsync(IKnitErpDbContext db, AccessContext ctx, IReadOnlyList<DiscussionComment> comments, CancellationToken ct)
    {
        var ids = comments.Select(c => c.Id).ToList();
        var mentionRows = await (from m in db.DiscussionMentions.AsNoTracking()
                                 join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                                 where ids.Contains(m.CommentId)
                                 select new { m.CommentId, u.DisplayName }).ToListAsync(ct);
        var authorIds = comments.Select(c => c.AuthorUserId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = clock.UtcNow;
        var moderator = ctx.Permissions.Has(Permissions.OrganizationEdit);
        return comments.Select(c => new CommentDto(c.Id, c.AuthorUserId, authors.GetValueOrDefault(c.AuthorUserId, "?"), c.Text, c.CreatedAtUtc, c.EditedAtUtc,
                c.IsDeleted, c.AuthorUserId == ctx.UserId, c.CanEdit(ctx.UserId, now), !c.IsDeleted && (c.AuthorUserId == ctx.UserId || moderator),
                mentionRows.Where(m => m.CommentId == c.Id).Select(m => m.DisplayName).ToList(), c.RowVersion))
            .ToList();
    }

    private static async Task<TaggedObjectDto> RequireObjectAsync(IKnitErpDbContext db, AccessContext ctx, TaggedEntity target, long id, CancellationToken ct) =>
        await ObjectDirectory.FindAsync(db, ctx, target, id, ct) ?? throw new NotFoundException("Объект");

    /// <summary>Комментарий своей организации по объекту, который пользователь видит.</summary>
    private static async Task<DiscussionComment> LoadCommentAsync(IKnitErpDbContext db, AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var comment = await db.DiscussionComments.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Комментарий");
        await RequireObjectAsync(db, ctx, comment.Target, comment.TargetId, ct);
        return comment.EnsureVersion(comment.RowVersion, rowVersion);
    }

    private AccessGuard Guard(IKnitErpDbContext db) => new(db, currentUser, clock);

    private void Audit(IKnitErpDbContext db, AccessContext ctx, string action, long commentId, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(DiscussionComment), commentId.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
