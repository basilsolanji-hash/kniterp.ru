using KnitErp.Domain.Catalog;

namespace KnitErp.UnitTests;

/// <summary>D325: ссылка печати этикеток со строками документа — число этикеток по количеству.</summary>
public sealed class CatLabelLinkTests
{
    [Fact]
    public void Build_counts_whole_quantities_one_for_fractional_and_sums_repeats()
    {
        Assert.Equal("12x5,13,14,15x1000", LabelLink.Build([(12, 3m), (13, 2.5m), (14, 0m), (12, 2m), (15, 5000m)], 100));
        Assert.Equal("12x5", LabelLink.Build([(12, 5m), (13, 1m)], 1));
        Assert.Equal("", LabelLink.Build([], 10));
    }

    [Fact]
    public void Parse_reads_counts_old_links_and_ignores_garbage()
    {
        Assert.Equal([(12L, 5), (13L, 1), (14L, 3)], LabelLink.Parse("12x5, 13,14*2,14X1,abc,-1,0,15x0,16x-2,1e3,"));
        Assert.Equal([(7L, LabelLink.MaxPerItem)], LabelLink.Parse("7x999,7x999"));
        Assert.Empty(LabelLink.Parse(null));
    }
}
