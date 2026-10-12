using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D322: психологическое округление и правило автоцены.</summary>
public sealed class CatPriceRuleTests
{
    [Theory]
    [InlineData(1985, PriceRounding.Nine, 1989)]
    [InlineData(2010, PriceRounding.Nine, 2009)]
    [InlineData(3, PriceRounding.Nine, 9)]
    [InlineData(1940, PriceRounding.NinetyNine, 1899)]
    [InlineData(1960, PriceRounding.NinetyNine, 1999)]
    [InlineData(20, PriceRounding.NinetyNine, 99)]
    [InlineData(1234.567, PriceRounding.Kopeck, 1234.57)]
    public void Rounding(decimal price, PriceRounding rounding, decimal expected) => Assert.Equal(expected, Revaluation.Round(price, rounding));

    [Fact]
    public void Rule_price_from_purchase()
    {
        var rule = PriceRule.Create(1, 1);
        rule.Set(2.2m, PriceRounding.NinetyNine, true, DateTime.UtcNow);
        Assert.Equal(1899m, rule.PriceFor(850));
        Assert.Null(rule.PriceFor(null));
        Assert.Null(rule.PriceFor(0));
        Assert.Equal("catalog.revaluation.factor", Assert.Throws<BusinessRuleException>(() => rule.Set(0, PriceRounding.Ruble, true, DateTime.UtcNow)).Code);
        Assert.Equal(2.2m, rule.Factor);
    }
}
