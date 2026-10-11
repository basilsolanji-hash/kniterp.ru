using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;

namespace KnitErp.UnitTests;

public sealed class TagRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(" #Срочно ", "Срочно")]
    [InlineData("##Новый   год", "Новый год")]
    [InlineData("  ", "")]
    public void Name_is_normalized(string input, string expected) => Assert.Equal(expected, Tag.Normalize(input));

    [Fact]
    public void Tag_validates_name_and_color_and_archive_hides_from_menu()
    {
        Assert.Equal("tag.name.required", Assert.Throws<BusinessRuleException>(() => Tag.Create(1, "#", TagColor.Red, 1, Now)).Code);
        Assert.Equal("tag.name.too_long", Assert.Throws<BusinessRuleException>(() => Tag.Create(1, new string('я', 41), TagColor.Red, 1, Now)).Code);
        Assert.Equal("tag.color", Assert.Throws<BusinessRuleException>(() => Tag.Create(1, "Тег", (TagColor)42, 1, Now)).Code);
        var tag = Tag.Create(1, "Срочно", TagColor.Red, 1, Now);
        Assert.True(tag.IsFavorite);
        tag.Archive();
        Assert.False(tag.IsFavorite);
        Assert.Throws<BusinessRuleException>(() => tag.Update("Горит", TagColor.Orange, true));
        Assert.Throws<BusinessRuleException>(() => TagLink.Create(1, 1, (TaggedEntity)100, 1, 1, Now));
    }

    [Fact]
    public void Finder_palette_has_seven_colors_and_none()
    {
        Assert.Equal(8, TagColors.All.Count);
        Assert.Equal("#ff3b30", TagColors.Css(TagColor.Red));
        Assert.All(Enum.GetValues<TaggedEntity>(), e => Assert.NotEqual(e.ToString(), TagColors.EntityName(e)));
    }
}
