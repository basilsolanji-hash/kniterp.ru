using KnitErp.Domain.Production;

namespace KnitErp.UnitTests;

public sealed class ShopFloorCodeTests
{
    [Theory]
    [InlineData("ОП-123-1", 123, ProductionStage.Knitting)]
    [InlineData("  оп-7-5 ", 7, ProductionStage.Packing)]
    [InlineData("OP-42-4", 42, ProductionStage.QualityControl)]
    [InlineData("https://erp.example/shop/op/55/3", 55, ProductionStage.Steaming)]
    [InlineData("/shop/op/9/2/", 9, ProductionStage.Assembly)]
    [InlineData("shop/op/9/2", 9, ProductionStage.Assembly)]
    public void Code_text_and_qr_link_are_recognized(string input, long order, ProductionStage stage)
    {
        Assert.True(ShopFloorCode.TryParse(input, out var o, out var s));
        Assert.Equal(order, o);
        Assert.Equal(stage, s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ОП-123")]
    [InlineData("ОП-0-1")]
    [InlineData("ОП-123-6")]
    [InlineData("ОП-123-0")]
    [InlineData("ОП--1-1")]
    [InlineData("ОП-12a-1")]
    [InlineData("https://erp.example/shop/op/1/1?x=javascript:alert(1)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ОП-99999999999999999999-1")]
    public void Wrong_codes_are_rejected(string? input) => Assert.False(ShopFloorCode.TryParse(input, out _, out _));

    [Fact]
    public void Text_and_path_round_trip()
    {
        foreach (var stage in ProductionFlow.Stages)
        {
            Assert.True(ShopFloorCode.TryParse(ShopFloorCode.Text(321, stage), out var a, out var b));
            Assert.Equal((321L, stage), (a, b));
            Assert.True(ShopFloorCode.TryParse(ShopFloorCode.Path(321, stage), out a, out b));
            Assert.Equal((321L, stage), (a, b));
        }
    }
}
