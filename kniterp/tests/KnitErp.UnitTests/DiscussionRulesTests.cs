using KnitErp.Domain.Collaboration;
using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;

namespace KnitErp.UnitTests;

/// <summary>D107: правила комментария — текст, окно исправления, удаление автором и модератором.</summary>
public sealed class DiscussionRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Text_is_trimmed_required_and_limited()
    {
        var c = DiscussionComment.Create(1, TaggedEntity.SalesOrder, 5, 10, "  Когда отгрузка?\r\nУточните  ", Now);
        Assert.Equal("Когда отгрузка?\nУточните", c.Text);
        Assert.Equal("discussion.text_required", Assert.Throws<BusinessRuleException>(() => DiscussionComment.Create(1, TaggedEntity.Item, 5, 10, "   ", Now)).Code);
        Assert.Equal("discussion.text_too_long", Assert.Throws<BusinessRuleException>(
            () => DiscussionComment.Create(1, TaggedEntity.Item, 5, 10, new string('я', DiscussionComment.TextMaxLength + 1), Now)).Code);
        Assert.Equal("discussion.target", Assert.Throws<BusinessRuleException>(() => DiscussionComment.Create(1, (TaggedEntity)99, 5, 10, "Текст", Now)).Code);
        Assert.Equal(DiscussionComment.TextMaxLength, DiscussionComment.Create(1, TaggedEntity.Item, 5, 10, new string('я', DiscussionComment.TextMaxLength), Now).Text.Length);
    }

    [Fact]
    public void Only_author_edits_within_window()
    {
        var c = DiscussionComment.Create(1, TaggedEntity.StockDocument, 5, 10, "Черновик", Now);
        Assert.True(c.CanEdit(10, Now.AddMinutes(15)));
        Assert.False(c.CanEdit(11, Now));
        Assert.False(c.CanEdit(10, Now.AddMinutes(16)));
        Assert.Equal("discussion.not_author", Assert.Throws<BusinessRuleException>(() => c.Edit(11, "Чужая правка", Now)).Code);
        Assert.Equal("discussion.edit_window", Assert.Throws<BusinessRuleException>(() => c.Edit(10, "Поздно", Now.AddMinutes(16))).Code);
        c.Edit(10, " Исправлено ", Now.AddMinutes(5));
        Assert.Equal(("Исправлено", Now.AddMinutes(5)), (c.Text, c.EditedAtUtc));
    }

    [Fact]
    public void Delete_by_author_or_moderator_erases_text_and_is_idempotent()
    {
        var c = DiscussionComment.Create(1, TaggedEntity.Item, 5, 10, "Лишнее", Now);
        Assert.Equal("discussion.not_author", Assert.Throws<BusinessRuleException>(() => c.Delete(11, false, Now)).Code);
        c.Delete(11, true, Now.AddHours(1));
        Assert.True(c.IsDeleted);
        Assert.Equal((string.Empty, 11L, Now.AddHours(1)), (c.Text, c.DeletedByUserId, c.DeletedAtUtc));
        c.Delete(10, false, Now.AddHours(2)); // повтор ничего не меняет
        Assert.Equal((11L, Now.AddHours(1)), (c.DeletedByUserId, c.DeletedAtUtc));
        Assert.False(c.CanEdit(10, Now));
        Assert.Equal("discussion.deleted", Assert.Throws<BusinessRuleException>(() => c.Edit(10, "Вернуть", Now)).Code);
    }
}
