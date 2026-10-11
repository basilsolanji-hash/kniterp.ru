using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;

namespace KnitErp.Domain.Collaboration;

/// <summary>
/// Комментарий в обсуждении документа или объекта (D107): заказ, складской документ, счёт, позиция, контрагент… Обсуждение живёт
/// рядом с документом — вопрос «почему такая цена», «когда привезут», «проверьте крашение» не теряется в мессенджерах. Автор может
/// исправить текст в течение <see cref="EditWindow"/>; удалённый комментарий остаётся в ленте с пометкой «удалён», текст стирается.
/// </summary>
public sealed class DiscussionComment
{
    public const int TextMaxLength = 4000;
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);

    private DiscussionComment()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public TaggedEntity Target { get; private set; }
    public long TargetId { get; private set; }
    public long AuthorUserId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? EditedAtUtc { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public long? DeletedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsDeleted => DeletedAtUtc is not null;

    public static DiscussionComment Create(long organizationId, TaggedEntity target, long targetId, long authorUserId, string? text, DateTime nowUtc)
    {
        if (!Enum.IsDefined(target))
        {
            throw new BusinessRuleException("discussion.target", "Этот объект нельзя обсуждать.");
        }

        return new DiscussionComment
        {
            OrganizationId = organizationId,
            Target = target,
            TargetId = targetId,
            AuthorUserId = authorUserId,
            Text = Normalize(text),
            CreatedAtUtc = nowUtc,
        };
    }

    public bool CanEdit(long userId, DateTime nowUtc) => !IsDeleted && userId == AuthorUserId && nowUtc - CreatedAtUtc <= EditWindow;

    public void Edit(long userId, string? text, DateTime nowUtc)
    {
        if (IsDeleted)
        {
            throw new BusinessRuleException("discussion.deleted", "Комментарий удалён.");
        }

        if (userId != AuthorUserId)
        {
            throw new BusinessRuleException("discussion.not_author", "Исправить можно только свой комментарий.");
        }

        if (nowUtc - CreatedAtUtc > EditWindow)
        {
            throw new BusinessRuleException("discussion.edit_window", $"Исправить можно в течение {(int)EditWindow.TotalMinutes} минут — напишите новый комментарий.");
        }

        Text = Normalize(text);
        EditedAtUtc = nowUtc;
    }

    /// <summary>Удалить может автор или тот, кто ведёт организацию (модерация); текст стирается, место в ленте остаётся.</summary>
    public void Delete(long userId, bool moderator, DateTime nowUtc)
    {
        if (IsDeleted)
        {
            return;
        }

        if (userId != AuthorUserId && !moderator)
        {
            throw new BusinessRuleException("discussion.not_author", "Удалить можно только свой комментарий.");
        }

        Text = string.Empty;
        DeletedAtUtc = nowUtc;
        DeletedByUserId = userId;
    }

    private static string Normalize(string? text)
    {
        var value = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (value.Length == 0)
        {
            throw new BusinessRuleException("discussion.text_required", "Напишите текст комментария.");
        }

        if (value.Length > TextMaxLength)
        {
            throw new BusinessRuleException("discussion.text_too_long", $"Комментарий — не длиннее {TextMaxLength} символов.");
        }

        return value;
    }
}

/// <summary>Упоминание коллеги в комментарии: ему приходит уведомление со ссылкой на обсуждение.</summary>
public sealed class DiscussionMention
{
    private DiscussionMention()
    {
    }

    public long Id { get; private set; }
    public long CommentId { get; private set; }
    public long UserId { get; private set; }

    public static DiscussionMention Create(long commentId, long userId) => new() { CommentId = commentId, UserId = userId };
}
