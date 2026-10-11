using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D315: внутренний EAN-13, рисунок штрихкода, шаблон артикула и счётчики настроек каталога.</summary>
public sealed class CatAutoCodesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(20, 1L, "2000000000015")]
    [InlineData(29, 9_999_999_999L, "2999999999991")]
    public void Internal_ean13_has_valid_check_digit(byte prefix, long number, string expected)
    {
        var code = InternalEan13.Build(prefix, number);
        Assert.Equal(expected, code);
        Assert.Equal(code, ItemBarcode.Normalize(BarcodeType.Ean13, code)); // проходит ту же проверку, что и ручной ввод
        Assert.True(InternalEan13.IsInternal(code));
    }

    [Theory]
    [InlineData(19, 1L)]
    [InlineData(30, 1L)]
    [InlineData(20, 0L)]
    [InlineData(20, 10_000_000_000L)]
    public void Out_of_range_prefix_or_number_is_rejected(byte prefix, long number) =>
        Assert.Equal("catalog.barcode.range", Assert.Throws<BusinessRuleException>(() => InternalEan13.Build(prefix, number)).Code);

    [Fact]
    public void Ean13_pattern_has_guards_and_known_digits()
    {
        // 4006381333931 — классический пример EAN-13: первая цифра 4 → чётности LGLLGG.
        var m = Ean13Pattern.Modules("4006381333931");
        Assert.Equal(95, m.Length);
        Assert.Equal(("101", "01010", "101"), (m[..3], m[45..50], m[92..]));
        Assert.Equal("0001101", m[3..10]);   // 0 в наборе L
        Assert.Equal("0100111", m[10..17]);  // 0 в наборе G
        Assert.Equal("1000010", m[50..57]);  // правая половина начинается с «3» в наборе R
        Assert.False(Ean13Pattern.CanEncode("4006381333932"));
        Assert.Throws<ArgumentException>(() => Ean13Pattern.Modules("123"));
    }

    [Fact]
    public void Article_template_and_counters()
    {
        var s = CatalogSettings.Default(1);
        Assert.Null(s.TakeArticle("ТВ-1"));
        s.SetCodes(true, 21, 5, true, "KN-{N}", 4, 41, Now);
        Assert.Equal(("2100000000050", "KN-0041"), (s.PreviewBarcode(), s.PreviewArticle()));
        Assert.Equal(("2100000000050", "2100000000067"), (s.TakeBarcode(), s.TakeBarcode()));
        Assert.Equal(("KN-0041", "KN-0042"), (s.TakeArticle("X"), s.TakeArticle("Y")));

        s.SetCodes(false, 21, 1, true, "{CODE}-A", 4, 1, Now);
        Assert.Equal(("ТВ-1-A", "ТВ-1-A", 1), (s.TakeArticle("ТВ-1"), s.TakeArticle("ТВ-1"), s.NextArticleNumber)); // без {N} счётчик не двигается
    }

    [Theory]
    [InlineData("KN-", "catalog.settings.article_template")]
    [InlineData("", "catalog.settings.article_template")]
    [InlineData("{N}{N}{N}{N}{N}{N}{N}{N}{N}{N}x", "catalog.settings.article_template")]
    public void Invalid_article_template_is_rejected_without_change(string template, string code)
    {
        var s = CatalogSettings.Default(1);
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => s.SetCodes(true, 25, 1, true, template, 5, 1, Now)).Code);
        Assert.Equal((false, (byte)20, false), (s.AutoBarcode, s.BarcodePrefix, s.AutoArticle));
    }
}
